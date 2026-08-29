# CommPanel

An audio routing control panel for Windows 11. Click a lamp, and Windows switches its
default playback or recording device immediately — no Sound control panel, no digging
through a game's audio menu when you go from speakers to a headset for multiplayer.

The panel is a bank of illuminated indicator lamps, one per device. The lit lamp is the
current default. Clicking any other lamp switches to it and the light moves.

## Installing

There is no installer. Copy the `CommPanel` folder wherever you want it and run
`CommPanel.exe`. Settings are written to `CommPanel.settings.json` beside the executable,
so the whole thing stays portable — move the folder and your settings move with it. If the
folder is read-only, settings fall back to `%APPDATA%\CommPanel`.

The only thing CommPanel writes outside its folder is the optional "Start with Windows"
entry, under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`. Turning the
option off removes it.

No administrator rights are needed, at install time or at run time.

## Using it

| Action | What happens |
| --- | --- |
| Click a lamp in **OUTPUT** | That device becomes the default playback device |
| Click a lamp in **INPUT** | That device becomes the default recording device |
| Drag a fader, or scroll on it | Sets that device's volume |
| **MUTE** | Mutes that device; the lamp lights red |
| **LINK COMMS** | When lit, the communications device switches too (see below) |
| **APP MIXER** | Expands or collapses the per-application mixer |
| Right-click a device | Assigns it the Communications role only (see LINK COMMS) |
| A device goes offline | CommPanel switches you to the best device still available |
| **RESCAN** | Re-enumerates devices; normally unnecessary, it happens automatically |
| **SETTINGS** | Watched programs, which devices to show, background behaviour |
| `Ctrl` + `Alt` + `C` | Show or hide the panel from anywhere |
| Left-click the tray icon | Show or hide the panel |
| Right-click the tray icon | Switch devices straight from the menu, without opening the panel |
| `Esc`, or the `✕` button | Hides the panel to the tray — it keeps running |

Closing the window does not quit. Use **Exit** on the tray menu for that.

### LINK COMMS

Windows keeps two separate defaults: the normal default device, and a "default
communications device" that voice chat apps use. With **LINK COMMS** lit, both move
together, which is what most people want. Switch it off if you deliberately keep voice chat
on a headset while everything else plays through speakers — then the panel only moves the
normal default and leaves your chat device alone. The small blue `COMMS` lamp on a device
marks the current communications default.

### Meters and volume

Each bank has a channel strip beneath it: an LED bargraph metering whatever is currently
playing or being picked up, a volume fader, and a mute key whose lamp lights red when the
endpoint is muted.

The meter is scaled in dB rather than raw amplitude — linear peak values bunch everything
audible into the bottom of the bar — and it has proper meter ballistics: level jumps instantly
to a louder reading and falls away gradually, with a peak-hold segment that hangs at the
loudest recent level before drifting back. A meter that simply follows the raw peak reads as
noise rather than as a level.

The fader drives the same master volume as the tray slider. Drag it, scroll on it, or use the
arrow keys. It follows changes made elsewhere too — volume keys, the tray, another app — and
a drag in progress always wins, so it never fights your hand.

### The application mixer

**APP MIXER** expands a section listing every application currently playing through the
output device — what the Windows volume mixer shows, but on the same panel. Each application
gets its own row: a lamp that lights while it is actively producing audio, its own level
meter, its own volume fader, and its own mute key.

The section is collapsed by default and remembers its state. Collapsed, it costs nothing at
all: no sessions are enumerated and no COM objects are held. Expanding it grows the window,
and CommPanel nudges the window back onto the screen if the new rows would have fallen off the
bottom.

Rows are bound to applications rather than rebuilt, so an application starting or stopping
elsewhere in the list never yanks a fader out from under your hand mid-drag. Applications are
listed with the active ones first; the list refreshes about once a second, because programs
come and go far more slowly than levels change. Beyond eight applications the list stops, which
is a limit on window height rather than on the mixer.

Names come from the executable's file description where it has one — so "Mozilla Firefox"
rather than "firefox" — falling back to the executable name, and to the session's own display
name for applications that set one. Windows' own beeps appear as **System Sounds**.

Cost with the mixer open and seven applications listed: 1.56% of one core, inside the same
range the meters alone occupy. Hidden, it is 0 ms, because hiding disposes every session
object along with the rest of the metering.

#### Metering the microphone opens the microphone

A playback endpoint meters itself. A **capture** endpoint does not: `IAudioMeterInformation`
on a microphone reports nothing at all unless something is actively recording from it.
Measured on real hardware — with a signal present, the endpoint meter returned `0.00000` with
no stream open and `0.19873` for the same signal with one open. Windows' own microphone bar
behaves the same way; the Settings app opens a stream to make it move.

So metering your microphone means CommPanel has to open it, and **Windows will show its
microphone-in-use indicator for as long as the panel is on screen.** CommPanel will also
appear under Settings ▸ Privacy ▸ Microphone.

What it does with the audio: computes a peak level and discards the buffer. Nothing is
recorded, written to disk, or sent anywhere. The stream is opened when the panel becomes
visible and closed the instant it hides — verified against the Windows access log, which
showed the microphone released on the same second the panel was hidden.

Switch it off with **Meter the microphone** in Settings. The input meter then still shows a
level whenever another application is recording — a call, a game with voice chat — because in
that case the endpoint meter reports on its own.

#### Panel size

**PANEL SIZE** in Settings scales the panel from 80% to 200%. It applies as you drag, and the
window resizes itself to match, so the size can be judged by eye rather than guessed from a
number. Cancel puts it back.

Text and layout scale **together**, deliberately. Enlarging only the font would push it out of
keys that stayed the same size — clipped names, labels colliding with lamps. Enlarging
everything keeps the proportions and simply gives the larger text room to sit in: lamps,
padding, meters, faders and the window all grow with it. At 200% the panel is roughly twice
the size in each direction, and nothing is clipped.

This is independent of Windows' own display scaling, which CommPanel already follows. Use it
if the panel is small relative to everything else on your screen, or if you simply want it
easier to read across the room.

#### Bloom

How much the lamps and lit meter segments glow is adjustable, under **LAMP BLOOM** in
Settings. The slider runs from 0 (crisp segments, no glow at all) through 50% — the reference
look — to 100%, where a loud bar blurs into one continuous band of light. The meter beside the
slider is the real renderer, updating as you drag, so you are judging the actual thing rather
than a description of it.

The setting drives every light source on the panel at once — device lamps, the section
indicators, the toggle lamps and the meters — because a panel whose lights disagree about how
brightly they glow stops looking like one piece of equipment.

Bloom on the meter is drawn per colour zone rather than per LED, so a full bar costs about
three gradient fills instead of twenty-four. Its measured cost is below the run-to-run
variance of the meters themselves - see the table below.

#### Colours

Green means active, everywhere: the lit lamp on a device, and the lower part of a meter.
Amber and red are reserved for meaning — amber for something wanting attention (a device gone
offline, a headset powered down, a watched game launching) and, on the meters, for a level
approaching and then hitting the ceiling. A lamp is never amber merely to say "this is a
microphone".

#### What this costs

CommPanel otherwise never polls, so it is worth being precise about the one part that does.
Windows has no notification for audio level, so a meter has to ask. The timer therefore runs
*only while the panel is on screen*, and stops the moment it hides:

| | CPU |
| --- | --- |
| Panel visible, meters running at 30 Hz | roughly **0.5% – 2% of one core** |
| Panel hidden in the tray (the state while gaming) | **0 ms** |

The visible figure is a range rather than a number for a reason worth knowing: a meter only
repaints when a segment boundary actually moves, so its cost tracks how much the meters are
moving — which means how much sound is in the room. Measured across several runs it wandered
between 0.5% and 2% purely on ambient noise reaching the microphone. Bloom is not part of that
variance; measured with bloom off it was, if anything, marginally higher, which puts its cost
below the noise floor.

The hidden figure is not a range. It is zero, because the timer is stopped and the microphone
released.

So metering costs something only when you are looking at it, which is exactly when you are not
playing. Turn the strips off entirely with **Show level meters and volume faders** in Settings
if you would rather not have them at all.

### When a device goes offline

Turn off a headset, unplug a dongle, switch off a monitor — CommPanel notices the endpoint
disappear and moves you to the best remaining device, lighting its lamp and reporting
`OUTPUT DEVICE OFFLINE → SPEAKERS` in the status line.

Windows does a fallback of its own here, but it picks from an internal preference order that
regularly lands you on a monitor's HDMI audio. CommPanel overrides that with the device *you*
would have picked:

1. **Whatever you most recently chose** and is still available. Every click on the panel
   records that preference, so the order configures itself — no list to maintain. If you
   were on speakers before switching to the headset, powering the headset off puts you back
   on speakers.
2. **Failing that, by device type** — real speakers, headphones and headsets first, monitor
   audio last. This is what keeps a fresh install, with no history yet, from failing over
   onto a display.
3. Hidden devices are never chosen, so hiding a device in Settings also removes it as a
   failover target.

**When nothing on the panel can take over.** Hiding a device means "do not offer me this", and
CommPanel will not quietly override that — a setting that gets ignored under pressure is not a
setting. But leaving you on a dead device while a working one sits hidden is worse, so in that
one case it asks: a dialog names the device that died, lists the hidden devices that could
stand in, and switches only if you say so. It offers to unhide the device at the same time,
and asks once per outage rather than every time it re-checks.

Automatic failovers deliberately do *not* rewrite your preference order — only your own
choices do. Otherwise one accidental failover would poison the ordering that drove it.

Turn it off with **Switch to another device when the current one goes offline** in Settings.

This covers everything Windows itself reports as gone: USB headsets, dongles, jack-sensing
analogue ports, HDMI displays.

### Wireless headsets that Windows cannot see turn off

A wireless headset with a base station is a special case, and it needs its own detection.

The base station is what is plugged into USB, so its audio endpoint stays `Active` whether or
not the headset attached to it is switched on. Windows will happily keep rendering audio into
a headset that is powered down and sitting on the desk, and reports nothing at all. This was
measured, not assumed: a full off/on cycle on an Arctis Nova Pro Wireless produced **zero**
Core Audio notifications, while the base station's own vendor HID interface reported the
change immediately.

So for supported base stations CommPanel reads that HID interface directly, and feeds the
result into the same failover path as everything else. Powering the headset off switches you
away from *all* of its endpoints — the microphone is as deaf as the headphones — and powering
it back on switches you back.

#### Devices marked offline

A headset that switches itself off after a period of inactivity is the awkward case, because
it can happen while the headset is *not* the device in use. Windows carries on listing the
endpoint — the base station is still plugged in — so without help the panel goes on offering a
headset that is sitting on the desk, switched off, and picking it produces silence.

CommPanel marks such a device **OFFLINE**: sunk and desaturated, lamp dark red, no glow. It
does this whether or not the device is the selected one. If it *is* the selected one it keeps
a dull red rim, so "the device you are on is dead" still reads differently from "this device
is dead".

Selecting an offline device is still allowed — you may be about to switch the headset back on
— but the status line says `(POWERED OFF)` rather than reporting a clean switch.

**Knowing the state at launch.** These base stations report only when something changes: no
heartbeat, and when asked read-only — via `HidD_GetInputReport` or `HidD_GetFeature` — they
answer with an empty payload carrying no status. So a headset already switched off when
CommPanel starts would go unnoticed.

The only way to know is to ask, which means sending the device one command. CommPanel does
this at launch and each time the panel opens, controlled by **Ask the headset its state** in
Settings. Turn it off and the HID connection stays strictly read-only, at the cost of not
knowing about a headset that was already off.

It is a single report, sent on a handle opened and closed around the call, using the report id
the device's own descriptor declares as a valid output — not a guessed byte. Verified on an
Arctis Nova Pro Wireless: the reply is `06-B0-01-00-01-00-00-08` with the headset off and
`06-B0-01-00-01-00-06-08` with it on. Byte 6 is a battery level, so the rule is "zero means
off, 1 to 8 means on", and any other value is treated as unknown rather than guessed at.

A headset whose state has still never been established is left unmarked rather than assumed
working or broken: wrongly greying out a working device would be worse than saying nothing.

While the panel is visible the device list is also re-checked every five seconds, as a safety
net for an endpoint notification missed while it was hidden. Windows' notifications remain the
primary path, and the re-check stops with everything else when the panel hides.

#### Teaching it your headset

There is no table of supported models that could stay correct. Every SteelSeries Arctis model
uses a different USB product id, models within the same family encode their status
differently, and a firmware update has been known to change a model's product id outright and
break tools that hardcoded it.

So instead of a table, CommPanel can **learn your headset**. In Settings, click
**Learn my headset…** and it walks you through four steps:

1. Pick which output device is your headset, confirm it is switched on
2. Switch it off
3. Switch it back on
4. Switch it off once more

While you do that it listens to every vendor-specific HID interface on the machine and works
out which byte of which report changes with the power state. It takes about a minute, once.

That fourth step is what makes the result trustworthy rather than a guess. Plenty of bytes
change when a headset dies — a battery reading, for instance — and on a single off/on cycle
they look exactly like a genuine power flag. Requiring the *same* value across two separate
power-offs eliminates counters and drifting readings. Where two candidates still survive, the
one that agrees across several of the device's report kinds wins, since that is what a real
state flag looks like and a battery level does not.

Learned profiles are stored as plain JSON in `CommPanel.settings.json`, so a working profile
can be pasted to someone with the same headset instead of them repeating the exercise. A
learned profile also overrides the built-in one for the same interface — so if a firmware
update changes the report format, re-running the wizard fixes it without waiting for a new
build.

One profile ships built in: the SteelSeries Arctis Nova Pro Wireless base station
(`VID_1038 PID_12E0`, usage page `0xFF00`), verified against real hardware.

The Settings dialog says plainly whether a base station is actually detected, so the option
can never look active on hardware it cannot see.

How it behaves:

- **Read-only.** Nothing is ever written to the device, so it cannot change a headset setting.
- **Strict parsing.** A report counts only if its id and tag match, and only the two known
  status values mean anything. Anything unrecognised is ignored rather than guessed at,
  because a wrong guess would move your audio for no reason.
- **Still no polling.** The watcher blocks on an overlapped read. With no supported base
  station present it opens no handles and starts no threads at all.
- **Switching back is narrow.** It only undoes a switch CommPanel itself made, and only while
  the fallback device is still in use. Choose a device manually in the meantime and the
  pending return is cancelled — automatic behaviour never overrides a deliberate choice.

There is a second or two of lag, and it is not CommPanel's: the headset runs its own
power-down sequence before the base station notices the link has dropped and says so.
CommPanel acts within a few milliseconds of being told. Nothing in software can shorten the
part that happens before that.

Both behaviours have their own switches in Settings — **Detect a wireless headset being
powered off** and **Switch back to the headset when it is powered on again**.

### Popping open when a game launches

In **Settings**, add the executables you care about (`game.exe`, or use **Browse…**). When
one of them comes to the foreground, CommPanel appears on top of it *without taking focus*,
so you can set your routing while the game is still loading, then dismiss it with `Esc`.
Taking focus is deliberately avoided — that would minimise a fullscreen game.

It triggers once per launch, so alt-tabbing back into a game later does not keep popping
the panel open.

## Performance

The point of a tool like this is to be invisible while a game runs, so:

- **No polling.** Device changes arrive as callbacks from the Windows audio service, and
  the game-launch trigger is a passive WinEvent hook that only fires when the foreground
  window changes. Measured idle CPU is 0 ms — not "close to zero", actually zero.
- **Runs at below-normal priority**, so it never competes with a game for CPU.
- **Trims its working set when hidden**, so a backgrounded CommPanel holds very little
  physical memory (~16 MB private bytes while visible; less once hidden).
- **Paints from a cached bitmap.** The metal chassis is rendered once and blitted, so even
  moving the mouse across the panel is close to free.
- No background threads, no timers except a 200 ms de-bounce that runs only while device
  changes are actually arriving.

## Building

Requires the .NET SDK 8 or newer.

```powershell
.\build.ps1
```

That produces `dist\CommPanel` (~320 KB), which needs the
[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) on the machine
that runs it. Most Windows 11 machines already have it.

To produce a folder that runs on any Windows 10/11 x64 machine with nothing installed:

```powershell
.\build.ps1 -SelfContained
```

That one is around 150 MB because it carries the .NET runtime with it. The running
footprint is the same either way.

## How the device switching works

Windows has no public API for changing the default audio endpoint — the Sound control panel
uses an undocumented COM interface, `IPolicyConfig`, and so does every audio switcher on
Windows, including this one. Devices are enumerated through the documented MMDevice API.

The catch with an undocumented interface is that its method order is load-bearing: there are
two published vtable layouts in the wild, differing by one slot near the front. Get it wrong
and the call lands on `SetEndpointVisibility`, which *hides* a device instead of selecting
it. `src/Audio/CoreAudio.cs` documents the layout that is correct on Windows 11, the
interface is chosen by `QueryInterface` alone, and a failed call is never retried against
the other layout. Every switch is then confirmed by reading the default back, so a
misleading success code can never show up as a lit lamp.

## Layout

```
src/Audio/    Core Audio COM interop, device enumeration, the default-device switch
src/Core/     Settings, Win32 interop, the foreground watcher, startup registration
src/Ui/       The panel window, the lamp controls, the drawing theme, settings dialog
```
