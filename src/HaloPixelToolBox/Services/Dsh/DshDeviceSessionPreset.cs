using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaloPixelToolBox.Services;

/// <summary>Build a temporary device persona for both official preset formats.</summary>
internal static class DshDeviceSessionPreset
{
    private const string Persona = "你是Halo PixelBar音箱助手。只使用PixelBar工具执行设备指令；多项设置优先configure_pixelbar。“默认场景”“恢复默认场景”“切回默认场景”指界面恢复默认场景操作，直接调用restore_pixelbar_default_scene；组合设置用restoreDefaultScene=true。它恢复最近使用的个性场景，无记录时回退默认时钟，不是目录里的场景名称，也不是氛围呼吸，不要猜分类或询问是哪种默认场景。氛围灯效是呼吸、潮汐等六种动画模式，用set_pixelbar_light_effect；“换个灯效”用next，明确随机用random。灯光预设是贺喜遥香等保存的配色，仅明确要求配色预设时使用。其他名称歧义先询问，不猜参数。均衡器、低音、高音、音效预设用独立音频工具configure_pixelbar_audio，先get_pixelbar_audio_status核对音效组件与预设；输出切换仅在明确要求时用set_pixelbar_audio_output。曲线保存不代表音效已生效。以工具实际结果为准，只用一句简短中文回复。";

    public static async Task<bool> UsesLegacyPresetsAsync(
        string executable, string home, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(DshIntegrationService.CreateDshStartInfo(
            executable, home, ["--version"], redirectStandardInput: false))
            ?? throw new InvalidOperationException("无法检测 DSH 版本。");
        DshOwnedProcessJob? job = null;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            try { if (!process.HasExited) job = DshOwnedProcessJob.Attach(process); }
            catch (Exception) when (process.HasExited) { }
            await process.WaitForExitAsync(timeout.Token);
            var version = Regex.Match(await output.WaitAsync(timeout.Token), @"(?<![0-9.])(?<major>\d+)\.(?<minor>\d+)\.\d+");
            await error.WaitAsync(timeout.Token);
            if (process.ExitCode != 0 || !version.Success)
                throw new InvalidOperationException("无法确认 DSH 版本，请在设置中检测 DSH 后重试。");
            var major = int.Parse(version.Groups["major"].Value);
            var minor = int.Parse(version.Groups["minor"].Value);
            if (major == 0 && minor == 1) return true;
            if (major > 0 || minor >= 2) return false;
            throw new InvalidOperationException("设备会话需要 DSH 0.1 或更新版本。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("DSH 版本检测超时，请在设置中检查启动程序与网络。");
        }
        finally
        {
            job?.Dispose();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
    }

    public static async Task<string> WritePatchAsync(
        string directory, string bridgePath, string home, string profile, string startupId,
        bool legacy, CancellationToken cancellationToken)
    {
        var persona = new
        {
            id = "halo-device-persona", name = "@deepseek-ai/dsh-persona",
            config = new { prefix = Persona, complete = true, includeRuntimeContext = false }
        };
        var bridge = new
        {
            id = "halo-toolbox-session-bridge", name = new Uri(bridgePath).AbsoluteUri,
            config = new { discoveryDirectory = directory, home, profile, ownedStartupId = startupId, deviceAgentPreset = "halo-device" }
        };
        object[] patch;
        if (legacy)
        {
            var root = Path.Combine(directory, "presets-v01");
            var preset = Path.Combine(root, "halo-device");
            Directory.CreateDirectory(preset);
            // JSON is valid YAML and preserves Windows paths and literal prompt text.
            await WriteJsonAsync(Path.Combine(preset, "preset.yml"),
                new { name = "音箱控制", description = "Halo PixelBar 专用设备会话。", order = 100 }, cancellationToken);
            await WriteJsonAsync(Path.Combine(preset, "agent.cordis.yml"), new[] { persona }, cancellationToken);
            patch =
            [
                new
                {
                    id = "agent-presets", name = "@deepseek-ai/dsh-agent-presets",
                    config = new
                    {
                        @default = "standard", roots = new[] { new { path = root, trust = "system" } },
                        includeShippedRoot = true, includeUserRoot = true
                    }
                },
                new { insert = new[] { bridge } }
            ];
        }
        else
        {
            patch =
            [
                new
                {
                    insert = new object[]
                    {
                        new
                        {
                            id = "halo-device-preset", name = "@deepseek-ai/dsh-agent-preset",
                            config = new { id = "halo-device", order = 100, plugins = new[] { persona } }
                        },
                        bridge
                    }
                }
            ];
        }
        var path = Path.Combine(directory, "toolbox-session-bridge.patch.json");
        await WriteJsonAsync(path, patch, cancellationToken);
        return path;
    }

    private static Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken)
        => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value), new UTF8Encoding(false), cancellationToken);
}
