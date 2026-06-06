# 歌词字幕开发计划

## 背景

浏览器 B 站视频字幕同步已经具备可用基础：B 站 CC 字幕优先直取，缺字幕时可用 ASR 兜底，并能跟随浏览器播放进度同步发送到 HaloPixel。

下一阶段希望支持“歌词字幕”。核心原因是 MV、演唱会、翻唱、音乐视频这类内容用 ASR 识别歌词准确率偏低，尤其容易受伴奏、人声混响、语言切换和重复副歌影响。歌词字幕应优先走确定性的歌词检索与时间轴同步路线，而不是继续把 ASR 当主方案。

## 需求整合

目标：
- 在 HaloPixelToolBox 中建立统一的歌词字幕能力，支持从多个平台获取同步歌词。
- 第一批适配网易云音乐、QQ 音乐、Spotify，以及本地 LRC 作为稳定测试入口。
- 保留现有“歌词字幕”页面，并把占位 Provider 替换为真实 Provider。
- 后续在“浏览器翻译字幕”中增加音乐模式：识别或填写歌曲信息后检索歌词，再按浏览器视频进度同步歌词，而不是对 MV 音频做 ASR。
- 兼容不同平台、不同客户端版本、不同歌词格式和不同时间轴质量。

非目标：
- 第一阶段不做通用音频指纹识别。
- 第一阶段不依赖 ASR 生成歌词正文。
- 第一阶段不承诺所有平台都能通过官方接口直接拿到逐行歌词。
- 第一阶段不处理歌词版权展示之外的下载、分发或批量缓存场景。

## 当前项目基础

已有基础：
- `LyricsProviderKind`、`LyricsQuery`、`LyricsTrack` 已存在。
- `ILyricsProvider` 和 `LyricsProviderRegistry` 已预留多平台扩展点。
- `PlaceholderLyricsProvider` 当前返回占位歌词。
- `LyricsSubtitleToolPageViewModel` 已能加载歌词、按播放秒数选择当前行并发送到 HaloPixel。
- `SubtitleCue`、`SubtitleDocument`、`SubtitleTimelineController` 可复用视频字幕时间轴思想。
- `CloudMusicLyricsReader` 已有旧版网易云桌面歌词内存读取方案，并记录了 3.1.25 到 3.1.30 的版本地址解析器。

需要补齐：
- LRC/逐字歌词解析与规范化。
- 歌曲搜索、候选项选择、匹配置信度与缓存。
- 播放进度来源抽象。
- 平台 Provider 的错误边界、版本探测和降级策略。
- 浏览器音乐模式与歌词同步的 UI 入口。

## 总体判断

推荐路线是先做“歌词引擎”，再做平台适配，最后接入浏览器音乐模式。

原因：
- 网易云、QQ 音乐、Spotify 的难点不一样，但它们最终都应产出统一的 `LyricsTrack`。
- 本地 LRC 是最稳定的验收入口，能先验证解析、时间轴、发送、暂停、拖动、滚动显示。
- 平台接口可能变化，桌面客户端也会随版本变动；Provider 层必须可替换、可降级。
- Spotify 官方 Web API 适合拿歌曲元数据和播放状态，但公开文档中未提供歌词正文接口，所以 Spotify 更适合作为“当前播放歌曲身份 + 进度”的来源，再接歌词检索 Provider。

## 建议架构

### 1. 歌词数据模型扩展

扩展 `LyricsQuery`：
- `Title`
- `Artist`
- `Album`
- `Duration`
- `Isrc`
- `PlatformTrackId`
- `SourceUrl`
- `PreferSyncedLyrics`

扩展 `LyricsTrack`：
- `Provider`
- `PlatformTrackId`
- `Album`
- `Duration`
- `IsSynced`
- `IsTranslation`
- `Offset`
- `Confidence`
- `RawSource`

保留 `SubtitleCue` 作为逐行歌词时间轴条目，避免重复造一套时间轴模型。

### 2. Provider 分层

建议拆成三类接口：
- `ILyricsProvider`：根据关键词或歌曲身份返回歌词候选。
- `IPlaybackMetadataProvider`：获取当前播放歌曲、播放进度、暂停状态。
- `ILyricsLiveLineProvider`：读取桌面歌词当前行，适合网易云旧版内存读取这类方案。

