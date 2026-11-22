using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Onus.Core;
using Onus.Services;

namespace Onus.ViewModels;

public sealed record GenreBar(string Name, int Count, double Fraction);

/// <summary>Home: library stats, top genres and recent additions, all computed from the library index.</summary>
public sealed partial class DashboardViewModel(LibraryViewModel library) : ObservableObject
{
    [ObservableProperty] private string _trackCount = "0";
    [ObservableProperty] private string _totalTime = "0m";
    [ObservableProperty] private string _storage = "0 MB";
    [ObservableProperty] private string _topGenre = "—";
    [ObservableProperty] private string _greeting = "";

    public ObservableCollection<GenreBar> Genres { get; } = [];
    public ObservableCollection<Track> Recent { get; } = [];
    public LibraryViewModel Library => library;

    public void Refresh()
    {
        var tracks = library.Tracks.Where(t => t.Tag != "trash").ToList();
        Greeting = DateTime.Now.Hour switch { < 5 => "Late session", < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
        TrackCount = tracks.Count.ToString("N0");
        var total = TimeSpan.FromSeconds(tracks.Sum(t => t.Duration));
        TotalTime = total.TotalHours >= 1 ? $"{(int)total.TotalHours}h {total.Minutes}m" : $"{total.Minutes}m";
        var bytes = tracks.Sum(t => t.Size);
        Storage = bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes / (double)(1L << 20):0.0} MB";

        var genres = tracks.Where(t => t.Genre.Length > 0)
            .GroupBy(t => t.Genre, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Genre, Count: g.Count()))
            .OrderByDescending(g => g.Count).Take(6).ToList();
        TopGenre = genres.Count > 0 ? genres[0].Name : "—";
        Genres.Clear();
        foreach (var g in genres) Genres.Add(new GenreBar(g.Name, g.Count, (double)g.Count / genres[0].Count));

        Recent.Clear();
        foreach (var t in tracks.OrderByDescending(t => t.Modified).Take(8)) Recent.Add(t);
    }
}

/// <summary>Prompt Vault: save, tag, search and copy prompts.</summary>
public sealed partial class VaultViewModel : ObservableObject
{
    private readonly PromptStore _store;

    public ObservableCollection<Prompt> Prompts { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private Prompt? _selected;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _tags = "";
    [ObservableProperty] private string _text = "";

    public event Action<string>? Toast;

    public VaultViewModel(PromptStore store)
    {
        _store = store;
        Refresh();
    }

    public void Refresh()
    {
        var keep = Selected?.Id;
        Prompts.Clear();
        var q = Search.Trim();
        foreach (var p in _store.All.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                     .Where(p => q.Length == 0 || $"{p.Title} {p.Tags} {p.Text}".Contains(q, StringComparison.OrdinalIgnoreCase)))
            Prompts.Add(p);
        Selected = Prompts.FirstOrDefault(p => p.Id == keep);
    }

    partial void OnSearchChanged(string value) => Refresh();

    partial void OnSelectedChanged(Prompt? value)
    {
        Title = value?.Title ?? "";
        Tags = value?.Tags ?? "";
        Text = value?.Text ?? "";
    }

    [RelayCommand]
    private void New() => Selected = null;

    [RelayCommand]
    private void Save()
    {
        if (Text.Trim().Length == 0) { Toast?.Invoke("Write a prompt first"); return; }
        var p = Selected ?? new Prompt();
        var firstLine = Text.Trim().Split('\n')[0].Trim();
        p.Title = Title.Trim() is { Length: > 0 } t ? t : firstLine[..Math.Min(40, firstLine.Length)];
        p.Tags = Tags.Trim();
        p.Text = Text;
        if (Selected is null) _store.All.Add(p);
        _store.Save();
        Selected = p;
        Refresh();
        Toast?.Invoke("Prompt saved");
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is null) return;
        _store.All.Remove(Selected);
        _store.Save();
        Selected = null;
        Refresh();
    }

    [RelayCommand]
    private void Copy()
    {
        if (Text.Length == 0) return;
        Clipboard.SetText(Text);
        Toast?.Invoke("Copied — paste it into Suno");
    }
}

/// <summary>Settings page. Binds straight to <see cref="Core.Settings"/> (auto-saved) plus account actions.</summary>
public sealed partial class SettingsViewModel(Settings settings, SunoAuth auth, PromptStore prompts, TagStore tags) : ObservableObject
{
    public Settings Settings => settings;
    public SunoAuth Auth => auth;
    public AudioFormat[] Formats { get; } = Enum.GetValues<AudioFormat>();

    [ObservableProperty] private string _cookie = "";

    public event Action<string>? Toast;
    public event Action? SignInRequested;
    public event Action? FolderChanged;

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where Onus keeps your songs", InitialDirectory = settings.DownloadFolder };
        if (dialog.ShowDialog() != true) return;
        settings.DownloadFolder = dialog.FolderName;
        FolderChanged?.Invoke();
    }

    [RelayCommand] private void SignIn() => SignInRequested?.Invoke();

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await auth.SignOutAsync();
        Toast?.Invoke("Signed out of Suno");
    }

    [RelayCommand]
    private async Task ImportCookieAsync()
    {
        if (Cookie.Trim().Length < 20) { Toast?.Invoke("Paste the value of the __client cookie first"); return; }
        await auth.ImportClientCookieAsync(Cookie);
        Cookie = "";
        Toast?.Invoke(auth.IsSignedIn ? "Signed in with the cookie" : "Cookie didn't work — it may have expired");
    }

    [RelayCommand]
    private void ImportSunoSync()
    {
        var before = settings.DownloadFolder;
        Toast?.Invoke(SunoSyncImport.Run(settings, prompts, tags));
        if (before != settings.DownloadFolder) FolderChanged?.Invoke();
    }

    [RelayCommand] private static void OpenDataFolder() => Process.Start("explorer.exe", $"\"{AppPaths.Root}\"");

    [RelayCommand]
    private static void OpenLog()
    {
        if (File.Exists(AppPaths.Log)) Process.Start(new ProcessStartInfo(AppPaths.Log) { UseShellExecute = true });
    }
}
