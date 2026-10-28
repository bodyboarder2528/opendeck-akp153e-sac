"use strict";
// Jarvis AKP153E device plugin (Jarvis, 27-10-2026). Stands in for st.lynx.plugins.opendeck-akp153, whose
// unsigned exe Smart App Control blocks. This side speaks OpenDeck's device protocol; bridge.cs (compiled and run
// by PowerShell) does the USB work through Windows' own hid.dll, so only signed programs run.
// Same device namespace (99) and id (99-<serial>), so every existing page carries over. No packages: Node 22+
// has WebSocket built in.
//
// Extras on top of plain device support (settings in %APPDATA%\opendeck\jarvis-akp153.json, re-read on change):
//   - "Animated GIF" action: plays a GIF on its key (OpenDeck itself only ever sends one still frame)
//   - screensaver: one picture/GIF across all 15 buttons after N idle minutes; the waking press is swallowed
//   - alerts: pings / HTTP checks; when one fails twice in a row a key flashes red until it's back
//   - night dimming: caps brightness overnight (work nights start earlier)
//   - "System monitor" action: CPU %, RAM % and GPU temperature, live
//   - "Audio output" action: shows the current playback device, press switches to the next one (audio.cs)
//   - device.maxKBps / heartbeatSeconds: write throttle + keep-alive (see bridge.cs Throttle), so the deck can't be flooded
const { spawn, execFile } = require("node:child_process");
const fs = require("node:fs");
const http = require("node:http");
const https = require("node:https");
const os = require("node:os");
const path = require("node:path");
const readline = require("node:readline");

const args = {};
for (let i = 2; i < process.argv.length - 1; i++) {
  if (process.argv[i].startsWith("-")) args[process.argv[i].replace(/^-+/, "")] = process.argv[++i];
}

const BRIDGE = path.join(__dirname, "bridge.cs");
const BRIDGE_CMD = `Add-Type -ReferencedAssemblies System.Drawing -Path '${BRIDGE.replace(/'/g, "''")}'; exit [Akp]::Run()`;
const CONFIG_FILE = path.join(process.env.APPDATA || os.homedir(), "opendeck", "jarvis-akp153.json");
const DEFAULTS = {
  saver: { minutes: 0, file: "builtin:starfield", area: "deck" },     // minutes 0 = off; file = a GIF/picture path; area "deck", "side" or "both" (side GIF + shadow keys)
  dim: { percent: 20, workNightsFrom: "21:00", otherNightsFrom: "23:00", until: "05:00" },   // work nights = Sun-Thu evenings
  alerts: { position: 5, everySeconds: 60, failuresBeforeAlert: 2, checks: [] },   // checks: { name, ping } or { name, http }
  // ~200 KB/s sustained locked the deck in ~2 min; ~40 KB/s ran all night (28-10-2026). Burst = a page change.
  device: { maxKBps: 48, burstKB: 160, heartbeatSeconds: 10 },
  audio: { cycle: "" },   // '|'-separated name words to switch between (e.g. "Headphones|Speakers"); empty = every output
};

let ws, bridge = null, deviceId = null, flushTimer = null, stopping = false, noDevLogged = false;
let config = DEFAULTS;
let userBrightness = 50, sentBrightness = null;
let lastActivity = Date.now(), saverOn = false, saverArea = "deck", swallowKey = null;
let alertText = null;
const gifs = new Map();       // action context -> { pos, file }
const monitors = new Set();   // System monitor action contexts
const audioKeys = new Set();  // Audio output action contexts

function log(msg) { console.log(`${new Date().toLocaleString("en-AU")} ${msg}`); }
function send(msg) { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg)); }
function toBridge(line) { if (bridge && deviceId) bridge.stdin.write(line + "\n"); }

// ---------- settings ----------
function loadConfig() {
  try {
    const user = JSON.parse(fs.readFileSync(CONFIG_FILE, "utf8").replace(/^﻿/, ""));   // Windows PowerShell writes a BOM
    config = {};
    for (const k of Object.keys(DEFAULTS)) config[k] = { ...DEFAULTS[k], ...(user[k] || {}) };
    log(`settings loaded from ${CONFIG_FILE}`);
  } catch (e) {
    config = DEFAULTS;
    if (e.code !== "ENOENT") log(`settings file unreadable, using defaults: ${e.message}`);
  }
}
loadConfig();
fs.watchFile(CONFIG_FILE, { interval: 5000 }, () => { loadConfig(); applyBrightness(); runChecks(); sendDeviceLimits(); });

