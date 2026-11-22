using CommunityToolkit.Mvvm.ComponentModel;

namespace Onus.Core;

public enum AudioFormat { Mp3, Wav }

/// <summary>User settings, persisted to settings.json on every change (App wires PropertyChanged → save).
/// Defaults match SunoSync's.</summary>
public sealed partial class Settings : ObservableObject
{
    [ObservableProperty] private string _downloadFolder = AppPaths.DefaultDownloads;
    [ObservableProperty] private AudioFormat _format = AudioFormat.Mp3;
    [ObservableProperty] private bool _embedMetadata = true;
    [ObservableProperty] private bool _saveLyricsFile = true;
    [ObservableProperty] private bool _saveArtFiles = true;
    [ObservableProperty] private bool _organizeByMonth;
    [ObservableProperty] private bool _playlistSubfolder;
    [ObservableProperty] private bool _stemsInTrackFolder;
    [ObservableProperty] private bool _smartResume = true;
    [ObservableProperty] private double _downloadDelaySeconds = 0.5;
    [ObservableProperty] private int _parallelDownloads = 3;
    [ObservableProperty] private int _maxPages;
    [ObservableProperty] private int _startPage = 1;
    [ObservableProperty] private double _volume = 0.8;
    /// <summary>Optional manual bearer token. Clerk tokens live ~60 s, so this is a last resort.</summary>
    [ObservableProperty] private string _manualToken = "";
    [ObservableProperty] private ClipFilter _filter = new();
    [ObservableProperty] private bool _importOffered;

    /// <summary>Smart Resume stops a scan after this many pages in a row with nothing new,
    /// scaled to library size (SunoSync's thresholds).</summary>
    public static int SmartResumePages(int librarySize) => librarySize switch
    {
        < 100 => 2,
        < 1000 => 5,
        < 5000 => 10,
        _ => 20,
    };
}
