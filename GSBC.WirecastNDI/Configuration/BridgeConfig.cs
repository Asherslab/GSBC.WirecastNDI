using System.Globalization;

namespace GSBC.WirecastNDI.Configuration;

public class BridgeConfig
{
    public const string Section = "WirecastNdi";

    public NdiConfig Ndi { get; set; } = new();

    public VideoConfig Video { get; set; } = new();

    public AudioConfig Audio { get; set; } = new();

    public CaptureConfig Capture { get; set; } = new();
}

public class NdiConfig
{
    /// <summary>Name receivers see. NDI prefixes it with the machine name, e.g. "WIRECAST-PC (Wirecast Program)".</summary>
    public string SourceName { get; set; } = "Wirecast Program";

    /// <summary>Comma separated NDI groups. Empty = "public".</summary>
    public string? Groups { get; set; }

    /// <summary>Full path to the NDI runtime library, if it isn't in the app folder or the NDI Runtime install.</summary>
    public string? LibraryPath { get; set; }

    /// <summary>Send black while the capture is down, so receivers show black instead of a frozen frame.</summary>
    public bool SendBlackWhenIdle { get; set; } = true;
}

public class VideoConfig
{
    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    /// <summary>"30", "60", "29.97", "59.94", "25", "50" or an exact "N/D" such as "30000/1001".</summary>
    public string FrameRate { get; set; } = "30";

    public (int N, int D) ParseFrameRate()
    {
        string s = FrameRate.Trim();

        if (s.Contains('/'))
        {
            string[] parts = s.Split('/', 2);
            return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        decimal fps = decimal.Parse(s, CultureInfo.InvariantCulture);
        if (fps == decimal.Truncate(fps))
            return ((int)fps, 1);

        // NTSC-style rates (23.976, 29.97, 59.94) are N*1000/1001.
        int rounded = (int)Math.Round(fps);
        if (Math.Abs(rounded * 1000m / 1001m - fps) < 0.01m)
            return (rounded * 1000, 1001);

        return ((int)Math.Round(fps * 1000), 1000);
    }
}

public class AudioConfig
{
    public bool Enabled { get; set; } = true;

    public int SampleRate { get; set; } = 48000;

    public int Channels { get; set; } = 2;

    /// <summary>Delays audio to fix lip-sync if the audio arrives ahead of the picture.</summary>
    public int DelayMs { get; set; }
}

public class CaptureConfig
{
    /// <summary>ffmpeg executable. A bare name is looked for next to the app first, then on PATH.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>DirectShow video device. If not found, the first device containing "Wirecast" is used.</summary>
    public string VideoDevice { get; set; } = "Wirecast Virtual Camera";

    /// <summary>DirectShow audio device. If not found, the first device containing "Wirecast" is used.</summary>
    public string AudioDevice { get; set; } = "Wirecast Virtual Microphone";

    /// <summary>Extra ffmpeg input options placed before -i, e.g. "-video_size 1920x1080 -framerate 30".</summary>
    public string? InputOptions { get; set; }

    /// <summary>
    /// Replaces the whole DirectShow input (everything from -f to -i) with raw ffmpeg input args.
    /// Used for testing without Wirecast, e.g. a lavfi test pattern.
    /// </summary>
    public string? CustomInput { get; set; }

    public string VideoMap { get; set; } = "0:v:0";

    public string AudioMap { get; set; } = "0:a:0";

    /// <summary>DirectShow audio buffer in ms. ffmpeg's default (~500ms) adds a lot of audio latency.</summary>
    public int AudioBufferMs { get; set; } = 40;

    public int RestartDelaySeconds { get; set; } = 5;

    /// <summary>Restart ffmpeg if no video frame arrives for this long.</summary>
    public int StallTimeoutSeconds { get; set; } = 10;

    public int StatsIntervalSeconds { get; set; } = 300;
}
