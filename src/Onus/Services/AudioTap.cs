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
        public readonly GainFollower Gain = new(); // undoes the player volume: analysis and captures describe the song itself
        public int Channels = 2;        // capture layout; only front L/R are kept
        public float[] Scaled = [];
    }

    internal AudioTap() { } // no sources: silent (tests)

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

    /// <summary>Channels of the default speaker layout (2 when unknown). Asking process loopback for stereo on a
    /// 5.1/7.1 device makes Windows downmix with headroom: a stereo song arrives ~13.5 dB low on 7.1 (measured).
    /// So capture in the device's own layout and keep front L/R (<see cref="FrontPair"/>).</summary>
    // ponytail: layout read once at start; switching speakers mid-session needs the analyzer reopened.
    private static int DeviceChannels()
    {
        try
        {
            using var dev = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var client = dev.CreateAudioClient();
            return Math.Max(2, client.MixFormat.Channels);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { return 2; }
    }

    private async Task AddAsync(uint pid)
    {
        var channels = DeviceChannels();
        // Activation is async COM; run it off the STA so its completion callback isn't marshalled to a busy UI thread.
        var rec = await Task.Run(() => new WasapiRecorderBuilder()
            .WithProcessLoopback(pid, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(channels == 2 ? WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2) : new WaveFormatExtensible(Rate, 32, channels))
            .WithBufferLength(40)
            .BuildAsync());
        var src = new Source { Channels = channels };
        rec.DataAvailable += (buffer, flags, _, _) => OnData(src, MemoryMarshal.Cast<byte, float>(buffer), flags.HasFlag(AudioClientBufferFlags.Silent));
        rec.RecordingStopped += (_, e) => { if (e.Exception is { } ex) Log.Warn($"Audio tap stopped: {ex.Message}"); };
        _sources.Add((rec, src));
        rec.StartRecording();
    }

    /// <summary>Quietest volume the players get (−100 dB): slider 0 plays here, inaudible but still measurable.
    /// Windows' media engine is exact down to it and floors anything lower there (measured).</summary>
    public const double MinVolume = 1e-5;

    /// <summary>Volume both players apply (Windows media engine, suno.com's &lt;audio&gt;), divided back out so the
    /// slider never changes analysis or captures. Both are linear and the capture is float, so this is exact
    /// (measured to −100 dB). Call it the moment the volume changes: a turn-up must lower the gain in time.</summary>
    public void SetVolume(double volume)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
            foreach (var (_, s) in _sources) s.Gain.Set(volume, now);
    }

    internal static float GainFor(double volume) => (float)(1 / Math.Max(MinVolume, volume));

    /// <summary>
    /// Gain that undoes the player volume. A volume change reaches the capture 40–52 ms after it is set
    /// (measured), so a lower gain applies at once and a higher one only once the louder audio still in flight has
    /// passed. A turn-down then leaves a short dip, never a ×100 000 burst in a capture.
    /// </summary>
    // ponytail: one fixed delay for both players; Chromium's path is unmeasured, so it has margin. Measure it if dips show.
    internal sealed class GainFollower
    {
        public static readonly long Delay = Stopwatch.Frequency * 3 / 20; // 150 ms
        private float _target = float.NaN; // NaN = never set: the first volume applies at once (nothing in flight)
        private long _due;

        public float Gain { get; private set; } = 1;

        public void Set(double volume, long now)
        {
            var g = GainFor(volume);
            if (g == _target) return; // re-sent every tick: only a change restarts the wait
            if (float.IsNaN(_target) || g < Gain) Gain = g;
            _target = g;
            _due = now + Delay;
        }

        public float At(long now)
        {
            if (_target > Gain && now >= _due) Gain = _target;
            return Gain;
        }
    }

    private void OnData(Source s, ReadOnlySpan<float> raw, bool silent)
    {
        lock (_gate)
        {
            var n = raw.Length / s.Channels * 2;
            if (s.Scaled.Length < n) s.Scaled = new float[n];
            FrontPair(raw, s.Channels, s.Gain.At(Stopwatch.GetTimestamp()), s.Scaled);
            ReadOnlySpan<float> samples = s.Scaled.AsSpan(0, n);
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

    /// <summary>Interleaved <paramref name="channels"/>-channel frames → interleaved front L/R × <paramref name="gain"/>.</summary>
    // ponytail: centre / surround dropped; Suno and local songs are stereo. Fold them in if multichannel sources appear.
    internal static void FrontPair(ReadOnlySpan<float> raw, int channels, float gain, Span<float> stereo)
    {
        for (int f = 0, o = 0; f + channels <= raw.Length; f += channels, o += 2)
        {
            stereo[o] = raw[f] * gain;
            stereo[o + 1] = raw[f + 1] * gain;
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
