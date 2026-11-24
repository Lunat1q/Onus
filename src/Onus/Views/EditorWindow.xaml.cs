using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Onus.Core;
using Onus.Services;
using Onus.ViewModels;
using Track = Onus.Core.Track;

namespace Onus.Views;

/// <summary>Library "Edit audio": the song decoded into memory, edited with <see cref="AudioEdit"/>, previewed through
/// WASAPI, saved over the file (original to the Recycle Bin, tags and SUNO_UUID kept) or as a copy.</summary>
public partial class EditorWindow : Window
{
    private const int UndoDepth = 8; // ponytail: whole-clip snapshots, ~90 MB each for 4 min of 48 kHz stereo; store diffs if songs get long
    private const int LatencyMs = 80;
    private static AudioClip? _clipboard; // static: copy in one song, paste into another

    private readonly Track _track;
    private readonly PlayerViewModel _player;
    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private AudioClip? _clip, _savedClip;
#pragma warning disable CS0618 // ponytail: WasapiOut is obsolete in NAudio 3 (→ WasapiPlayerBuilder) but works; move when it's removed
    private WasapiOut? _out;
#pragma warning restore CS0618
    private ClipReader? _reader;
    private bool _busy;

    private sealed record Snapshot(AudioClip Clip, int Caret, int SelStart, int SelEnd);

    /// <summary>Something was written (in place or a copy): the library rescans.</summary>
    public bool Saved { get; private set; }

    public EditorWindow(Track track, PlayerViewModel player)
    {
        InitializeComponent();
        _track = track;
        _player = player;
        TitleText.Text = track.Title;
        FileText.Text = track.Path;
        Title = $"Edit audio — {track.Title}";
        Wave.Changed += UpdateStatus;
        _timer.Tick += (_, _) => Tick();
        Loaded += async (_, _) => await LoadAsync();
    }

    private bool Dirty => _clip is not null && _clip != _savedClip;
    /// <summary>Onus writes MP3 / WAV / M4A only (a FLAC can be saved as a copy).</summary>
    private bool CanReplace => Exporter.Formats.Contains(Path.GetExtension(_track.Path).ToLowerInvariant());
    private int Frames => _clip?.Frames ?? 0;

