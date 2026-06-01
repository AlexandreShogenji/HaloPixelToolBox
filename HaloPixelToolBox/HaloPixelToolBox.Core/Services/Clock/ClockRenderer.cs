using HaloPixelToolBox.Core.Models.Display;

namespace HaloPixelToolBox.Core.Services.Clock;

public static class ClockRenderer
{
    public static string Render(DateTimeOffset now, ClockConfiguration configuration)
    {
        var template = configuration.Template;
        var timeText = now.ToString(string.IsNullOrWhiteSpace(template.TimeFormat) ? "HH:mm" : template.TimeFormat);
        if (!configuration.ShowDate)
            return timeText;

        var dateText = now.ToString(string.IsNullOrWhiteSpace(template.DateFormat) ? "MM-dd" : template.DateFormat);
        return $"{dateText} {timeText}";
    }
}
