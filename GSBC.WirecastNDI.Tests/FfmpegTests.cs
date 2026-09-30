using GSBC.WirecastNDI.Capture;
using GSBC.WirecastNDI.Configuration;

namespace GSBC.WirecastNDI.Tests;

public class FfmpegTests
{
    [Fact]
    public void ParsesCurrentDshowFormat()
    {
        const string output = """
            [dshow @ 000001e4a4f0c540] "Integrated Camera" (video)
            [dshow @ 000001e4a4f0c540]   Alternative name "@device_pnp_\\?\usb#vid_04f2&pid_b6be&mi_00#6&1a2b&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global"
            [dshow @ 000001e4a4f0c540] "Wirecast Virtual Camera" (video)
            [dshow @ 000001e4a4f0c540]   Alternative name "@device_sw_{860BB310-5D01-11D0-BD3B-00A0C911CE86}\{8E14549A-DB61-4309-AFA1-3578E927E933}"
            [dshow @ 000001e4a4f0c540] "Some Filter" (none)
            [dshow @ 000001e4a4f0c540] "Microphone (Realtek(R) Audio)" (audio)
            [dshow @ 000001e4a4f0c540]   Alternative name "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{1B4B2C4A}"
            [dshow @ 000001e4a4f0c540] "Wirecast Virtual Microphone" (audio)
            [in#0 @ 000001e4a4f0c3c0] Error opening input: Immediate exit requested
            """;

        var devices = Ffmpeg.ParseDeviceList(output);

        Assert.Equal(
            [
                new CaptureDevice("Integrated Camera", "video"),
                new CaptureDevice("Wirecast Virtual Camera", "video"),
                new CaptureDevice("Microphone (Realtek(R) Audio)", "audio"),
                new CaptureDevice("Wirecast Virtual Microphone", "audio"),
            ],
            devices);
    }

    [Fact]
    public void ParsesLegacySectionedDshowFormat()
    {
        const string output = """
            [dshow @ 0000000000343ea0] DirectShow video devices (some may be both video and audio devices)
            [dshow @ 0000000000343ea0]  "Wirecast Virtual Camera"
            [dshow @ 0000000000343ea0]     Alternative name "@device_sw_{860BB310}\{8E14549A}"
            [dshow @ 0000000000343ea0] DirectShow audio devices
            [dshow @ 0000000000343ea0]  "Wirecast Virtual Microphone"
            [dshow @ 0000000000343ea0]     Alternative name "@device_cm_{33D9A762}\Wirecast"
            """;

        var devices = Ffmpeg.ParseDeviceList(output);

        Assert.Equal(
            [new CaptureDevice("Wirecast Virtual Camera", "video"), new CaptureDevice("Wirecast Virtual Microphone", "audio")],
            devices);
    }

    [Fact]
    public void ParsesAvFoundationFormat()
    {
        const string output = """
            [AVFoundation indev @ 0x7f8] AVFoundation video devices:
            [AVFoundation indev @ 0x7f8] [0] FaceTime HD Camera
            [AVFoundation indev @ 0x7f8] [1] Capture screen 0
            [AVFoundation indev @ 0x7f8] AVFoundation audio devices:
            [AVFoundation indev @ 0x7f8] [0] MacBook Pro Microphone
            """;

        var devices = Ffmpeg.ParseDeviceList(output);

        Assert.Equal(
            [
                new CaptureDevice("FaceTime HD Camera", "video"),
                new CaptureDevice("Capture screen 0", "video"),
                new CaptureDevice("MacBook Pro Microphone", "audio"),
            ],
            devices);
    }

    [Theory]
    [InlineData("-video_size 1920x1080 -framerate 30", new[] { "-video_size", "1920x1080", "-framerate", "30" })]
    [InlineData("""-i "video=Wirecast Virtual Camera" -f lavfi""", new[] { "-i", "video=Wirecast Virtual Camera", "-f", "lavfi" })]
    [InlineData("""-i "" """, new[] { "-i", "" })]
    [InlineData("   ", new string[0])]
    public void SplitsArgs(string input, string[] expected) => Assert.Equal(expected, Ffmpeg.SplitArgs(input));

    [Theory]
    [InlineData("30", 30, 1)]
    [InlineData("60", 60, 1)]
    [InlineData("25", 25, 1)]
    [InlineData("29.97", 30000, 1001)]
    [InlineData("59.94", 60000, 1001)]
    [InlineData("23.976", 24000, 1001)]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData(" 50 ", 50, 1)]
    public void ParsesFrameRate(string value, int n, int d) =>
        Assert.Equal((n, d), new VideoConfig { FrameRate = value }.ParseFrameRate());
}