这样 Spotify、浏览器、网易云桌面客户端可以只负责“现在播什么”和“播到哪里”；歌词正文仍由网易云/QQ/本地 LRC/其他歌词源统一检索。

### 3. 同步控制器

新增或复用一个歌词时间轴控制器：
- 输入 `LyricsTrack` 与播放位置。
- 输出当前应发送的 `SubtitleCue`。
- 支持暂停、恢复、seek、循环播放、手动偏移。
- 支持去重，避免同一句歌词反复发送。
- 支持末尾停留和恢复默认场景。

### 4. 缓存与版本适配

缓存内容：
- 搜索结果缓存。
- 歌词正文缓存。
- 解析后的 `LyricsTrack` 缓存。
- 用户手动选择过的歌曲匹配关系。
- 平台客户端版本与探测结果。

版本适配：
- 网易云旧版内存地址解析器保留，但迁移到 Provider/LiveLineProvider。
- 新增版本适配不要写死在页面层。
- 支持“自动探测失败后手动输入地址/手动选择歌词文件/手动搜索歌词”的降级路线。

## 开发阶段

### Phase 0：歌词核心模型和本地 LRC MVP

目标：先让歌词字幕链路不依赖任何线上平台也能稳定跑通。

任务：
- 扩展 `LyricsQuery`、`LyricsTrack`。
- 新增 LRC 解析器，支持 `[mm:ss.xx]`、多时间标签、一行多时间、无时间纯文本。
- 新增本地 LRC Provider。
- 歌词页面增加“选择本地歌词文件”。
- 使用当前 `PositionSeconds` 验证发送当前行。
- 加入手动 offset 调整。

验收：
- 选择本地 LRC 后能预览歌词。
- 拖动播放秒数后发送正确行。
- 同一句歌词不会重复刷屏。
- 歌词过长时能按现有滚动设置发送。

### Phase 1：统一歌词同步服务

目标：把页面里的临时同步逻辑抽成可复用服务，为平台播放进度和浏览器音乐模式做准备。

任务：
- 新增 `LyricsTimelineSyncService` 或复用并扩展现有字幕时间轴控制逻辑。
- 支持本地计时、外部播放进度、暂停状态。
- 支持用户手动上一句/下一句/重新发送当前句。
- 支持 offset、去重、末尾恢复默认场景。
- 页面从“发送当前行”升级为“开始同步/停止同步”。

验收：
- 本地 LRC 可连续同步发送。
- 暂停时不继续推进。
- seek 后能定位到新位置。
- 关闭同步后恢复当前使用中的默认显示内容。

### Phase 2：网易云音乐适配

目标：优先复用现有网易云能力，并补齐搜索式歌词获取。

任务：
- 将 `CloudMusicLyricsReader` 迁移到 `NetEaseCloudMusicLiveLineProvider`。
- 保留 3.1.25 到 3.1.30 版本解析器。
- 页面显示当前检测到的网易云客户端版本与适配状态。
- 新增 `NetEaseCloudMusicLyricsProvider`，用于关键词/歌曲 ID 检索同步歌词。
- 支持桌面客户端当前行读取和搜索歌词两种模式。
- 自动失败时回退到手动搜索或本地 LRC。

验收：
- 已适配版本的网易云桌面歌词能读取当前行。
- 能通过关键词加载同步歌词并按时间轴发送。
- 未适配版本有清晰状态提示，不影响其他 Provider 使用。

### Phase 3：QQ 音乐适配

目标：实现 QQ 音乐歌词检索与后续客户端版本扩展空间。

任务：
- 新增 `QQMusicLyricsProvider`。
- 先走关键词/歌曲信息检索同步歌词。
- 预留 QQ 音乐桌面客户端探测接口。
- 若后续需要客户端当前播放进度，再实现 `QQMusicPlaybackMetadataProvider`。
- 对候选项加入歌手、时长、专辑匹配。

验收：
- 关键词搜索能返回可选歌词候选。
- 选择候选后能同步发送。
- 搜索失败可回退到其他 Provider 或本地 LRC。

### Phase 4：Spotify 适配

目标：把 Spotify 作为歌曲身份和播放进度来源，再用歌词 Provider 匹配歌词。

