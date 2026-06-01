using HaloPixelToolBox.Core.Models;
using HidSharp;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Models.Scenes;
using XFEExtension.NetCore.StringExtension;
using System.Diagnostics;
using System.Text;

namespace HaloPixelToolBox.Core.Utilities;

public partial class HaloPixelDevice
{
    public HidDevice? CurrentDevice { get; set; }

    public HaloPixelDevice()
    {
        DeviceList.Local.Changed += Local_Changed;
    }

    private void Local_Changed(object? sender, DeviceListChangedEventArgs e)
    {
        Console.WriteLine(sender.X());
    }

    public bool Initialize()
    {
        if (GetPixelDevice().FirstOrDefault() is HidDevice device)
        {
            CurrentDevice = device;
            return true;
        }
        else
        {
            return false;
        }
    }

    public void ShowText(string text)
    {
        using var stream = CurrentDevice?.Open();
        var data = HidPacketBuilder.BuildText(text);
        stream?.Write(data);
        stream?.Close();
    }

    public void SetTextLayout(HaloPixelTextLayout layout)
    {
        using var stream = CurrentDevice?.Open();
        byte[] package = new byte[64];
        var data = HidPacketBuilder.Build(HidPacketBuilder.ConvertLayout(layout));
        Array.Copy(data, package, data.Length);
        stream?.Write(package);
        stream?.Close();
    }

    public void SetUIModel(HaloPixelUIModel haloPixelUIModel)
    {
        using var stream = CurrentDevice?.Open();
        byte[] package = new byte[64];
        var data = HidPacketBuilder.Build(HidPacketBuilder.ConvertUIModel(haloPixelUIModel));
        Array.Copy(data, package, data.Length);
        stream?.Write(package);
        stream?.Close();
    }

    public void SetScreenScene(byte group, byte category, byte index, byte option)
    {
        using var stream = CurrentDevice?.Open();
        var data = HidPacketBuilder.BuildScreenSetting(group, category, index, option);
        stream?.Write(data);
        stream?.Close();
    }

    public void SetPersonalScene(byte categoryId, byte sceneId, string? resourceUrl)
    {
        using var stream = CurrentDevice?.Open();
        if (stream is null)
            return;

        foreach (var packet in HidPacketBuilder.BuildPersonalScenePackets(categoryId, sceneId, resourceUrl))
        {
            stream.Write(packet);
            Thread.Sleep(20);
        }

        stream.Close();
    }

    public bool SetPixelSceneResource(byte categoryId, byte sceneId, byte[] resourceBytes, HaloPixelColor? backgroundColor = null, IProgress<PixelSceneUploadProgress>? uploadProgress = null, CancellationToken cancellationToken = default)
    {
        using var stream = CurrentDevice?.Open();
        if (stream is null || resourceBytes.Length == 0)
            return false;

        try
        {
            var color = backgroundColor ?? new HaloPixelColor(0, 0, 0);
            stream.ReadTimeout = 5000;
            stream.Write(HidPacketBuilder.BuildPixelSceneCategorySelect(categoryId, color));
            WaitPixelSceneCategoryAck(stream, categoryId, TimeSpan.FromSeconds(2));

            stream.Write(HidPacketBuilder.BuildPixelSceneOptionSelect(categoryId, sceneId, color));
            if (!WaitPixelSceneSelectionAck(stream, categoryId, sceneId, TimeSpan.FromSeconds(5)))
                AppendPixelUploadLog($"scene-ack-timeout category={categoryId} scene={sceneId}");

            AppendPixelUploadLog($"send-start category={categoryId} scene={sceneId} file={resourceBytes.Length}");
            return PushPixelSceneResource(stream, resourceBytes, uploadProgress, cancellationToken);
        }
        finally
        {
            stream.Close();
        }
    }

    public void SetPixelScreenColor(HaloPixelColor color)
    {
        using var stream = CurrentDevice?.Open();
        stream?.Write(HidPacketBuilder.BuildPixelScreenColor(color));
        stream?.Close();
    }

    public void SetAmbientLight(AmbientLightOptions options)
    {
        using var stream = CurrentDevice?.Open();
        stream?.Write(HidPacketBuilder.BuildAmbientLight(options));
        stream?.Close();
    }

    public void SetAmbientLightEnabled(bool enabled)
    {
        using var stream = CurrentDevice?.Open();
        stream?.Write(HidPacketBuilder.BuildAmbientLightPower(enabled));
        stream?.Close();
    }

