using System.Runtime.InteropServices;
using NewTek;

namespace GSBC.WirecastNDI.Ndi;

/// <summary>
/// One long-lived NDI source. It outlives individual capture sessions so that receivers never see
/// the source disappear when Wirecast or ffmpeg restarts. Video and audio may be sent from
/// different threads (the NDI SDK allows this).
/// </summary>
public sealed class NdiSender : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameRateN;
    private readonly int _frameRateD;
    private readonly Lock _videoLock = new();
    private readonly Lock _audioLock = new();

    private IntPtr _instance;
    private IntPtr _pName;
    private IntPtr _pGroups;
    private byte[]? _black;

    public NdiSender(string name, string? groups, int width, int height, int frameRateN, int frameRateD)
    {
        _width = width;
        _height = height;
        _frameRateN = frameRateN;
        _frameRateD = frameRateD;

        _pName = Marshal.StringToCoTaskMemUTF8(name);
        if (!string.IsNullOrWhiteSpace(groups))
            _pGroups = Marshal.StringToCoTaskMemUTF8(groups);

        var create = new NDIlib.send_create_t
        {
            p_ndi_name = _pName,
            p_groups = _pGroups,
            // The capture is already real-time; letting NDI clock as well would add latency
            // whenever the two clocks drift apart.
            clock_video = false,
            clock_audio = false,
        };

        _instance = NDIlib.send_create(ref create);
        if (_instance == IntPtr.Zero)
        {
            FreeStrings();
            throw new InvalidOperationException("NDIlib_send_create failed");
        }
    }

    public int FrameBytes => _width * _height * 2;

    public int Connections => _instance == IntPtr.Zero ? 0 : NDIlib.send_get_no_connections(_instance, 0);

    /// <summary>Sends one packed UYVY 4:2:2 frame of exactly <see cref="FrameBytes"/> bytes. Blocks until NDI has consumed it.</summary>
    public unsafe void SendVideoUyvy(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameBytes)
            throw new ArgumentException($"Expected {FrameBytes} bytes, got {frame.Length}", nameof(frame));

        fixed (byte* p = frame)
        {
            var vf = new NDIlib.video_frame_v2_t
            {
                xres = _width,
                yres = _height,
                FourCC = NDIlib.FourCC_type_e.FourCC_type_UYVY,
                frame_rate_N = _frameRateN,
                frame_rate_D = _frameRateD,
                picture_aspect_ratio = (float)_width / _height,
                frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                timecode = NDIlib.send_timecode_synthesize,
                p_data = (IntPtr)p,
                line_stride_in_bytes = _width * 2,
                p_metadata = IntPtr.Zero,
                timestamp = 0,
            };

            lock (_videoLock)
            {
                if (_instance != IntPtr.Zero)
                    NDIlib.send_send_video_v2(_instance, ref vf);
            }
        }
    }

    public void SendBlack()
    {
        if (_black == null)
        {
            // UYVY black (video range): U=128 Y=16 V=128 Y=16
            _black = GC.AllocateUninitializedArray<byte>(FrameBytes);
            for (int i = 0; i < _black.Length; i += 4)
            {
                _black[i] = 0x80;
                _black[i + 1] = 0x10;
                _black[i + 2] = 0x80;
                _black[i + 3] = 0x10;
            }
        }

        SendVideoUyvy(_black);
    }

    /// <summary>Sends interleaved 32-bit float PCM.</summary>
    public unsafe void SendAudio(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        fixed (float* p = interleaved)
        {
            var af = new NDIlib.audio_frame_interleaved_32f_t
            {
                sample_rate = sampleRate,
                no_channels = channels,
                no_samples = interleaved.Length / channels,
                timecode = NDIlib.send_timecode_synthesize,
                p_data = (IntPtr)p,
            };

            lock (_audioLock)
            {
                if (_instance != IntPtr.Zero)
                    NDIlib.util_send_send_audio_interleaved_32f(_instance, ref af);
            }
        }
    }

    public void Dispose()
    {
        lock (_videoLock)
        lock (_audioLock)
        {
            if (_instance != IntPtr.Zero)
            {
                NDIlib.send_destroy(_instance);
                _instance = IntPtr.Zero;
            }
        }

        FreeStrings();
    }

    private void FreeStrings()
    {
        if (_pName != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_pName);
            _pName = IntPtr.Zero;
        }

        if (_pGroups != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_pGroups);
            _pGroups = IntPtr.Zero;
        }
    }
}
