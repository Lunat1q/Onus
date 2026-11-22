using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Onus.Services;

namespace Onus.ViewModels;

public sealed record NavItem(string Title, string Glyph, ObservableObject Page);

/// <summary>Shell: navigation, toasts, account badge, lyrics and analyzer panels. Wires the pages' events together.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _toastTimer;

    public PlayerViewModel Player { get; }
    public LibraryViewModel Library { get; }
    public CloudViewModel Cloud { get; }
    public DashboardViewModel Dashboard { get; }
    public VaultViewModel Vault { get; }
    public SettingsViewModel SettingsPage { get; }
    public SunoAuth Auth { get; }
    public AnalyticsHub Analytics { get; }
    public IReadOnlyList<NavItem> Nav { get; }

    [ObservableProperty] private NavItem _selectedNav;
    [ObservableProperty] private ObservableObject _currentPage;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _lyricsVisible;
    [ObservableProperty] private bool _analyzerVisible;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _accountText = "Connecting to Suno…";

    /// <summary>The window opens the sign-in browser (a view concern).</summary>
    public event Action? SignInRequested;

    public MainViewModel(PlayerViewModel player, LibraryViewModel library, CloudViewModel cloud, DashboardViewModel dashboard,
        VaultViewModel vault, SettingsViewModel settingsPage, SunoAuth auth, AnalyticsHub analytics, Dispatcher ui)
    {
        (Player, Library, Cloud, Dashboard, Vault, SettingsPage, Auth, Analytics) = (player, library, cloud, dashboard, vault, settingsPage, auth, analytics);
        Nav =
        [
            new("Home", "", dashboard),
            new("Library", "", library),
            new("Suno", "", cloud),
            new("Prompt Vault", "", vault),
            new("Settings", "", settingsPage),
        ];
        _selectedNav = Nav[0];
        _currentPage = dashboard;
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(3.5), DispatcherPriority.Normal, (_, _) => HideToast(), ui);

        player.Problem += ShowToast;
        library.Toast += ShowToast;
        cloud.Toast += ShowToast;
        vault.Toast += ShowToast;
        settingsPage.Toast += ShowToast;
        cloud.SignInNeeded += () => { ShowToast("Sign in to Suno to continue"); SignInRequested?.Invoke(); };
        settingsPage.SignInRequested += () => SignInRequested?.Invoke();
        cloud.Downloaded += () => _ = library.RescanAsync();
        analytics.Saved += (id, path) =>
        {
            cloud.OnCached(id, path);
            ShowToast("Saved to the analytics cache: it now plays without Suno's player");
        };
        settingsPage.FolderChanged += () => _ = library.RescanAsync();
        library.Changed += () => { if (CurrentPage == dashboard) dashboard.Refresh(); };
        auth.Changed += () =>
        {
            IsSignedIn = auth.IsSignedIn;
            AccountText = auth.Status;
        };
        dashboard.Refresh();
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        CurrentPage = value.Page;
        if (value.Page == Dashboard) Dashboard.Refresh();
        if (value.Page == Vault) Vault.Refresh();
    }

    public void ShowToast(string message)
    {
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast = null;
    }

    [RelayCommand] private void ToggleLyrics() => LyricsVisible = !LyricsVisible;

    [RelayCommand] private void ToggleAnalyzer() => AnalyzerVisible = !AnalyzerVisible;

    [RelayCommand] private void SignIn() => SignInRequested?.Invoke();

    [RelayCommand] private void GoTo(string title) => SelectedNav = Nav.First(n => n.Title == title);

    /// <summary>Home's "Sync new songs" button.</summary>
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        GoTo("Suno");
        await Cloud.SyncCommand.ExecuteAsync(null);
    }
}
