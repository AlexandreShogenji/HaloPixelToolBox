using HaloPixelToolBox.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HaloPixelToolBox.Services;

/// <summary>Read a chronological device timeline without changing original DSH logs.</summary>
internal static class DshDeviceHistoryReader
{
    private const int PageSize = 100;

    public static async Task<DshDeviceHistoryPage> ReadAsync(
        IReadOnlyList<string> sessionIds, string scope, string? cursor,
        Func<string, long?, CancellationToken, Task<DshHistoryPage>> read,
        CancellationToken cancellationToken)
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (ids.Length is 0 or > 200 || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 512))
            throw new ArgumentException("音箱历史需要 1 到 200 个有效会话。");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\n" + string.Join("\n", ids))));
        var state = ReadCursor(cursor, fingerprint, ids);
        using var concurrency = new SemaphoreSlim(4);
        var tasks = ids.Where(id => !state.Sources[id].Exhausted).Select(async id =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var before = state.Sources[id].BeforeSeq;
                // Non-message events can occupy an entire page. Skip only such
                // empty pages, preserving the official exclusive sequence cursor.
                for (var attempt = 0; attempt < 16; attempt++)
                {
                    var page = await read(id, before, cancellationToken);
                    if (page.Entries.Any(entry => entry.SessionId != id))
                        throw new InvalidDataException("音箱历史包含其他来源的消息。");
                    if (page.Entries.Count > 0 || !page.HasMore)
                        return (Id: id, Page: page);
                    if (page.BeforeSeq is not { } next || next < 0 || (before is { } prior && next >= prior))
                        throw new InvalidDataException("音箱历史游标没有前进。");
                    before = next;
                }
                throw new InvalidDataException("音箱历史连续空页过多，请单独检查该会话。");
            }
            finally { concurrency.Release(); }
        }).ToArray();
        var pages = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        if (pages.Sum(source => source.Page.Entries.Sum(entry => (long)Encoding.UTF8.GetByteCount(entry.Text))) > 16 * 1024 * 1024)
            throw new InvalidDataException("音箱历史页超过读取限制，请缩小会话范围。");
        var candidates = pages.SelectMany(source => source.Page.Entries)
            .OrderByDescending(entry => entry.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(entry => entry.SessionId, StringComparer.Ordinal)
            .ThenByDescending(entry => entry.Sequence).Take(PageSize).ToArray();
        var selected = candidates.ToHashSet();
        var output = new List<DshHistoryEntry>();
        foreach (var source in pages)
        {
            var consumed = source.Page.Entries.Where(selected.Contains).ToArray();
            if (consumed.Length == 0)
            {
                if (source.Page.Entries.Count == 0)
                    state.Sources[source.Id] = new(source.Page.BeforeSeq, true);
                continue;
            }
            var firstConsumed = consumed.Min(entry => entry.Sequence);
            // Journal sequence is the source of truth even if a message carries
            // a retroactive timestamp. Consume its entire suffix to avoid gaps.
            var suffix = source.Page.Entries.Where(entry => entry.Sequence >= firstConsumed).ToArray();
            output.AddRange(suffix);
            var remainingInPage = source.Page.Entries.Any(entry => entry.Sequence < firstConsumed);
            state.Sources[source.Id] = remainingInPage
                ? new(firstConsumed, false)
                : new(source.Page.BeforeSeq, !source.Page.HasMore);
        }
        var hasMore = state.Sources.Values.Any(source => !source.Exhausted);
        var beforeCursor = hasMore ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state)) : null;
        return new(output.OrderBy(entry => entry.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(entry => entry.SessionId, StringComparer.Ordinal).ThenBy(entry => entry.Sequence).ToArray(),
            beforeCursor, hasMore, pages.Any(source => source.Page.Truncated));
    }

    private static CursorState ReadCursor(string? cursor, string fingerprint, string[] ids)
    {
        if (cursor is null)
            return new(1, fingerprint, ids.ToDictionary(id => id, _ => new SourceCursor(null, false), StringComparer.Ordinal));
        if (cursor.Length > 64 * 1024) throw new InvalidDataException("音箱历史游标过长。");
        try
        {
            var state = JsonSerializer.Deserialize<CursorState>(Convert.FromBase64String(cursor));
            if (state is null || state.Version != 1 || state.Scope != fingerprint || state.Sources is null
                || state.Sources.Count != ids.Length || ids.Any(id => !state.Sources.ContainsKey(id))
                || state.Sources.Values.Any(source => source is null || source.BeforeSeq < 0))
                throw new InvalidDataException("音箱历史来源已改变，请刷新后重新加载。");
            return state;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new InvalidDataException("音箱历史游标无效。", exception);
        }
    }

    private sealed record SourceCursor(long? BeforeSeq, bool Exhausted);
    private sealed record CursorState(int Version, string Scope, Dictionary<string, SourceCursor> Sources);
}
