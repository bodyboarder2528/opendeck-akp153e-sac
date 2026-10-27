# OpenDeck AKP153E plugin that Smart App Control won't block

An [OpenDeck](https://github.com/nekename/OpenDeck) device plugin for the **AJAZZ AKP153E (rev. 2)** stream deck on Windows.

> **Made with AI.** This plugin was written by an AI (Anthropic's Claude), working for the repo owner, who directed it and tested it on his own deck. It works on that one AKP153E rev. 2. Nobody else has reviewed it, and the code has no automated tests. It's small (two files in `bin/`), so read it before you run it. Use it at your own risk.

> **Known issue: leave the screensaver off for now.** Animating the tall side screen for more than about 2 minutes made the deck stop responding until it was unplugged and plugged back in (it happened three times). The screensaver is **off by default** in this version. Don't turn it on (`saver.minutes` in the settings file) until this is fixed. Static pages, GIF keys, alerts and dimming are fine.

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
| **Screensaver** | After N idle minutes, one picture or GIF covers the whole deck, side screen included. The default is a built-in twinkling night sky. The first press only wakes the deck; it doesn't trigger that key. |
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

- `saver.minutes`: idle minutes before the screensaver starts (`0` turns it off). `saver.file` is a GIF or picture path, or `builtin:starfield`.
- `dim`: `percent` is the night brightness cap. `workNightsFrom` applies to Sunday–Thursday evenings and `otherNightsFrom` to Friday–Saturday; both run `until` the morning time.
- `alerts.checks`: a list of `{ "name": "NAS", "ping": "192.168.1.10" }` or `{ "name": "HA", "http": "http://host:port" }`. Any HTTP answer counts as up, and self-signed certificates are fine. `position` is the side-screen tile the alert uses (5 = top).

Logs: `%LOCALAPPDATA%\OpenDeck\logs\plugins\com.jarvis.akp153.sdPlugin.log`.

## Things learned the hard way (in case you hack on it)

- **Don't flood the deck.** Short bursts are fine (about 290 images a second for a page change). But a sustained whole-deck animation at about 225 images a second made Windows report *"A device attached to the system is not functioning"* after roughly 9 seconds. The deck then **locked up until it was unplugged and plugged back in**. Whole-deck frames are therefore held for at least 300 ms each. That passed a 2-minute soak.
- At about 3 frames a second, anything that **moves** looks jittery on the deck. Brightness changes look smooth, which is why the built-in screensaver twinkles instead of scrolling.
- **Use separate handles for reading and writing** the HID device. One shared overlapped handle used by both a reader thread and a writer fails with *"The parameter is incorrect"* during page changes.
- **Side screen geometry** (measured on one unit): keys 5, 11 and 17 write strips starting at rows 0, 146 and 219 of the tall screen, and a later write covers an earlier one. The bridge sends three 82 × 200 strips in that order. If your seams look a pixel or two off, adjust `SlotTop` in `bridge.cs`.
- PowerShell is started with a plain `-Command` that compiles `bridge.cs`, never `-ExecutionPolicy Bypass` and no `.ps1` file, so it works with Windows' default execution policy.

## Credits and license

The USB protocol is ported from Andrey Viktorov's [mirajazz](https://github.com/4ndv/mirajazz) (MPL-2.0) and [opendeck-akp153](https://github.com/4ndv/opendeck-akp153) (GPL-3.0); the plugin icon also comes from opendeck-akp153. Thanks for doing the hard part.

This project is licensed under **GPL-3.0**; see `LICENSE`.
