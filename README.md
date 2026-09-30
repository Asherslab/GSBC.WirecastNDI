# GSBC.WirecastNDI

Publishes Wirecast's program output to the network as an **NDI source**, without Wirecast Pro.

Wirecast's own NDI output only comes with **Wirecast Pro**. **Wirecast Studio** includes the
**Virtual Camera** and **Virtual Microphone** outputs. This app captures those two devices and
re-publishes them as NDI with the free NDI SDK. It runs hidden in the background on the Wirecast PC
and starts at logon. There is no window and nothing for volunteers to click.

```
Wirecast ──► Wirecast Virtual Camera ─┐                                   ┌──► NDI source
         └─► Wirecast Virtual Mic ────┴─► ffmpeg (DirectShow) ──► pipes ──┤    "PC-NAME (Wirecast Program)"
                                           scale/fps → UYVY         │     └──► black while capture is down
                                           resample  → f32 PCM      └── GSBC.WirecastNDI (NDI SDK)
```

## How it behaves

- **One NDI source for the life of the process.** If Wirecast is closed, ffmpeg crashes, or the
  capture stalls, receivers see **black**. They don't see a frozen frame or lose the source.
  The app retries every 5 s, backing off to at most 60 s.
- **Audio and video share one DirectShow clock.** A single ffmpeg process captures both, and the
  audio is resampled continuously (`aresample=async`) so it doesn't drift.
- **Fixed output format.** Whatever resolution Wirecast outputs, the camera is asked for the
  configured size, then the picture is scaled to the configured size and frame rate, using the BT.709 colour that NDI receivers expect for HD.
- **Stall watchdog.** If no video frame arrives for 10 s, ffmpeg is killed and restarted.
- **Self-healing startup.** A scheduled task starts the app at every logon and checks every 5
  minutes that it is still running. A mutex stops a second copy from starting.
- **Logs.** Daily logs in `C:\ProgramData\GSBC.WirecastNDI\logs`, kept for 30 days. A status line
  (fps, audio rate, receiver count) is written every 5 minutes.

## Requirements (Wirecast PC)

