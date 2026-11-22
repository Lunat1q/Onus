namespace Onus.Core;

/// <summary>Where Onus keeps its state: %LocalAppData%\Onus.</summary>
public static class AppPaths
{
    public static string Root { get; } = Init(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Onus"));

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string LibraryCache => Path.Combine(Root, "library-cache.json");
    public static string Tags => Path.Combine(Root, "tags.json");
    public static string Prompts => Path.Combine(Root, "prompts.json");
    public static string Log => Path.Combine(Root, "onus.log");
    public static string WebView => Init(Path.Combine(Root, "WebView"));
    public static string ArtCache => Init(Path.Combine(Root, "art"));

    public static string DefaultDownloads =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Suno");

    private static string Init(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
