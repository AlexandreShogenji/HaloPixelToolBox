# 浏览器翻译字幕开发计划

## 背景

测试项目位于 `D:\BilibiliSubtitle\BilibiliSubtitle`，用于验证从浏览器/B 站视频播放场景中获取字幕，并为 HaloPixelToolBox 的“浏览器翻译字幕”模块提供后续开发依据。

目标是在 HaloPixelToolBox 中支持浏览器字幕捕获、翻译，并允许用户选择发送原文字幕、翻译字幕或双语字幕到 HaloPixel 字幕屏。

## 测试结果评估

### B 站 CC 字幕直取

测试脚本已经验证 B 站 WBI 签名、视频信息接口、播放器字幕列表接口和字幕 JSON 转 SRT 流程。

结论：

- 对存在 CC 字幕的视频，API 直取是最可靠路线。
- 准确率最高，延迟最低，依赖最少。
- 只能覆盖外挂 CC 字幕，不能覆盖硬字幕或无字幕视频。

### SenseVoice ASR

Phase 1 显示 SenseVoiceSmall 在 CPU 上达到约 22-25x 实时比，明显优于 Whisper medium 的约 0.45-0.60x。

结论：

- SenseVoiceSmall 更适合实时字幕。
- 模型体积较小，约 200MB，适合后续打包集成。
- 对话内容表现可用，音乐/强 BGM 场景仍需优化 VAD 和过滤策略。

### 实时流式 ASR

Phase 2 使用 WAV 文件模拟 100ms 音频块，验证了 VAD、滑动窗口和 SenseVoice 推理链路。

结论：

- ASR 推理本身不是瓶颈，CPU 单段约 100ms。
- 端到端延迟主要由 VAD 分段长度决定。
- 真正未打通的是浏览器/系统音频 WASAPI loopback 捕获。

### 翻译

Phase 3 验证了 MarianMT 翻译路线。

结论：

- ja -> en -> zh 两步翻译质量优于社区 ja -> zh 直译模型。
- CPU 约 400ms/句，短句质量约 60-70% 可接受。
- 人名、长句、ASR 碎片输入容易翻错。
- 适合作为离线辅助理解方案，不适合作为高质量翻译主方案。

## 开发路线

### Phase 0：发送模式选择

目标：先实现用户选择发送原文、译文或双语字幕。

任务：

- 增加 `原文`、`译文`、`双语` 三种输出模式。
- 在 `BrowserTranslationConfiguration` 中保存输出模式。
- 在浏览器字幕页面增加模式选择控件。
- ViewModel 保留原文、译文和实际发送文本。
- 发送到 HaloPixel 时根据模式选择文本。

这是当前最小可落地功能，可以先使用占位捕获源验证链路。

### Phase 1：B 站 CC 字幕直取 MVP

目标：输入 B 站 URL 或 BV 号后，直接获取 CC 字幕并同步发送。

任务：

- 将测试项目中的 WBI 签名和 CC 字幕下载逻辑移植到 C#。
- 新增 `BilibiliCcSubtitleCapture : IBrowserSubtitleCapture`。
- 页面先提供手动输入 B 站 URL/BV 的入口。
- 将字幕 JSON 转为 `SubtitleCue`。
- 第一版按本地计时播放字幕，后续再对接真实浏览器播放进度。

### Phase 2：浏览器当前页面识别

目标：减少用户手动复制 URL。

优先级：

- 先支持用户粘贴 URL。
- 后续可通过 CDP 获取当前 Edge/Chrome tab URL。
- CDP 不放进 MVP，避免过早增加复杂度。
当前建议：
- 第一版自动化使用 Chrome/Edge DevTools Protocol。若浏览器未开启 remote debugging，则在页面提示并保留手动 URL 输入。
- 通过 `/json/list` 找到当前 B 站视频 tab，再用 Runtime.evaluate 读取 `location.href`、`document.querySelector('video').currentTime`、`duration`、`paused`、`playbackRate`。
- 字幕发送不再从本地 `DateTimeOffset.Now` 固定起跑，而是按浏览器 `currentTime` 校准；播放暂停时停止推进，拖动进度时重新定位到最近 cue。
- 长期方案可以改为浏览器扩展或内嵌 WebView2 注入脚本，减少用户手动开启 remote debugging 的门槛。

