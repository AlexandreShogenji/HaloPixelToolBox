# HaloPixelToolBox（花再字幕屏工具箱）

面向花再 / HaloPixel 字幕屏与音箱像素屏的 Windows 桌面工具箱，用于歌词同步、视频字幕同步、浏览器翻译字幕、自定义字幕、灯光控制和个性场景切换。

当前版本：`v2.1.0`

## 主要功能

- 歌词字幕：支持网易云音乐桌面歌词、本地 LRC、Spotify 当前播放同步。
- Spotify 歌词：根据当前播放歌曲自动填充歌名/歌手，多源并发检索 LRCLIB、网易云、酷狗歌词，线上未命中时可手动选择本地 LRC 兜底。
- 同步行为：支持 Spotify 播放、暂停、拖动进度和连续切歌；开启同步后可自动重新加载新歌歌词。
- 字幕输出：超过字幕屏字符限制时会自动分段发送，尽量在两句歌词时间之间完成多段显示。
- 个性场景：关闭歌词同步时恢复当前默认个性场景；退出应用时先隐藏窗口，再在后台恢复场景，避免退出时卡住界面。
- 字幕音箱：歌词字幕模块提供 0-16 的设备音量调节。
- 视频字幕：支持 PotPlayer 字幕同步，并在同步结束后恢复当前默认个性场景。
- 浏览器翻译字幕：支持浏览器字幕捕获、浏览器播放进度同步、腾讯翻译 API 配置和字幕输出。
- B 站音乐模式：在浏览器翻译字幕模块中识别当前 B 站音乐视频，优先读取页面音乐信息并加载同步歌词；可手动修正歌名/歌手后发送查询，支持 50ms 级歌词同步偏移。
- B 站 ASR 兜底：同步歌词或 B 站字幕不可用时可回退到 ASR；Whisper 模型使用独立模型目录缓存，支持 `HF_TOKEN` / `HF_ENDPOINT` 配置，并过滤常见静音幻觉字幕。
- 自定义字幕：支持固定、左滚、右滚等字幕输出模式，并限制字幕内容长度。

## 项目架构

仓库采用“桌面入口 + 核心服务 + 安装器 + 自解压包”的结构：

```text
HaloPixelToolBox/
  HaloPixelToolBox/                 WinUI 3 桌面主程序，承载页面、ViewModel、托盘、更新入口
  HaloPixelToolBox.Core/            设备通信、字幕解析、歌词源、翻译、浏览器字幕、灯光、场景等领域能力
  HaloPixelToolBox.Test/            面向硬件和核心服务的测试/验证控制台
HaloPixelToolBox.Installer/         WPF 安装器，内嵌主程序 Source.zip 并负责安装/升级
HaloPixelToolBox.Installer.Package/ 自解压启动器，内嵌安装器 Source.zip 并以管理员权限启动 Installer.exe
scripts/Build-Release.ps1           发布脚本，自动串起主程序、安装器和自解压安装包
Directory.Build.props               仓库级版本号、程序集版本和 Roslyn 4.12 编译器工具集
global.json                         固定 .NET 8 SDK，避免本机默认 SDK 差异影响 WinUI/WPF 构建
```

核心分层现状：

- UI 层：`Views` 与 `ViewModels` 负责界面状态、用户交互、页面导航和少量编排逻辑。
- 核心层：`HaloPixelToolBox.Core` 放置可复用业务能力，主要按 `Services`、`Models`、`Utilities` 组织。
- 配置层：`Profiles/CrossVersionProfiles` 和 `Profiles/CacheProfiles` 管理跨版本配置与缓存配置。
- 发布层：安装器项目通过嵌入 `Source.zip` 交付主程序，自解压包再嵌入安装器发布目录，形成单文件安装入口。

## 架构复盘结论

当前架构的主方向是合理的：硬件通信、字幕解析、歌词源、翻译和场景恢复已经从 WinUI 项目中抽到 `HaloPixelToolBox.Core`，主程序更多承担 UI 与流程编排，安装器也独立成项目，适合桌面工具箱的发布形态。

这次调整没有做大规模目录搬迁。原因是浏览器字幕和 ASR 链路较长，直接拆分大型 ViewModel 会影响面较大；在发布 2.0.0 前，更合理的是先把版本、SDK 和打包流程稳定下来。本次已完成的结构调整：

