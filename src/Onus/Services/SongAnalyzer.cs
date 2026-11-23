using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Onus.Core;

namespace Onus.Services;

/// <summary>Decodes a library file (Media Foundation, resampled to 48 kHz float stereo) through <see cref="SongProfiler"/>.</summary>
public static class SongAnalyzer
{
    public static SongProfile Analyze(string path, CancellationToken ct = default)
    {
        using var reader = new MediaFoundationReader(path);
        using var pcm = new MediaFoundationResampler(reader, WaveFormat.CreateIeeeFloatWaveFormat(LoudnessMeter.SampleRate, 2));
        var profiler = new SongProfiler();
        var buffer = new byte[LoudnessMeter.SampleRate * 8]; // 0.5 s
        int read;
        while ((read = pcm.Read(buffer.AsSpan())) > 0)
        {
            ct.ThrowIfCancellationRequested();
            profiler.Process(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, read - read % 8)));
        }
        return profiler.Result();
    }
}
