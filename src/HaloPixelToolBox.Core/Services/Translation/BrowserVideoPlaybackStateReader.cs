using HaloPixelToolBox.Core.Models.Subtitles;
using HaloPixelToolBox.Core.Utilities;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace HaloPixelToolBox.Core.Services.Translation;

public sealed class BrowserVideoPlaybackStateReader
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private static readonly int[] DevToolsPorts = [9222, 9223, 9224, 9225];

    public Task<BrowserVideoPlaybackSnapshot?> GetCurrentBilibiliSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return GetCurrentBilibiliSnapshotAsync("chrome", cancellationToken);
    }

    public async Task<BrowserVideoPlaybackSnapshot?> GetCurrentBilibiliSnapshotAsync(string browserProcessName, CancellationToken cancellationToken = default)
    {
        return (await ProbeCurrentBilibiliSnapshotAsync(browserProcessName, cancellationToken)).Snapshot;
    }

    public Task<BrowserVideoPlaybackProbeResult> ProbeCurrentBilibiliSnapshotAsync(string browserProcessName, CancellationToken cancellationToken = default)
    {
        return ProbeCurrentBilibiliSnapshotAsync(browserProcessName, string.Empty, cancellationToken);
    }

    public Task<BrowserVideoPlaybackProbeResult> ProbeCurrentBilibiliSnapshotAsync(string browserProcessName, string preferredBvid, CancellationToken cancellationToken = default)
    {
        return ProbeCurrentBilibiliSnapshotAsync(browserProcessName, preferredBvid, readMusicMetadata: false, cancellationToken);
    }

    public async Task<BrowserVideoPlaybackProbeResult> ProbeCurrentBilibiliSnapshotAsync(
        string browserProcessName,
        string preferredBvid,
        bool readMusicMetadata,
        CancellationToken cancellationToken = default)
    {
        var observedTabs = new List<BrowserVideoPlaybackTabInfo>();
        var hasDevToolsConnection = false;
        var hasBilibiliDevToolsTab = false;
        var candidateProbes = new List<BrowserVideoPlaybackProbeResult>();
        preferredBvid = BilibiliVideoUrlHelper.ExtractBvid(preferredBvid);
        var addressBarSnapshot = TryReadAddressBarSnapshot(browserProcessName);
        var addressBarBvid = BilibiliVideoUrlHelper.ExtractBvid(addressBarSnapshot?.Url ?? string.Empty);
        if (string.IsNullOrWhiteSpace(preferredBvid) && !string.IsNullOrWhiteSpace(addressBarBvid))
            preferredBvid = addressBarBvid;

        var portTabResults = await Task.WhenAll(DevToolsPorts.Select(async port => new
        {
            Port = port,
            Tabs = await TryGetTabsAsync(port, cancellationToken)
        }));

        foreach (var portTabs in portTabResults)
        {
            if (portTabs.Tabs.Count > 0)
                hasDevToolsConnection = true;

            foreach (var tab in portTabs.Tabs)
            {
                var isBilibiliVideo = IsBilibiliVideoTab(tab);
                hasBilibiliDevToolsTab |= isBilibiliVideo;
                observedTabs.Add(new BrowserVideoPlaybackTabInfo
                {
                    Port = portTabs.Port,
                    Type = tab.Type,
                    Title = tab.Title,
                    Url = tab.Url,
                    IsBilibiliVideo = isBilibiliVideo
                });
            }
        }

        var probeTasks = portTabResults
            .SelectMany(portTabs => portTabs.Tabs
                .Where(IsBilibiliVideoTab)
                .Select(async tab =>
                {
                    var snapshot = await TryReadSnapshotAsync(tab, portTabs.Port, readMusicMetadata, cancellationToken);
                    if (snapshot?.Position is { } position)
                    {
                        return new BrowserVideoPlaybackProbeResult
                        {
                            Snapshot = snapshot,
                            Tabs = observedTabs,
                            HasDevToolsConnection = true,
                            HasBilibiliDevToolsTab = true,
                            Message = $"CDP:{portTabs.Port} 已读取播放进度 {FormatTime(position)}"
                        };
                    }

                    if (snapshot is not null)
                    {
                        return new BrowserVideoPlaybackProbeResult
                        {
                            Snapshot = snapshot,
                            Tabs = observedTabs,
                            HasDevToolsConnection = true,
                            HasBilibiliDevToolsTab = true,
                            Message = $"CDP:{portTabs.Port} 找到 B 站页面，但尚未读到有效 video.currentTime"
                        };
                    }

                    return null;
                }))
            .ToList();

        if (probeTasks.Count > 0)
        {
            foreach (var probe in await Task.WhenAll(probeTasks))
            {
                if (probe is not null)
                    candidateProbes.Add(probe);
            }
        }

        foreach (var port in Array.Empty<int>())
        {
            var tabs = await TryGetTabsAsync(port, cancellationToken);
            if (tabs.Count > 0)
                hasDevToolsConnection = true;

            foreach (var tab in tabs)
            {
                var isBilibiliVideo = IsBilibiliVideoTab(tab);
                hasBilibiliDevToolsTab |= isBilibiliVideo;
                observedTabs.Add(new BrowserVideoPlaybackTabInfo
                {
                    Port = port,
                    Type = tab.Type,
                    Title = tab.Title,
                    Url = tab.Url,
                    IsBilibiliVideo = isBilibiliVideo
                });
            }

            foreach (var tab in tabs.Where(IsBilibiliVideoTab)
                         .OrderByDescending(tab => IsPreferredBvid(tab.Url, preferredBvid))
                         .ThenByDescending(tab => tab.Url.Contains("/video/", StringComparison.OrdinalIgnoreCase)))
            {
                var snapshot = await TryReadSnapshotAsync(tab, port, readMusicMetadata, cancellationToken);
                if (snapshot?.Position is { } position)
                {
                    var probe = new BrowserVideoPlaybackProbeResult
                    {
                        Snapshot = snapshot,
                        Tabs = observedTabs,
                        HasDevToolsConnection = true,
                        HasBilibiliDevToolsTab = true,
                        Message = $"CDP:{port} 已读取播放进度 {FormatTime(position)}"
                    };

                    candidateProbes.Add(probe);
                    continue;
                }

                if (snapshot is not null)
                {
                    var probe = new BrowserVideoPlaybackProbeResult
                    {
                        Snapshot = snapshot,
                        Tabs = observedTabs,
                        HasDevToolsConnection = true,
                        HasBilibiliDevToolsTab = true,
                        Message = $"CDP:{port} 找到 B 站页，但尚未读到有效 video.currentTime"
                    };

                    candidateProbes.Add(probe);
                }
            }
        }

        if (candidateProbes.Count > 0)
        {
            return candidateProbes
                .OrderByDescending(probe => ScoreProbe(probe, preferredBvid, addressBarBvid))
                .First();
        }

        if (addressBarSnapshot is not null)
        {
            var message = hasDevToolsConnection
                ? "已从地址栏捕获 B 站 URL，但 CDP 标签页中未发现该视频页"
                : "已从地址栏捕获 B 站 URL，但未连接到 Chrome CDP 端口";

            return new BrowserVideoPlaybackProbeResult
            {
                Snapshot = addressBarSnapshot,
                Tabs = observedTabs,
                HasDevToolsConnection = hasDevToolsConnection,
                HasBilibiliDevToolsTab = hasBilibiliDevToolsTab,
                Message = message
            };
        }

        return new BrowserVideoPlaybackProbeResult
        {
            Tabs = observedTabs,
            HasDevToolsConnection = hasDevToolsConnection,
            HasBilibiliDevToolsTab = hasBilibiliDevToolsTab,
            Message = BuildNoSnapshotMessage(hasDevToolsConnection, observedTabs)
        };
    }

    private static async Task<IReadOnlyList<DevToolsTab>> TryGetTabsAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            var json = await HttpClient.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken);
            return JsonSerializer.Deserialize<List<DevToolsTab>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsBilibiliVideoTab(DevToolsTab tab)
    {
        return tab.Type.Equals("page", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(tab.WebSocketDebuggerUrl)
               && BilibiliVideoUrlHelper.IsBilibiliVideoLike(tab.Url);
    }

    private static bool IsPreferredBvid(string input, string preferredBvid)
    {
        return string.IsNullOrWhiteSpace(preferredBvid)
               || BilibiliVideoUrlHelper.ExtractBvid(input).Equals(preferredBvid, StringComparison.OrdinalIgnoreCase);
    }

    private static BrowserVideoPlaybackSnapshot? TryReadAddressBarSnapshot(string browserProcessName)
    {
        var processName = string.IsNullOrWhiteSpace(browserProcessName) ? "chrome" : browserProcessName.Trim();
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            processName = processName[..^4];

        try
        {
            foreach (var process in Process.GetProcessesByName(processName)
                         .Where(process => process.MainWindowHandle != IntPtr.Zero)
                         .OrderByDescending(process => process.MainWindowTitle.Contains("bilibili", StringComparison.OrdinalIgnoreCase)))
            {
                var window = AutomationElement.FromHandle(process.MainWindowHandle);
                var url = TryReadBilibiliUrlFromWindow(window);
                if (url is null)
                    continue;

                return new BrowserVideoPlaybackSnapshot
                {
                    Url = url,
                    Title = TrimBrowserTitle(process.MainWindowTitle),
                    IsPaused = true,
                    PlaybackRate = 1,
                    Source = $"{processName} address bar"
                };
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? TryReadBilibiliUrlFromWindow(AutomationElement window)
    {
        var editControls = window.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

        foreach (AutomationElement editControl in editControls)
        {
            try
            {
                if (editControl.GetCurrentPattern(ValuePattern.Pattern) is not ValuePattern valuePattern)
                    continue;

                var url = BilibiliVideoUrlHelper.NormalizeVideoUrl(valuePattern.Current.Value);
                if (url is not null)
                {
                    if (string.IsNullOrWhiteSpace(url))
                        continue;
                    return url;
                }
            }
            catch
            {
                // Browser controls can briefly refuse pattern access while rendering.
            }
        }

        return null;
    }

    private static async Task<BrowserVideoPlaybackSnapshot?> TryReadSnapshotAsync(DevToolsTab tab, int port, bool readMusicMetadata, CancellationToken cancellationToken)
    {
        try
        {
            using var webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(new Uri(tab.WebSocketDebuggerUrl), cancellationToken);

            var command = JsonSerializer.Serialize(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = """
                    (async () => {
                      const shouldReadMusicMetadata = __READ_MUSIC_METADATA__;
                      const normalize = value => String(value || '').replace(/\s+/g, ' ').trim();
                      const escapeRegex = value => String(value || '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
                      const musicTitleLabels = ['歌名', '歌曲', '曲名', '标题', 'Song', 'Title'];
                      const musicArtistLabels = ['原唱', '演唱者', '歌手', '艺人', 'Artist', 'Singer'];
                      const musicAlbumLabels = ['专辑', 'Album'];
                      const allMusicLabels = ['音乐信息', ...musicTitleLabels, ...musicArtistLabels, ...musicAlbumLabels, '出处'];
                      const normalizeLabel = value => normalize(value).replace(/^[\s:：]+|[\s:：]+$/g, '').replace(/\s+/g, '');
                      const knownMusicLabels = new Set(allMusicLabels.map(normalizeLabel));
                      const wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
                      const cleanFieldValue = value => {
                        let result = normalize(value);
                        result = result.replace(/^(音乐信息|歌名|歌曲|曲名|标题|Song|Title|原唱|演唱者|歌手|艺人|Artist|Singer|专辑|Album|出处)\s*[:：]\s*/i, '');
                        result = result.replace(/\s*(演唱者|歌手|艺人)\s*$/i, '').trim();
                        return result;
                      };
                      const isKnownLabel = value => {
                        const label = normalizeLabel(value);
                        return knownMusicLabels.has(label);
                      };
                      const getClassText = element => {
                        try {
                          return String((element && element.getAttribute && element.getAttribute('class')) || '');
                        } catch {
                          return '';
                        }
                      };
                      const hasValueClass = element => /(^|[\s_-])value([\s_-]|$)/i.test(getClassText(element));
                      const ownElementText = element => {
                        try {
                          return normalize(Array.from(element.childNodes || [])
                            .filter(node => node.nodeType === Node.TEXT_NODE)
                            .map(node => node.textContent || '')
                            .join(' '));
                        } catch {
                          return '';
                        }
                      };
                      const musicNoisePatterns = [
                        /音乐信息|音乐详情|音乐视频|相关推荐|包含同款音乐|人气飙升|点击前往|分享|收藏|评论/,
                        /播放\s*\d*|弹幕\s*\d*|点赞|投币|转发/
                      ];
                      const isUsableMusicValue = value => {
                        const text = cleanFieldValue(value);
                        if (!text || isKnownLabel(text)) return false;
                        if (text.length > 120) return false;
                        const labelLikeCount = allMusicLabels
                          .filter(label => new RegExp(escapeRegex(label) + '\\s*[:：]', 'i').test(text))
                          .length;
                        if (labelLikeCount > 0) return false;
                        return !musicNoisePatterns.some(pattern => pattern.test(text));
                      };
                      const pickUsableValue = values => {
                        for (const value of values) {
                          const text = cleanFieldValue(value);
                          if (isUsableMusicValue(text)) return text;
                        }
                        return '';
                      };
                      const fireHover = element => {
                        if (!element || !element.dispatchEvent) return;
                        const options = { bubbles: true, cancelable: true, view: window };
                        for (const type of ['pointerenter', 'pointerover', 'mouseenter', 'mouseover']) {
                          try {
                            if (type.startsWith('pointer') && typeof PointerEvent === 'function') {
                              element.dispatchEvent(new PointerEvent(type, options));
                            } else {
                              element.dispatchEvent(new MouseEvent(type, options));
                            }
                          } catch {
                          }
                        }
                      };
                      const revealMusicInfoPanels = async () => {
                        const selectors = [
                          '#musicApp [class*="titleWrapper"]',
                          '#musicApp [class*="_target_"]',
                          '#musicApp [class*="target"]',
                          '#musicApp [class*="_PcDetailInfo_"] .right',
                          '#musicApp [class*="right"]',
                          '#musicApp',
                          '.tag-link.bgm-link',
                          '[class*="bgm-link"]',
                          '[class*="bgmLink"]',
                          '[title*="发现《"]',
                          '[title*="发现「"]',
                          '[class*="music"]',
                          '[class*="Music"]',
                          '[id*="music"]',
                          '[id*="Music"]',
                          '[title*="音乐"]',
                          '[aria-label*="音乐"]',
                          'a[href*="music.bilibili.com"]'
                        ];
                        const candidates = [];
                        const seen = new Set();
                        const looksLikeMusicTrigger = element => {
                          const classText = getClassText(element);
                          const idText = String((element && element.id) || '');
                          let text = '';
                          try {
                            text = normalize(element.innerText || element.textContent || '').slice(0, 240);
                          } catch {
                            text = '';
                          }
                          return element.id === 'musicApp'
                            || /music/i.test(classText)
                            || /bgm/i.test(classText)
                            || /music/i.test(idText)
                            || /bgm/i.test(idText)
                            || /音乐|歌曲|歌名|原唱|BGM|发现《|发现「/i.test(text);
                        };
                        for (const selector of selectors) {
                          for (const element of Array.from(document.querySelectorAll(selector))) {
                            if (!element || seen.has(element)) continue;
                            if (!looksLikeMusicTrigger(element)) continue;
                            seen.add(element);
                            candidates.push(element);
                            if (candidates.length >= 32) break;
                          }
                          if (candidates.length >= 32) break;
                        }
                        for (const element of candidates) {
                          fireHover(element);
                          fireHover(element.parentElement);
                        }
                        if (candidates.length > 0) await wait(180);
                      };
                      if (shouldReadMusicMetadata) await revealMusicInfoPanels();
                      const collectRoots = () => {
                        const roots = [document];
                        const visited = new Set();
                        for (let index = 0; index < roots.length; index++) {
                          const root = roots[index];
                          if (!root || visited.has(root)) continue;
                          visited.add(root);
                          let elements = [];
                          try {
                            elements = Array.from(root.querySelectorAll ? root.querySelectorAll('*') : []);
                          } catch {
                            elements = [];
                          }
                          for (const element of elements) {
                            if (element.shadowRoot && !visited.has(element.shadowRoot)) {
                              roots.push(element.shadowRoot);
                            }
                            if (element.tagName === 'IFRAME') {
                              try {
                                const frameDocument = element.contentDocument;
                                if (frameDocument && !visited.has(frameDocument)) roots.push(frameDocument);
                              } catch {
                              }
                            }
                          }
                        }
                        return roots;
                      };
                      const roots = shouldReadMusicMetadata ? collectRoots() : [document];
                      const getElements = selector => roots.flatMap(root => {
                        try {
                          return Array.from(root.querySelectorAll ? root.querySelectorAll(selector) : []);
                        } catch {
                          return [];
                        }
                      });
                      const rootText = root => {
                        try {
                          if (root.body && root.body.innerText) return root.body.innerText;
                          if (root.host && root.host.innerText) return root.host.innerText;
                          return root.textContent || '';
                        } catch {
                          return '';
                        }
                      };
                      const getAllPageText = () => roots.map(rootText).filter(Boolean).join('\n');
                      const elementText = element => {
                        try {
                          return normalize(element.innerText || element.textContent || '');
                        } catch {
                          return '';
                        }
                      };
                      const elementTextCandidates = element => [ownElementText(element), elementText(element)].filter(Boolean);
                      const pickSelectorText = (root, selectors) => {
                        if (!root) return '';
                        for (const selector of selectors) {
                          let elements = [];
                          try {
                            elements = Array.from(root.querySelectorAll(selector));
                          } catch {
                            elements = [];
                          }
                          for (const element of elements) {
                            const text = pickUsableValue(elementTextCandidates(element));
                            if (text) return text;
                          }
                        }
                        return '';
                      };
                      const pickBilibiliMusicAppDetail = () => {
                        const app = getElements('#musicApp')[0] || document.querySelector('#musicApp');
                        if (!app) return { title: '', artist: '', album: '' };
                        const detail = app.querySelector('[class*="_PcDetailInfo_"], [class*="PcDetailInfo"]') || app;
                        const title = pickSelectorText(detail, [
                          '.right [class*="titleWrapper"] .title',
                          '.right [class*="titleWrapper"] [class*="title"]',
                          '.right [class*="titleWrapper"] span',
                          '.right [class*="_target_"] .title',
                          '.right [class*="_target_"] span',
                          '.right .titleWrapper .title',
                          '.right .title'
                        ]);
                        const artist = pickSelectorText(detail, [
                          '.right [class*="_musicInfo_"] .noMidSinger',
                          '.right [class*="musicInfo"] .noMidSinger',
                          '.right [class*="_musicInfo_"] .singer',
                          '.right [class*="musicInfo"] .singer',
                          '.right .centerContent .singer',
                          '.right .centerContent [class*="Singer"]'
                        ]);
                        const album = pickSelectorText(detail, [
                          '.right [class*="album"]',
                          '.right [class*="Album"]'
                        ]);
                        return { title, artist, album };
                      };
                      const cleanBgmTagTitle = value => {
                        const text = cleanFieldValue(value);
                        const match = text.match(/发现\s*[《「『"“]\s*([^》」』"”]{1,120})\s*[》」』"”]/);
                        if (!match) return '';
                        return cleanFieldValue(match[1]);
                      };
                      const pickBilibiliBgmTagTitle = () => {
                        const selectors = [
                          '.tag-link.bgm-link',
                          '[class*="bgm-link"]',
                          '[class*="bgmLink"]',
                          '[title^="发现《"]',
                          '[title*="发现《"]',
                          '[title^="发现「"]',
                          '[title*="发现「"]',
                          '[class*="tag-txt"]',
                          '[class*="tagTxt"]'
                        ];
                        const values = [];
                        const seen = new Set();
                        for (const selector of selectors) {
                          for (const element of getElements(selector)) {
                            if (!element || seen.has(element)) continue;
                            seen.add(element);
                            try {
                              const titleAttr = element.getAttribute && element.getAttribute('title');
                              if (titleAttr) values.push(titleAttr);
                            } catch {
                            }
                            values.push(...elementTextCandidates(element));
                          }
                        }

                        for (const value of values) {
                          const title = cleanBgmTagTitle(value);
                          if (isUsableMusicValue(title)) return title;
                        }

                        return '';
                      };
                      const containsAnyLabel = (text, labels) => labels.some(label => normalize(text).includes(label));
                      const findScopedMusicInfoCandidates = () => {
                        const selectors = [
                          '#musicApp [class*="_content_"]',
                          '#musicApp [class*="content"]',
                          '#musicApp [class*="_pop_"]',
                          '#musicApp [class*="pop"]',
                          '#musicApp [class*="_PcDetailInfo_"]',
                          '#musicApp'
                        ];
                        const candidates = [];
                        const seen = new Set();
                        for (const selector of selectors) {
                          for (const element of getElements(selector)) {
                            if (!element || seen.has(element)) continue;
                            seen.add(element);
                            candidates.push(element);
                          }
                        }

                        if (candidates.length > 0) return candidates;
                        return getElements('[class*="_content_"], [class*="content"], [class*="_pop_"], [class*="pop"]');
                      };
                      const findMusicInfoContainers = () => {
                        const candidates = [];
                        const scopedCandidates = findScopedMusicInfoCandidates();
                        const elementsToSearch = scopedCandidates.length > 0 ? scopedCandidates : getElements('*');
                        for (const element of elementsToSearch) {
                          const text = elementText(element);
                          if (!text.includes('音乐信息')) continue;
                          if (!containsAnyLabel(text, musicTitleLabels) && !containsAnyLabel(text, musicArtistLabels)) continue;
                          const score = text.length - (element.closest && element.closest('#musicApp') ? 10000 : 0);
                          candidates.push({ element, score, text });
                        }
                        return candidates
                          .sort((left, right) => left.score - right.score)
                          .map(candidate => candidate.element);
                      };
                      const pickFromText = (text, labels) => {
                        const source = String(text || '').replace(/\r/g, '\n');
                        const musicInfoIndex = source.indexOf('音乐信息');
                        const searchSource = musicInfoIndex >= 0 ? source.slice(musicInfoIndex, musicInfoIndex + 1200) : source;
                        const stopLabels = allMusicLabels.map(escapeRegex).join('|');
                        for (const rawLabel of labels) {
                          const escaped = escapeRegex(rawLabel);
                          const pattern = escaped + '\\s*[:：]\\s*([\\s\\S]{1,160}?)(?=(?:\\s|\\n)*(?:' + stopLabels + ')\\s*[:：]|\\n|$)';
                          const match = searchSource.match(new RegExp(pattern, 'i'));
                          if (match) {
                            const value = cleanFieldValue(match[1]);
                            if (isUsableMusicValue(value)) return value;
                          }
                        }
                        return '';
                      };
                      const pickSiblingValue = (children, labelIndex) => {
                        const byValueClass = children
                          .filter((child, index) => index !== labelIndex && hasValueClass(child))
                          .map(child => elementText(child));
                        const valueFromClass = pickUsableValue(byValueClass);
                        if (valueFromClass) return valueFromClass;

                        const afterLabel = children
                          .slice(Math.max(0, labelIndex + 1))
                          .map(child => elementText(child));
                        const afterValue = pickUsableValue(afterLabel);
                        if (afterValue) return afterValue;

                        return pickUsableValue(children
                          .slice(0, Math.max(0, labelIndex))
                          .reverse()
                          .map(child => elementText(child)));
                      };
                      const pickStructuredField = (container, labels) => {
                        const wanted = new Set(labels.map(normalizeLabel));
                        const nodes = Array.from(container.querySelectorAll ? container.querySelectorAll('span, div, p, dt, dd, label') : []);
                        for (const node of nodes) {
                          const nodeText = elementText(node);
                          const label = normalizeLabel(nodeText);
                          if (!wanted.has(label)) {
                            const inline = pickFromText(nodeText, labels);
                            if (inline) return inline;
                            continue;
                          }

                          const next = node.nextElementSibling ? pickUsableValue(elementTextCandidates(node.nextElementSibling)) : '';
                          if (next) return next;

                          const parent = node.parentElement;
                          if (!parent) continue;
                          const children = Array.from(parent.children);
                          const nodeIndex = children.indexOf(node);
                          const siblingValue = pickSiblingValue(children, nodeIndex);
                          if (siblingValue) return siblingValue;
                        }

                        return pickFromText(elementText(container), labels);
                      };
                      const getStructuredMusicNodes = () => {
                        const rootSelectors = [
                          '#musicApp [class*="_content_"]',
                          '#musicApp [class*="_pop_"]',
                          '#musicApp [class*="_PcDetailInfo_"]'
                        ];
                        const rootsForNodes = [];
                        const seenRoots = new Set();
                        for (const selector of rootSelectors) {
                          for (const element of getElements(selector)) {
                            if (!element || seenRoots.has(element)) continue;
                            seenRoots.add(element);
                            rootsForNodes.push(element);
                          }
                        }

                        if (rootsForNodes.length <= 0) return getElements('span, div, p, dt, dd, label');

                        const nodes = [];
                        const seenNodes = new Set();
                        for (const root of rootsForNodes) {
                          for (const node of Array.from(root.querySelectorAll ? root.querySelectorAll('span, div, p, dt, dd, label') : [])) {
                            if (!node || seenNodes.has(node)) continue;
                            seenNodes.add(node);
                            nodes.push(node);
                          }
                        }
                        return nodes;
                      };
                      const pickMusicField = (labels, allowPageTextFallback = true) => {
                        const wanted = new Set(labels.map(normalizeLabel));
                        for (const container of findMusicInfoContainers()) {
                          const value = pickStructuredField(container, labels);
                          if (value && !isKnownLabel(value)) return value;
                        }

                        const nodes = getStructuredMusicNodes();
                        for (const node of nodes) {
                          const nodeText = elementText(node);
                          const label = normalizeLabel(nodeText);
                          if (!wanted.has(label)) {
                            const inline = pickFromText(nodeText, labels);
                            if (inline) return inline;
                            continue;
                          }
                          const next = node.nextElementSibling ? pickUsableValue(elementTextCandidates(node.nextElementSibling)) : '';
                          if (next) return next;
                          const parent = node.parentElement;
                          if (!parent) continue;
                          const children = Array.from(parent.children);
                          const value = pickSiblingValue(children, children.indexOf(node));
                          if (value) return value;
                        }
                        return allowPageTextFallback ? pickFromText(getAllPageText(), labels) : '';
                      };
                      const waitForMusicMetadataReady = async () => {
                        for (let attempt = 0; attempt < 6; attempt++) {
                          const detail = pickBilibiliMusicAppDetail();
                          if (detail.title || detail.artist || pickBilibiliBgmTagTitle() || findMusicInfoContainers().length > 0) return;
                          await revealMusicInfoPanels();
                          await wait(150);
                        }
                      };
                      if (shouldReadMusicMetadata) await waitForMusicMetadataReady();
                      const bilibiliMusicAppDetail = shouldReadMusicMetadata ? pickBilibiliMusicAppDetail() : { title: '', artist: '', album: '' };
                      const structuredMusicTitle = shouldReadMusicMetadata ? pickMusicField(musicTitleLabels, false) : '';
                      const bilibiliBgmTagTitle = shouldReadMusicMetadata ? pickBilibiliBgmTagTitle() : '';
                      const musicTitle = shouldReadMusicMetadata
                        ? bilibiliMusicAppDetail.title || structuredMusicTitle || bilibiliBgmTagTitle || pickMusicField(musicTitleLabels)
                        : '';
                      const musicArtist = shouldReadMusicMetadata ? bilibiliMusicAppDetail.artist || pickMusicField(musicArtistLabels) : '';
                      const musicAlbum = shouldReadMusicMetadata ? bilibiliMusicAppDetail.album || pickMusicField(musicAlbumLabels) : '';
                      const videos = Array.from(document.querySelectorAll('video'));
                      const states = videos.map((video, index) => {
                        const rect = video.getBoundingClientRect ? video.getBoundingClientRect() : { width: 0, height: 0 };
                        const area = Math.max(0, rect.width || 0) * Math.max(0, rect.height || 0);
                        const duration = Number.isFinite(video.duration) ? video.duration : null;
                        const currentTime = Number.isFinite(video.currentTime) ? video.currentTime : null;
                        const hasPosition = currentTime !== null;
                        const hasTimeline = hasPosition && (duration === null || duration > 0);
                        const visible = area > 0 && getComputedStyle(video).visibility !== 'hidden' && getComputedStyle(video).display !== 'none';
                        const score =
                          (hasTimeline ? 100000 : 0) +
                          (!video.paused ? 10000 : 0) +
                          (visible ? 1000 : 0) +
                          Math.min(area, 999999) / 1000 +
                          (video.readyState || 0);
                        return {
                          index,
                          score,
                          currentTime,
                          duration,
                          paused: video.paused,
                          ended: video.ended,
                          loop: video.loop,
                          playbackRate: video.playbackRate || 1,
                          readyState: video.readyState || 0,
                          area
                        };
                      }).sort((a, b) => b.score - a.score);
                      const selected = states[0] || null;
                      return JSON.stringify({
                        url: location.href,
                        title: document.title,
                        musicTitle,
                        musicArtist,
                        musicAlbum,
                        visibilityState: document.visibilityState || '',
                        hasFocus: document.hasFocus ? document.hasFocus() : false,
                        videoCount: videos.length,
                        selectedVideoIndex: selected ? selected.index : null,
                        selectedVideoScore: selected ? selected.score : 0,
                        currentTime: selected ? selected.currentTime : null,
                        duration: selected ? selected.duration : null,
                        paused: selected ? selected.paused : true,
                        ended: selected ? selected.ended : false,
                        loop: selected ? selected.loop : false,
                        playbackRate: selected ? selected.playbackRate : 1
                      });
                    })()
                    """.Replace("__READ_MUSIC_METADATA__", readMusicMetadata ? "true" : "false"),
                    returnByValue = true,
                    awaitPromise = true
                }
            });

            await SendJsonAsync(webSocket, command, cancellationToken);
            var response = await ReceiveJsonAsync(webSocket, cancellationToken);
            var payload = ExtractRuntimeValue(response);
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            var browserState = JsonSerializer.Deserialize<BrowserRuntimeVideoState>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            var normalizedUrl = BilibiliVideoUrlHelper.NormalizeVideoUrl(browserState?.Url ?? string.Empty);
            if (browserState is null || string.IsNullOrWhiteSpace(normalizedUrl))
                return null;

            return new BrowserVideoPlaybackSnapshot
            {
                Url = normalizedUrl,
                Title = browserState.Title,
                MusicTitle = browserState.MusicTitle,
                MusicArtist = browserState.MusicArtist,
                MusicAlbum = browserState.MusicAlbum,
                Position = browserState.CurrentTime is null ? null : TimeSpan.FromSeconds(Math.Max(0, browserState.CurrentTime.Value)),
                Duration = browserState.Duration is null ? null : TimeSpan.FromSeconds(Math.Max(0, browserState.Duration.Value)),
                IsPaused = browserState.Paused,
                IsEnded = browserState.Ended,
                IsLooping = browserState.Loop,
                PlaybackRate = browserState.PlaybackRate <= 0 ? 1 : browserState.PlaybackRate,
                Source = $"Chrome DevTools Protocol:{port}",
                IsPageVisible = browserState.VisibilityState.Equals("visible", StringComparison.OrdinalIgnoreCase),
                HasDocumentFocus = browserState.HasFocus,
                SelectionScore = browserState.SelectedVideoScore
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket webSocket, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string> ReceiveJsonAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return builder.ToString();
    }

    private static string? ExtractRuntimeValue(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("result", out var innerResult)
            || !innerResult.TryGetProperty("value", out var value))
        {
            return null;
        }

        return value.GetString();
    }

    private static string BuildNoSnapshotMessage(bool hasDevToolsConnection, IReadOnlyList<BrowserVideoPlaybackTabInfo> tabs)
    {
        if (!hasDevToolsConnection)
            return "未连接到 Chrome CDP 端口 9222-9225，也未从地址栏捕获到 B 站 URL";

        var pageTabs = tabs.Where(tab => tab.Type.Equals("page", StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
        if (pageTabs.Count == 0)
            return "CDP 已连接，但没有可用页面标签";

        var titles = string.Join(" / ", pageTabs.Select(tab => string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title));
        return $"CDP 已连接，但未发现 B 站视频页；当前页面：{titles}";
    }

    private static string TrimBrowserTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        return title
            .Replace(" - Google Chrome", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" - Microsoft Edge", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string FormatTime(TimeSpan value)
    {
        return value.ToString(value.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
    }

    private static double ScoreProbe(BrowserVideoPlaybackProbeResult probe, string preferredBvid, string addressBarBvid)
    {
        var snapshot = probe.Snapshot;
        if (snapshot is null)
            return 0;

        var score = 0d;
        if (snapshot.HasDocumentFocus)
            score += 5_000_000;
        if (snapshot.IsPageVisible)
            score += 3_000_000;
        if (!string.IsNullOrWhiteSpace(addressBarBvid) && IsPreferredBvid(snapshot.Url, addressBarBvid))
            score += 2_000_000;
        if (!snapshot.IsPaused)
            score += 100_000;
        if (snapshot.HasReliablePosition)
            score += 50_000;
        if (!string.IsNullOrWhiteSpace(preferredBvid) && IsPreferredBvid(snapshot.Url, preferredBvid))
            score += 10_000;

        score += Math.Min(snapshot.SelectionScore, 999_999) / 1000;
        return score;
    }

    private sealed class DevToolsTab
    {
        public string Type { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string WebSocketDebuggerUrl { get; set; } = string.Empty;
    }

    private sealed class BrowserRuntimeVideoState
    {
        public string Url { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string MusicTitle { get; set; } = string.Empty;

        public string MusicArtist { get; set; } = string.Empty;

        public string MusicAlbum { get; set; } = string.Empty;

        public string VisibilityState { get; set; } = string.Empty;

        public bool HasFocus { get; set; }

        public double SelectedVideoScore { get; set; }

        public double? CurrentTime { get; set; }

        public double? Duration { get; set; }

        public bool Paused { get; set; }

        public bool Ended { get; set; }

        public bool Loop { get; set; }

        public double PlaybackRate { get; set; } = 1;
    }
}
