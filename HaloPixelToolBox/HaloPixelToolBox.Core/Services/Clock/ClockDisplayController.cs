using HaloPixelToolBox.Core.Models;
using HaloPixelToolBox.Core.Models.Display;
using HaloPixelToolBox.Core.Services;

namespace HaloPixelToolBox.Core.Services.Clock;

public class ClockDisplayController
{
    private readonly HaloPixelDisplayService displayService;
    private CancellationTokenSource? cancellationTokenSource;

    public bool IsRunning => cancellationTokenSource is not null;

    public ClockDisplayController(HaloPixelDisplayService displayService)
    {
        this.displayService = displayService;
    }

    public async Task SendOnceAsync(ClockConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (configuration.Template.UseBuiltInClockUi)
        {
            displayService.ShowBuiltInUi(HaloPixelUIModel.Clock);
            return;
        }

        await displayService.SendTextAsync(new DisplayTextOptions
        {
            Text = ClockRenderer.Render(DateTimeOffset.Now, configuration),
            Layout = HaloPixelTextLayout.Center
        }, cancellationToken);
    }

    public void Start(ClockConfiguration configuration)
    {
        Stop();
        cancellationTokenSource = new CancellationTokenSource();
        _ = RunAsync(configuration, cancellationTokenSource.Token);
    }

    public void Stop()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource = null;
    }

    private async Task RunAsync(ClockConfiguration configuration, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await SendOnceAsync(configuration, cancellationToken);
            await Task.Delay(Math.Max(200, configuration.RefreshIntervalMilliseconds), cancellationToken);
        }
    }
}
