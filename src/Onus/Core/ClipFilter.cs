namespace Onus.Core;

public enum ClipKind { All, Generations, Uploads }

/// <summary>What to keep from the Suno feed. Defaults mirror SunoSync's filter popup.</summary>
public sealed record ClipFilter
{
    public bool LikedOnly { get; init; }
    public bool HideDisliked { get; init; } = true;
    public bool HideStems { get; init; } = true;
    public bool StemsOnly { get; init; }
    public bool HideStudioClips { get; init; } = true;
    public bool PublicOnly { get; init; }
    public bool IncludeTrashed { get; init; }
    public ClipKind Kind { get; init; } = ClipKind.All;
    /// <summary>Comma-separated style tags; a clip must contain at least one.</summary>
    public string TagsInclude { get; init; } = "";
    /// <summary>Comma-separated style tags; a clip containing any is dropped.</summary>
    public string TagsExclude { get; init; } = "";
    public string Search { get; init; } = "";

    public bool Matches(ClipInfo c)
    {
        if (c.IsTrashed && !IncludeTrashed) return false;
        if (StemsOnly) { if (!c.IsStem) return false; }
        else if (HideStems && c.IsStem) return false;
        if (LikedOnly && !c.IsLiked) return false;
        if (HideDisliked && c.IsDisliked) return false;
        if (PublicOnly && !c.IsPublic) return false;
        if (HideStudioClips && c.ClipType == "studio_clip") return false;
        if (Kind == ClipKind.Uploads && c.ClipType != "upload") return false;
        if (Kind == ClipKind.Generations && c.ClipType == "upload") return false;

        var include = Split(TagsInclude);
        if (include.Length > 0 && !include.Any(t => c.Tags.Contains(t, StringComparison.OrdinalIgnoreCase))) return false;
        if (Split(TagsExclude).Any(t => c.Tags.Contains(t, StringComparison.OrdinalIgnoreCase))) return false;

        var q = Search.Trim();
        return q.Length == 0 || $"{c.Title} {c.Tags} {c.Prompt}".Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Split(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
