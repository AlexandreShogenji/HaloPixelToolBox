using HaloPixelToolBox.Models;
using HaloPixelToolBox.Services.Audio;

// This probe never calls the native setter for a valid device. It exercises the real
// MMDevice interfaces, cancellation and invalid-device guards without rerouting audio.
var service = new WindowsAudioEndpointService();
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}
string Defaults(IReadOnlyList<AudioEndpointInfo> endpoints) => string.Join("|", endpoints
    .Where(endpoint => endpoint.IsDefault || endpoint.IsDefaultCommunications)
    .OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal)
    .Select(endpoint => $"{endpoint.Id}:{endpoint.IsDefault}:{endpoint.IsDefaultCommunications}"));

var initial = await service.GetEndpointsAsync();
Check(initial.All(endpoint => !string.IsNullOrWhiteSpace(endpoint.Id)), "Empty endpoint ID");
Check(initial.All(endpoint => !string.IsNullOrWhiteSpace(endpoint.Name)), "Empty friendly name");
Check(initial.Select(endpoint => endpoint.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == initial.Count, "Duplicate endpoint ID");
Check(initial.Count(endpoint => endpoint.IsDefault) <= 1, "Multiple multimedia defaults");
Check(initial.Count(endpoint => endpoint.IsDefaultCommunications) <= 1, "Multiple communications defaults");

var empty = await service.SetDefaultEndpointAsync(" ");
Check(!empty.Success && empty.FailedRoles.Count == 0 && empty.Message.Length > 0, "Missing selection must be rejected");
var invalid = await service.SetDefaultEndpointAsync("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}");
Check(!invalid.Success && invalid.FailedRoles.Count == 0 && invalid.Message.Contains("不可用"), "Unknown device must be rejected before any output change");
Check(Defaults(await service.GetEndpointsAsync()) == Defaults(initial), "Invalid selection changed defaults");

using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await service.GetEndpointsAsync(cancellation.Token);
        throw new InvalidOperationException("Enumeration ignored cancellation");
    }
    catch (OperationCanceledException) { passed++; }
    try
    {
        await service.SetDefaultEndpointAsync(initial.FirstOrDefault()?.Id ?? "cancelled", cancellation.Token);
        throw new InvalidOperationException("Output change ignored pre-cancellation");
    }
    catch (OperationCanceledException) { passed++; }
}

for (var index = 0; index < 10; index++)
{
    var current = await service.GetEndpointsAsync();
    Check(Defaults(current) == Defaults(initial), "Read-only refresh changed Windows defaults");
}
Console.WriteLine($"{passed} read-only endpoint checks passed; {initial.Count} active playback endpoints.");
foreach (var endpoint in initial)
    Console.WriteLine($"{endpoint.Name} | mediaDefault={endpoint.IsDefault}, communicationsDefault={endpoint.IsDefaultCommunications}");
