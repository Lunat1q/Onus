using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Threading;
using Onus.Core;
using Onus.Services;
using Onus.ViewModels;

namespace Onus;

/// <summary>Composition root: builds the services and view models by hand (no DI container needed for one window).</summary>
public partial class App
{
    private Settings? _settings;
    private PlayerViewModel? _player;
    private AnalyticsHub? _analytics;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Trim();
        Log.Info($"Onus {typeof(App).Assembly.GetName().Version} starting");
        DispatcherUnhandledException += OnUiCrash;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error($"Background task failed: {args.Exception.GetBaseException()}");
            args.SetObserved();
        };

        var settings = _settings = JsonStore.Load<Settings>(AppPaths.Settings);
        settings.PropertyChanged += (_, _) => SaveSettings();

        var ui = Dispatcher;
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Onus", "1.0"));

        var auth = new SunoAuth(ui) { ManualToken = () => settings.ManualToken };
        var suno = new SunoClient(http, auth.GetTokenAsync);
        var tags = new TagStore();
        var prompts = new PromptStore();
        var index = new LibraryIndex();

        var player = _player = new PlayerViewModel(ui, tags, settings, new SunoWebPlayer(auth));
        var library = new LibraryViewModel(index, tags, prompts, settings, player);
        var downloader = new Downloader(suno, http);
        var cloud = new CloudViewModel(suno, downloader, index, settings, player, ui);
        var analytics = _analytics = new AnalyticsHub(player, auth, settings, downloader, cloud.Find, ui);
        var main = new MainViewModel(player, library, cloud, new DashboardViewModel(library), new VaultViewModel(prompts),
            new SettingsViewModel(settings, auth, prompts, tags), auth, analytics, ui);

        var window = new MainWindow(main);
        MainWindow = window;
        window.Show();
        _ = library.RescanAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SaveSettings();
        _analytics?.Dispose(); // drops an unfinished capture before the player goes
        _player?.Dispose();
        base.OnExit(e);
    }

    private void SaveSettings()
    {
        if (_settings is null) return;
        try { JsonStore.Save(AppPaths.Settings, _settings); }
        catch (IOException ex) { Log.Warn($"Settings not saved: {ex.Message}"); }
    }

    private static void OnUiCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Unhandled: {e.Exception}");
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in {AppPaths.Log}.",
            "Onus", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // keep the app (and the music) running
    }
}
