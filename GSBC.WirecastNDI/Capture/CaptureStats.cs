using System.Diagnostics;

namespace GSBC.WirecastNDI.Capture;

/// <summary>Counters shared between the capture threads and the periodic status log.</summary>
public sealed class CaptureStats
{
    private long _videoFrames;
    private long _audioSamples;
    private long _lastVideoTimestamp = Stopwatch.GetTimestamp();

    public long VideoFrames => Interlocked.Read(ref _videoFrames);

    public long AudioSamples => Interlocked.Read(ref _audioSamples);

    public TimeSpan SinceLastVideoFrame => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastVideoTimestamp));

    public void VideoFrame()
    {
        Interlocked.Increment(ref _videoFrames);
        Interlocked.Exchange(ref _lastVideoTimestamp, Stopwatch.GetTimestamp());
    }

    public void Audio(int samples) => Interlocked.Add(ref _audioSamples, samples);

    public void ResetLastVideo() => Interlocked.Exchange(ref _lastVideoTimestamp, Stopwatch.GetTimestamp());
}