function sendDeviceLimits() {
  const d = config.device;
  toBridge(`RATE ${Math.round(d.maxKBps * 1000)} ${Math.round(d.burstKB * 1000)}`);
  toBridge(`HEARTBEAT ${d.heartbeatSeconds}`);
}

// ---------- device ----------
// Images arrive in bursts on a page change: commit (STP) once things go quiet for 50 ms, like the original
function scheduleFlush() {
  clearTimeout(flushTimer);
  flushTimer = setTimeout(() => toBridge("STP"), 50);
}

function startBridge() {
  if (stopping) return;
  bridge = spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", BRIDGE_CMD],
    { windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
  readline.createInterface({ input: bridge.stdout }).on("line", onBridgeLine);
  readline.createInterface({ input: bridge.stderr }).on("line", (l) => log(`bridge: ${l}`));
  bridge.on("exit", (code) => {
    bridge = null;
    if (deviceId) {
      log(`deck lost (bridge exit ${code}), deregistering ${deviceId}`);
      send({ event: "deregisterDevice", payload: deviceId });
      deviceId = null;
    }
    // 2 = deck not plugged in: keep looking; 4 = deck locked up: it stays dead until it's replugged, so look less often
    setTimeout(startBridge, code === 2 ? 5000 : code === 4 ? 15000 : 3000);
  });
}

function onBridgeLine(line) {
  const [kind, a, b] = line.split(" ");
  if (kind === "READY") {
    noDevLogged = false;
    deviceId = `99-${a || "unknown"}`;
    sentBrightness = null; saverOn = false; lastActivity = Date.now();
    log(`deck open, registering ${deviceId}`);
    send({ event: "registerDevice", payload: { id: deviceId, name: "Ajazz AKP153E (rev. 2)", rows: 3, columns: 6, encoders: 0, type: 0 } });
    sendDeviceLimits();
    if (alertText) toBridge(`ALERT ${config.alerts.position} ${alertText}`);
  } else if (kind === "KEY" && deviceId) {
    onKey(Number(a), b === "1");
  } else if (kind === "GONE") {
    log(`bridge: ${line}`);
    // A stuck write may never return, so don't wait for the bridge to exit by itself
    if (a === "stuck:" && bridge) { const b2 = bridge; setTimeout(() => { try { b2.kill(); } catch { } }, 1000); }
  } else if (kind === "LOADED") {
    log(`loaded ${a} (${b} frames)`);
  } else if (kind === "NODEV" && !noDevLogged) {
    log("deck not found, retrying every 5 s");
    noDevLogged = true;
  }
}

function onKey(pos, down) {
  lastActivity = Date.now();
  if (saverOn && down) {
    saverOn = false;
    toBridge("SAVEROFF");
    // whole-deck screensaver: the first press only wakes the deck; side-only: the buttons were showing, so it counts
    if (saverArea !== "side") { swallowKey = pos; return; }
  }
  if (swallowKey === pos) { if (!down) swallowKey = null; return; }
  send({ event: down ? "keyDown" : "keyUp", payload: { device: deviceId, position: pos } });
}

// ---------- screensaver ----------
setInterval(() => {
  const s = config.saver;
  if (!deviceId || saverOn || !s.minutes || !s.file) return;
  if (Date.now() - lastActivity >= s.minutes * 60000) {
    saverOn = true; saverArea = s.area;
    // area "both" (v1.3): the GIF on the side screen + the built-in "shadow" keys (clock, ARISE, eyes)
    const cmd = s.area === "side" ? "SIDESAVER" : s.area === "both" ? "SHADOWSAVER" : "SAVER";
    log(`screensaver on (${s.area === "side" ? "side screen" : s.area === "both" ? "side screen + shadow keys" : "whole deck"}: ${s.file})`);
    toBridge(`${cmd} ${s.file}`);
  }
}, 15000);

// ---------- night dimming ----------
function minutesOf(hhmm) { const [h, m] = String(hhmm).split(":").map(Number); return h * 60 + (m || 0); }
function isNight(now = new Date()) {
  const d = config.dim, t = now.getHours() * 60 + now.getMinutes();
  if (t < minutesOf(d.until)) return true;   // small hours: always night
  const workNight = now.getDay() <= 4;       // Sun (0) to Thu (4) evenings
  return t >= minutesOf(workNight ? d.workNightsFrom : d.otherNightsFrom);
}
function applyBrightness() {
  if (!deviceId) return;
  const want = isNight() ? Math.min(userBrightness, config.dim.percent) : userBrightness;
  if (want !== sentBrightness) { sentBrightness = want; toBridge(`BRI ${want}`); log(`brightness ${want}%${isNight() ? " (night)" : ""}`); }
}
setInterval(applyBrightness, 60000);

// ---------- homelab alerts ----------
const checkState = new Map();   // name -> consecutive failures
function pingOk(host) {
  return new Promise((res) => execFile("ping", ["-n", "1", "-w", "1500", host], { windowsHide: true, timeout: 5000 },
    (err, stdout) => res(!err && /TTL=/i.test(stdout))));
}
function httpOk(url) {
  return new Promise((res) => {
    const lib = url.startsWith("https") ? https : http;
    // Any answer (even 401) counts as up; self-signed certs (Proxmox) are fine for a liveness check.
    const req = lib.get(url, { timeout: 4000, rejectUnauthorized: false }, (r) => { r.resume(); res(true); });
    req.on("timeout", () => { req.destroy(); res(false); });
    req.on("error", () => res(false));
  });
}
let checking = false;
async function runChecks() {
  if (checking) return;
  checking = true;
  try {
    const a = config.alerts, down = [];
    for (const c of a.checks || []) {
      const ok = c.ping ? await pingOk(c.ping) : c.http ? await httpOk(c.http) : true;
      const fails = ok ? 0 : (checkState.get(c.name) || 0) + 1;
      if (ok && checkState.get(c.name) >= a.failuresBeforeAlert) log(`${c.name} is back`);
      if (fails === a.failuresBeforeAlert) log(`${c.name} is DOWN`);
      checkState.set(c.name, fails);
      if (fails >= a.failuresBeforeAlert) down.push(c.name);
    }
    const text = down.length === 0 ? null : down.length === 1 ? `${down[0]}|DOWN` : `${down.length}|DOWN`;
    if (text !== alertText) {
      alertText = text;
      toBridge(text ? `ALERT ${a.position} ${text}` : `ALERTOFF ${a.position}`);
    }
  } finally { checking = false; }
}
setInterval(runChecks, Math.max(15, config.alerts.everySeconds) * 1000);
setTimeout(runChecks, 10000);

// ---------- System monitor key ----------
// CPU from Node's own counters, RAM from the OS, GPU temperature from nvidia-smi (NVIDIA-signed). Every 2 s,
// so it costs about one key image every 2 s, well inside the throttle.
let cpuPrev = null, gpuTemp = null;
function cpuPercent() {
  const now = os.cpus().reduce((a, c) => {
    const t = c.times; a.idle += t.idle; a.total += t.user + t.nice + t.sys + t.idle + t.irq; return a;
  }, { idle: 0, total: 0 });
  const prev = cpuPrev; cpuPrev = now;
  if (!prev || now.total === prev.total) return null;
  return Math.round(100 * (1 - (now.idle - prev.idle) / (now.total - prev.total)));
}
function readGpu() {
  execFile("nvidia-smi", ["--query-gpu=temperature.gpu", "--format=csv,noheader"], { windowsHide: true, timeout: 5000 },
    (err, stdout) => { gpuTemp = err ? null : parseInt(stdout, 10); });
}
setInterval(() => {
  if (monitors.size === 0) { cpuPrev = null; return; }
  const cpu = cpuPercent();
  const ram = Math.round(100 * (1 - os.freemem() / os.totalmem()));
  const title = `CPU ${cpu == null ? "--" : cpu}%\nRAM ${ram}%\nGPU ${Number.isFinite(gpuTemp) ? gpuTemp + "C" : "--"}`;
  for (const context of monitors) send({ event: "setTitle", context, payload: { title, target: 0 } });
}, 2000);
setInterval(() => { if (monitors.size) readGpu(); }, 5000);

// ---------- Audio output key ----------
const AUDIO = path.join(__dirname, "audio.cs");
function audioCmd(cmd, arg = "") {
  const ps = `Add-Type -Path '${AUDIO.replace(/'/g, "''")}'; exit [JarvisAudio]::Run('${cmd}', '${String(arg).replace(/'/g, "''")}')`;
  return new Promise((res) => execFile("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", ps],
    { windowsHide: true, timeout: 20000 }, (err, stdout) => res(err ? null : stdout.trim())));
}
// "Speakers (Realtek(R) Audio)" -> "Speakers"; the headset's two channels keep their Game/Chat tag
function shortName(name) {
  if (!name) return "?";
  const tag = /- (Game|Chat)\)$/.exec(name);
  return name.split(" (")[0] + (tag ? `\n${tag[1]}` : "");
}
async function refreshAudio() {
  if (audioKeys.size === 0) return;
  const title = shortName(await audioCmd("current"));
  for (const context of audioKeys) send({ event: "setTitle", context, payload: { title, target: 0 } });
}
setInterval(refreshAudio, 30000);
async function audioPressed(context) {
  const name = await audioCmd("next", config.audio.cycle || "");
  if (!name || name === "none") { send({ event: "showAlert", context }); return; }
  log(`audio output -> ${name}`);
  for (const c of audioKeys) send({ event: "setTitle", context: c, payload: { title: shortName(name), target: 0 } });
}