    private static bool PushPixelSceneResource(HidStream stream, byte[] resourceBytes, IProgress<PixelSceneUploadProgress>? uploadProgress, CancellationToken cancellationToken)
    {
        const int maxHidDataPayload = 53;
        var packetIndex = 0;
        var negotiatedPacketSize = maxHidDataPayload;
        var hasSentEnd = false;
        var startedAt = Stopwatch.StartNew();
        var resendAt = Stopwatch.StartNew();
        byte[]? lastPacket = null;
        var lastPacketNote = "start";

        void SendAndTrack(byte[] packet, string note)
        {
            stream.Write(packet);
            lastPacket = packet;
            lastPacketNote = note;
            resendAt.Restart();
        }

        ReportUploadProgress(uploadProgress, 0, resourceBytes.Length, "Preparing");
        SendAndTrack(HidPacketBuilder.BuildPixelCustomImageStart(resourceBytes.Length), "start");
        stream.ReadTimeout = 250;
        while (startedAt.Elapsed < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = ReadPixelCustomImageResponse(stream);
            if (response is null)
            {
                if (lastPacket is not null && resendAt.Elapsed >= TimeSpan.FromMilliseconds(750))
                {
                    stream.Write(lastPacket);
                    AppendPixelUploadLog($"retry packet={lastPacketNote} elapsed={startedAt.Elapsed.TotalSeconds:F1}s");
                    resendAt.Restart();
                }

                continue;
            }

            AppendPixelUploadLog($"response state={response.State} data={response.DataValue}");
            if (response.DeviceIndex != 0x00)
                continue;

            lastPacket = null;
            switch (response.State)
            {
                case 0x01:
                    packetIndex = 0;
                    negotiatedPacketSize = response.DataValue > 0 ? Math.Min(response.DataValue, maxHidDataPayload) : maxHidDataPayload;
                    AppendPixelUploadLog($"start file={resourceBytes.Length} negotiated={response.DataValue} used={negotiatedPacketSize}");
                    ReportUploadProgress(uploadProgress, 0, resourceBytes.Length, "Uploading");
                    SendPixelSceneDataPacket(SendAndTrack, resourceBytes, packetIndex++, negotiatedPacketSize, uploadProgress, out hasSentEnd);
                    break;
                case 0x03:
                    AppendPixelUploadLog($"next index={packetIndex}");
                    SendPixelSceneDataPacket(SendAndTrack, resourceBytes, packetIndex++, negotiatedPacketSize, uploadProgress, out hasSentEnd);
                    break;
                case 0x05:
                    AppendPixelUploadLog($"finish hasSentEnd={hasSentEnd} elapsed={startedAt.Elapsed.TotalSeconds:F1}s");
                    if (hasSentEnd)
                        ReportUploadProgress(uploadProgress, resourceBytes.Length, resourceBytes.Length, "Completed");
                    return hasSentEnd;
                case 0x06:
                    packetIndex = response.DataValue;
                    AppendPixelUploadLog($"cursor-error retryIndex={packetIndex}");
                    SendPixelSceneDataPacket(SendAndTrack, resourceBytes, packetIndex++, negotiatedPacketSize, uploadProgress, out hasSentEnd);
                    break;
                case 0x07:
                case 0x08:
                    AppendPixelUploadLog($"device-error state={response.State} elapsed={startedAt.Elapsed.TotalSeconds:F1}s");
                    return false;
            }
        }

        AppendPixelUploadLog($"timeout elapsed={startedAt.Elapsed.TotalSeconds:F1}s sentIndex={packetIndex}");
        return false;
    }

    private static void SendPixelSceneDataPacket(Action<byte[], string> send, byte[] resourceBytes, int packetIndex, int packetSize, IProgress<PixelSceneUploadProgress>? uploadProgress, out bool hasSentEnd)
    {
        packetSize = Math.Clamp(packetSize, 1, 53);
        var offset = packetIndex * packetSize;
        hasSentEnd = offset + packetSize >= resourceBytes.Length;
        if (offset >= resourceBytes.Length)
        {
            send(HidPacketBuilder.BuildPixelCustomImageEnd(), "end");
            hasSentEnd = true;
            return;
        }

        var count = Math.Min(packetSize, resourceBytes.Length - offset);
        send(HidPacketBuilder.BuildPixelCustomImageData((ushort)packetIndex, resourceBytes.AsSpan(offset, count)), $"data:{packetIndex}");
        AppendPixelUploadLog($"send index={packetIndex} offset={offset} count={count} last={hasSentEnd}");
        ReportUploadProgress(uploadProgress, Math.Min(offset + count, resourceBytes.Length), resourceBytes.Length, "Uploading");
        if (hasSentEnd)
            send(HidPacketBuilder.BuildPixelCustomImageEnd(), "end");
    }

    private static void ReportUploadProgress(IProgress<PixelSceneUploadProgress>? uploadProgress, long sentBytes, long totalBytes, string stage)
        => uploadProgress?.Report(new PixelSceneUploadProgress(sentBytes, totalBytes, stage));

