using System.Reflection;
using HaloPixelToolBox.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Disconnected refresh discovers a same-PID replacement port", ReplacementPortAsync),
    ("Disconnected refresh discovers a same-port replacement token", ReplacementTokenAsync),
    ("Transient list failure retains the original endpoint for an in-flight command", SameEndpointCommandAsync),
    ("Replacement endpoint rejects the old command reply without replaying", ReplacedEndpointCommandAsync),
    ("Disconnected discovery rejects a foreign Profile", ForeignProfileAsync),
    ("Refresh with no live descriptor remains disconnected without starting DSH", MissingHostAsync)
};
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + exception.Message); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} session service tests passed");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

static object Endpoint(DshSessionsService service)
    => typeof(DshSessionsService).GetField("endpoint", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(service) ?? throw new InvalidOperationException("Fixture expected a retained descriptor.");

static void OnlyReads(params MockHost[] hosts)
    => Check(hosts.All(host => host.Calls.All(call => call.Method == "GET"
        && call.Path is "/v1/status" or "/v1/sessions")), "Refresh sent a task/device mutation.");

static async Task DisconnectByListAsync(Fixture fixture)
{
    fixture.Original.ListStatus = 503;
    await fixture.Client.RefreshAsync();
    Check(!fixture.Client.Current.IsConnected, "Fixture failed to create a transient list disconnect.");
    _ = Endpoint(fixture.Client);
}

static async Task ReplacementPortAsync()
{
    await using var fixture = new Fixture();
    await fixture.Client.RefreshAsync();
    Check(fixture.Client.Current.IsConnected, "Initial fixture connection failed.");
    await DisconnectByListAsync(fixture);
    var replacement = fixture.NewHost();
    replacement.SessionId = "replacement-session";
    fixture.WriteDescriptor(replacement);
    await fixture.Client.RefreshAsync();
    Check(fixture.Client.Current.IsConnected && fixture.Client.Current.HostUrl == replacement.BaseUri.ToString(),
        "Refresh kept the stale port although the same PID has a healthy replacement descriptor.");
    Check(fixture.Client.Current.Sessions.Single().Id == replacement.SessionId, "Replacement history was not loaded.");
    OnlyReads(fixture.Original, replacement);
}

static async Task ReplacementTokenAsync()
{
    await using var fixture = new Fixture();
    await fixture.Client.RefreshAsync();
    var originalEndpoint = Endpoint(fixture.Client);
    fixture.Original.Token = "test_rotated_" + new string('b', 48);
    await fixture.Client.RefreshAsync();
    Check(!fixture.Client.Current.IsConnected, "Old fixture bearer should have been rejected.");
    fixture.WriteDescriptor(fixture.Original);
    await fixture.Client.RefreshAsync();
    Check(fixture.Client.Current.IsConnected, "Same-PID/token rotation did not recover.");
    Check(!ReferenceEquals(originalEndpoint, Endpoint(fixture.Client)), "Changed bearer reused the old endpoint identity.");
    Check(fixture.Original.UnauthorizedCount == 1, "Refresh continued sending the stale bearer after rediscovery.");
    OnlyReads(fixture.Original);
}

static async Task SameEndpointCommandAsync()
{
    await using var fixture = new Fixture();
    await fixture.Client.RefreshAsync();
    var originalEndpoint = Endpoint(fixture.Client);
    fixture.Original.HoldCommand = true;
    var command = fixture.Client.ExecuteDeviceCommandAsync("fixture only");
    await fixture.Original.CommandReceived.Task;
    await DisconnectByListAsync(fixture);
    fixture.Original.ListStatus = 200;
    await fixture.Client.RefreshAsync();
    Check(fixture.Client.Current.IsConnected, "Transient list disconnect did not recover.");
    Check(ReferenceEquals(originalEndpoint, Endpoint(fixture.Client)), "Identical discovered descriptor changed the in-flight endpoint identity.");
    fixture.Original.CommandReply.TrySetResult();
    Check((await command).Success, "A confirmed command was invalidated by a transient list disconnect.");
    Check(fixture.Original.Calls.Count(call => call.Path == "/v1/device-command") == 1, "Command was replayed.");
}

static async Task ReplacedEndpointCommandAsync()
{
    await using var fixture = new Fixture();
    await fixture.Client.RefreshAsync();
    fixture.Original.HoldCommand = true;
    var command = fixture.Client.ExecuteDeviceCommandAsync("fixture only");
    await fixture.Original.CommandReceived.Task;
    await DisconnectByListAsync(fixture);
    var replacement = fixture.NewHost();
    fixture.WriteDescriptor(replacement);
    await fixture.Client.RefreshAsync();
    Check(fixture.Client.Current.HostUrl == replacement.BaseUri.ToString(), "Replacement fixture did not become active.");
    fixture.Original.CommandReply.TrySetResult();
    try { await command; throw new InvalidOperationException("Old endpoint reply was accepted after replacement."); }
    catch (OperationCanceledException) { }
    Check(fixture.Original.Calls.Count(call => call.Path == "/v1/device-command") == 1, "Old command was replayed.");
    OnlyReads(replacement);
}

static async Task ForeignProfileAsync()
{
    await using var fixture = new Fixture();
    await fixture.Client.RefreshAsync();
    await DisconnectByListAsync(fixture);
    var replacement = fixture.NewHost();
    fixture.WriteDescriptor(replacement, profile: "foreign-profile");
    await fixture.Client.RefreshAsync();
    Check(!fixture.Client.Current.IsConnected, "Foreign Profile was adopted on disconnect.");
    Check(replacement.Calls.IsEmpty, "Client sent a request to a descriptor from another Profile.");
    OnlyReads(fixture.Original);
}

static async Task MissingHostAsync()
{
    await using var fixture = new Fixture();
    await fixture.Client.RefreshAsync();
    await DisconnectByListAsync(fixture);
    fixture.Original.Ready = false;
    await fixture.Client.RefreshAsync();
    Check(!fixture.Client.Current.IsConnected, "Unready host was recovered.");
    Check(!fixture.Client.Current.Message.Contains("启动程序", StringComparison.Ordinal), "Refresh tried the host-start path.");
    OnlyReads(fixture.Original);
}
