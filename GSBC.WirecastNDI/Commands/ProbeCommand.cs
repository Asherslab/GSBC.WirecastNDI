using System.Diagnostics;
using System.Runtime.InteropServices;
using GSBC.WirecastNDI.Ndi;
using NewTek;

namespace GSBC.WirecastNDI.Commands;

/// <summary>
/// "probe [name] [--seconds N]": lists NDI sources on the network, then receives from the one
/// matching <c>name</c> and reports what actually arrives (resolution, fps, audio level).
/// Useful for checking the bridge end-to-end without NDI Tools.
/// </summary>
public static class ProbeCommand
{
    public static unsafe int Run(string[] args, string? libraryPath)
    {
        string? filter = args.FirstOrDefault(a => !a.StartsWith("--"));
        int seconds = 5;
        int idx = Array.IndexOf(args, "--seconds");
        if (idx >= 0 && idx + 1 < args.Length)
            seconds = int.Parse(args[idx + 1]);

        NdiRuntime.Initialise(libraryPath);
        Console.WriteLine($"NDI {NdiRuntime.Version} ({NdiRuntime.LoadedFrom})");

        var findCreate = new NDIlib.find_create_t { show_local_sources = true };
        IntPtr finder = NDIlib.find_create_v2(ref findCreate);
        try
        {
            Console.WriteLine("Looking for NDI sources...");
            var deadline = Stopwatch.StartNew();
            List<(string Name, NDIlib.source_t Source)> sources = [];
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                NDIlib.find_wait_for_sources(finder, 1000);
                sources = GetSources(finder);
                if (filter != null && sources.Any(s => Matches(s.Name, filter)))
                    break;
            }

            foreach ((string name, _) in sources)
                Console.WriteLine($"  {name}");
            if (sources.Count == 0)
                Console.WriteLine("  (none found)");

            if (filter == null)
                return 0;

            var match = sources.FirstOrDefault(s => Matches(s.Name, filter));
            if (match.Name == null)
            {
                Console.WriteLine($"No source matching '{filter}'.");
                return 2;
            }

            return Receive(match.Name, match.Source, seconds);
        }
        finally
        {
            NDIlib.find_destroy(finder);
        }
    }

    private static bool Matches(string name, string filter) => name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static List<(string, NDIlib.source_t)> GetSources(IntPtr finder)
    {
        uint count = 0;
        IntPtr p = NDIlib.find_get_current_sources(finder, ref count);
        int size = Marshal.SizeOf<NDIlib.source_t>();
        var list = new List<(string, NDIlib.source_t)>();
        for (int i = 0; i < count; i++)
        {
            var src = Marshal.PtrToStructure<NDIlib.source_t>(p + i * size);
            list.Add((Marshal.PtrToStringUTF8(src.p_ndi_name) ?? "?", src));
        }

        return list;
    }

    private static unsafe int Receive(string name, NDIlib.source_t source, int seconds)
    {
        Console.WriteLine($"Receiving from '{name}' for {seconds}s...");

        IntPtr recvName = Marshal.StringToCoTaskMemUTF8("GSBC.WirecastNDI probe");
        var create = new NDIlib.recv_create_v3_t
        {
            source_to_connect_to = source,
            color_format = NDIlib.recv_color_format_e.recv_color_format_UYVY_BGRA,
            bandwidth = NDIlib.recv_bandwidth_e.recv_bandwidth_highest,
            allow_video_fields = false,
            p_ndi_recv_name = recvName,
        };

        IntPtr recv = NDIlib.recv_create_v3(ref create);
        try
        {
            int videoFrames = 0, xres = 0, yres = 0, rateN = 0, rateD = 1;
            long audioSamples = 0;
            int sampleRate = 0, channels = 0;
            float peak = 0;
            TimeSpan? firstVideo = null, lastVideo = null;
            double lumaSum = 0;
            long lumaCount = 0;

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                var video = new NDIlib.video_frame_v2_t();
                var audio = new NDIlib.audio_frame_v2_t();
                var meta = new NDIlib.metadata_frame_t();

                switch (NDIlib.recv_capture_v2(recv, ref video, ref audio, ref meta, 500))
                {
                    case NDIlib.frame_type_e.frame_type_video:
                        videoFrames++;
                        firstVideo ??= clock.Elapsed;
                        lastVideo = clock.Elapsed;
                        (xres, yres, rateN, rateD) = (video.xres, video.yres, video.frame_rate_N, video.frame_rate_D);
                        if (video.FourCC == NDIlib.FourCC_type_e.FourCC_type_UYVY)
                        {
                            // Sample the luma of the middle row: tells black (16) from picture.
                            byte* row = (byte*)video.p_data + video.line_stride_in_bytes * (video.yres / 2);
                            for (int x = 1; x < video.xres * 2; x += 8)
                            {
                                lumaSum += row[x];
                                lumaCount++;
                            }
                        }

                        NDIlib.recv_free_video_v2(recv, ref video);
                        break;

                    case NDIlib.frame_type_e.frame_type_audio:
                        audioSamples += audio.no_samples;
                        (sampleRate, channels) = (audio.sample_rate, audio.no_channels);
                        for (int ch = 0; ch < audio.no_channels; ch++)
                        {
                            float* plane = (float*)((byte*)audio.p_data + ch * audio.channel_stride_in_bytes);
                            for (int s = 0; s < audio.no_samples; s++)
                                peak = Math.Max(peak, Math.Abs(plane[s]));
                        }

                        NDIlib.recv_free_audio_v2(recv, ref audio);
                        break;

                    case NDIlib.frame_type_e.frame_type_metadata:
                        NDIlib.recv_free_metadata(recv, ref meta);
                        break;
                }
            }

            double measuredFps = videoFrames > 1 && lastVideo > firstVideo
                ? (videoFrames - 1) / (lastVideo.Value - firstVideo!.Value).TotalSeconds
                : 0;

            Console.WriteLine($"Video: {videoFrames} frames, {xres}x{yres}, declared {rateN}/{rateD} " +
                              $"({(rateD == 0 ? 0 : (double)rateN / rateD):0.##} fps), measured {measuredFps:0.##} fps, " +
                              $"mid-row luma avg {(lumaCount == 0 ? 0 : lumaSum / lumaCount):0} (16 = black)");
            Console.WriteLine($"Audio: {audioSamples} samples, {sampleRate} Hz x{channels}, " +
                              $"peak {(peak <= 0 ? "-inf" : (20 * Math.Log10(peak)).ToString("0.0"))} dBFS");

            return videoFrames > 0 ? 0 : 3;
        }
        finally
        {
            NDIlib.recv_destroy(recv);
            Marshal.FreeCoTaskMem(recvName);
        }
    }
}
