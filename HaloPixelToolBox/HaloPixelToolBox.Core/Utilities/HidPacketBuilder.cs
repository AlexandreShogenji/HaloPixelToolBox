using HaloPixelToolBox.Core.Models;
using System.Text;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;

namespace HaloPixelToolBox.Core.Utilities;

public class HidPacketBuilder
{
    /// <summary>
    /// 文本头
    /// </summary>
    public static readonly byte[] TextHeader =
    [
        0x2E, 0xAA, 0xEC, 0xE8, 0x00
    ];

    /// <summary>
    /// 布局头
    /// </summary>
    public static readonly byte[] LayoutHeader =
    [
        0x2E, 0xAA, 0xEC, 0xEF, 0x00, 0x09, 0x01, 0xf0, 0xb4, 0xc8, 0x00, 0x02, 0x00
    ];

    /// <summary>
    /// 固定包长度（64 bytes）
    /// </summary>
    private const int FixedPacketLength = 64;
    private const int SceneUrlPayloadSize = 42;
    private const int PixelCustomImageCommand = 0x17;
    private const int PixelCustomImageDevice = 0x00;

    /// <summary>
    /// 构造 HID 协议包
    /// </summary>
    public static byte[] BuildText(string text)
    {
        var textBytes = TruncateUtf8(text, 55);
        byte textLen = (byte)textBytes.Length;
        // 有效载荷长度 = TextLen(1) + Text(N) + Checksum(1)
        ushort totalLen = (ushort)(1 + textLen + 1);

        var list = TextHeader.ToList();

        // TotalLen (2 bytes, little-endian)
        list.AddRange(BitConverter.GetBytes(totalLen));

        // TextLen (1 byte)
        list.Add(textLen);

        // Text bytes
        list.AddRange(textBytes);

        // Checksum (1 byte)
        list.Add(byte.Parse(Checksum(textBytes).ToString()));

        return Build(list);
    }

    private static byte[] TruncateUtf8(string text, int maxByteCount)
    {
        var result = new List<byte>(maxByteCount);
        foreach (var rune in text.EnumerateRunes())
        {
            var runeText = rune.ToString();
            var bytes = Encoding.UTF8.GetBytes(runeText);
            if (result.Count + bytes.Length > maxByteCount)
                break;

            result.AddRange(bytes);
        }

        return [.. result];
    }