任务：
- 新增 `SpotifyPlaybackMetadataProvider`。
- 支持 OAuth 配置、当前播放曲目、播放进度、暂停状态。
- 使用 Spotify track id、标题、歌手、专辑、时长、ISRC 生成 `LyricsQuery`。
- 歌词正文从本地 LRC、网易云、QQ 音乐或后续通用歌词源检索。
- UI 明确区分“Spotify 当前播放”和“歌词来源”。

验收：
- Spotify 正在播放歌曲时能获取标题、歌手、时长、进度。
- 能根据 Spotify 歌曲信息匹配到同步歌词。
- 暂停、切歌、seek 后歌词同步能跟随。

风险：
- Spotify 官方 Web API 适合元数据和播放控制，不应把它规划成直接歌词正文来源。
- OAuth、Premium 账号、地区可用性和 API 权限会影响体验，需要做清晰提示。
- Spotify Player API 文档包含平台政策提示，正式发布前需要确认该集成是否符合 Spotify Developer Policy；若存在风险，Spotify 适配应保持为本地/实验性开关。

### Phase 5：浏览器音乐模式

目标：在 B 站 MV/音乐视频场景中，用歌词检索替代 ASR。

任务：
- 在浏览器字幕页面增加“音乐模式”。
- 当前 B 站 URL/标题/UP 主/视频标题解析为歌曲搜索 query。
- 支持用户手动修正歌名、歌手。
- 通过 Provider 搜索歌词候选，用户选择后进入同步。
- 同步时继续使用浏览器 `currentTime`，而不是本地计时。
- 支持 offset 调整，解决 MV 前奏、片头、剪辑版、Live 版和非官方字幕时间轴不一致。
- 若找不到歌词，再提示可使用 ASR 兜底或本地 LRC。

验收：
- B 站 MV 无 CC 字幕时，可手动/半自动选择歌词并同步到 HaloPixel。
- 拖动浏览器进度后歌词能重新定位。
- 切换视频后能停止旧歌词并提示重新匹配。
- 歌词时间轴偏移可保存到该 URL 或 BV 的缓存。

### Phase 6：匹配质量与体验增强

目标：减少误匹配，让日常听歌场景顺手。

任务：
- 歌曲候选列表显示标题、歌手、专辑、时长、来源、同步歌词可用性。
- 匹配评分使用标题相似度、歌手相似度、时长差、ISRC。
- 对 MV、Live、翻唱、剪辑版提供“手动 offset”和“保存本视频匹配”。
- 支持简繁转换、空白/标点规范化。
- 支持翻译歌词、罗马音、双语显示的后续扩展。
- 增加缓存清理入口。

验收：
- 常见中文/日文/英文歌曲能稳定匹配。
- 误匹配时用户能快速换候选。
- 同一首歌或同一个 B 站视频下次能直接复用上次选择。

## 推荐执行顺序

1. 本地 LRC Provider + LRC 解析器。
2. 歌词同步服务和页面同步按钮。
3. 网易云 Provider：先迁移旧内存读取，再做搜索式歌词获取。
4. QQ 音乐 Provider：先做搜索式歌词获取。
5. Spotify 当前播放元数据 Provider。
6. 浏览器音乐模式：B 站标题解析、歌词候选、offset、按浏览器进度同步。
7. 匹配质量、缓存、错误提示和多语言歌词增强。

## 开发注意点

- 歌词是文本版权内容，缓存只用于本地用户体验，不做分享或批量导出。
- 平台接口和桌面客户端内存地址都可能变化，Provider 必须允许失败并清晰提示。
- 不要把歌词检索逻辑写进 ViewModel，ViewModel 只负责绑定状态与命令。
- 歌词同步应复用字幕发送能力，避免视频字幕和歌词字幕分裂成两套发送协议。
- 浏览器音乐模式不替换普通字幕模式，只作为 MV/音乐视频的单独开关。
- ASR 在音乐模式里只作为最后兜底，不作为主路径。

## 参考

- [Spotify Web API - Get Playback State](https://developer.spotify.com/documentation/web-api/reference/get-information-about-the-users-current-playback)：用于读取当前播放内容、播放进度、暂停状态和设备信息。
- [Spotify Web API - Get Track](https://developer.spotify.com/documentation/web-api/reference/get-track)：Track 对象包含标题、歌手、时长、ISRC 等元数据，但不是歌词正文接口。