    private static PixelCustomImageResponse? ReadPixelCustomImageResponse(HidStream stream)
    {
        var response = ReadEdifierResponse(stream);
        if (response is not { Command: 0x17 } || response.Payload.Length < 2)
            return null;

        var deviceIndex = response.Payload[0];
        var state = response.Payload[1];
        var data1 = response.Payload.Length > 2 ? response.Payload[2] : (byte)0;
        var data2 = response.Payload.Length > 3 ? response.Payload[3] : (byte)0;
        return new PixelCustomImageResponse(deviceIndex, state, (data1 << 8) | data2);
    }

    private static void WaitPixelSceneCategoryAck(HidStream stream, byte categoryId, TimeSpan timeout)
    {
        var previousTimeout = stream.ReadTimeout;
        stream.ReadTimeout = 250;
        var startedAt = Stopwatch.StartNew();

        try
        {
            while (startedAt.Elapsed < timeout)
            {
                var response = ReadEdifierResponse(stream);
                if (response is null)
                    continue;

                AppendPixelUploadLog($"category-ack-read cmd=0x{response.Command:X2} payload={ToHex(response.Payload)}");
                if (response.Command is (0xee or 0xef) && response.Payload.Length >= 7 && response.Payload[5] == 0x01 && response.Payload[6] == categoryId)
                    return;
            }
        }
        finally
        {
            stream.ReadTimeout = previousTimeout;
        }

        AppendPixelUploadLog($"category-ack-timeout category={categoryId}");
    }

    private static bool WaitPixelSceneSelectionAck(HidStream stream, byte categoryId, byte sceneId, TimeSpan timeout)
    {
        var previousTimeout = stream.ReadTimeout;
        stream.ReadTimeout = 250;
        var startedAt = Stopwatch.StartNew();

        try
        {
            while (startedAt.Elapsed < timeout)
            {
                var response = ReadEdifierResponse(stream);
                if (response is null)
                    continue;

                AppendPixelUploadLog($"ack-read cmd=0x{response.Command:X2} payload={ToHex(response.Payload)}");
                if (response.Command is not (0xee or 0xef) || response.Payload.Length < 8)
                    continue;

                if (response.Payload[5] == 0x01 && response.Payload[6] == categoryId && response.Payload[7] == sceneId)
                    return true;
            }
        }
        finally
        {
            stream.ReadTimeout = previousTimeout;
        }

        return false;
    }

    private static EdifierResponse? ReadEdifierResponse(HidStream stream)
    {
        var buffer = new byte[64];
        int read;
        try
        {
            read = stream.Read(buffer, 0, buffer.Length);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        for (var start = 0; start <= read - 8; start++)
        {
            // 上位机发送包头为 2E AA EC，设备回包头为 2F BB EC；两者 payload 布局一致。
            if (buffer[start] is not (0x2e or 0x2f) || buffer[start + 1] is not (0xaa or 0xbb) || buffer[start + 2] != 0xec)
                continue;

            var payloadLength = (buffer[start + 4] << 8) | buffer[start + 5];
            var payloadStart = start + 6;
            if (payloadStart + payloadLength > read)
                continue;

            var payload = new byte[payloadLength];
            Array.Copy(buffer, payloadStart, payload, 0, payloadLength);
            return new EdifierResponse(buffer[start + 3], payload);
        }

        return null;
    }

    private sealed record EdifierResponse(byte Command, byte[] Payload);

    private sealed record PixelCustomImageResponse(byte DeviceIndex, byte State, int DataValue);

    private static void AppendPixelUploadLog(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HaloPixelToolBox",
                "Logs");
            Directory.CreateDirectory(directory);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "pixel-scene-upload.log"), line, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string ToHex(byte[] bytes)
        => BitConverter.ToString(bytes).Replace("-", string.Empty);

    public static IEnumerable<HidDevice> GetPixelDevice()
    {
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            var name = string.Empty;
            try
            {
                name = device.GetFriendlyName();
            }
            catch { }
            if (device.GetMaxInputReportLength() == 64 && name.Contains("花再 Halo PixelBar"))
                yield return device;
        }
    }

    public static void PrintDeviceList()
    {
        foreach (var subDeivce in DeviceList.Local.GetHidDevices())
        {
            try
            {
                Console.WriteLine($"""
                    ----------------------
                    {subDeivce.GetFriendlyName()}
                    VendorID：{subDeivce.VendorID}
                    ProductID：{subDeivce.ProductID}
                    串口号：{subDeivce.GetSerialNumber()}
                    串口：{string.Join(',', subDeivce.GetSerialPorts())}
                    ReleaseNumberBcd：{subDeivce.ReleaseNumberBcd}
                    UsbPort：{subDeivce.GetUsbPort()}
                    ----------------------


                    """);
            }
            catch { }
        }
    }
}
