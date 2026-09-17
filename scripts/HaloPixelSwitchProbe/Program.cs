using System.Diagnostics;
using System.Text.Json;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Models.Lighting;
using HaloPixelToolBox.Core.Utilities;
using HidSharp;

const int ReportLength = 64;
const int DefaultOffMilliseconds = 3000;

var execute = args.Contains("--execute", StringComparer.OrdinalIgnoreCase);
var offMilliseconds = ReadIntArgument(args, "--off-ms", DefaultOffMilliseconds, 1000, 10000);
var logPath = ReadStringArgument(args, "--log")
    ?? Path.Combine(Environment.CurrentDirectory, $"switch-probe-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

var fallbackRed = (byte)0x87;
var fallbackGreen = (byte)0xce;
var fallbackBlue = (byte)0xeb;

byte[] BuildPixelPower(bool enabled, byte red, byte green, byte blue)
    => HidPacketBuilder.BuildPixelScreenPower(new HaloPixelColor(red, green, blue), enabled);

byte[] BuildAmbientPower(bool enabled)
    => HidPacketBuilder.BuildAmbientLightPower(enabled);

var packetPreview = new
{
    PixelOff = HexPrefix(BuildPixelPower(false, fallbackRed, fallbackGreen, fallbackBlue)),
    PixelOn = HexPrefix(BuildPixelPower(true, fallbackRed, fallbackGreen, fallbackBlue)),
    AmbientOff = HexPrefix(BuildAmbientPower(false)),
    AmbientOn = HexPrefix(BuildAmbientPower(true))
};

if (!execute)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Mode = "dry-run",
        Message = "Pass --execute to send the four known switch packets.",
        Packets = packetPreview
    }, ProbeJson.Options));
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
using var logger = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
{
    AutoFlush = true
};

void Log(string kind, object value)
{
    var line = JsonSerializer.Serialize(new
    {
        Time = DateTimeOffset.Now,
        Kind = kind,
        Value = value
    }, ProbeJson.Options);
    logger.WriteLine(line);
    Console.WriteLine(line);
}

var candidates = HaloPixelDevice.GetPixelDevice().ToList();
Log("discovery", new { CandidateCount = candidates.Count });
if (candidates.Count == 0)
{
    Log("result", new { Success = false, Reason = "validated-control-interface-not-found" });
    return 2;
}

var pixelRestored = false;
var ambientRestored = false;
var pixelOffVerified = false;
var pixelOnVerified = false;
var ambientOffVerified = false;
var ambientOnVerified = false;

try
{
    using var stream = candidates[0].Open();
    stream.ReadTimeout = 150;
    Log("device-open", new
    {
        candidates[0].VendorID,
        candidates[0].ProductID,
        InputReportLength = candidates[0].GetMaxInputReportLength(),
        OutputReportLength = candidates[0].GetMaxOutputReportLength()
    });

    Drain(stream);
    var initialPixel = Exchange(stream, "pixel-query-initial", HidPacketBuilder.BuildPixelScreenStateQuery(), 0xee, 900, Log);
    var initialPixelPayload = LastPayload(initialPixel, 0xee);
    if (initialPixelPayload is { Length: >= 4 })
    {
        fallbackRed = initialPixelPayload[1];
        fallbackGreen = initialPixelPayload[2];
        fallbackBlue = initialPixelPayload[3];
    }

    var initialAmbient = Exchange(stream, "ambient-query-initial", HidPacketBuilder.BuildAmbientLightStateQuery(), 0x6a, 900, Log);
    Log("initial-state", new
    {
        PixelPayload = Hex(initialPixelPayload),
        AmbientPayload = Hex(LastPayload(initialAmbient, 0x6a)),
        PixelColor = $"#{fallbackRed:X2}{fallbackGreen:X2}{fallbackBlue:X2}"
    });

    Exchange(stream, "pixel-off-write", BuildPixelPower(false, fallbackRed, fallbackGreen, fallbackBlue), 0xef, 700, Log);
    var pixelOffState = Exchange(stream, "pixel-off-query", HidPacketBuilder.BuildPixelScreenStateQuery(), 0xee, 900, Log);
    var pixelOffPayload = LastPayload(pixelOffState, 0xee);
    pixelOffVerified = IsPixelOff(pixelOffPayload);
    Log("assert", new { Name = "pixel-off-query", Passed = pixelOffVerified, Payload = Hex(pixelOffPayload) });
    Thread.Sleep(offMilliseconds);

    Exchange(stream, "pixel-on-write", BuildPixelPower(true, fallbackRed, fallbackGreen, fallbackBlue), 0xef, 700, Log);
    pixelRestored = true;
    var pixelOnState = Exchange(stream, "pixel-on-query", HidPacketBuilder.BuildPixelScreenStateQuery(), 0xee, 900, Log);
    var pixelOnPayload = LastPayload(pixelOnState, 0xee);
    pixelOnVerified = IsPixelOn(pixelOnPayload);
    Log("assert", new { Name = "pixel-on-query", Passed = pixelOnVerified, Payload = Hex(pixelOnPayload) });

    Exchange(stream, "ambient-off-write", BuildAmbientPower(false), 0x6b, 700, Log);
    var ambientOffState = Exchange(stream, "ambient-off-query", HidPacketBuilder.BuildAmbientLightStateQuery(), 0x6a, 900, Log);
    var ambientOffPayload = LastPayload(ambientOffState, 0x6a);
    ambientOffVerified = IsAmbientPower(ambientOffPayload, false);
    Log("assert", new { Name = "ambient-off-query", Passed = ambientOffVerified, Payload = Hex(ambientOffPayload) });
    Thread.Sleep(offMilliseconds);

    Exchange(stream, "ambient-on-write", BuildAmbientPower(true), 0x6b, 700, Log);
    ambientRestored = true;
    var ambientOnState = Exchange(stream, "ambient-on-query", HidPacketBuilder.BuildAmbientLightStateQuery(), 0x6a, 900, Log);
    var ambientOnPayload = LastPayload(ambientOnState, 0x6a);
    ambientOnVerified = IsAmbientPower(ambientOnPayload, true);
    Log("assert", new { Name = "ambient-on-query", Passed = ambientOnVerified, Payload = Hex(ambientOnPayload) });
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
{
    Log("exception", new { Type = exception.GetType().FullName, exception.Message });
}
finally
{
    if (!pixelRestored || !ambientRestored)
    {
        try
        {
            using var recoveryStream = candidates[0].Open();
            recoveryStream.ReadTimeout = 150;
            if (!pixelRestored)
            {
                recoveryStream.Write(BuildPixelPower(true, fallbackRed, fallbackGreen, fallbackBlue));
                Log("recovery", new { Target = "pixel", Enabled = true });
            }
            if (!ambientRestored)
            {
                recoveryStream.Write(BuildAmbientPower(true));
                Log("recovery", new { Target = "ambient", Enabled = true });
            }
        }
        catch (Exception exception)
        {
            Log("recovery-exception", new { Type = exception.GetType().FullName, exception.Message });
        }
    }
}

var success = pixelOffVerified && pixelOnVerified && ambientOffVerified && ambientOnVerified;
Log("result", new
{
    Success = success,
    PixelOffVerified = pixelOffVerified,
    PixelOnVerified = pixelOnVerified,
    AmbientOffVerified = ambientOffVerified,
    AmbientOnVerified = ambientOnVerified,
    LogPath = Path.GetFullPath(logPath)
});
return success ? 0 : 3;

static List<Frame> Exchange(
    HidStream stream,
    string step,
    byte[] packet,
    byte expectedCommand,
    int readWindowMilliseconds,
    Action<string, object> log)
{
    log("out", new { Step = step, Command = $"0x{packet[3]:X2}", Frame = Hex(packet), ChecksumValid = HasValidChecksum(packet) });
    stream.Write(packet);
    var frames = new List<Frame>();
    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.ElapsedMilliseconds < readWindowMilliseconds)
    {
        var frame = ReadFrame(stream);
        if (frame is null)
            continue;
        frames.Add(frame);
        log("in", new
        {
            Step = step,
            Command = $"0x{frame.Command:X2}",
            Payload = Hex(frame.Payload),
            Frame = Hex(frame.Raw),
            frame.ChecksumValid
        });
        if (frame.Command == expectedCommand)
            break;
    }
    if (frames.All(frame => frame.Command != expectedCommand))
        log("missing-response", new { Step = step, ExpectedCommand = $"0x{expectedCommand:X2}" });
    return frames;
}

