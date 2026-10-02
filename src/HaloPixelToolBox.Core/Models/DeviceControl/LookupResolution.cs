namespace HaloPixelToolBox.Core.Models.DeviceControl;

public enum LookupMatchKind
{
    None,
    Exact,
    Alias,
    Contains,
    Fuzzy,
    Ordinal,
    Random,
    Ambiguous
}

/// <summary>
/// A named value and the alternative phrases by which a user may refer to it.
/// </summary>
public sealed record DeviceLookupCandidate<T>(
    T Value,
    string DisplayName,
    IReadOnlyList<string>? Aliases = null);

/// <summary>
/// Result of a defensive natural-language lookup. Suggestions are display names and remain
/// useful both when no match exists and when multiple candidates are equally plausible.
/// </summary>
public sealed record LookupResolution<T>(
    bool IsResolved,
    bool IsAmbiguous,
    T? Value,
    string? MatchedName,
    LookupMatchKind MatchKind,
    IReadOnlyList<string> Suggestions,
    string? Error = null)
{
    public static LookupResolution<T> Resolved(T value, string matchedName, LookupMatchKind matchKind)
        => new(true, false, value, matchedName, matchKind, []);

    public static LookupResolution<T> NotFound(IReadOnlyList<string> suggestions, string? error = null)
        => new(false, false, default, null, LookupMatchKind.None, suggestions, error);

    public static LookupResolution<T> Ambiguous(IReadOnlyList<string> suggestions, string? error = null)
        => new(false, true, default, null, LookupMatchKind.Ambiguous, suggestions, error);
}