// ---------- OpenDeck ----------
ws = new WebSocket(`ws://127.0.0.1:${args.port}`);
ws.onopen = () => { send({ event: args.registerEvent, uuid: args.pluginUUID }); startBridge(); };
ws.onmessage = (m) => {
  let e;
  try { e = JSON.parse(m.data); } catch { return; }
  if (!deviceId || (e.device && e.device !== deviceId)) return;
  switch (e.event) {
    case "setImage":
      if (e.controller === "Encoder") return;
      if (e.position == null) { toBridge("CLRALL"); return; }
      if (!e.image) toBridge(`CLR ${e.position}`);
      else toBridge(`IMG ${e.position} ${e.image.slice(e.image.indexOf(",") + 1)}`);
      scheduleFlush();
      break;
    case "setBrightness":
      userBrightness = e.brightness; sentBrightness = null;
      applyBrightness();
      break;
    case "systemDidWakeUp":
      log("system woke, resetting deck");
      sentBrightness = null;
      toBridge(`RESET ${userBrightness}`);
      applyBrightness();
      send({ event: "rerenderImages", payload: deviceId });
      break;
    case "willAppear":
    case "didReceiveSettings": {
      const p = e.payload || {}, c = p.coordinates;
      if (!c || p.isInMultiAction) return;
      const pos = c.row * 6 + c.column;
      if (e.action === "com.jarvis.akp153.gif") {
        const file = (p.settings || {}).file || "";
        const old = gifs.get(e.context);
        gifs.set(e.context, { pos, file });
        if (file && (!old || old.file !== file || e.event === "willAppear")) toBridge(`ANIM ${pos} ${file}`);
        else if (!file && old && old.file) toBridge(`ANIMOFF ${pos}`);
      } else if (e.action === "com.jarvis.akp153.sysmon") {
        monitors.add(e.context);
        readGpu();
      } else if (e.action === "com.jarvis.akp153.audio") {
        audioKeys.add(e.context);
        refreshAudio();
      }
      break;
    }
    case "willDisappear":
      if (gifs.has(e.context)) { const g = gifs.get(e.context); gifs.delete(e.context); if (g.file) toBridge(`ANIMOFF ${g.pos}`); }
      monitors.delete(e.context);
      audioKeys.delete(e.context);
      break;
    case "keyDown":
      if (e.action === "com.jarvis.akp153.audio") audioPressed(e.context);
      break;
  }
};
ws.onclose = () => { stopping = true; if (bridge) bridge.kill(); process.exit(0); };