| What | Where |
|---|---|
| Windows 10/11 x64 | |
| Wirecast **Studio** (or any edition with Virtual Camera + Virtual Microphone) | |
| NDI 6 Runtime (or NDI Tools, which includes it) | https://ndi.link/NDIRedistV6 |
| ffmpeg (any recent build with DirectShow) | `winget install Gyan.FFmpeg`, or [gyan.dev essentials](https://www.gyan.dev/ffmpeg/builds/) |

.NET does **not** need to be installed, because the release is self-contained.

## Install

1. On the Wirecast PC, install the NDI Runtime and ffmpeg (see above).
2. Download the latest `GSBC.WirecastNDI-x.y.z-win-x64.zip` from
   [Releases](https://github.com/Asherslab/GSBC.WirecastNDI/releases/latest).
3. Unzip it and double-click **`Install.cmd`**, then accept the admin prompt.
   Optionally, drop `ffmpeg.exe` into the unzipped folder first, or run
   `install.ps1 -FfmpegExe C:\path\to\ffmpeg.exe`.
4. In Wirecast, turn on **Output → Virtual Camera** and **Virtual Microphone**. Make sure they are
   on in the saved Wirecast document, so they come up when the volunteers open it.

The installer does the following:

- copies the app, and `ffmpeg.exe`, to `C:\Program Files\GSBC.WirecastNDI`
- keeps any existing `appsettings.json`
- adds inbound firewall rules for the app on all profiles, so nobody is shown a firewall prompt
- registers the hidden **"GSBC WirecastNDI"** scheduled task for any user who logs in
- starts the task

To upgrade, run `Install.cmd` from a newer zip. To remove, run
`C:\Program Files\GSBC.WirecastNDI\uninstall.ps1` as admin.

> **Why not a Windows Service?** Services run in session 0. Wirecast's virtual devices are fed
> from the logged-in user's session, so a service can't see them. For a PC that nobody touches,
> set Windows to log in automatically (for example with Sysinternals *Autologon*). Also launch
> Wirecast with its document at logon.

## Verify

Run these from PowerShell on the Wirecast PC:

```powershell
cd 'C:\Program Files\GSBC.WirecastNDI'
.\GSBC.WirecastNDI.exe list-devices | Out-Host        # the Wirecast devices should be listed
.\GSBC.WirecastNDI.exe probe | Out-Host               # lists NDI sources on the network
.\GSBC.WirecastNDI.exe probe "Wirecast Program" --seconds 5 | Out-Host
```

`probe` receives from the source and prints the resolution, the declared and measured fps, and the
average picture brightness (16 = black). It also prints the audio sample rate and peak level. You
can run it on any machine that has the NDI runtime. NDI Studio Monitor also works.

## Configuration

`C:\Program Files\GSBC.WirecastNDI\appsettings.json`. After editing it, restart the app: run
`Stop-ScheduledTask "GSBC WirecastNDI"; Start-ScheduledTask "GSBC WirecastNDI"`, or reboot.

| Setting | Default | Notes |
|---|---|---|
| `Ndi.SourceName` | `Wirecast Program` | Receivers see `PC-NAME (Wirecast Program)` |
| `Ndi.Groups` | *(empty = public)* | Comma-separated NDI groups |
| `Ndi.SendBlackWhenIdle` | `true` | Black instead of a frozen frame while capture is down |
| `Ndi.LibraryPath` | | Only if the NDI runtime is somewhere unusual |
| `Video.Width` / `Video.Height` | `1920` / `1080` | Output size. See `Video.ScaleMode` for how the input is fitted |
| `Video.FrameRate` | `30` | `25`, `29.97`, `30`, `50`, `59.94`, `60`, or exact `N/D` |
| `Video.ScaleMode` | `Stretch` | `Stretch` fills the frame. `Fit` keeps the input's shape and adds black bars. A virtual camera's frame shape often doesn't match its picture, so Stretch is usually right |
| `Audio.Enabled` | `true` | |
| `Audio.SampleRate` / `Audio.Channels` | `48000` / `2` | |
| `Audio.DelayMs` | `0` | Delays audio if it arrives ahead of the picture (lip-sync) |
| `Capture.VideoDevice` | `Wirecast Virtual Camera` | If not found exactly, the first device containing "Wirecast" is used |
| `Capture.AudioDevice` | `Wirecast Virtual Microphone` | Same fallback. If none is found, the app runs video-only |
| `Capture.RequestVideoSize` | `true` | Asks the camera for `Video.Width`×`Video.Height` instead of its default format. Falls back automatically if the camera refuses |
| `Capture.InputOptions` | | Extra DirectShow options, e.g. `-video_size 1920x1080 -framerate 30` |
| `Capture.AudioBufferMs` | `40` | DirectShow audio buffer. ffmpeg's default of about 500 ms adds latency |
| `Capture.FfmpegPath` | `ffmpeg` | Found next to the exe first, then on PATH |
| `Capture.RestartDelaySeconds` | `5` | |
| `Capture.StallTimeoutSeconds` | `10` | |
| `Capture.StatsIntervalSeconds` | `300` | |
| `Logging.Directory` | `%ProgramData%\GSBC.WirecastNDI\logs` | |

## Troubleshooting

Start with the newest file in `C:\ProgramData\GSBC.WirecastNDI\logs`.

| Symptom | Likely cause |
|---|---|
| `Video device ... not found` | Wirecast isn't installed, or its virtual camera has a different name. Run `list-devices` and set `Capture.VideoDevice` |
| `NDI runtime not found` | Install the NDI 6 Runtime |
| Source is visible but black | Wirecast's Virtual Camera output isn't turned on, or Wirecast isn't running |
| `real-time buffer too full` warnings | The PC can't keep up. Lower `Video.FrameRate` or the resolution |
| Source not visible on other machines | Different subnet or VLAN, a firewall on the receiver, or an NDI groups mismatch |
| Picture squished, with black bars left and right | The camera is delivering a 4:3 format. Check the `ffmpeg input:` line in the log, and make sure `Video.ScaleMode` is `Stretch` |
| Audio ahead of video | Raise `Audio.DelayMs` in steps of 20–40 ms |

To see what the Wirecast virtual camera itself offers:
`ffmpeg -f dshow -list_options true -i video="Wirecast Virtual Camera"`

## Development

Only the DirectShow capture is Windows-specific. Everything else, including the ffmpeg-to-NDI
pipeline, runs on macOS against a test pattern. Copy `appsettings.Local.example.json` to
`appsettings.Local.json` in the project folder, set `Ndi.LibraryPath` if needed, and then:

```bash
dotnet run --project GSBC.WirecastNDI                       # publishes "WirecastNDI Test"
dotnet run --project GSBC.WirecastNDI -- probe "WirecastNDI Test"
dotnet test
./publish.sh                                                # local Windows zip in artifacts/
```

### Releasing

Push a version tag (`git tag v1.2.0 && git push origin v1.2.0`), or run the **Release** workflow
from the Actions tab. It builds on Windows, runs the tests, checks that the PowerShell scripts
parse, and attaches the zip to a GitHub release.

NDI® is a registered trademark of Vizrt NDI AB.
