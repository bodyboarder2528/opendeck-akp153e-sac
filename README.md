# OpenDeck AKP153E plugin that Smart App Control won't block

An [OpenDeck](https://github.com/nekename/OpenDeck) device plugin for the **AJAZZ AKP153E (rev. 2)** stream deck on Windows.

> **Made with AI.** This plugin was written by an AI (Anthropic's Claude), working for the repo owner, who directed it and tested it on his own deck. It works on that one AKP153E rev. 2. Nobody else has reviewed it, and the code has no automated tests. It's small (three files in `bin/`), so read it before you run it. Use it at your own risk.

> **Fixed in 1.3: the animation lock-up.** Older versions froze the deck after a few minutes of any sustained animation (screensaver, side-screen GIF) until it was unplugged and plugged back in. The cause turned out to be **image size**: the deck's display crashes on a steady stream of key images bigger than about 3 KB. A USB capture of the official AJAZZ app showed it never sends a key image over about 2.9 KB, and sends a "show" command after every image. Version 1.3 does the same (images are re-compressed until they fit in 3 KB). Tested on one deck: a side-screen GIF ran 16 minutes and AJAZZ's own images ran 15 minutes at about 480 images a second with no lock-up; the old version died within 3 minutes. The screensaver is still **off by default**, so turn it on yourself (`saver.minutes`).

The usual plugin, [opendeck-akp153](https://github.com/4ndv/opendeck-akp153), ships an unsigned `.exe`. With Windows 11 **Smart App Control** on, Windows can block that file (you'll see *"Part of this app has been blocked… opendeck-akp153-win.exe"*), and then the deck stays dark. Smart App Control has no per-app exceptions, and once it's off you can only turn it back on by resetting Windows.

This plugin does the same job using only programs Windows already trusts:

- **Node.js** (signed) talks to OpenDeck.
- **Windows PowerShell** (part of Windows) compiles a small C# file, `bin/bridge.cs`, that talks to the deck over USB through Windows' own `hid.dll`.

Nothing unsigned runs, so Smart App Control has nothing to block. It uses the same device namespace (`99`) and device ID (`99-<serial>`) as the original plugin, so **your existing OpenDeck pages carry over unchanged**.

## Extras

| Feature | What it does |
|---|---|
| **Full-height side screen** | The right-hand column is one tall screen (about 82 × 400 px), not three keys. The original plugin sends three 82 × 82 squares, which leaves black gaps between them. This plugin draws the column full height: the three tiles go top, middle and bottom, and the space between them is filled with a smooth blend. |
| **Animated GIF** action | Plays a GIF on a key. OpenDeck itself only ever sends a still frame. Drag *Animated GIF* onto a key and paste the file's full path into its settings. |
| **Screensaver** | After N idle minutes. Three modes (`saver.area`): `deck` = one picture or GIF across the whole deck (default: a built-in twinkling night sky); `side` = a GIF on the tall side screen only, the buttons keep their page; `both` = a GIF on the side screen plus built-in "shadow" keys (a clock and date, the word ARISE glowing, and pairs of glowing eyes). With `deck` or `both`, the first press only wakes the deck; it doesn't trigger that key. |
| **System monitor** action | CPU %, RAM % and GPU temperature (NVIDIA, via `nvidia-smi`), updated every 2 seconds. |
| **Audio output** action | Shows the current playback device; press to switch to the next one. Uses Windows' own audio API through `bin/audio.cs`. `audio.cycle` limits which devices it rotates through. |
| **Alerts** | Pings or HTTP checks on your own machines. When one fails twice in a row, the top of the side screen flashes red with its name until it's back. |
| **Night dimming** | Caps the brightness overnight. Sunday–Thursday evenings can start earlier than Friday–Saturday. |

## Requirements

- Windows 10 or 11, and OpenDeck 2.14 or later.
- [Node.js](https://nodejs.org/) **22 or later**, installed normally so `node` is on PATH. OpenDeck uses it to run the plugin, and the plugin needs Node's built-in WebSocket.
- An **AKP153E rev. 2**: USB VID `0300`, PID `3010`. To check yours, open Device Manager, find the deck's *USB Composite Device*, and look under Details → Hardware Ids. Other AKP153 variants use other IDs and **aren't supported** by this plugin.

## Install

1. Close OpenDeck. It keeps running in the system tray; if it won't quit, run `taskkill /IM opendeck.exe /T /F`.
2. Open `%APPDATA%\opendeck\plugins`. **Move** the folder `st.lynx.plugins.opendeck-akp153.sdPlugin` somewhere else, for example your Desktop. Don't delete it, so you can switch back. Two plugins can't both drive device namespace 99.
3. Copy this repo's `com.jarvis.akp153.sdPlugin` folder into `%APPDATA%\opendeck\plugins`.
4. Optional: copy `jarvis-akp153.example.json` to `%APPDATA%\opendeck\jarvis-akp153.json` and edit it. Without that file you get night dimming only: no screensaver, no alerts. The plugin re-reads the file within about 5 seconds whenever it changes.
5. Start OpenDeck. The deck should light up with your pages within a few seconds.

**To switch back:** close OpenDeck, delete `com.jarvis.akp153.sdPlugin`, and move the original folder back.

## Settings (`%APPDATA%\opendeck\jarvis-akp153.json`)

- `saver.minutes`: idle minutes before the screensaver starts (`0` turns it off). `saver.file` is a GIF or picture path, or `builtin:starfield`. `saver.area` is `deck`, `side` or `both` (see Extras).
- `device.maxKBps`: a write-speed cap, 48 KB/s by default. It was an early guess at the lock-up and isn't needed since 1.3, but it does slow big animations: set it to e.g. `3000` for full speed. `device.heartbeatSeconds` (default 10) sends the keep-alive the official app sends.
- `audio.cycle`: `|`-separated words from the device names to switch between, e.g. `"Headphones|Speakers"`; empty = every output.
- `dim`: `percent` is the night brightness cap. `workNightsFrom` applies to Sunday–Thursday evenings and `otherNightsFrom` to Friday–Saturday; both run `until` the morning time.
- `alerts.checks`: a list of `{ "name": "NAS", "ping": "192.168.1.10" }` or `{ "name": "HA", "http": "http://host:port" }`. Any HTTP answer counts as up, and self-signed certificates are fine. `position` is the side-screen tile the alert uses (5 = top).

Logs: `%LOCALAPPDATA%\OpenDeck\logs\plugins\com.jarvis.akp153.sdPlugin.log`.

## Things learned the hard way (in case you hack on it)

- **Keep every key image at or under 3 KB** (three 1 KB packets). Bigger images in a sustained animation eventually crash the deck's display: either Windows reports *"A device attached to the system is not functioning"*, or the deck falls back to its boot logo while still accepting writes. Either way only unplugging it fixes it. Traffic volume is not the problem: the official app streams about 2.2 MB/s all day with images of about 2.9 KB.
- **Send `STP` after every image**, as the official app does, and keep a reader on the input endpoint (a separate handle, see below). Without a reader, writes were also much slower.
- Whole-deck screensaver frames are still held for at least 300 ms each, from before the real cause was found.
- At about 3 frames a second, anything that **moves** looks jittery on the deck. Brightness changes look smooth, which is why the built-in screensaver twinkles instead of scrolling.
- **Use separate handles for reading and writing** the HID device. One shared overlapped handle used by both a reader thread and a writer fails with *"The parameter is incorrect"* during page changes.
- **Side screen geometry** (measured on one unit): keys 5, 11 and 17 write strips starting at rows 0, 146 and 219 of the tall screen, and a later write covers an earlier one. The bridge sends three 82 × 200 strips in that order. If your seams look a pixel or two off, adjust `SlotTop` in `bridge.cs`.
- PowerShell is started with a plain `-Command` that compiles `bridge.cs`, never `-ExecutionPolicy Bypass` and no `.ps1` file, so it works with Windows' default execution policy.

## Credits and license

The USB protocol is ported from Andrey Viktorov's [mirajazz](https://github.com/4ndv/mirajazz) (MPL-2.0) and [opendeck-akp153](https://github.com/4ndv/opendeck-akp153) (GPL-3.0); the plugin icon also comes from opendeck-akp153. Thanks for doing the hard part.

This project is licensed under **GPL-3.0**; see `LICENSE`.
