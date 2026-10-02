using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Services;

internal sealed class Fixture : IAsyncDisposable
{
    private readonly List<MockHost> hosts = [];
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "halo-dsh-session-test-" + Guid.NewGuid().ToString("N"));
    public string Home { get; set; }
    public string Profile { get; set; } = "halo-pixelbar";
    public string Discovery => Path.Combine(Root, "discovery");
    public DshSessionsService Client { get; }
    public MockHost Original { get; }

    public Fixture()
    {
        Home = Path.Combine(Root, "home");
        Directory.CreateDirectory(Home);
        DisplayFeatureProfile.DshVoiceTargetSessionId = string.Empty;
        DisplayFeatureProfile.DshVoiceTargetHomePath = string.Empty;
        DisplayFeatureProfile.DshDeviceSessionId = string.Empty;
        DisplayFeatureProfile.DshDeviceSessionHomePath = string.Empty;
        DisplayFeatureProfile.DshDeviceSessionProfileName = string.Empty;
        Original = NewHost();
        WriteDescriptor(Original);
        Client = new(() => new(string.Empty, Home, Profile), Discovery);
    }

    public MockHost NewHost()
    {
        var host = new MockHost(Home);
        hosts.Add(host);
        return host;
    }

    public void WriteDescriptor(MockHost host, string? profile = null, string? home = null)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Home.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()))).ToLowerInvariant();
        var directory = Path.Combine(Discovery, key);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "test-host.json"), JsonSerializer.Serialize(new
        {
            protocolVersion = 1, home = home ?? Home, profile = profile ?? Profile,
            pid = Environment.ProcessId, baseUrl = host.BaseUri.ToString(), token = host.Token
        }));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        foreach (var host in hosts) await host.DisposeAsync();
    }
}

internal sealed class MockHost : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stopped = new();
    private readonly ConcurrentBag<Task> requests = [];
    private readonly Task pump;
    public Uri BaseUri { get; }
    public string Home { get; }
    // Synthetic bearer used only by this loopback fixture.
    public string Token { get; set; } = "test_only_" + Guid.NewGuid().ToString("N") + new string('a', 20);
    public bool Ready { get; set; } = true;
    public int ListStatus { get; set; } = 200;
    public string SessionId { get; set; } = "fixture-session";
    public ConcurrentQueue<(string Method, string Path)> Calls { get; } = new();
    public TaskCompletionSource CommandReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CommandReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HoldCommand { get; set; }
    public int UnauthorizedCount;

    public MockHost(string home)
    {
        Home = home;
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        BaseUri = new($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(BaseUri.ToString());
        listener.Start();
        pump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!stopped.IsCancellationRequested)
                requests.Add(HandleAsync(await listener.GetContextAsync().WaitAsync(stopped.Token)));
        }
        catch (Exception exception) when (stopped.IsCancellationRequested
            && exception is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            Calls.Enqueue((context.Request.HttpMethod, path));
            if (context.Request.Headers["Authorization"] != "Bearer " + Token)
            {
                Interlocked.Increment(ref UnauthorizedCount);
                await ReplyAsync(context, new { code = "unauthorized" }, 401);
                return;
            }
            if (path == "/v1/status")
                await ReplyAsync(context, new
                {
                    protocolVersion = 1, ready = Ready, home = Home, pid = Environment.ProcessId,
                    capabilities = new { prompt = true, deviceHistoryGrouping = true },
                    deviceAgentPreset = "halo-device", deviceSessionId = string.Empty
                });
            else if (path == "/v1/sessions")
                await ReplyAsync(context, new
                {
                    home = Home, sessions = new[] { new
                    {
                        id = SessionId, title = "fixture", workingDirectory = Home,
                        updatedAt = "2026-10-01T08:00:00+08:00", runtimeStatus = "idle"
                    } }
                }, ListStatus);
            else if (path == "/v1/device-command")
            {
                using var document = await JsonDocument.ParseAsync(context.Request.InputStream,
                    cancellationToken: stopped.Token);
                CommandReceived.TrySetResult();
                if (HoldCommand) await CommandReply.Task.WaitAsync(stopped.Token);
                var command = document.RootElement;
                await ReplyAsync(context, new
                {
                    success = true, accepted = true, completed = true,
                    message = "fixture completed", finalText = "mock only",
                    sessionId = command.GetProperty("sessionId").GetString(),
                    requestId = command.GetProperty("requestId").GetString(),
                    calledTools = new[] { "configure_pixelbar" }, successfulTools = new[] { "configure_pixelbar" }
                });
            }
            else await ReplyAsync(context, new { code = "fixture_unexpected_path" }, 404);
        }
        catch (Exception exception) when (stopped.IsCancellationRequested
            && exception is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException) { }
    }

    private static async Task ReplyAsync(HttpListenerContext context, object value, int status = 200)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        stopped.Cancel();
        listener.Close();
        await pump;
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(3));
        stopped.Dispose();
    }
}
