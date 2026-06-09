# HaloPixelToolBox（花再字幕屏工具箱）

面向花再 / HaloPixel 字幕屏与音箱像素屏的 Windows 桌面工具箱，用于歌词同步、视频字幕同步、浏览器翻译字幕、自定义字幕、灯光控制和个性场景切换。

当前版本：`v0.5.0-beta.1`

## 主要功能

- 歌词字幕：支持网易云音乐桌面歌词、本地 LRC、Spotify 当前播放同步。
- Spotify 歌词：根据当前播放歌曲自动填充歌名/歌手，多源并发检索 LRCLIB、网易云、酷狗歌词，线上未命中时可手动选择本地 LRC 兜底。
- 同步行为：支持 Spotify 播放、暂停、拖动进度和连续切歌；开启同步后可自动重新加载新歌歌词。
- 字幕输出：超过字幕屏字符限制时会自动分段发送，尽量在两句歌词时间之间完成多段显示。
- 个性场景：关闭歌词同步时恢复当前默认个性场景；退出应用时先隐藏窗口，再在后台恢复场景，避免退出时卡住界面。
- 字幕音箱：歌词字幕模块提供 0-16 的设备音量调节。
- 视频字幕：支持 PotPlayer 字幕同步，并在同步结束后恢复当前默认个性场景。
- 浏览器翻译字幕：支持浏览器字幕捕获、翻译 API 配置和字幕输出。
- 自定义字幕：支持固定、左滚、右滚等字幕输出模式，并限制字幕内容长度。

## 使用提示

1. 首次使用请先确认字幕屏 / 音箱设备已连接。
2. 在“个性场景”中选择一个常用场景后，歌词、视频或浏览器字幕结束时会回到该场景。
3. Spotify 模式需要先打开 Spotify 并播放音乐；自动匹配失败时，可选择本地 LRC 作为兜底。
4. 网易云音乐模式依赖桌面歌词和当前适配版本；如果版本不匹配，界面会显示就绪状态或错误提示。
5. 退出应用时窗口会立即关闭，后台最多等待一小段时间恢复场景后结束进程。

## 构建

需要 Windows 与 .NET 8 SDK。

```powershell
dotnet build HaloPixelToolBox/HaloPixelToolBox/HaloPixelToolBox.csproj -c Release -p:Platform=x64
```

发布 x64 目录：

```powershell
dotnet publish HaloPixelToolBox/HaloPixelToolBox/HaloPixelToolBox.csproj -c Release -p:Platform=x64 -p:PublishProfile= -r win-x64 --self-contained false -o release/v0.5.0-beta.1/HaloPixelToolBox-v0.5.0-beta.1-win-x64
```

## 反馈

如果遇到歌词无法匹配、字幕不同步、设备连接失败或场景恢复异常，欢迎提交 Issue。Issue 支持中文，请尽量附上歌曲名、歌手名、当前来源、操作步骤和错误提示。

提交前可参考 [CONTRIBUTING.md](./CONTRIBUTING.md)。