static Frame? ReadFrame(HidStream stream)
{
    var buffer = new byte[ReportLength];
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

    for (var start = 0; start <= read - 7; start++)
    {
        if (buffer[start] != 0x2f || buffer[start + 1] != 0xbb || buffer[start + 2] != 0xec)
            continue;
        var payloadLength = (buffer[start + 4] << 8) | buffer[start + 5];
        var checksumOffset = start + 6 + payloadLength;
        if (payloadLength > ReportLength - 7 || checksumOffset >= read)
            continue;
        var raw = buffer.Skip(start).Take(Math.Min(ReportLength, read - start)).ToArray();
        var payload = buffer.Skip(start + 6).Take(payloadLength).ToArray();
        return new Frame(buffer[start + 3], payload, raw, HasValidChecksum(raw));
    }
    return null;
}

static void Drain(HidStream stream)
{
    for (var index = 0; index < 3; index++)
    {
        if (ReadFrame(stream) is null)
            return;
    }
}

static byte[]? LastPayload(IEnumerable<Frame> frames, byte command)
    => frames.LastOrDefault(frame => frame.Command == command)?.Payload;

static bool IsPixelOff(byte[]? payload)
    => payload is { Length: >= 8 } && payload[5] == 0x03 && payload[7] == 0x00;

static bool IsPixelOn(byte[]? payload)
    => payload is { Length: >= 8 } && !(payload[5] == 0x03 && payload[7] == 0x00);

static bool IsAmbientPower(byte[]? payload, bool expected)
    => payload is { Length: >= 6 }
       && payload[^6] == 0x07
       && payload[^3] == (expected ? (byte)0x01 : (byte)0x00)
       && payload[^2] == 0xff
       && payload[^1] == 0xff;

static bool HasValidChecksum(byte[] frame)
{
    if (frame.Length < 7)
        return false;
    var payloadLength = (frame[4] << 8) | frame[5];
    var checksumOffset = 6 + payloadLength;
    if (checksumOffset >= frame.Length)
        return false;
    var sum = 0;
    for (var index = 1; index < checksumOffset; index++)
        sum += frame[index];
    return (byte)sum == frame[checksumOffset];
}

static string Hex(byte[]? bytes) => bytes is null ? string.Empty : BitConverter.ToString(bytes).Replace('-', ' ');

static string HexPrefix(byte[] packet)
{
    var payloadLength = (packet[4] << 8) | packet[5];
    return Hex(packet[..(7 + payloadLength)]);
}

static string? ReadStringArgument(string[] values, string name)
{
    var index = Array.FindIndex(values, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static int ReadIntArgument(string[] values, string name, int fallback, int minimum, int maximum)
{
    var text = ReadStringArgument(values, name);
    return int.TryParse(text, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;
}

internal sealed record Frame(byte Command, byte[] Payload, byte[] Raw, bool ChecksumValid);

internal static class ProbeJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
}
