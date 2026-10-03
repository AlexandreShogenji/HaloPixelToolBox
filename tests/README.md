# 回归测试

测试源码统一放在 `tests/`，直接链接 `src/` 中的真实业务代码。DSH 插件和会话桥接的运行时代码仍在 `integrations/deepseek-harness/`。测试不会启动真实模型任务、采集麦克风、连接音箱或改动个人配置；HTTP 和命名管道检查仅使用临时的假服务。

在 Windows 仓库根目录运行完整检查：

```powershell
pwsh -File eng/Test.ps1
```

也可使用 Windows PowerShell 5.1 的 `powershell -File eng/Test.ps1`。脚本通过自身位置解析仓库根目录，因此可以从其他工作目录调用绝对路径。任一步骤失败都会返回非零退出码。

| 入口 | 内容 | 环境 |
| --- | --- | --- |
| `-Suite Dsh` | 会话服务、语音任务路由、口述选项解析与字幕分页、聊天 ViewModel、页面绑定、任务交互、监听入口、JS 桥接、工具协议与包内容 | .NET 8 SDK、Node.js 20+、npm、插件依赖 |
| `-Suite Audio` | 已有音频文件、配置、端点契约、设备命令和 ViewModel 回归 | .NET 8 SDK |
| `-Suite Voice` | 唤醒匹配、句尾检测、缓存边界、动态提示语音与有界缓存、取消、版本失效和提示后续听 | Python、numpy |
| `-Suite All`（默认） | 全部以上测试 | 全部以上环境 |

脚本不自动安装依赖。首次运行 DSH 检查前，使用插件锁文件安装依赖：

```powershell
pnpm --dir integrations/deepseek-harness/halo-pixelbar-tools install --frozen-lockfile
pwsh -File eng/Test.ps1 -Suite Dsh
pwsh -File eng/Test.ps1 -Suite Voice -PythonPath C:/path/to/python.exe
```

语音测试仅需要 numpy，不要求下载 ASR 模型、安装 FunASR 或接入音频硬件。可用 `-NodePath` 指定 Node.js 可执行文件，`-Configuration Debug` 改变 .NET 测试配置；已完成对应配置还原时，可加 `-NoRestore`。

DSH UI 检查使用假 WinUI 控件，XAML 由项目复制到测试输出的 `Fixtures/`，不会依赖本机绝对路径或输出目录深度。业务回归不能替代实际 WinUI 的视觉检查、语音识别准确率测试或真实音效听感验证。音效方向当前暂缓，这里的检查只保护现有行为。

各组的独立执行方式和具体覆盖范围见对应目录 README。`integrations/deepseek-harness/halo-pixelbar-tools/scripts/pipe-smoke-test.mjs` 是面向已运行工具箱的人工诊断脚本，不在离线测试入口中执行。