    private async Task LoadAsync()
    {
        SetBusy(true);
        try { _clip = _savedClip = await Task.Run(() => Exporter.Decode(_track.Path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"Editor: couldn't decode {Path.GetFileName(_track.Path)}: {e.Message}");
            MessageBox.Show(this, $"Couldn't open this song for editing:\n{e.Message}", "Onus", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        LoadingText.Visibility = Visibility.Collapsed;
        Wave.Audio = _clip;
        Wave.Fit();
        if (!CanReplace) SaveButton.ToolTip = "Onus can't write this format; use Save as copy";
        SetBusy(false);
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action }) _ = Run(action);
    }

    private async Task Run(string action)
    {
        if (_clip is not { } c || _busy) return;
        int s = Wave.SelStart, e = Wave.SelEnd, caret = Wave.Caret;
        var sel = Wave.HasSelection;
        var (rs, re) = sel ? (s, e) : (0, c.Frames); // gain / normalize: selection, else the whole song
        switch (action)
        {
            case "play": TogglePlay(); break;
            case "home": Wave.Select(0); Wave.SetView(0, Wave.ViewEnd - Wave.ViewStart); break;
            case "end": Wave.Select(c.Frames); break;
            case "selectAll": Wave.Select(0, 0, c.Frames); break;
            case "clear": Wave.Select(caret); break;
            case "undo": Restore(_undo, _redo); break;
            case "redo": Restore(_redo, _undo); break;
            case "copy" when sel: _clipboard = AudioEdit.Slice(c, s, e); Status($"Copied {Time((e - s) / (double)c.SampleRate)}"); break;
            case "cut" when sel:
                _clipboard = AudioEdit.Slice(c, s, e);
                Edit(AudioEdit.Delete(c, s, e), s);
                break;
            case "paste" when _clipboard is { } piece:
                try
                {
                    var at = sel ? s : caret;
                    Edit(AudioEdit.Insert(sel ? AudioEdit.Delete(c, s, e) : c, at, piece), at, at, at + piece.Frames);
                }
                catch (ArgumentException ex) { Status(ex.Message); }
                break;
            case "delete" when sel: Edit(AudioEdit.Delete(c, s, e), s); break;
            case "trim" when sel: Edit(AudioEdit.Crop(c, s, e), 0); Wave.Fit(); break;
            case "trimStart" when caret > 0: Edit(AudioEdit.Delete(c, 0, caret), 0); break;
            case "trimEnd" when caret < c.Frames: Edit(AudioEdit.Delete(c, caret, c.Frames), caret); break;
            case "trimSilence":
                var (a, b) = AudioEdit.Audible(c);
                if (b <= a) Status("The whole song is silent");
                else if (a == 0 && b == c.Frames) Status("No silence to trim");
                else { Edit(AudioEdit.Crop(c, a, b), 0); Wave.Fit(); Status($"Trimmed {Time(a / (double)c.SampleRate)} lead-in, {Time((c.Frames - b) / (double)c.SampleRate)} tail"); }
                break;
            case "silence" when sel: Edit(AudioEdit.Silence(c, s, e), caret, s, e); break;
            case "fadeIn" when sel || caret > 0: Edit(AudioEdit.Fade(c, sel ? s : 0, sel ? e : caret, fadeIn: true), caret, s, e); break;
            case "fadeOut" when sel || caret < c.Frames: Edit(AudioEdit.Fade(c, sel ? s : caret, sel ? e : c.Frames, fadeIn: false), caret, s, e); break;
            case "louder": Edit(AudioEdit.Gain(c, rs, re, 1), caret, s, e); break;
            case "quieter": Edit(AudioEdit.Gain(c, rs, re, -1), caret, s, e); break;
            case "normalize":
                var peak = AudioEdit.Peak(c, rs, re);
                if (peak <= 1e-6) { Status("Nothing to normalize (silent)"); break; }
                Edit(AudioEdit.Normalize(c, rs, re), caret, s, e);
                Status($"Normalized to −1 dBFS ({-1 - 20 * Math.Log10(peak):+0.0;−0.0} dB)");
                break;
            case "zoomIn": Wave.Zoom(0.5); break;
            case "zoomOut": Wave.Zoom(2); break;
            case "zoomSel": Wave.ZoomToSelection(); break;
            case "fit": Wave.Fit(); break;
            case "save" when CanReplace: await SaveAsync(copy: false); break;
            case "saveCopy": await SaveAsync(copy: true); break;
            default:
                Status(action switch
                {
                    "paste" => "Nothing copied yet",
                    "trimStart" or "trimEnd" or "fadeIn" or "fadeOut" => "Move the cursor first",
                    "save" => "",
                    _ => "Select a range first (drag on the waveform)",
                });
                break;
        }
    }

    /// <summary>Apply an edit: the old clip goes on the undo stack, redo is cleared, playback stops.</summary>
    private void Edit(AudioClip next, int caret, int selStart = 0, int selEnd = 0)
    {
        StopPlayback();
        _undo.Add(new Snapshot(_clip!, Wave.Caret, Wave.SelStart, Wave.SelEnd));
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        _redo.Clear();
        Show(next, caret, selStart, selEnd);
    }

    /// <summary>Undo (from = undo, to = redo) or redo (the other way round).</summary>
    private void Restore(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0) { Status("Nothing to " + (from == _undo ? "undo" : "redo")); return; }
        StopPlayback();
        var snap = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(new Snapshot(_clip!, Wave.Caret, Wave.SelStart, Wave.SelEnd));
        Show(snap.Clip, snap.Caret, snap.SelStart, snap.SelEnd);
    }

