using System.Text.RegularExpressions;

namespace Onus.Core;

/// <summary>Turns raw Suno titles/prompts into something readable (ported from SunoSync's utils).</summary>
public static partial class TitleText
{
    [GeneratedRegex(@"(\d+)\s*bpm", RegexOptions.IgnoreCase)]
    private static partial Regex BpmRegex();

    [GeneratedRegex("^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex UuidRegex();

    private static readonly string[] GenreSeparators = [",", "with", "featuring", "|", "-", "and"];

    public static bool IsUuid(string? s) => s is { Length: >= 30 } && UuidRegex().IsMatch(s);

    /// <summary>First few words of a style prompt: "Dark Techno, fast" → "Dark Techno". Max 20 chars.</summary>
    public static string? Genre(string? prompt)
    {
        var text = prompt?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        // ponytail: plain substring split like the original ("and" also splits "band"); good enough for a label.
        foreach (var sep in GenreSeparators)
        {
            var i = text.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) { text = text[..i].Trim(); break; }
        }
        var genre = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4));
        if (genre.Length > 20) genre = genre[..17] + "...";
        return genre.Length == 0 ? null : genre;
    }

    /// <summary>"120 bpm" anywhere in the text, if it is a plausible tempo (40–300).</summary>
    public static int? Bpm(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = BpmRegex().Match(text);
        return m.Success && int.TryParse(m.Groups[1].Value, out var bpm) && bpm is >= 40 and <= 300 ? bpm : null;
    }

    /// <summary>Clean list-like prompt artifacts ("['control: ...']") and UUID titles.</summary>
    public static string Display(string? title, string? prompt = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Untitled Track";
        var t = title.Trim();

        if (t.StartsWith('[') && (t.Contains("control:", StringComparison.OrdinalIgnoreCase)
                                  || t.Contains("verse]", StringComparison.OrdinalIgnoreCase)
                                  || t.Contains("chorus]", StringComparison.OrdinalIgnoreCase)))
        {
            var genre = Genre(t.Replace("['", "").Replace("']", "").Replace("[", "").Replace("]", ""));
            return genre is null ? "Untitled Track" : $"Untitled ({genre})";
        }

        if (t.StartsWith("['") && t.EndsWith("']") && t.Length >= 4) t = t[2..^2];
        else if (t.StartsWith('[') && t.EndsWith(']') && t.Length >= 2) t = t[1..^1];

        if (!IsUuid(t)) return t.Length == 0 ? "Untitled Track" : t;

        var words = prompt?.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Take(5).ToArray() ?? [];
        if (words.Length == 0) return "Untitled Track";
        var fromPrompt = string.Join(' ', words);
        fromPrompt = char.ToUpperInvariant(fromPrompt[0]) + fromPrompt[1..];
        return fromPrompt.Length > 50 ? fromPrompt[..47] + "..." : fromPrompt;
    }

    public static string Duration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds)) return "--:--";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
