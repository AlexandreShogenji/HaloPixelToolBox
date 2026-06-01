using HaloPixelToolBox.Core.Models.Display;
using System.Text.Json;

namespace HaloPixelToolBox.Core.Services.Clock;

/// <summary>
/// 时钟资源加载器。官方软件图案导出后可放到 templates 目录，通过 json 描述文件接入。
/// </summary>
public class ClockTemplateResourceLoader
{
    public IReadOnlyList<ClockTemplateDefinition> BuiltInTemplates { get; } =
    [
        new() { Id = "builtin-clock-ui", Name = "设备内置时钟", UseBuiltInClockUi = true },
        new() { Id = "digital-hhmm", Name = "数字时钟 HH:mm", TimeFormat = "HH:mm" },
        new() { Id = "digital-hhmmss", Name = "数字时钟 HH:mm:ss", TimeFormat = "HH:mm:ss" },
        new() { Id = "date-time", Name = "日期 + 时间", TimeFormat = "HH:mm", DateFormat = "MM-dd" }
    ];

    public IReadOnlyList<ClockTemplateDefinition> LoadTemplates(string resourceDirectory)
    {
        var result = BuiltInTemplates.ToList();
        if (!Directory.Exists(resourceDirectory))
            return result;

        foreach (var file in Directory.EnumerateFiles(resourceDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(file);
                var template = JsonSerializer.Deserialize<ClockTemplateDefinition>(json);
                if (template is null)
                    continue;

                if (!string.IsNullOrWhiteSpace(template.ResourcePath) && !Path.IsPathRooted(template.ResourcePath))
                    template.ResourcePath = Path.Combine(resourceDirectory, template.ResourcePath);

                result.Add(template);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN]加载时钟模板失败：{file}，{ex.Message}");
            }
        }

        return result;
    }
}
