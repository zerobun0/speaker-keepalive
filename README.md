# speaker-keepalive

Stops powered speakers from cutting out when Windows volume is low.

## The problem

Music plays fine at normal volume, but below roughly 20% the speakers go silent in the middle of a song.
Nudging the volume up or down, or seeking, brings the sound back for a while.

## Why it happens

Most powered speakers have an auto-standby circuit (EU ErP rules require one): if the signal at their
input stays below a threshold for a while, they switch their amplifier off. The Windows volume slider
does not turn the speakers down. It shrinks the signal *before* it leaves the PC. At 20% that is
about -24 dB, at 2% about -52 dB, so quiet music falls under the speakers' threshold and they fall
asleep. A volume change or seek causes a small transient that wakes them again, which is why nudging
the volume "fixes" it.

FxSound makes no difference here: it copies its slider onto the real output device, so the real
output is attenuated just the same.

Tools like SoundKeeper don't fix this case. They play their keep-alive signal through the same
attenuated output, so at low volume the keep-alive is just as quiet as the music (about -98 dBFS at
2%).

## What this does

`SpeakerKeepAlive.exe` is a small background program (no window, about 0% CPU) that:

- Works out which device actually drives your speakers: the Windows default output, or, when the
  default is FxSound, the device FxSound forwards to. It follows you when you switch devices.
- Plays an inaudible 20 Hz tone on that real device and **continuously compensates for the
  device's current volume**, so the tone reaches the speakers at a constant level (-32 dBFS by
  default) whether Windows is at 100% or 8%. Below that the compensation hits its safety cap and the
  level drops gradually (about -53 dBFS at 2%).
- Runs only while some app is actually producing sound, plus 5 minutes, so your speakers can still
  sleep when the PC is idle. It also pauses while the PC is locked.
- Survives device unplug/replug, format changes and sleep/resume, and keeps a log of what it did.

## Install

```powershell
git clone https://github.com/zerobun0/speaker-keepalive
cd speaker-keepalive
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

No admin rights needed. It compiles itself with the C# compiler built into Windows (.NET Framework
4.x), installs to `%LOCALAPPDATA%\SpeakerKeepAlive`, adds itself to Startup and starts. If a
SoundKeeper autostart shortcut exists, the installer disables it (and keeps a backup); `uninstall.ps1`
puts it back.

## See what it's doing

- Log: `%LOCALAPPDATA%\SpeakerKeepAlive\SpeakerKeepAlive.log`, which records the device it targets,
  on/off, volume and the tone level at the speaker every 5 minutes.
- Snapshot: `SpeakerKeepAlive.exe --diag` writes `SpeakerKeepAlive-diag.txt`, listing every
  output device, its volume in dB, the tone level that device needs, and every app's live audio peak.

## Tuning (`SpeakerKeepAlive.ini`)

| Setting | Default | Meaning |
|---|---|---|
| `TargetDbfs` | `-32` | Tone level at the speakers. Raise toward `-26` if they still sleep. |
| `MaxAmplitude` | `0.9` | Cap for very low volumes, so a sudden jump to high volume can't thump. |
| `FrequencyHz` | `20` | Tone frequency. |
| `HangSeconds` | `300` | How long to keep going after the last real sound. |
| `AlwaysOn` | `0` | `1` = ignore activity detection. |
| `PauseWhenLocked` | `1` | Stop while locked. |
| `Device` | `auto` | Or part of a device name to pin it. |

## The limit, and the bulletproof setup

Software can't push a signal past the attenuation Windows applies to the output. At 1-2% even a
full-scale tone only reaches about -52 dBFS. If your speakers still sleep at those settings, do one of these:

1. Turn off the speakers' auto-standby, if they have a switch or button combo for it (check the manual
   for "auto standby", "APD" or "ErP").
2. Fix the gain staging: keep Windows at 40-100% and turn the speakers' own volume knob down instead.
   The signal reaching the speakers stays strong, so they never think it's silence.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

## License

MIT
