using System.Text.Json;

namespace Onus.Core;

/// <summary>One Suno clip as Onus needs it, parsed defensively from whatever shape the API returns.</summary>
public sealed record ClipInfo
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string? AudioUrl { get; init; }
    /// <summary>Suno withheld the audio file (downloads not unlocked for this account).</summary>
    public bool AudioLocked { get; init; }
    /// <summary>The song's page on suno.com; Onus plays locked clips through that page's own player.</summary>
    public string SongPage => $"https://suno.com/song/{Uri.EscapeDataString(Id)}";
    public string? WavUrl { get; init; }
    public string? ImageUrl { get; init; }
    public string? ImageLargeUrl { get; init; }
    public string CreatedAt { get; init; } = "";
    /// <summary>Style tags, e.g. "dark synthwave, female vocals".</summary>
    public string Tags { get; init; } = "";
    /// <summary>metadata.prompt: in custom mode this holds the lyrics.</summary>
    public string Prompt { get; init; } = "";
    public string Description { get; init; } = "";
    public string Lyrics { get; init; } = "";
    public double Duration { get; init; }
    public string ClipType { get; init; } = "";
    public string TopType { get; init; } = "";
    public bool IsLiked { get; init; }
    public bool IsDisliked { get; init; }
    public bool IsTrashed { get; init; }
    public bool IsPublic { get; init; }
    public string? PersonaId { get; init; }

    public static readonly string[] StemIndicators =
    [
        "(bass)", "(drums)", "(backing vocal)", "(backing vocals)", "(vocals)", "(instrumental)",
        "(woodwinds)", "(brass)", "(fx)", "(synth)", "(strings)", "(percussion)", "(keyboard)", "(guitar)",
    ];

    public bool IsStem =>
        ClipType is "gen_stem" or "stem" || TopType.Contains("stem", StringComparison.OrdinalIgnoreCase)
        || StemIndicators.Any(i => Title.Contains(i, StringComparison.OrdinalIgnoreCase));

    /// <summary>Title with the stem suffix removed, used as the folder name that groups stems.</summary>
    public string BaseTitle => StemIndicators
        .OrderByDescending(i => i.Length) // "(backing vocals)" before "(backing vocal)"
        .Aggregate(Title, (t, i) => t.Replace(i, "", StringComparison.OrdinalIgnoreCase)).Trim();

    public string? Year => CreatedAt.Length >= 4 ? CreatedAt[..4] : null;
    public string? Month => CreatedAt.Length >= 7 ? CreatedAt[..7] : null;

    /// <summary>Parse one clip. Accepts both a bare clip and the {"clip": {...}} wrapper projects use.
    /// Returns null for "ghost" entries without an id.</summary>
    public static ClipInfo? Parse(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (item.TryGetProperty("clip", out var inner) && inner.ValueKind == JsonValueKind.Object) item = inner;

        var id = Str(item, "id");
        if (string.IsNullOrEmpty(id)) return null;

        var meta = item.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        var reaction = item.TryGetProperty("reaction", out var r) && r.ValueKind == JsonValueKind.Object
            ? Str(r, "reaction_type") : "";
        var vote = Str(item, "vote") is { Length: > 0 } v ? v : Str(meta, "vote");
        var prompt = Str(meta, "prompt");

        return new ClipInfo
        {
            Id = id,
            Title = Str(item, "title"),
            Artist = Str(item, "display_name"),
            AudioUrl = IsWithheld(Str(item, "audio_url")) ? null : NullIfEmpty(Str(item, "audio_url")),
            AudioLocked = IsWithheld(Str(item, "audio_url")),
            WavUrl = FindWavUrl(item),
            ImageUrl = NullIfEmpty(Str(item, "image_url")),
            ImageLargeUrl = NullIfEmpty(Str(item, "image_large_url")),
            CreatedAt = Str(item, "created_at"),
            Tags = Str(meta, "tags"),
            Prompt = prompt,
            Description = Str(meta, "gpt_description_prompt"),
            Lyrics = FirstNonEmpty(Str(meta, "lyrics"), Str(meta, "text"), prompt),
            Duration = Num(meta, "duration"),
            ClipType = Str(meta, "type"),
            TopType = Str(item, "type"),
            IsLiked = Bool(item, "is_liked") || reaction == "L" || vote == "up",
            IsDisliked = reaction == "D" || vote == "down",
            IsTrashed = Bool(item, "is_trashed"),
            IsPublic = Bool(item, "is_public"),
            PersonaId = NullIfEmpty(Str(meta, "persona_id")),
        };
    }

    /// <summary>Without unlocked downloads Suno puts ".../api/forbidden" in audio_url. The site then plays an
    /// encrypted stream from media_urls ("encoding" 1.0.0, decrypted in its JS). Onus doesn't decrypt it: such
    /// clips can't be downloaded and are streamed through suno.com's own player (SunoWebPlayer).</summary>
    public static bool IsWithheld(string audioUrl) => audioUrl.Contains("/api/forbidden", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pull the clip list out of any of the response shapes Suno uses
    /// (feed: bare array or {clips}; project: {project_clips}; playlist: {playlist_clips} or nested).</summary>
    public static IReadOnlyList<JsonElement> ExtractItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return [.. root.EnumerateArray()];
        if (root.ValueKind != JsonValueKind.Object) return [];
        foreach (var key in ListKeys)
            if (root.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                return [.. list.EnumerateArray()];
        foreach (var key in NestKeys)
            if (root.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Object) return ExtractItems(nested);
        return [];
    }

    // search: {"result":{"public_song":{"result":[...]}}}
    private static readonly string[] ListKeys = ["project_clips", "playlist_clips", "clips", "items", "songs", "tracks", "result"];
    private static readonly string[] NestKeys = ["playlist", "result", "public_song"];
    private static readonly string[] WavKeys = ["audio_url_wav", "wav_url", "wav_audio_url", "master_wav_url", "preview_wav_url"];

    /// <summary>Find a WAV link anywhere in the payload (known keys first, then a deep search).</summary>
    public static string? FindWavUrl(JsonElement data)
    {
        static bool IsWav(string s) => s.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                       && s.Contains(".wav", StringComparison.OrdinalIgnoreCase);
        switch (data.ValueKind)
        {
            case JsonValueKind.String:
                return data.GetString() is { } s && IsWav(s) ? s.Trim() : null;
            case JsonValueKind.Object:
                foreach (var key in WavKeys)
                    if (Str(data, key) is { Length: > 0 } k && IsWav(k)) return k;
                foreach (var p in data.EnumerateObject())
                    if (FindWavUrl(p.Value) is { } found) return found;
                return null;
            case JsonValueKind.Array:
                foreach (var e in data.EnumerateArray())
                    if (FindWavUrl(e) is { } found) return found;
                return null;
            default:
                return null;
        }
    }

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static double Num(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";
}
