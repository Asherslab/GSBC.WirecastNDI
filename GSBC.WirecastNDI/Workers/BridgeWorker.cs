using GSBC.WirecastNDI.Capture;
using GSBC.WirecastNDI.Configuration;
using GSBC.WirecastNDI.Ndi;
using Microsoft.Extensions.Options;

namespace GSBC.WirecastNDI.Workers;

/// <summary>
/// Owns the NDI source for the life of the process and keeps a capture session running behind it.
/// Nothing here ever gives up: if ffmpeg dies, the device is missing, or Wirecast is closed, it
/// sends black, waits, and tries again - nobody should have to touch this machine.
/// </summary>
public sealed class BridgeWorker(IOptions<BridgeConfig> options, ILogger<BridgeWorker> logger) : BackgroundService
{
    private readonly BridgeConfig _config = options.Value;
    private readonly CaptureStats _stats = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let host startup finish before doing blocking native work.
        await Task.Yield();

        NdiRuntime.Initialise(_config.Ndi.LibraryPath);
        logger.LogInformation("NDI runtime {Version} loaded from {Path}", NdiRuntime.Version, NdiRuntime.LoadedFrom);

        (int n, int d) = _config.Video.ParseFrameRate();
        using var sender = new NdiSender(_config.Ndi.SourceName, _config.Ndi.Groups,
            _config.Video.Width, _config.Video.Height, n, d);

        logger.LogInformation("NDI source '{Name}' is up ({Width}x{Height} @ {N}/{D})",
            _config.Ndi.SourceName, _config.Video.Width, _config.Video.Height, n, d);

        using var statsCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task statsTask = LogStatsAsync(sender, statsCts.Token);

        int consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime started = DateTime.UtcNow;
            string reason;

            try
            {
                var session = new CaptureSession(_config, sender, _stats, logger);
                reason = await session.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            // A session that ran for a while was healthy; start counting again.
            consecutiveFailures = DateTime.UtcNow - started > TimeSpan.FromMinutes(1) ? 1 : consecutiveFailures + 1;

            // Back off gently when something is persistently wrong so the log stays readable.
            int delay = Math.Min(_config.Capture.RestartDelaySeconds * consecutiveFailures, 60);
            logger.LogWarning("Capture stopped: {Reason}. Retrying in {Delay}s", reason, delay);

            await IdleAsync(sender, TimeSpan.FromSeconds(delay), stoppingToken);
        }

        await statsCts.CancelAsync();
        await statsTask;
        logger.LogInformation("NDI source '{Name}' shutting down", _config.Ndi.SourceName);
    }

    private async Task IdleAsync(NdiSender sender, TimeSpan duration, CancellationToken token)
    {
        try
        {
            if (!_config.Ndi.SendBlackWhenIdle)
            {
                await Task.Delay(duration, token);
                return;
            }

            // A few fps of black is enough for receivers to show black instead of a frozen frame.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            DateTime until = DateTime.UtcNow + duration;
            do
            {
                sender.SendBlack();
            } while (DateTime.UtcNow < until && await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LogStatsAsync(NdiSender sender, CancellationToken token)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(10, _config.Capture.StatsIntervalSeconds));
        long lastFrames = _stats.VideoFrames;
        long lastSamples = _stats.AudioSamples;

        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(token))
            {
                long frames = _stats.VideoFrames;
                long samples = _stats.AudioSamples;

                logger.LogInformation(
                    "Status: {Fps:0.00} fps, audio {AudioRate:0} Hz, {Receivers} NDI receiver(s) connected",
                    (frames - lastFrames) / interval.TotalSeconds,
                    (samples - lastSamples) / interval.TotalSeconds,
                    sender.Connections);

                lastFrames = frames;
                lastSamples = samples;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