    /// <summary>
    /// 转换布局枚举到对应的 HID 包字节数组
    /// </summary>
    /// <param name="haloPixelTextLayout"></param>
    /// <returns></returns>
    public static byte[] ConvertLayout(HaloPixelTextLayout haloPixelTextLayout) => haloPixelTextLayout switch
    {
        HaloPixelTextLayout.Left => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x01, 0xf0, 0xb4, 0xc8, 0x00, 0x02, 0x00, 0x00, 0xff, 0xfc, 0x00],
        HaloPixelTextLayout.Center => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x01, 0xf0, 0xb4, 0xc8, 0x00, 0x02, 0x00, 0x01, 0xff, 0xfd, 0x00],
        HaloPixelTextLayout.Right => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x01, 0xf0, 0xb4, 0xc8, 0x00, 0x02, 0x00, 0x02, 0xff, 0xfe, 0x00],
        HaloPixelTextLayout.Stretch => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x01, 0xf0, 0xb4, 0xc8, 0x00, 0x02, 0x00, 0x03, 0xff, 0xff, 0x00],
        HaloPixelTextLayout.ScrollLeftToRight => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x01, 0xf0, 0xb4, 0xc8, 0x00, 0x02, 0x01, 0x00, 0xff, 0xfd, 0x00],
        HaloPixelTextLayout.ScrollRightToLeft => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x01, 0xf0, 0xb4, 0xc8, 0x00, 0x02, 0x01, 0x01, 0xff, 0xfe, 0x00],
        _ => [],
    };

    public static byte[] ConvertUIModel(HaloPixelUIModel haloPixelUIModel) => haloPixelUIModel switch
    {
        HaloPixelUIModel.Clock => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x02, 0xf0, 0xb4, 0xc8, 0x00, 0x01, 0x00, 0xff, 0xff, 0xfb, 0x00],
        HaloPixelUIModel.Game => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x02, 0xf0, 0xb4, 0xc8, 0x00, 0x01, 0x01, 0xff, 0xff, 0xfc, 0x00],
        HaloPixelUIModel.Work => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x02, 0xf0, 0xb4, 0xc8, 0x00, 0x01, 0x02, 0xff, 0xff, 0xfd, 0x00],
        HaloPixelUIModel.Read => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x02, 0xf0, 0xb4, 0xc8, 0x00, 0x01, 0x03, 0xff, 0xff, 0xfe, 0x00],
        HaloPixelUIModel.Cats => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x02, 0xf0, 0xb4, 0xc8, 0x00, 0x01, 0x04, 0xff, 0xff, 0xff, 0x00],
        HaloPixelUIModel.Dogs => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x02, 0xf0, 0xb4, 0xc8, 0x00, 0x01, 0x05, 0xff, 0xff, 0x00, 0x00],
        HaloPixelUIModel.Memes => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x02, 0xf0, 0xb4, 0xc8, 0x00, 0x01, 0x06, 0xff, 0xff, 0x01, 0x00],
        HaloPixelUIModel.Cyber => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x02, 0xf0, 0xb4, 0xc8, 0x00, 0x01, 0x07, 0xff, 0xff, 0x02, 0x00],
        HaloPixelUIModel.Waves => [0x2e, 0xaa, 0xec, 0xef, 0x00, 0x09, 0x02, 0xf0, 0xb4, 0xc8, 0x00, 0x01, 0x08, 0xff, 0xff, 0x03, 0x00],
        _ => [],
    };

    /// <summary>
    /// 构造官方个性场景屏幕设置包。
    /// 参数来源于 TempoHub 的 ScreenSettings.json，例如 1-0-0-255 表示时钟类第 0 个场景。
    /// LiLyric 的 11 个时钟场景使用同一协议，首字节必须保留为传入的 group，不能固定成分类 UI 命令 0x02。
    /// </summary>
    public static byte[] BuildScreenSetting(byte group, byte category, byte index, byte option)
    {
        byte[] payload = [group, 0xf0, 0xb4, 0xc8, 0x00, 0x01, category, index, option];

        return BuildEdifierPacket(0xef, payload);
    }

    private static byte ScreenSettingChecksum(params byte[] values)
        => (byte)((0xfc + values.Sum(value => value)) & 0xff);

    /// <summary>
    /// 构造官方 TempoHub 个性场景发送包。
    /// 设备需要收到场景分类、场景序号以及官方 .bin URL；URL 较长时按官方协议每 42 字节分片发送。
    /// </summary>
    public static IReadOnlyList<byte[]> BuildPersonalScenePackets(byte categoryId, byte sceneId, string? resourceUrl)
    {
        byte[] basePayload = [0x01, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, categoryId, sceneId];
        if (string.IsNullOrWhiteSpace(resourceUrl))
            return [BuildEdifierPacket(0xef, basePayload), BuildEdifierPacket(0xef, [0x06])];

        var urlBytes = Encoding.UTF8.GetBytes(resourceUrl);
        var packetCount = (int)Math.Ceiling(urlBytes.Length / (double)SceneUrlPayloadSize);
        var packets = new List<byte[]>(packetCount + 1);
        var urlLengthBytes = new[]
        {
            (byte)((urlBytes.Length >> 24) & 0xff),
            (byte)((urlBytes.Length >> 16) & 0xff),
            (byte)((urlBytes.Length >> 8) & 0xff),
            (byte)(urlBytes.Length & 0xff)
        };

        for (var packetIndex = 0; packetIndex < packetCount; packetIndex++)
        {
            var offset = packetIndex * SceneUrlPayloadSize;
            var count = Math.Min(SceneUrlPayloadSize, urlBytes.Length - offset);
            var payload = new List<byte>(basePayload.Length + 6 + count);
            payload.AddRange(basePayload);
            payload.AddRange(urlLengthBytes);
            payload.Add((byte)packetCount);
            payload.Add((byte)packetIndex);
            payload.AddRange(urlBytes.Skip(offset).Take(count));
            packets.Add(BuildEdifierPacket(0xef, payload));
        }

        packets.Add(BuildEdifierPacket(0xef, [0x06]));
        return packets;
    }

    /// <summary>
    /// 构造官方像素屏场景分类切换包。
    /// TempoHub 在点击分类时会先发送该包，随后点击具体场景才进入 .bin 资源上传流程。
    /// </summary>
    public static byte[] BuildPixelSceneCategorySelect(byte categoryId, HaloPixelColor backgroundColor, byte modeGroupIndex = 0x00)
        => BuildEdifierPacket(0xef, [0x02, backgroundColor.Red, backgroundColor.Green, backgroundColor.Blue, modeGroupIndex, 0x01, categoryId, 0xff, 0xff]);

    /// <summary>
    /// 构造官方像素屏场景选项切换包。
    /// TempoHub 会先发送该包选择分类/场景，再通过 0x17 自定义图像通道上传对应 .bin 场景资源。
    /// </summary>
    public static byte[] BuildPixelSceneOptionSelect(byte categoryId, byte sceneId, HaloPixelColor backgroundColor, byte modeGroupIndex = 0x00)
        => BuildEdifierPacket(0xef, [0x01, backgroundColor.Red, backgroundColor.Green, backgroundColor.Blue, modeGroupIndex, 0x01, categoryId, sceneId, 0xff]);

    /// <summary>
    /// 通知设备准备接收像素屏 .bin 资源。
    /// payload: [设备类型 PIXEL, NotifyDeviceReceive, 文件长度 4 bytes big-endian]。
    /// </summary>
    public static byte[] BuildPixelCustomImageStart(int fileSize)
    {
        byte[] payload =
        [
            PixelCustomImageDevice,
            0x00,
            (byte)((fileSize >> 24) & 0xff),
            (byte)((fileSize >> 16) & 0xff),
            (byte)((fileSize >> 8) & 0xff),
            (byte)(fileSize & 0xff)
        ];

        return BuildEdifierPacket(PixelCustomImageCommand, payload);
    }

    /// <summary>
    /// 构造像素屏 .bin 数据分片包。
    /// payload: [设备类型 PIXEL, SendDataToDevice, 分片序号高位, 分片序号低位, 数据...]。
    /// </summary>
    public static byte[] BuildPixelCustomImageData(ushort packetIndex, ReadOnlySpan<byte> data)
    {
        var payload = new List<byte>(4 + data.Length)
        {
            PixelCustomImageDevice,
            0x02,
            (byte)((packetIndex >> 8) & 0xff),
            (byte)(packetIndex & 0xff)
        };
        payload.AddRange(data.ToArray());

        return BuildEdifierPacket(PixelCustomImageCommand, payload);
    }

    /// <summary>
    /// 通知设备像素屏 .bin 分片已发送完成，设备随后会校验并返回完成状态。
    /// </summary>
    public static byte[] BuildPixelCustomImageEnd()
        => BuildEdifierPacket(PixelCustomImageCommand, [PixelCustomImageDevice, 0x04]);

    /// <summary>
    /// 构造像素屏主题颜色包。
    /// 协议来自 LiLyric 的 RGBController.set_pixel_color：2E AA EC EF 00 04 03 + RGB + checksum。
    /// </summary>
    public static byte[] BuildPixelScreenColor(HaloPixelColor color)
        => BuildEdifierPacket(0xef, [0x03, color.Red, color.Green, color.Blue]);

    /// <summary>
    /// 构造氛围灯效果包。
    /// 协议来自 LiLyric 的 RGBController._set_light_color：2E AA EC 6B 00 07 13 + effect + RGB + brightness + speed + checksum。
    /// </summary>
    public static byte[] BuildAmbientLight(AmbientLightOptions options)
    {
        var speed = Math.Clamp(options.Speed, (byte)1, (byte)10);
        byte[] payload =
        [
            0x13,
            (byte)options.Effect,
            options.Color.Red,
            options.Color.Green,
            options.Color.Blue,
            ConvertAmbientBrightness(options.Brightness),
            speed
        ];

        return BuildEdifierPacket(0x6b, payload);
    }

    /// <summary>
    /// 构造氛围灯开关包。
    /// 协议来自 TempoHub 的 mood_lighting_splice/set_device_light：B 字段 1=开启、0=关闭。
    /// </summary>
    public static byte[] BuildAmbientLightPower(bool enabled)
    {
        byte mode = enabled ? (byte)0x01 : (byte)0x00;
        return BuildEdifierPacket(0x6b, [0x00, 0x00, 0x00, 0x00, mode, 0xff, 0xff]);
    }

    /// <summary>
    /// 构造字幕音箱音量包。协议参考 LiLyric 的 VolumeController.set_volume：
    /// 2E AA EC 67 00 01 + volume(0-16) + checksum。
    /// </summary>
    public static byte[] BuildDeviceVolume(byte volume)
        => BuildEdifierPacket(0x67, [(byte)Math.Clamp((int)volume, 0, 16)]);

    private static byte ConvertAmbientBrightness(AmbientLightBrightness brightness) => brightness switch
    {
        AmbientLightBrightness.Low => 0x14,
        AmbientLightBrightness.Medium => 0x28,
        AmbientLightBrightness.High => 0x3c,
        _ => 0x3c
    };

    /// <summary>
    /// 构造通用 Edifier HID 包：2E AA EC + 指令号 + payload 长度 + payload + 校验。
    /// </summary>
    public static byte[] BuildEdifierPacket(byte commandIndex, IReadOnlyCollection<byte> payload)
    {
        var packet = new List<byte>(6 + payload.Count + 1)
        {
            0x2e,
            0xaa,
            0xec,
            commandIndex,
            (byte)((payload.Count >> 8) & 0xff),
            (byte)(payload.Count & 0xff)
        };
        packet.AddRange(payload);
        packet.Add(CalculateEdifierChecksum(packet));
        return Build(packet);
    }

    private static byte CalculateEdifierChecksum(IReadOnlyList<byte> packetWithoutChecksum)
    {
        var sum = 0;
        for (var index = 1; index < packetWithoutChecksum.Count; index++)
            sum += packetWithoutChecksum[index];

        return (byte)(sum & 0xff);
    }

    /// <summary>
    /// 构造 HID 协议包
    /// </summary>
    public static byte[] Build(byte[] bytes) => Build(bytes.ToList());

    /// <summary>
    /// 构造 HID 协议包
    /// </summary>
    public static byte[] Build(List<byte> bytes)
    {
        // Padding 补 0 到固定长度（64 字节）
        while (bytes.Count < FixedPacketLength)
            bytes.Add(0x00);

        return [.. bytes];
    }

    /// <summary>
    /// 校验算法
    /// </summary>
    public static int Checksum(byte[] textBytes)
    {
        int acc = 128;
        foreach (char ch in textBytes.Select(v => (char)v))
        {
            acc += ch + 2;
        }
        return acc % 256;
    }

    /// <summary>
    /// 把包转成 hex 字符串（小写，不带空格）
    /// </summary>
    public static string ToHex(byte[] packet)
    {
        return BitConverter.ToString(packet).Replace("-", "").ToLower();
    }
}
