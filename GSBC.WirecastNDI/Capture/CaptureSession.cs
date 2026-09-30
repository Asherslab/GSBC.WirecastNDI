using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using GSBC.WirecastNDI.Configuration;
using GSBC.WirecastNDI.Ndi;

namespace GSBC.WirecastNDI.Capture;

/// <summary>
/// One run of ffmpeg: capture the Wirecast virtual camera + microphone through DirectShow and feed
/// the raw frames into the NDI sender. Video (UYVY) and audio (f32le) come back on two named pipes
/// from a single ffmpeg process, so both share one DirectShow clock and stay in sync.
/// Ends when ffmpeg exits, the video stalls, or the token is cancelled.
/// </summary>
public sealed partial class CaptureSession(
    BridgeConfig config, NdiSender sender, CaptureStats stats, ILogger logger, bool requestVideoSize)
{
    private const int StderrHistory = 20;
    private const int StderrWarningLimit = 20;
    private static int _pipeCounter;

    private readonly Queue<string> _stderrTail = new();
    private int _stderrLogged;
    private bool _inInputSection;

    /// <summary>The device refused the -video_size we asked for; retry without it.</summary>
    public bool VideoSizeRejected { get; private set; }

    /// <summary>Runs the session and returns a human-readable reason it ended.</summary>
    public async Task<string> RunAsync(CancellationToken token)
    {
        string ffmpeg = Ffmpeg.Resolve(config.Capture.FfmpegPath);
        (IReadOnlyList<string> inputArgs, bool hasAudio) = await BuildInputAsync(ffmpeg, token);

        string pipeId = $"gsbc-wirecastndi-{Environment.ProcessId}-{Interlocked.Increment(ref _pipeCounter)}";
        (NamedPipeServerStream videoPipe, string videoUrl) = CreatePipe(pipeId + "-video", sender.FrameBytes * 2);
        (NamedPipeServerStream? audioPipe, string? audioUrl) = hasAudio
            ? CreatePipe(pipeId + "-audio", 1 << 16)
            : (null, null);

        using var _v = videoPipe;
        using var _a = audioPipe;

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in BuildArgs(inputArgs, videoUrl, audioUrl))
            psi.ArgumentList.Add(arg);

        logger.LogInformation("Starting ffmpeg: {Command}", FormatCommand(ffmpeg, psi.ArgumentList));

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) => OnStderr(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return $"could not start ffmpeg ({ffmpeg}): {ex.Message}";
        }

        process.BeginErrorReadLine();
        Task exited = process.WaitForExitAsync(CancellationToken.None);

        try
        {
            // ffmpeg opens its outputs only after the capture device is open.
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(30));

            Task connect = Task.WhenAll(
                videoPipe.WaitForConnectionAsync(connectCts.Token),
                audioPipe?.WaitForConnectionAsync(connectCts.Token) ?? Task.CompletedTask);

            if (await Task.WhenAny(connect, exited) == exited)
            {
                connectCts.Cancel();
                return $"ffmpeg exited during startup (code {process.ExitCode}){LastError()}";
            }

            await connect; // surfaces timeout / cancellation

            logger.LogInformation("Capture running: {Width}x{Height} @ {Rate} fps{Audio}",
                config.Video.Width, config.Video.Height, config.Video.FrameRate,
                hasAudio ? $", audio {config.Audio.SampleRate} Hz x{config.Audio.Channels}" : ", no audio");

            stats.ResetLastVideo();

            Task video = Task.Factory.StartNew(() => PumpVideo(videoPipe), TaskCreationOptions.LongRunning);
            Task audio = audioPipe == null
                ? Task.CompletedTask
                : Task.Factory.StartNew(() => PumpAudio(audioPipe), TaskCreationOptions.LongRunning);

            TimeSpan stallTimeout = TimeSpan.FromSeconds(Math.Max(2, config.Capture.StallTimeoutSeconds));

            while (true)
            {
                Task finished = await Task.WhenAny(video, exited, Task.Delay(1000, token));
                token.ThrowIfCancellationRequested(); // WhenAny doesn't throw for the cancelled delay

                if (finished == exited)
                    return $"ffmpeg exited (code {process.ExitCode}){LastError()}";

                if (finished == video)
                {
                    await video; // surface exceptions
                    return $"video stream ended{LastError()}";
                }

                if (stats.SinceLastVideoFrame > stallTimeout)
                    return $"no video for {stallTimeout.TotalSeconds:0}s (stalled){LastError()}";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return "stopping";
        }
        catch (OperationCanceledException)
        {
            return $"ffmpeg did not open its outputs within 30s{LastError()}";
        }
        finally
        {
            await StopProcessAsync(process, exited);
        }
    }

    private async Task<(IReadOnlyList<string> Args, bool HasAudio)> BuildInputAsync(string ffmpeg, CancellationToken token)
    {
        CaptureConfig c = config.Capture;

        if (!string.IsNullOrWhiteSpace(c.CustomInput))
            return (Ffmpeg.SplitArgs(c.CustomInput).ToList(), config.Audio.Enabled);

        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                "DirectShow capture only exists on Windows. Set WirecastNdi:Capture:CustomInput to test on this OS.");

        IReadOnlyList<CaptureDevice> devices = await Ffmpeg.ListDevicesAsync(ffmpeg, token);

        string video = PickDevice(devices, "video", c.VideoDevice)
            ?? throw new InvalidOperationException(
                $"Video device '{c.VideoDevice}' not found. Is Wirecast installed? Found: {Describe(devices, "video")}");

        string? audio = config.Audio.Enabled ? PickDevice(devices, "audio", c.AudioDevice) : null;
        if (config.Audio.Enabled && audio == null)
            logger.LogWarning("Audio device '{Device}' not found, sending video only. Found: {Devices}",
                c.AudioDevice, Describe(devices, "audio"));

        var args = new List<string> { "-f", "dshow", "-rtbufsize", "256M" };
        if (audio != null)
            args.AddRange(["-audio_buffer_size", c.AudioBufferMs.ToString()]);
        List<string> inputOptions = Ffmpeg.SplitArgs(c.InputOptions).ToList();
        // Without this DirectShow uses the device's first format, which can be a small 4:3 size.
        if (requestVideoSize && !inputOptions.Contains("-video_size"))
            args.AddRange(["-video_size", $"{config.Video.Width}x{config.Video.Height}"]);
        args.AddRange(inputOptions);
        args.AddRange(["-i", audio == null ? $"video={video}" : $"video={video}:audio={audio}"]);

        return (args, audio != null);
    }

    private string? PickDevice(IReadOnlyList<CaptureDevice> devices, string kind, string wanted)
    {
        var ofKind = devices.Where(d => d.Kind == kind).ToList();

        CaptureDevice? exact = ofKind.FirstOrDefault(d => d.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact.Name;

        CaptureDevice? wirecast = ofKind.FirstOrDefault(d => d.Name.Contains("Wirecast", StringComparison.OrdinalIgnoreCase));
        if (wirecast != null)
            logger.LogWarning("{Kind} device '{Wanted}' not found, using '{Found}' instead", kind, wanted, wirecast.Name);

        return wirecast?.Name;
    }

    private static string Describe(IReadOnlyList<CaptureDevice> devices, string kind)
    {
        string[] names = devices.Where(d => d.Kind == kind).Select(d => $"'{d.Name}'").ToArray();
        return names.Length == 0 ? "(none)" : string.Join(", ", names);
    }

    private IEnumerable<string> BuildArgs(IReadOnlyList<string> input, string videoUrl, string? audioUrl)
    {
        VideoConfig v = config.Video;
        AudioConfig a = config.Audio;
        (int n, int d) = v.ParseFrameRate();

        // -y: the named pipe already "exists", ffmpeg would otherwise refuse to overwrite it.
        // level+info: tag each line with its level so OnStderr can route it; info is needed to see
        // the input format ffmpeg negotiated with the device.
        string[] global = ["-hide_banner", "-nostdin", "-nostats", "-y", "-loglevel", "level+info"];

        // Force the configured size/rate regardless of what Wirecast outputs, converting with the
        // BT.709 matrix NDI receivers assume for HD.
        const string colour = "out_color_matrix=bt709:out_range=tv";
        string scale = v.Letterbox
            ? $"scale={v.Width}:{v.Height}:force_original_aspect_ratio=decrease:{colour}," +
              $"pad={v.Width}:{v.Height}:(ow-iw)/2:(oh-ih)/2"
            : $"scale={v.Width}:{v.Height}:{colour}";
        string vf = $"fps={n}/{d},{scale},setsar=1,format=uyvy422";

        string[] videoOut =
        [
            "-map", config.Capture.VideoMap, "-an", "-sn",
            "-vf", vf, "-c:v", "rawvideo", "-pix_fmt", "uyvy422",
            "-f", "rawvideo", "-flush_packets", "1", videoUrl,
        ];

        IEnumerable<string> audioOut = [];
        if (audioUrl != null)
        {
            string af = "aresample=async=1000";
            if (a.DelayMs > 0)
                af += $",adelay={a.DelayMs}:all=1";

            audioOut =
            [
                "-map", config.Capture.AudioMap, "-vn", "-sn",
                "-af", af, "-c:a", "pcm_f32le", "-ar", a.SampleRate.ToString(), "-ac", a.Channels.ToString(),
                "-f", "f32le", "-flush_packets", "1", audioUrl,
            ];
        }

        return global.Concat(input).Concat(videoOut).Concat(audioOut);
    }

    private void PumpVideo(Stream pipe)
    {
        byte[] frame = GC.AllocateUninitializedArray<byte>(sender.FrameBytes, pinned: true);

        while (true)
        {
            try
            {
                pipe.ReadExactly(frame);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException)
            {
                return;
            }

            sender.SendVideoUyvy(frame);
            stats.VideoFrame();
        }
    }

    private void PumpAudio(Stream pipe)
    {
        int channels = config.Audio.Channels;
        int sampleRate = config.Audio.SampleRate;
        // 10ms chunks: small enough to add no noticeable latency.
        int samplesPerChunk = sampleRate / 100;
        byte[] chunk = GC.AllocateUninitializedArray<byte>(samplesPerChunk * channels * sizeof(float), pinned: true);

        while (true)
        {
            try
            {
                pipe.ReadExactly(chunk);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException)
            {
                return;
            }

            sender.SendAudio(MemoryMarshal.Cast<byte, float>(chunk), channels, sampleRate);
            stats.Audio(samplesPerChunk);
        }
    }

    private static (NamedPipeServerStream Server, string FfmpegUrl) CreatePipe(string name, int bufferSize)
    {
        var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, bufferSize, 0);

        // .NET "named pipes" are real named pipes on Windows but Unix domain sockets elsewhere.
        string url = OperatingSystem.IsWindows()
            ? $@"\\.\pipe\{name}"
            : "unix:" + Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name);

        return (server, url);
    }

    private async Task StopProcessAsync(Process process, Task exited)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            await Task.WhenAny(exited, Task.Delay(5000));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error stopping ffmpeg");
        }
    }

    private void OnStderr(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        // Harmless swscale notice (one per slice thread) when converting BT.601 input to BT.709.
        if (line.Contains("No accelerated colorspace conversion", StringComparison.Ordinal))
            return;

        Match m = LevelTagRegex().Match(line);
        string level = m.Success ? m.Groups["level"].Value : "info";
        if (m.Success)
            line = line.Remove(m.Index, m.Length);

        if (level is "info" or "verbose" or "debug" or "trace")
        {
            LogInfoLine(line);
            return;
        }

        if (line.Contains("Could not set video options", StringComparison.OrdinalIgnoreCase))
            VideoSizeRejected = true;

        lock (_stderrTail)
        {
            _stderrTail.Enqueue(line);
            while (_stderrTail.Count > StderrHistory)
                _stderrTail.Dequeue();
        }

        // Keep a noisy device (e.g. repeated "buffer too full") from filling the log.
        int count = Interlocked.Increment(ref _stderrLogged);
        if (count <= StderrWarningLimit)
            logger.LogWarning("ffmpeg: {Line}", line);
        else if (count == StderrWarningLimit + 1)
            logger.LogWarning("ffmpeg: further output for this session logged at Debug level");
        else
            logger.LogDebug("ffmpeg: {Line}", line);
    }

    /// <summary>Shows the negotiated input format (size, pixel format, fps) at Information; the rest at Debug.</summary>
    private void LogInfoLine(string line)
    {
        string text = line.Trim();
        if (text.StartsWith("Input #", StringComparison.Ordinal))
            _inInputSection = true;
        else if (text.StartsWith("Output #", StringComparison.Ordinal) || text.StartsWith("Stream mapping", StringComparison.Ordinal))
            _inInputSection = false;

        if (_inInputSection && text.StartsWith("Stream #", StringComparison.Ordinal))
            logger.LogInformation("ffmpeg input: {Line}", text);
        else
            logger.LogDebug("ffmpeg: {Line}", text);
    }

    [GeneratedRegex(@"\[(?<level>panic|fatal|error|warning|info|verbose|debug|trace)\] ")]
    private static partial Regex LevelTagRegex();

    private string LastError()
    {
        lock (_stderrTail)
            return _stderrTail.Count == 0 ? "" : $" - last ffmpeg output: {_stderrTail.Last()}";
    }

    private static string FormatCommand(string exe, IEnumerable<string> args) =>
        string.Join(' ', new[] { exe }.Concat(args).Select(a => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a));
}
