using NAudio.MediaFoundation;
using NAudio.Wave;
using Onus.Core;

namespace Onus.Services;

/// <summary>
/// While the analyzer listens to a stream-only song (suno.com's player), records the web source from 0:00 to the
/// end into <see cref="AnalyticsCache"/>. Only an uninterrupted listen counts: a seek, a lost buffer or leaving the
/// song early discards it. Pauses are fine (nothing is written while paused). Driven from the UI thread each frame.
/// ponytail: pause/resume edges may trim or pad a few ms (play state arrives ~250 ms apart); fine for analysis.
/// </summary>
public sealed class AnalyticsRecorder(AudioTap tap, Func<string> downloadFolder, Func<string, string, Task>? tag = null) : IDisposable
{
    private readonly List<float> _buf = [];
    private string? _id;          // song currently considered (recording or not)
    private string? _target;      // final .m4a path while recording
    private WaveFileWriter? _wav;
    private string _wavPath = "";
    private long _cursor = -1, _frames;
    private double _lastPos, _lastTime, _duration;
    private bool _settled;        // this song is cached (or not cacheable): stop trying to start a capture

    /// <summary>Clip id + saved path, raised on the UI thread's synchronization context.</summary>
    public event Action<string, string>? Saved;

    public bool IsRecording => _wav is not null;

    /// <summary>"REC 42 %" while capturing, else null.</summary>
    public string? State { get; private set; }

    public void Tick(string? clipId, bool viaSuno, bool playing, double position, double duration, double now)
    {
        var id = viaSuno ? clipId : null;
        if (id != _id)
        {
            Finish();
            _id = id;
            _settled = false;
        }
        // Not only on the switch itself: the old song's page keeps reporting its position until the new one loads,
        // and a dropped capture (seek, Previous) starts over once the song is back at 0:00.
        if (_wav is null && !_settled && id is not null && position < 1) Begin(id, now);
        _buf.Clear();
        var intact = tap.ReadNew(AudioTap.Web, ref _cursor, _buf);
        if (_wav is null) { State = null; return; }

        if (!intact) { Abort("audio buffer lost"); return; }
        if (playing && AnalyticsCache.IsJump(_lastPos, position, now - _lastTime)) { Abort("seeked"); return; }
        _lastPos = position;
        _lastTime = now;
        if (duration > 0) _duration = duration;
        if (playing)
        {
            var pcm = new short[_buf.Count];
            for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)(Math.Clamp(_buf[i], -1f, 1f) * short.MaxValue);
            _wav.WriteSamples(pcm, 0, pcm.Length);
            _frames += pcm.Length / 2;
        }
        var seconds = (double)_frames / AudioTap.Rate;
        State = _duration > 0 ? $"REC {Math.Min(99, seconds / _duration * 100):0} %" : "REC";
        // Done: every frame is in, or the song ended (stopped back at 0:00) with nothing meaningful missing.
        if (_duration > 0 && seconds >= _duration || !playing && position < 1 && AnalyticsCache.IsComplete(seconds, _duration)) Finish();
    }

    private void Begin(string id, double now)
    {
        var folder = downloadFolder();
        if (AnalyticsCache.PathFor(folder, id) is not { } target || File.Exists(target)) { _settled = true; return; }
        var dir = Directory.CreateDirectory(AnalyticsCache.Folder(folder));
        foreach (var stale in dir.EnumerateFiles("*.wav.part")) TryDelete(stale.FullName); // left by a crash or kill
        _target = target;
        _wavPath = target + ".wav.part";
        _wav = new WaveFileWriter(_wavPath, new WaveFormat(AudioTap.Rate, 16, 2));
        _frames = 0;
        _cursor = -1; // from now on
        _lastPos = 0;
        _lastTime = now;
        _duration = 0;
    }

    /// <summary>Keep the capture if it covers the song, else drop it.</summary>
    private void Finish()
    {
        if (_wav is null || _target is null) return;
        if (!AnalyticsCache.IsComplete((double)_frames / AudioTap.Rate, _duration)) { Abort("song left early"); return; }
        var (wavPath, target, id) = (_wavPath, _target, _id!);
        _settled = true; // encoding runs on; don't start a second capture over the same .wav.part
        _wav.Dispose();
        _wav = null;
        _target = null;
        State = null;
        var ui = SynchronizationContext.Current;
        _ = Task.Run(async () =>
        {
            try
            {
                MediaFoundationApi.Startup();
                // Media Foundation picks the container from the extension, so the temp name must end in .m4a.
                using (var reader = new WaveFileReader(wavPath))
                    MediaFoundationEncoder.EncodeToAac(reader, Encoding(target), 192_000);
                if (tag is not null)
                {
                    try { await tag(id, Encoding(target)); } // before the move: a half-tagged file never looks finished
                    catch (Exception e) when (e is IOException or HttpRequestException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or InvalidOperationException)
                    { Log.Warn($"Analytics cache: {Path.GetFileName(target)} saved without tags: {e.Message}"); }
                }
                File.Move(Encoding(target), target, overwrite: true);
                Log.Info($"Analytics cache: saved {Path.GetFileName(target)}");
                if (ui is null) Saved?.Invoke(id, target); else ui.Post(_ => Saved?.Invoke(id, target), null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                Log.Warn($"Analytics cache: could not save {Path.GetFileName(target)}: {e.Message}");
                TryDelete(Encoding(target));
            }
            finally { TryDelete(wavPath); }
        });
    }

    private void Abort(string why)
    {
        if (_wav is null) return;
        var path = _wavPath;
        _wav.Dispose();
        _wav = null;
        _target = null;
        State = null;
        TryDelete(path);
        Log.Info($"Analytics cache: capture dropped ({why}); play the song through from the start to cache it");
    }

    private static string Encoding(string target) => Path.ChangeExtension(target, ".encoding.m4a");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => Abort("analyzer closed");
}