    private void Show(AudioClip clip, int caret, int selStart, int selEnd)
    {
        _clip = clip;
        Wave.Audio = clip;
        Wave.Select(caret, selStart, selEnd);
    }

    private async Task<bool> SaveAsync(bool copy)
    {
        if (_clip is not { } clip) return false;
        StopPlayback();
        string target;
        if (copy)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                InitialDirectory = Path.GetDirectoryName(_track.Path),
                FileName = FileNames.Sanitize(_track.Title) + " (edit)",
                Filter = "MP3|*.mp3|WAV|*.wav|M4A (AAC)|*.m4a",
                FilterIndex = Math.Max(0, Array.IndexOf(Exporter.Formats, Path.GetExtension(_track.Path).ToLowerInvariant())) + 1,
                AddExtension = true,
            };
            if (dialog.ShowDialog(this) != true) return false;
            target = Exporter.Formats.Contains(Path.GetExtension(dialog.FileName).ToLowerInvariant())
                ? dialog.FileName : dialog.FileName + Exporter.Formats[dialog.FilterIndex - 1];
        }
        else
        {
            target = _track.Path;
            _player.Release(target); // Windows' player keeps the file open
        }

        SetBusy(true);
        Status(copy ? $"Saving {Path.GetFileName(target)}…" : "Saving…");
        try
        {
            // A copy never carries the SUNO_UUID (it would count as the download); the song itself keeps it.
            await Task.Run(() => Exporter.Write(_track, clip, target, keepId: !copy, copy ? null : Recycle));
            if (!copy) _savedClip = clip;
            Saved = true;
            Status(copy ? $"Saved {Path.GetFileName(target)}" : "Saved · the original is in the Recycle Bin");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or InvalidOperationException
                                   or ArgumentException or OperationCanceledException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            Log.Warn($"Editor: saving {Path.GetFileName(target)} failed: {e.Message}");
            MessageBox.Show(this, $"Couldn't save:\n{e.Message}\n\nThe original file is unchanged.", "Onus", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("");
            return false;
        }
        finally { SetBusy(false); }
    }

    private static void Recycle(string path) => FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    private void TogglePlay()
    {
        if (_out is not null) { StopPlayback(); return; }
        if (_clip is not { } c) return;
        if (_player.IsPlaying) _player.TogglePlayCommand.Execute(null);
        var (from, to) = Wave.HasSelection ? (Wave.SelStart, Wave.SelEnd) : (Wave.Caret >= c.Frames ? 0 : Wave.Caret, c.Frames);
        _reader = new ClipReader(c, from, to) { Volume = (float)_player.AppliedVolume };
#pragma warning disable CS0618
        var output = _out = new WasapiOut(AudioClientShareMode.Shared, LatencyMs);
#pragma warning restore CS0618
        output.PlaybackStopped += (_, _) => { if (_out == output) StopPlayback(); };
        try
        {
            output.Init(_reader);
            output.Play();
        }
        catch (COMException e)
        {
            StopPlayback();
            Status($"Can't play: {e.Message}");
            return;
        }
        PlayButton.Content = "";
        _timer.Start();
    }

    private void StopPlayback()
    {
        var output = _out;
        _out = null;
        _reader = null;
        output?.Dispose();
        _timer.Stop();
        Wave.Playhead = -1;
        PlayButton.Content = "";
    }

    private void Tick()
    {
        if (_reader is not { } r || _clip is not { } c) return;
        Wave.Playhead = Math.Max(0, r.Position - LatencyMs * c.SampleRate / 1000);
        Wave.Follow();
        StatusText.Text = $"▶ {Time(Wave.Playhead / (double)c.SampleRate)}  ·  {StatusLine(c)}";
    }

    /// <summary>A click while playing restarts playback from the new cursor.</summary>
    private void OnWaveClick(object sender, MouseButtonEventArgs e)
    {
        if (_out is null) return;
        StopPlayback();
        TogglePlay();
    }

    private void OnScroll(object sender, ScrollEventArgs e) => Wave.SetView(e.NewValue, e.NewValue + Wave.ViewEnd - Wave.ViewStart);

    private void UpdateStatus()
    {
        if (_clip is not { } c) return;
        var width = Wave.ViewEnd - Wave.ViewStart;
        Scroll.Maximum = Math.Max(0, c.Frames - width);
        Scroll.ViewportSize = Scroll.LargeChange = width;
        Scroll.SmallChange = width / 10;
        Scroll.Value = Wave.ViewStart;
        if (_out is null) StatusText.Text = StatusLine(c);
    }

    private string StatusLine(AudioClip c)
    {
        double r = c.SampleRate;
        var sel = Wave.HasSelection
            ? $"Selection {Time(Wave.SelStart / r)} – {Time(Wave.SelEnd / r)} ({Time((Wave.SelEnd - Wave.SelStart) / r)})"
            : $"Cursor {Time(Wave.Caret / r)}";
        var channels = c.Channels switch { 1 => "mono", 2 => "stereo", var n => $"{n} ch" };
        return $"{sel}  ·  Length {Time(c.Seconds)}  ·  {c.SampleRate / 1000.0:0.#} kHz {channels}{(Dirty ? "  ·  unsaved changes" : "")}";
    }

    private void Status(string message)
    {
        UpdateStatus();
        if (message.Length > 0) StatusText.Text = $"{message}  ·  {StatusText.Text}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Tools.IsEnabled = Wave.IsEnabled = CopyButton.IsEnabled = !busy && _clip is not null;
        SaveButton.IsEnabled = !busy && _clip is not null && CanReplace;
    }

    /// <summary>m:ss.fff (or m:ss for ruler labels a second or more apart).</summary>
    public static string Time(double seconds, bool ms = true)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ms ? $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        string? action = e.Key switch
        {
            Key.Space => "play",
            Key.Home => "home",
            Key.End => "end",
            Key.Z when ctrl && shift => "redo",
            Key.Z when ctrl => "undo",
            Key.Y when ctrl => "redo",
            Key.X when ctrl => "cut",
            Key.C when ctrl => "copy",
            Key.V when ctrl => "paste",
            Key.A when ctrl => "selectAll",
            Key.S when ctrl && shift => "saveCopy",
            Key.S when ctrl => "save",
            Key.T when ctrl => "trim",
            Key.L when ctrl => "silence",
            Key.Up when ctrl => "louder",
            Key.Down when ctrl => "quieter",
            Key.Delete or Key.Back => "delete",
            Key.OemOpenBrackets => "trimStart",
            Key.OemCloseBrackets => "trimEnd",
            Key.I when mods == 0 => "fadeIn",
            Key.O when mods == 0 => "fadeOut",
            Key.N when mods == 0 => "normalize",
            Key.Z when mods == 0 => "zoomSel",
            Key.F when mods == 0 => "fit",
            Key.OemPlus or Key.Add => "zoomIn",
            Key.OemMinus or Key.Subtract => "zoomOut",
            Key.Escape when Wave.HasSelection => "clear",
            Key.Escape => "close",
            _ => null,
        };
        if (action is null) return;
        e.Handled = true;
        if (action == "close") Close();
        else _ = Run(action);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy && _clip is not null) { e.Cancel = true; return; } // mid-save
        if (Dirty)
        {
            var answer = MessageBox.Show(this, $"Save your changes to \"{_track.Title}\"?", "Onus", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (answer == MessageBoxResult.Yes)
            {
                e.Cancel = true; // can't await inside Closing: save, then close for real
                if (!await SaveAsync(copy: !CanReplace)) return;
                _savedClip = _clip;
                Close();
                return;
            }
            _savedClip = _clip;
        }
        StopPlayback();
    }
}
