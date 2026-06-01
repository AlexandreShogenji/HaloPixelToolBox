namespace HaloPixelToolBox.Core.Models.Display;

/// <summary>
/// 时钟图案模板定义。官方软件资源可导入为 json，并在 ResourcePath 指向实际图案文件。
/// </summary>
public class ClockTemplateDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "默认时钟";
    public string? ResourcePath { get; set; }
    public string TimeFormat { get; set; } = "HH:mm";
    public string DateFormat { get; set; } = "MM-dd";
    public int X { get; set; }
    public int Y { get; set; }
    public bool UseBuiltInClockUi { get; set; }

    public override string ToString() => Name;
}
