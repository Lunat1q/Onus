using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Onus.Core;

/// <summary>Something the player can play: a local file or a Suno stream.</summary>
public sealed record PlayItem(
    string Key, string Title, string Artist, Uri Source, string? Art, string Lyrics, double Duration, string? FilePath)
{
    public bool IsStream => FilePath is null;
}

/// <summary>A local audio file in the library, as read from its tags (cached by path + mtime + size).</summary>
public sealed partial class Track : ObservableObject
{
    public required string Path { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Genre { get; init; } = "";
    public string StyleTags { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string Lyrics { get; init; } = "";
    public int? Bpm { get; init; }
    public double Duration { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string? SunoId { get; init; }
    public string? ArtPath { get; init; }

    /// <summary>keep / star / trash — same values as SunoSync's tags.json.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string? _tag;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isPlaying;

    /// <summary>Whole-song analysis (mood, key, energy, loudness), filled in the background; cached with the
    /// library, so it is redone only when the file changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoodText), nameof(KeyText), nameof(EnergyValue), nameof(ProfileTip))]
    private SongProfile? _profile;

    [JsonIgnore] public string MoodText => Profile?.Mood ?? "";
    [JsonIgnore] public string KeyText => Profile?.Key ?? "";
    /// <summary>Sort key: unanalyzed songs last.</summary>
    [JsonIgnore] public double EnergyValue => Profile?.Energy ?? -1;
    [JsonIgnore] public string? ProfileTip => Profile is not { } p ? null : p.Key.Length == 0 ? "Couldn't analyze this file (or it is silent)" :
        (p.Mood is null ? "Too few clear chords for a mood\n" : "") +
        $"Major {p.MajorShare:P0} · minor {1 - p.MajorShare:P0}\nEnergy {p.Energy:P0}\n" +
        $"Key {(p.Key.Length > 0 ? $"{p.Key} ({Math.Max(0, p.KeyScore):P0} sure)" : "—")}\n" +
        $"Chords {(p.TopChords.Length > 0 ? p.TopChords : "—")} · {p.ChangesPerMinute:0} changes/min\n" +
        $"Loudness {(p.Lufs > -70 ? $"{p.Lufs:0.0} LUFS ({p.Lufs + 14:+0.0;-0.0} LU vs. streaming)" : "—")}";

    [JsonIgnore] public string TagKey => SunoId ?? Path;
    [JsonIgnore] public string DurationText => TitleText.Duration(Duration);
    [JsonIgnore] public string BpmText => Bpm?.ToString() ?? "";

    public PlayItem ToPlayItem() =>
        new(TagKey, Title, Artist.Length > 0 ? Artist : "Unknown artist", new Uri(Path), ArtPath, Lyrics, Duration, Path);
}

public sealed class Prompt
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Tags { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
}
