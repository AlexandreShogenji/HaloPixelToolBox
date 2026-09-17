using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HaloPixelToolBox.Core.Services;
using HaloPixelToolBox.Core.Utilities;
using HidSharp;

const int ReportLength = 64;
const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss";

var execute = args.Contains("--execute", StringComparer.OrdinalIgnoreCase);
var restoreNow = args.Contains("--restore-now", StringComparer.OrdinalIgnoreCase);
var productionService = args.Contains("--production-service", StringComparer.OrdinalIgnoreCase);
var requestedText = ReadStringArgument(args, "--time");
var logPath = ReadStringArgument(args, "--log")
    ?? Path.Combine(Environment.CurrentDirectory, $"time-probe-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

DateTime requestedTime;
if (restoreNow)
{
    requestedTime = DateTime.Now;
}
else if (!DateTime.TryParseExact(
             requestedText,
             TimeFormat,
             CultureInfo.InvariantCulture,
             DateTimeStyles.None,
             out requestedTime))
{
    Console.Error.WriteLine($"Use --time {TimeFormat} or --restore-now.");
    return 1;
}

var packet = BuildTimePacket(requestedTime);
var preview = new
{
    Time = requestedTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
    UnknownU = packet[13],
    DayPeriodV = packet[14],
    Packet = HexPrefix(packet)
};

if (!execute)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Mode = "dry-run",
        Message = "Pass --execute to send this single known 0x77 packet.",
        Value = preview
    }));
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
using var logger = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
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
    });
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

if (productionService)
{
    if (restoreNow)
    {
        requestedTime = DateTime.Now;
        packet = BuildTimePacket(requestedTime);
        preview = new
        {
            Time = requestedTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            UnknownU = packet[13],
            DayPeriodV = packet[14],
            Packet = HexPrefix(packet)
        };
    }

    var service = new HaloPixelDisplayService();
    var success = await service.CalibrateDeviceTimeAsync(requestedTime);
    Log("result", new
    {
        Success = success,
        Mode = "production-service",
        RequestedTime = preview.Time,
        LogPath = Path.GetFullPath(logPath)
    });
    return success ? 0 : 3;
}

try
{
    using var stream = candidates[0].Open();
    stream.ReadTimeout = 150;
    Drain(stream);

    if (restoreNow)
    {
        requestedTime = DateTime.Now;
        packet = BuildTimePacket(requestedTime);
        preview = new
        {
            Time = requestedTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            UnknownU = packet[13],
            DayPeriodV = packet[14],
            Packet = HexPrefix(packet)
        };
    }

    Log("out", new
    {
        RequestedTime = preview.Time,
        preview.UnknownU,
        preview.DayPeriodV,
        Frame = Hex(packet),
        ChecksumValid = HasValidChecksum(packet)
    });
    stream.Write(packet);

    var response = WaitForResponse(stream, 0x77, 1200, Log);
    var accepted = response is { ChecksumValid: true, Payload.Length: 1 } && response.Payload[0] == 0x01;
    Log("result", new
    {
        Success = accepted,
        RequestedTime = preview.Time,
        AckPayload = response is null ? string.Empty : Hex(response.Payload),
        ResponseChecksumValid = response?.ChecksumValid ?? false,
        LogPath = Path.GetFullPath(logPath)
    });
    return accepted ? 0 : 3;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
{
    Log("exception", new { Type = exception.GetType().FullName, exception.Message });
    Log("result", new { Success = false, RequestedTime = preview.Time, Reason = "device-io-error" });
    return 4;
}

static byte[] BuildTimePacket(DateTime value)
    => HidPacketBuilder.BuildDeviceTime(value);

static Frame? WaitForResponse(
    HidStream stream,
    byte expectedCommand,
    int timeoutMilliseconds,
    Action<string, object> log)
{
    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
    {
        var frame = ReadFrame(stream);
        if (frame is null)
            continue;

        log("in", new
        {
            Command = $"0x{frame.Command:X2}",
            Payload = Hex(frame.Payload),
            Frame = Hex(frame.Raw),
            frame.ChecksumValid
        });
        if (frame.Command == expectedCommand
            && frame.ChecksumValid
            && frame.Payload is [0x01])
            return frame;
    }
    return null;
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

static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

static string HexPrefix(byte[] bytes)
{
    var payloadLength = (bytes[4] << 8) | bytes[5];
    return Hex(bytes[..(7 + payloadLength)]);
}

static string? ReadStringArgument(string[] values, string name)
{
    var index = Array.FindIndex(values, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

internal sealed record Frame(byte Command, byte[] Payload, byte[] Raw, bool ChecksumValid);