- 使用 `global.json` 固定 .NET 8 SDK，降低本机默认 SDK 变化导致的构建风险。
- 使用 `Directory.Build.props` 统一 `Version`、`AssemblyVersion`、`FileVersion`、`InformationalVersion`，当前统一为 `2.1.0`。
- 在仓库级引入 `Microsoft.Net.Compilers.Toolset` 4.12，匹配现有 AutoConfig 源生成器所需的编译器版本。
- 新增 `scripts/Build-Release.ps1`，把主程序发布、安装器嵌包、自解压安装包和校验文件生成合并为可重复流程。

后续更适合渐进拆分的点：

- `BrowserTranslationSubtitleToolPageViewModel` 仍承担较多浏览器字幕、B 站音乐、ASR、翻译和发送编排逻辑，可继续把“捕获会话编排”和“UI 状态”拆开。
- `BilibiliAsrSubtitleCapture` 体量较大，可按下载、音频抽取、模型选择、字幕过滤拆成更细的服务。
- 安装器与主程序之间的升级协议目前依赖固定文件名和命令行参数，可进一步收敛成共享常量或小型发布清单。

## 使用提示

1. 首次使用请先确认字幕屏 / 音箱设备已连接。
2. 在“个性场景”中选择一个常用场景后，歌词、视频或浏览器字幕结束时会回到该场景。
3. Spotify 模式需要先打开 Spotify 并播放音乐；自动匹配失败时，可选择本地 LRC 作为兜底。
4. 网易云音乐模式依赖桌面歌词和当前适配版本；如果版本不匹配，界面会显示就绪状态或错误提示。
5. B 站音乐模式建议使用 CDP 浏览器打开视频页；页面识别失败时可手动填写歌名/歌手并点击“发送手动修改”重新查询同步歌词。
6. Whisper ASR 首次使用会下载模型；可在浏览器翻译字幕 API 配置中填写 `HF_TOKEN` 或 `HF_ENDPOINT` 加速下载。模型缓存位于本地应用数据目录，不会被普通缓存清理误删。
7. 退出应用时窗口会立即关闭，后台最多等待一小段时间恢复场景后结束进程。

## 开发环境

需要 Windows 与 .NET 8 SDK。仓库包含 `global.json`，在安装了 .NET 8.0.421 或同 feature band 更新版本的机器上会优先使用 .NET 8 构建；编译器版本由 `Microsoft.Net.Compilers.Toolset` 4.12 补齐，以满足当前源生成器要求。

```powershell
dotnet --version
dotnet build HaloPixelToolBox/HaloPixelToolBox/HaloPixelToolBox.csproj -c Release -p:Platform=x64
```

## 发布打包

推荐使用发布脚本生成 2.1.0 的 x64 安装包与便携包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-Release.ps1 -Version 2.1.0 -Platform x64 -Runtime win-x64
```

脚本会执行以下流程：

1. 发布 WinUI 主程序到 `release/v2.1.0/HaloPixelToolBox-v2.1.0-win-x64/`。
2. 将主程序发布目录压缩为 `HaloPixelToolBox.Installer/Resources/Resource/Source.zip`，嵌入 WPF 安装器。
3. 发布 WPF 安装器到 `release/v2.1.0/HaloPixelToolBox.Installer-v2.1.0-win-x64/`。
4. 将安装器发布目录压缩为 `HaloPixelToolBox.Installer.Package/Source.zip`，嵌入自解压启动器。
5. 发布最终自解压包，并复制为 `release/v2.1.0/HaloPixelToolBox-v2.1.0-installer-win-x64.exe`。
6. 额外生成 `HaloPixelToolBox-v2.1.0-win-x64.zip` 便携包和 `SHA256SUMS.txt` 校验文件。

`Source.zip` 与 `release/` 都是构建产物，不提交到 Git；发布时作为 GitHub Release 附件上传。

## GitHub Release

2.1.0 发布建议从仓库默认主分支 `master` 打标签：

```powershell
gh release create v2.1.0 `
  release/v2.1.0/HaloPixelToolBox-v2.1.0-installer-win-x64.exe `
  release/v2.1.0/HaloPixelToolBox-v2.1.0-win-x64.zip `
  release/v2.1.0/SHA256SUMS.txt `
  --target master `
  --title "HaloPixelToolBox v2.1.0" `
  --notes "2.1.0 release"
```

## 反馈

如果遇到歌词无法匹配、字幕不同步、设备连接失败或场景恢复异常，欢迎提交 Issue。Issue 支持中文，请尽量附上歌曲名、歌手名、当前来源、操作步骤和错误提示。

提交前可参考 [CONTRIBUTING.md](./CONTRIBUTING.md)。