### Phase 3：实时音频 ASR

目标：无 CC 字幕时，通过系统音频识别原文字幕。

建议路线：

- 在 C# 中实现 WASAPI loopback 捕获。
- 音频转为 16kHz mono PCM。
- 第一版用外部 Python worker 承载 SenseVoice。
- C# 与 Python worker 通过 stdin/stdout JSON 通信。
- 稳定后再考虑 ONNX Runtime C# 原生集成。

当前进展：

- 已增加 B 站 URL 的 ASR 兜底 MVP。
- 当 B 站 CC API 无字幕时，自动尝试 `yt-dlp -> ffmpeg -> Python ASR`。
- Python ASR 已支持引擎选择：
  - 快速模式：`funasr` / SenseVoiceSmall，模型约 900MB，速度快，适合快速兜底。
  - 高质量模式：`faster-whisper` / `large-v3-turbo`，模型约 1.55GB，字幕分句和时间戳更接近 SRT，已作为默认 ASR 引擎。
  - 日语优化模式：`kotoba-tech/kotoba-whisper-v2.0-faster`，模型约 1.45GB，按需下载，暂不预下载以控制本机缓存空间。
- Whisper 分支不启用 VAD 过滤，避免 MV/歌曲人声被误判为背景而丢句；使用 word timestamps 后处理生成短字幕 cue。
- BV1KAdwBsE5z 实测：SenseVoiceSmall 可得到约 32 条 cue，但歌词识别和分句不稳定；Whisper large-v3-turbo 得到约 32 条 cue，歌词文本和时间切分更自然，CPU 耗时约 1 分 21 秒。
- ASR worker 已支持设备自动选择：Whisper 优先检测 CTranslate2 CUDA 与 `cublas64_12.dll`，可用时使用 GPU/float16；运行时不可用则自动回退 CPU/int8。SenseVoice 仅在 torch CUDA 可用时使用 GPU。
- 当前机器 CTranslate2 可看到 CUDA 设备，但缺少 `cublas64_12.dll`，因此实测自动回退 CPU。
- ASR 已接入项目缓存路径 `AppData\Local\Temp\HaloPixelToolBox`：`BrowserSubtitleAsr\Work` 存放转码/worker 临时文件，`BrowserSubtitleAsr\Audio` 按 BV/URL hash 缓存已下载音频，`BrowserSubtitleAsr\Models` 存放 HuggingFace、ModelScope、Torch 模型缓存。
- 长视频加速建议改成分段流水线：
  - 下载/抽取音频后按 5-10 分钟切片，片段之间保留 5-10 秒 overlap。
  - 优先识别浏览器当前播放点附近的 chunk，而不是从 0 秒顺序识别完整视频。
  - 发送当前 chunk 字幕时，后台预识别下一 chunk，并缓存 `chunkStart + cue.Start` 的绝对时间。
  - 若用户拖动播放进度，取消低优先级识别任务，优先识别新播放点附近 chunk。
  - 通过 cue 文本相似度或时间 overlap 去重，避免 chunk 边界重复发送。
- 缺少 Python、yt-dlp、ffmpeg 或 ASR Python 包时，页面会显示明确错误。
- 该 MVP 仍属于离线批处理路线，不是浏览器实时 WASAPI loopback 路线。

### Phase 4：翻译服务分层

目标：让用户根据质量、速度和离线需求选择翻译方式。

建议支持：

- 无翻译：只发送原文。
- 本地轻量翻译：MarianMT，用于离线辅助理解。
- 在线翻译 API：用于质量优先场景。

### Phase 5：可靠性和体验

任务：

- 重复字幕去抖。
- 字幕长度裁剪。
- 翻译失败时回退到原文。
- BGM/空文本跳过。
- 状态提示清晰化。
- 保存输出模式、目标语言、翻译服务和 B 站 URL。

## 推荐执行顺序

1. 实现原文/译文/双语发送模式。
2. 实现 B 站 CC 字幕直取，先手动输入 URL/BV。
3. 实现字幕按时间同步发送到 HaloPixel。
4. 接入真实翻译服务。
5. 实现 WASAPI + SenseVoice 实时 ASR。
