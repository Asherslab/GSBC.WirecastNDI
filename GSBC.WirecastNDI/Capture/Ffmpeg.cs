using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GSBC.WirecastNDI.Capture;

public record CaptureDevice(string Name, string Kind);

public static partial class Ffmpeg
{
    /// <summary>Resolves a bare "ffmpeg" to a copy next to the app first, so the install is self-contained.</summary>
    public static string Resolve(string configured)
    {
        if (Path.IsPathRooted(configured))
            return configured;

        string exe = OperatingSystem.IsWindows() && !configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? configured + ".exe"
            : configured;

        string local = Path.Combine(AppContext.BaseDirectory, exe);
        return File.Exists(local) ? local : configured;
    }

    /// <summary>Lists DirectShow (Windows) or AVFoundation (macOS) capture devices.</summary>
    public static async Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync(string ffmpegPath, CancellationToken token)
    {
        string[] args = OperatingSystem.IsWindows()
            ? ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"]
            : ["-hide_banner", "-list_devices", "true", "-f", "avfoundation", "-i", ""];

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {ffmpegPath}");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        string stderr = await process.StandardError.ReadToEndAsync(token);
        await stdout;
        await process.WaitForExitAsync(token);

        return ParseDeviceList(stderr);
    }

    /// <summary>
    /// Handles both the current dshow format ("name" (video)), the older sectioned format
    /// ("DirectShow video devices" header, then "name" lines) and avfoundation ([0] name).
    /// </summary>
    public static IReadOnlyList<CaptureDevice> ParseDeviceList(string output)
    {
        var devices = new List<CaptureDevice>();
        string section = "unknown";

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.Contains("video devices", StringComparison.OrdinalIgnoreCase))
            {
                section = "video";
                continue;
            }

            if (line.Contains("audio devices", StringComparison.OrdinalIgnoreCase))
            {
                section = "audio";
                continue;
            }

            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
                continue;

            Match m = DshowDeviceRegex().Match(line);
            if (m.Success)
            {
                string kind = m.Groups["kind"].Success ? m.Groups["kind"].Value : section;
                if (kind is "video" or "audio")
                    devices.Add(new CaptureDevice(m.Groups["name"].Value, kind));
                continue;
            }

            m = AvfDeviceRegex().Match(line);
            if (m.Success && section is "video" or "audio")
                devices.Add(new CaptureDevice(m.Groups["name"].Value, section));
        }

        return devices;
    }

    /// <summary>Splits an argument string, honouring double quotes.</summary>
    public static IEnumerable<string> SplitArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            yield break;

        var current = new StringBuilder();
        bool inQuotes = false;
        bool hasToken = false;

        foreach (char c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    yield return current.ToString();
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
            yield return current.ToString();
    }

    [GeneratedRegex("""\]\s+"(?<name>[^"]+)"(?:\s+\((?<kind>video|audio|none)\))?""")]
    private static partial Regex DshowDeviceRegex();

    [GeneratedRegex(@"\]\s+\[(?<index>\d+)\]\s+(?<name>.+)$")]
    private static partial Regex AvfDeviceRegex();
}
