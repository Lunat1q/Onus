using System.Security.Cryptography;
using System.Text;
using Onus.Core;
using TagLib;
using TagLib.Id3v2;
using TagLib.Mpeg4;
using File = System.IO.File;
using Tag = TagLib.Id3v2.Tag;

namespace Onus.Services;

/// <summary>Reads and writes audio tags: ID3v2.3 in MP3 and in WAV's id3 chunk (same as SunoSync/mutagen);
/// iTunes atoms in M4A, with custom fields as "----:com.apple.iTunes:NAME" boxes.</summary>
public static class TagIO
{
    public const string UuidFrame = "SUNO_UUID";
    private const string ITunes = "com.apple.iTunes";
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".flac", ".m4a"];
    private static readonly string[] PromptFrames = ["prompt", "gpt_description_prompt", "description"];

    static TagIO()
    {
        Tag.DefaultVersion = 3; // v2.3: what Windows Explorer and most players read best
        Tag.ForceDefaultVersion = true;
    }

    public static Track Read(string path)
    {
        var fi = new FileInfo(path);
        string title = "", artist = "", genre = "", lyrics = "", prompt = "", comment = "";
        string? sunoId = null, embeddedArt = null;
        int? bpm = null;
        double duration = 0;
        try
        {
            using var f = TagLib.File.Create(path, ReadStyle.Average);
            var t = f.Tag;
            title = t.Title ?? "";
            artist = t.FirstPerformer ?? "";
            genre = t.FirstGenre ?? "";
            lyrics = t.Lyrics ?? "";
            comment = t.Comment ?? "";
            if (t.BeatsPerMinute > 0) bpm = (int)t.BeatsPerMinute;
            duration = f.Properties?.Duration.TotalSeconds ?? 0;
            if (f.GetTag(TagTypes.Id3v2, false) is Tag id3)
            {
                foreach (var frame in id3.GetFrames<UserTextInformationFrame>())
                {
                    var value = frame.Text.FirstOrDefault() ?? "";
                    if (frame.Description == UuidFrame) sunoId = value;
                    else if (prompt.Length == 0 && PromptFrames.Contains(frame.Description, StringComparer.OrdinalIgnoreCase))
                        prompt = value;
                }
            }
            else if (f.GetTag(TagTypes.Apple, false) is AppleTag apple)
            {
                sunoId = apple.GetDashBox(ITunes, UuidFrame);
                prompt = apple.GetDashBox(ITunes, "prompt") ?? "";
            }
            if (SidecarArt(path) is null && t.Pictures.FirstOrDefault() is { } pic) embeddedArt = CacheArt(path, pic.Data.Data);
        }
        catch (Exception e) when (e is CorruptFileException or UnsupportedFormatException or IOException)
        {
            Log.Warn($"Unreadable tags in {Path.GetFileName(path)}: {e.Message}");
        }

        if (prompt.Length == 0 && comment.Length > 20) prompt = comment; // SunoSync stored prompts in COMM
        prompt = prompt.Replace("\\n", "\n");
        if (lyrics.Trim().Length == 0 && SidecarText(path) is { } txt) lyrics = txt;
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(path).Replace('_', ' ');

        return new Track
        {
            Path = path,
            Title = TitleText.Display(title, prompt),
            Artist = artist,
            Genre = genre.Length > 0 ? TitleText.Genre(genre) ?? genre : TitleText.Genre(prompt) ?? "",
            StyleTags = genre,
            Prompt = prompt,
            Lyrics = lyrics,
            Bpm = bpm ?? TitleText.Bpm(genre) ?? TitleText.Bpm(prompt),
            Duration = duration,
            Size = fi.Length,
            Modified = fi.LastWriteTimeUtc,
            SunoId = sunoId,
            ArtPath = SidecarArt(path) ?? embeddedArt,
        };
    }

    /// <summary>Tag a freshly downloaded Suno file.</summary>
    public static void WriteSuno(string path, ClipInfo clip, byte[]? art, bool full)
    {
        using var f = TagLib.File.Create(path);
        var t = Writable(f);
        if (full)
        {
            t.Title = clip.Title.Length > 0 ? clip.Title : clip.Id;
            if (clip.Artist.Length > 0) t.Performers = [clip.Artist];
            if (clip.Tags.Length > 0) t.Genres = [clip.Tags];
            if (uint.TryParse(clip.Year, out var year)) t.Year = year;
            var comment = clip.Description.Length > 0 ? clip.Description : clip.Tags;
            if (comment.Length > 0) t.Comment = comment;
            if (clip.Prompt.Length > 0) SetCustom(t, "prompt", clip.Prompt);
            SetCustom(t, UuidFrame, clip.Id);
            if (art is { Length: > 0 })
                t.Pictures = [new Picture(new ByteVector(art))
                    { Type = PictureType.FrontCover, MimeType = "image/jpeg", Description = "Cover" }];
        }
        if (clip.Lyrics.Length > 0) t.Lyrics = clip.Lyrics;
        f.Save();
    }

    /// <summary>Metadata editor save. Empty strings clear nothing (same as SunoSync's editor).</summary>
    public static void Update(string path, string title, string artist, string genre, string bpm, string lyrics)
    {
        using var f = TagLib.File.Create(path);
        var t = Writable(f);
        if (title.Length > 0) t.Title = title;
        if (artist.Length > 0) t.Performers = [artist];
        if (genre.Length > 0) t.Genres = [genre];
        if (uint.TryParse(bpm, out var b)) t.BeatsPerMinute = b;
        if (lyrics.Length > 0) t.Lyrics = lyrics;
        f.Save();
    }

    /// <summary>ID3v2 for MP3/WAV, the iTunes atom for M4A (an MP4 file can't hold ID3).</summary>
    private static TagLib.Tag Writable(TagLib.File f) =>
        (TagLib.Tag?)(f.GetTag(TagTypes.Id3v2, true) as Tag) ?? f.GetTag(TagTypes.Apple, true) ?? f.Tag;

    private static void SetCustom(TagLib.Tag t, string name, string value)
    {
        if (t is Tag id3) UserTextInformationFrame.Get(id3, name, true).Text = [value];
        else if (t is AppleTag apple) apple.SetDashBox(ITunes, name, value);
    }

    private static string? SidecarText(string path)
    {
        var txt = Path.ChangeExtension(path, ".txt");
        try { return File.Exists(txt) ? File.ReadAllText(txt) : null; }
        catch (IOException) { return null; }
    }

    private static string? SidecarArt(string path)
    {
        var jpg = Path.ChangeExtension(path, ".jpg");
        if (File.Exists(jpg)) return jpg;
        var cover = Path.Combine(Path.GetDirectoryName(path) ?? "", "cover.jpg");
        return File.Exists(cover) ? cover : null;
    }

    private static string CacheArt(string audioPath, byte[] data)
    {
        var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(audioPath.ToLowerInvariant())));
        var target = Path.Combine(AppPaths.ArtCache, name + ".jpg");
        if (!File.Exists(target)) File.WriteAllBytes(target, data);
        return target;
    }
}
