using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Onus.Core;

namespace Onus.Services;

/// <summary>
/// Hears what Onus itself plays via WASAPI process loopback (ADR 0005). Process loopback only reaches a target's
/// direct children, and Chromium renders audio in a grandchild (Onus → msedgewebview2 browser → audio service),
/// so there are two sources: Onus (Windows media engine) and the WebView2 browser process (suno.com's player).
/// Each fills its own stereo ring; snapshots read whichever source played last. Only one plays at a time.
/// </summary>
public sealed class AudioTap : IDisposable
{
    public const int Rate = LoudnessMeter.SampleRate;
    public const int Onus = 0, Web = 1;
    private const int Ring = 1 << 17; // ~2.7 s: also the recorder's slack if the UI thread stalls
    private static readonly long StaleTicks = Stopwatch.Frequency / 4;

    private readonly List<(WasapiRecorder Rec, Source Src)> _sources = [];
    private readonly LoudnessMeter _loudness = new();
    private readonly Lock _gate = new();
    private int _clips;
    private double _maxPeak;

    private sealed class Source
    {
        public readonly float[] L = new float[Ring], R = new float[Ring];
        public long Written; // frames ever written; ring index = Written % Ring
        public long LastSound;
        public float Gain = 1;          // undoes the player volume: analysis and captures describe the song itself
        public float[] Scaled = [];
    }

    private AudioTap() { }

    /// <summary>Starts capture of Onus and, when given, the WebView2 browser tree. Throws when Windows refuses.</summary>
    public static async Task<AudioTap> StartAsync(uint? webBrowserProcessId)
    {
        var tap = new AudioTap();
        try
        {
            await tap.AddAsync((uint)Environment.ProcessId);
            if (webBrowserProcessId is { } pid) await tap.AddAsync(pid);
            return tap;
        }
        catch
        {
            tap.Dispose();
            throw;
        }
    }

    private async Task AddAsync(uint pid)
    {
        // Activation is async COM; run it off the STA so its completion callback isn't marshalled to a busy UI thread.
        var rec = await Task.Run(() => new WasapiRecorderBuilder()
            .WithProcessLoopback(pid, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2))
            .WithBufferLength(40)
            .BuildAsync());
        var src = new Source();
        rec.DataAvailable += (buffer, flags, _, _) => OnData(src, MemoryMarshal.Cast<byte, float>(buffer), flags.HasFlag(AudioClientBufferFlags.Silent));
        rec.RecordingStopped += (_, e) => { if (e.Exception is { } ex) Log.Warn($"Audio tap stopped: {ex.Message}"); };
        _sources.Add((rec, src));
        rec.StartRecording();
    }

    /// <summary>Volumes the players apply (local = Windows media engine, web = suno.com's &lt;audio&gt;). Gain is
    /// capped at +26 dB (volume 0.05): below that there's nothing meaningful left to restore.</summary>
    // ponytail: assumes both volumes are linear amplitude (HTML media volume is; MediaPlayer.Volume behaves so).
    public void SetVolumes(double local, double web)
    {
        lock (_gate)
            for (var i = 0; i < _sources.Count; i++)
                _sources[i].Src.Gain = (float)(1 / Math.Max(0.05, i == Onus ? local : web));
    }

    private void OnData(Source s, ReadOnlySpan<float> raw, bool silent)
    {
        lock (_gate)
        {
            if (s.Scaled.Length < raw.Length) s.Scaled = new float[raw.Length];
            for (var i = 0; i < raw.Length; i++) s.Scaled[i] = raw[i] * s.Gain;
            ReadOnlySpan<float> samples = s.Scaled.AsSpan(0, raw.Length);
            float peak = 0;
            var clips = 0;
            for (var i = 0; i + 1 < samples.Length; i += 2)
            {
                float l = silent ? 0 : samples[i], r = silent ? 0 : samples[i + 1];
                var idx = (int)(s.Written++ & (Ring - 1));
                s.L[idx] = l;
                s.R[idx] = r;
                var p = Math.Max(Math.Abs(l), Math.Abs(r));
                if (p >= 0.999f) clips++;
                peak = Math.Max(peak, p);
            }
            if (peak <= 1e-5f) return;
            var now = Stopwatch.GetTimestamp();
            s.LastSound = now;
            // Stats from one source only: should Windows ever route the web audio through both, count it once (web wins).
            var mine = _sources.FindIndex(x => x.Src == s);
            if (_sources.Skip(mine + 1).Any(x => now - x.Src.LastSound < StaleTicks)) return;
            _clips += clips;
            _maxPeak = Math.Max(_maxPeak, peak);
            _loudness.Process(samples);
        }
    }

    /// <summary>Copies the newest <c>l.Length</c> frames of the source that played last; zeros when nothing has for a moment.</summary>
    public void Snapshot(float[] l, float[] r)
    {
        lock (_gate)
        {
            var s = _sources.Select(x => x.Src).MaxBy(x => x.LastSound);
            if (s is null || Stopwatch.GetTimestamp() - s.LastSound > StaleTicks) { Array.Clear(l); Array.Clear(r); return; }
            var start = s.Written - l.Length;
            for (var i = 0; i < l.Length; i++)
            {
                var idx = (int)((start + i) & (Ring - 1));
                l[i] = start + i < 0 ? 0 : s.L[idx];
                r[i] = start + i < 0 ? 0 : s.R[idx];
            }
        }
    }

    /// <summary>Appends frames of <paramref name="source"/> captured since <paramref name="cursor"/> (interleaved) and
    /// advances it. A negative cursor starts at "now". False when frames were lost (cursor fell a ring behind).</summary>
    public bool ReadNew(int source, ref long cursor, List<float> sink)
    {
        lock (_gate)
        {
            if (source >= _sources.Count) return true;
            var s = _sources[source].Src;
            if (cursor < 0) cursor = s.Written;
            var ok = s.Written - cursor <= Ring;
            if (!ok) cursor = s.Written - Ring;
            for (; cursor < s.Written; cursor++)
            {
                var idx = (int)(cursor & (Ring - 1));
                sink.Add(s.L[idx]);
                sink.Add(s.R[idx]);
            }
            return ok;
        }
    }

    public TrackStats Stats()
    {
        lock (_gate) return new(_loudness.Momentary, _loudness.ShortTerm, _loudness.Integrated, _maxPeak, _clips);
    }

    /// <summary>New song: integrated loudness, max peak and clip count start over.</summary>
    public void ResetTrack()
    {
        lock (_gate) { _loudness.Reset(); _clips = 0; _maxPeak = 0; }
    }

    public void Dispose()
    {
        foreach (var (rec, _) in _sources)
        {
            try { rec.StopRecording(); } catch (COMException) { }
            rec.Dispose();
        }
        _sources.Clear();
    }
}

public readonly record struct TrackStats(double Momentary, double ShortTerm, double Integrated, double MaxPeak, int Clips);
