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
    private const int HaloPixelVendorId = 0x2d99;
    private const int HaloPixelProductId = 0xa106;
    private const int ControlReportLength = 64;
    private const int ControlUsagePage = 0xff14;
    private const int ControlUsage = 0x01;
    private const int LightingWriteRepetitions = 3;
    private static readonly TimeSpan LightingWriteDelay = TimeSpan.FromMilliseconds(30);

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

    public bool SetPixelScreenEnabled(HaloPixelColor color, bool enabled)
    {
        if (CurrentDevice is null)
            return false;

        using var stream = CurrentDevice.Open();
        stream.ReadTimeout = 250;
        stream.Write(HidPacketBuilder.BuildPixelScreenStateQuery());
        var currentState = WaitForEdifierResponse(
            stream,
            0xee,
            response => IsKnownPixelScreenState(response.Payload),
            TimeSpan.FromSeconds(1));
        var packetColor = !enabled && currentState?.Payload is { Length: >= 4 } payload
            ? new HaloPixelColor(payload[1], payload[2], payload[3])
            : color;

        stream.Write(HidPacketBuilder.BuildPixelScreenPower(packetColor, enabled));
        var acknowledged = WaitForEdifierResponse(
            stream,
            0xef,
            response => IsPixelScreenPowerAck(response.Payload, enabled),
            TimeSpan.FromSeconds(1)) is not null;

        stream.Write(HidPacketBuilder.BuildPixelScreenStateQuery());
        var stateConfirmed = WaitForEdifierResponse(
            stream,
            0xee,
            response => IsPixelScreenPowerState(response.Payload, enabled),
            TimeSpan.FromSeconds(1)) is not null;
        return acknowledged && stateConfirmed;
    }

    public void SetAmbientLight(AmbientLightOptions options)
    {
        using var stream = CurrentDevice?.Open();
        WriteLightingPacket(stream, HidPacketBuilder.BuildAmbientLight(options));
        stream?.Close();
    }

    public bool SetAmbientLightEnabled(bool enabled)
    {
        if (CurrentDevice is null)
            return false;

        using var stream = CurrentDevice.Open();
        stream.ReadTimeout = 250;
        stream.Write(HidPacketBuilder.BuildAmbientLightPower(enabled));
        var acknowledged = WaitForEdifierResponse(
            stream,
            0x6b,
            response => IsAmbientLightPowerAck(response.Payload, enabled),
            TimeSpan.FromSeconds(1)) is not null;

        stream.Write(HidPacketBuilder.BuildAmbientLightStateQuery());
        var stateConfirmed = WaitForEdifierResponse(
            stream,
            0x6a,
            response => IsAmbientLightPowerState(response.Payload, enabled),
            TimeSpan.FromSeconds(1)) is not null;
        return acknowledged && stateConfirmed;
    }

    public bool CalibrateTime(DateTime localTime)
    {
        if (CurrentDevice is null)
            return false;

        using var stream = CurrentDevice.Open();
        stream.ReadTimeout = 250;
        stream.Write(HidPacketBuilder.BuildDeviceTime(localTime));

        return WaitForEdifierResponse(
                   stream,
                   0x77,
                   response => response.Payload is [0x01],
                   TimeSpan.FromSeconds(1)) is not null;
    }

    private static void WriteLightingPacket(HidStream? stream, byte[] packet)
    {
        if (stream is null)
            return;

        for (var attempt = 0; attempt < LightingWriteRepetitions; attempt++)
        {
            stream.Write(packet);
            if (attempt + 1 < LightingWriteRepetitions)
                Thread.Sleep(LightingWriteDelay);
        }
    }

    public bool SetDeviceVolume(int volume)
    {
        var targetVolume = Math.Clamp(volume, 0, 16);
        foreach (var device in GetVolumeControlCandidates())
        {
            try
            {
                using var stream = device.Open();
                stream.ReadTimeout = 250;
                stream.Write(HidPacketBuilder.BuildDeviceVolume((byte)targetVolume));
                Thread.Sleep(30);
                if (TryReadDeviceVolume(stream, out _, out var current))
                {
                    CurrentDevice = device;
                    return current == targetVolume;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                // 某个 HID collection 可能被其他软件短暂占用；继续尝试其他合格候选接口。
            }
        }

        CurrentDevice = null;
        return false;
    }

    public bool TryGetDeviceVolume(out int maximum, out int current)
    {
        maximum = 0;
        current = 0;
        foreach (var device in GetVolumeControlCandidates())
        {
            try
            {
                using var stream = device.Open();
                stream.ReadTimeout = 250;
                if (!TryReadDeviceVolume(stream, out maximum, out current))
                    continue;

                CurrentDevice = device;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
            }
        }

        CurrentDevice = null;
        return false;
    }

    private static bool TryReadDeviceVolume(HidStream stream, out int maximum, out int current)
    {
        maximum = 0;
        current = 0;
        stream.Write(HidPacketBuilder.BuildDeviceVolumeQuery());

        var startedAt = Stopwatch.StartNew();
        while (startedAt.Elapsed < TimeSpan.FromSeconds(1))
        {
            var response = ReadEdifierResponse(stream);
            if (response is not { Command: 0x66 } || response.Payload.Length < 2)
                continue;

            maximum = response.Payload[0];
            current = response.Payload[1];
            return maximum > 0 && current <= maximum;
        }

        return false;
    }

    private IEnumerable<HidDevice> GetVolumeControlCandidates()
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (CurrentDevice is not null
            && IsValidatedControlInterface(CurrentDevice)
            && seenPaths.Add(CurrentDevice.DevicePath))
        {
            yield return CurrentDevice;
        }

        foreach (var device in GetPixelDevice())
        {
            if (seenPaths.Add(device.DevicePath))
                yield return device;
        }
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
            // 兼容旧的 EC 主机帧；当前已验证的设备回包头为 2F BB EC。
            if (buffer[start] is not (0x2e or 0x2f) || buffer[start + 1] is not (0xaa or 0xbb) || buffer[start + 2] != 0xec)
                continue;

            var payloadLength = (buffer[start + 4] << 8) | buffer[start + 5];
            var payloadStart = start + 6;
            var checksumOffset = payloadStart + payloadLength;
            if (checksumOffset >= read)
                continue;

            var checksum = 0;
            for (var index = start + 1; index < checksumOffset; index++)
                checksum += buffer[index];
            if ((byte)checksum != buffer[checksumOffset])
                continue;

            var payload = new byte[payloadLength];
            Array.Copy(buffer, payloadStart, payload, 0, payloadLength);
            return new EdifierResponse(buffer[start + 3], payload);
        }

        return null;
    }

    private static EdifierResponse? WaitForEdifierResponse(
        HidStream stream,
        byte command,
        Func<EdifierResponse, bool> predicate,
        TimeSpan timeout)
    {
        var startedAt = Stopwatch.StartNew();
        while (startedAt.Elapsed < timeout)
        {
            var response = ReadEdifierResponse(stream);
            if (response is not null && response.Command == command && predicate(response))
                return response;
        }

        return null;
    }

    private static bool IsPixelScreenPowerState(byte[] payload, bool enabled)
    {
        if (!IsKnownPixelScreenState(payload))
            return false;

        var isOffState = payload[5] == 0x03 && payload[7] == 0x00;
        return enabled ? !isOffState : isOffState;
    }

    private static bool IsKnownPixelScreenState(byte[] payload)
        => payload.Length == 12
           && payload[0] == 0x00
           && payload[4] == 0x00
           && payload[8] == 0xff
           && payload[9] == 0x01;

    private static bool IsPixelScreenPowerAck(byte[] payload, bool enabled)
        => payload.Length == 9
           && payload[0] == 0x01
           && payload[5] == 0x03
           && payload[6] == 0x00
           && payload[7] == (enabled ? (byte)0x01 : (byte)0x00)
           && payload[8] == 0xff;

    private static bool IsAmbientLightPowerAck(byte[] payload, bool enabled)
        => payload.Length == 7
           && payload[0] == 0x13
           && payload[1] == 0x07
           && payload[2] == 0x00
           && payload[3] == 0x00
           && payload[4] == (enabled ? (byte)0x01 : (byte)0x00)
           && payload[5] == 0xff
           && payload[6] == 0xff;

    private static bool IsAmbientLightPowerState(byte[] payload, bool enabled)
        => payload.Length >= 6
           && payload[^6] == 0x07
           && payload[^3] == (enabled ? (byte)0x01 : (byte)0x00)
           && payload[^2] == 0xff
           && payload[^1] == 0xff;

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
        foreach (var device in DeviceList.Local.GetHidDevices(HaloPixelVendorId, HaloPixelProductId))
        {
            if (IsValidatedControlInterface(device))
                yield return device;
        }
    }

    private static bool IsValidatedControlInterface(HidDevice device)
    {
        if (device.VendorID != HaloPixelVendorId
            || device.ProductID != HaloPixelProductId
            || device.GetMaxInputReportLength() != ControlReportLength
            || device.GetMaxOutputReportLength() != ControlReportLength)
        {
            return false;
        }

        try
        {
            var descriptor = device.GetRawReportDescriptor();
            for (var index = 0; index <= descriptor.Length - 5; index++)
            {
                if (descriptor[index] == 0x06
                    && descriptor[index + 1] == (ControlUsagePage & 0xff)
                    && descriptor[index + 2] == ((ControlUsagePage >> 8) & 0xff)
                    && descriptor[index + 3] == 0x09
                    && descriptor[index + 4] == ControlUsage)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
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
