namespace HaloPixelToolBox.Core.Services.DeviceControl;

/// <summary>
/// Serializes every HID transaction in the process. The PixelBar exposes several
/// collections, but concurrent reads and writes can still consume each other's replies.
/// </summary>
public static class HaloPixelDeviceOperationQueue
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static T Run<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Gate.Wait();
        try
        {
            return operation();
        }
        finally
        {
            Gate.Release();
        }
    }
}
