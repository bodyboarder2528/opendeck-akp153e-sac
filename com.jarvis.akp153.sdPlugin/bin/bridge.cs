// USB side of the Jarvis AKP153E plugin (Jarvis, 27-10-2026). plugin.cjs runs it with:
//   powershell -NoProfile -NonInteractive -Command "Add-Type -ReferencedAssemblies System.Drawing -Path bridge.cs; exit [Akp]::Run()"
// (a -Command string, not a .ps1, so no execution-policy change is needed). Windows PowerShell compiles C# 5: keep it that way.
// Drives the AJAZZ AKP153E rev 2 (VID 0300, PID 3010, protocol v3) through Windows' own hid.dll / setupapi /
// kernel32, so nothing unsigned loads (Smart App Control blocked the original opendeck-akp153-win.exe).
// Protocol ported from 4ndv/mirajazz (MPL-2.0) + 4ndv/opendeck-akp153 (GPL-3.0); see README.md.
//
// Layers: each key shows the top layer that has something for it: screensaver > alert > animation > OpenDeck's image.
// Keys 5/11/17 are really one tall side screen: see SidePos / SendSide below.
// stdin  (from plugin.cjs):
//   IMG <pos> <base64>   OpenDeck's image for a key (png/jpeg)      CLR <pos> / CLRALL   clear OpenDeck's image(s)
//   STP                  commit                                     BRI <0-100>          brightness
//   RESET <brightness>   re-initialise after sleep, redraw all
//   ANIM <pos> <file>    play a GIF (or show any image) on one key  ANIMOFF <pos>
//   SAVER <file|builtin:starfield>  one picture/GIF across the 15 buttons            SAVEROFF
//   ALERT <pos> <text>   flashing red tile, '|' = new line          ALERTOFF <pos>
// stdout (to plugin.cjs):   READY <serial> | KEY <pos> <1|0> | NODEV | GONE <reason> | LOADED <what> <frames>
// <pos> is OpenDeck's key index (row * 6 + column). Errors on a single command go to stderr and are skipped.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class Akp {
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref ATTR a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out CAPS c);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref IFDATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref IFDATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string n, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr t);
    [StructLayout(LayoutKind.Sequential)] struct IFDATA { public int cbSize; public Guid cls; public int flags; public IntPtr reserved; }
    [StructLayout(LayoutKind.Sequential)] struct ATTR { public int Size; public ushort Vid; public ushort Pid; public ushort Ver; }
    [StructLayout(LayoutKind.Sequential)] struct CAPS { public ushort Usage, UsagePage, InLen, OutLen, FeatLen; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] r; public ushort a, b, c, d, e, f, g, h, i, j; }

    const int Packet = 1024;   // protocol v3
    const int MaxFrames = 300;
    // OpenDeck key index -> device key index, and back (device reports key+1)
    static readonly int[] ToDevice = { 12, 9, 6, 3, 0, 15, 13, 10, 7, 4, 1, 16, 14, 11, 8, 5, 2, 17 };
    static readonly int[] ToOpenDeck = { 4, 10, 16, 3, 9, 15, 2, 8, 14, 1, 7, 13, 0, 6, 12, 5, 11, 17 };
    static readonly int[] Buttons = { 0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 12, 13, 14, 15, 16 };   // the 15 pressable keys
    static FileStream dev, rd;   // dev = write handle, rd = read handle
    static int inLen, outLen;
    static readonly object outLock = new object();
    static readonly object sync = new object();   // guards the layers and every write to the deck
    public static string Serial;

    // The right-hand column is ONE tall screen (about 82 x 400 px), not three keys. Each of keys 5/11/17 writes a
    // strip from a fixed row (measured with ruler images, 27-10-2026: 0, 146, 219; a later write covers an earlier
    // one), so we draw one 82 x 400 canvas and send it as three overlapping 82 x 200 strips, top to bottom.
    // OpenDeck's three square side tiles (cells, 82 x 82) go top, middle and bottom of the canvas, and the two gaps
    // between them are filled with a row-by-row blend from one tile's edge to the next, so banners and the Claude
    // panel read as one continuous full-height strip. (Stretching each tile's own edge rows left the dark margin at
    // the ends and hard "blocks" between tiles.)
    static readonly int[] SidePos = { 5, 11, 17 };
    static readonly int[] SlotTop = { 0, 146, 219 };
    const int SideW = 82, SideH = 400, SlotH = 200, CellH = 82;
    static readonly int[] TileY = { 0, (SideH - CellH) / 2, SideH - CellH };
    static bool IsSide(int pos) { return pos == 5 || pos == 11 || pos == 17; }

    // A layer: per-key frame lists sharing one timeline. Buttons hold pre-encoded JPEGs; side keys hold 82 x 133
    // cells; a screensaver also holds whole side canvases (Side).
    class Layer {
        public Dictionary<int, byte[][]> Tiles = new Dictionary<int, byte[][]>();
        public Dictionary<int, Bitmap[]> Cells = new Dictionary<int, Bitmap[]>();
        public Bitmap[] Side;
        public int[] Delays = { 1000 };
        public int Frame;
        public DateTime Next = DateTime.MaxValue;
        public byte[] Current(int pos) { byte[][] f; return Tiles.TryGetValue(pos, out f) ? f[Math.Min(Frame, f.Length - 1)] : null; }
        public Bitmap CurrentCell(int pos) { Bitmap[] f; return Cells.TryGetValue(pos, out f) ? f[Math.Min(Frame, f.Length - 1)] : null; }
        public Bitmap CurrentSide() { return Side == null ? null : Side[Math.Min(Frame, Side.Length - 1)]; }
        public IEnumerable<int> Positions() {
            foreach (var p in Tiles.Keys) yield return p;
            foreach (var p in Cells.Keys) yield return p;
            if (Side != null) yield return 5;
        }
    }
    static readonly byte[][] baseImg = new byte[18][];
    static readonly Bitmap[] baseCell = new Bitmap[18];
    static readonly Layer[] anim = new Layer[18], alert = new Layer[18];
    static Layer saver;
    static bool sideDirty;

    public static void Emit(string line) { lock (outLock) { Console.Out.WriteLine(line); Console.Out.Flush(); } }

    public static bool Open() {
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        try {
            for (int i = 0; ; i++) {
                var d = new IFDATA(); d.cbSize = Marshal.SizeOf(d);
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) return false;
                int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(req);
                try {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                    if (h.IsInvalid) continue;
                    var a = new ATTR(); a.Size = Marshal.SizeOf(a);
                    IntPtr pp; CAPS c;
                    if (!HidD_GetAttributes(h, ref a) || a.Vid != 0x0300 || a.Pid != 0x3010 || !HidD_GetPreparsedData(h, out pp)) { h.Dispose(); continue; }
                    HidP_GetCaps(pp, out c); HidD_FreePreparsedData(pp);
                    if (c.UsagePage != 0xFFA0) { h.Dispose(); continue; }
                    var sb = new byte[256];
                    Serial = HidD_GetSerialNumberString(h, sb, sb.Length) ? System.Text.Encoding.Unicode.GetString(sb).TrimEnd('\0') : "";
                    inLen = c.InLen; outLen = c.OutLen;
                    h.Dispose();
                    // Separate plain (non-overlapped) handles for reading and writing: one shared FileStream used by the
                    // reader thread and the writer at once failed with "The parameter is incorrect" on page changes.
                    // Buffer size 1 = unbuffered, so every report goes straight to the device.
                    var hr = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    var hw = CreateFile(path, 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (hr.IsInvalid || hw.IsInvalid) { hr.Dispose(); hw.Dispose(); return false; }
                    rd = new FileStream(hr, FileAccess.Read, 1, false);
                    dev = new FileStream(hw, FileAccess.Write, 1, false);
                    return true;
                } finally { Marshal.FreeHGlobal(buf); }
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // ---------- raw device commands (call inside lock(sync)) ----------
    static void Send(byte[] payload) {
        var buf = new byte[outLen];
        Array.Copy(payload, buf, Math.Min(payload.Length, buf.Length));
        dev.Write(buf, 0, buf.Length);
    }
    // Report id 0 + "CRT\0\0" + command bytes, padded to a full report
    static void Cmd(params byte[] tail) {
        var p = new byte[6 + tail.Length];
        p[1] = 0x43; p[2] = 0x52; p[3] = 0x54;
        Array.Copy(tail, 0, p, 6, tail.Length);
        Send(p);
    }
    static void Commit() { if (sideDirty) SendSide(); Cmd(0x53, 0x54, 0x50); }                     // STP
    static void SendJpeg(int pos, byte[] jpg) {
        Cmd(0x42, 0x41, 0x54, 0, 0, (byte)(jpg.Length >> 8), (byte)jpg.Length, (byte)(ToDevice[pos] + 1));   // BAT
        for (int o = 0; o < jpg.Length; o += Packet) {
            var chunk = new byte[Packet + 1];
            Array.Copy(jpg, o, chunk, 1, Math.Min(Packet, jpg.Length - o));
            dev.Write(chunk, 0, chunk.Length);
        }
    }
    static void ClearKey(int pos) { Cmd(0x43, 0x4C, 0x45, 0, 0, 0, (byte)(ToDevice[pos] + 1)); }   // CLE one key
    static void Brightness(int pct) { Cmd(0x4C, 0x49, 0x47, 0, 0, (byte)Math.Max(0, Math.Min(100, pct))); }

    // Show whatever is on top for this key (side keys are redrawn together as one screen at the next commit)
    static void Draw(int pos) {
        if (IsSide(pos)) { sideDirty = true; return; }
        byte[] img = null;
        if (saver != null) img = saver.Current(pos);
        if (img == null && alert[pos] != null) img = alert[pos].Current(pos);
        if (img == null && anim[pos] != null) img = anim[pos].Current(pos);
        if (img == null) img = baseImg[pos];
        if (img != null) SendJpeg(pos, img); else ClearKey(pos);
    }
    static void DrawAll() { for (int p = 0; p < 18; p++) Draw(p); Commit(); }

    // Compose the tall side screen and send it as three overlapping strips, top slot first
    static void SendSide() {
        sideDirty = false;
        using (var canvas = new Bitmap(SideW, SlotTop[2] + SlotH)) {
            using (var g = Graphics.FromImage(canvas)) {
                g.Clear(Color.Black);
                Bitmap whole = saver != null ? saver.CurrentSide() : null;
                if (whole != null) g.DrawImageUnscaled(whole, 0, 0);
                else for (int k = 0; k < 3; k++) {
                    int pos = SidePos[k]; Bitmap c = null;
                    if (alert[pos] != null) c = alert[pos].CurrentCell(pos);
                    if (c == null && anim[pos] != null) c = anim[pos].CurrentCell(pos);
                    if (c == null) c = baseCell[pos];
                    if (c != null) g.DrawImage(c, new Rectangle(0, TileY[k], SideW, CellH));
                }
            }
            // Blend from 2 px inside each tile: the banners and Claude panel draw tick marks on their tile edges (made
            // for the old gappy layout), and stretching those made solid "blocks".
            const int Inset = 2;
            if (saver == null || saver.Side == null) {
                BlendGap(canvas, TileY[0] + CellH - Inset, TileY[1] + Inset);
                BlendGap(canvas, TileY[1] + CellH - Inset, TileY[2] + Inset);
            }
            for (int k = 0; k < 3; k++)
                using (var strip = canvas.Clone(new Rectangle(0, SlotTop[k], SideW, SlotH), PixelFormat.Format24bppRgb)) SendJpeg(SidePos[k], Encode(strip));
        }
    }
    // Fill rows [from, to) by blending the row above the gap into the row below it
    static void BlendGap(Bitmap canvas, int from, int to) {
        int n = to - from; if (n <= 0) return;
        var top = new Color[SideW]; var bot = new Color[SideW];
        for (int x = 0; x < SideW; x++) { top[x] = canvas.GetPixel(x, from - 1); bot[x] = canvas.GetPixel(x, to); }
        for (int y = 0; y < n; y++) {
            double t = (y + 1.0) / (n + 1);
            for (int x = 0; x < SideW; x++)
                canvas.SetPixel(x, from + y, Color.FromArgb((int)(top[x].R + (bot[x].R - top[x].R) * t), (int)(top[x].G + (bot[x].G - top[x].G) * t), (int)(top[x].B + (bot[x].B - top[x].B) * t)));
        }
    }
    // A side tile: OpenDeck's square image fitted to 82 x 82
    static Bitmap Cell(Image src) { return Fit(src, SideW, CellH, false); }

    static void Init(int brightness) {
        Cmd(0x44, 0x49, 0x53);                         // DIS
        Cmd(0x4C, 0x49, 0x47, 0, 0, 0, 0);             // LIG
        Brightness(brightness);
        Cmd(0x43, 0x4C, 0x45, 0, 0, 0, 0xFF); Commit();   // CLE all
    }

    // ---------- images ----------
    static int SizeOf(int pos) { return (pos == 5 || pos == 11 || pos == 17) ? 82 : 95; }   // side-screen tiles are smaller
    static ImageCodecInfo jpegCodec;
    static byte[] Encode(Bitmap tile) {
        tile.RotateFlip(RotateFlipType.Rotate270FlipNone);   // device wants rotate 90 + mirror both
        if (jpegCodec == null) foreach (var e in ImageCodecInfo.GetImageEncoders()) if (e.MimeType == "image/jpeg") jpegCodec = e;
        var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        var ms = new MemoryStream(); tile.Save(ms, jpegCodec, ep); return ms.ToArray();
    }
    static Bitmap Fit(Image src, int w, int h, bool cover) {
        var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp)) {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            double s = cover ? Math.Max((double)w / src.Width, (double)h / src.Height) : Math.Min((double)w / src.Width, (double)h / src.Height);
            int dw = (int)Math.Round(src.Width * s), dh = (int)Math.Round(src.Height * s);
            g.DrawImage(src, (w - dw) / 2, (h - dh) / 2, dw, dh);
        }
        return bmp;
    }
    static byte[] EncodeKey(Image src, int pos) { using (var b = Fit(src, SizeOf(pos), SizeOf(pos), false)) return Encode(b); }

    // Every frame of a GIF (one frame for any other picture), with its delay in ms (min 50 ms)
    static List<Bitmap> Frames(string file, out int[] delays) {
        var list = new List<Bitmap>();
        using (var img = System.Drawing.Image.FromFile(file)) {
            int n = 1;
            var dims = img.FrameDimensionsList;
            FrameDimension dim = dims.Length > 0 ? new FrameDimension(dims[0]) : null;
            if (dim != null && dim.Guid == FrameDimension.Time.Guid) n = Math.Min(img.GetFrameCount(dim), MaxFrames);
            delays = new int[n];
            byte[] raw = null;
            try { raw = img.GetPropertyItem(0x5100).Value; } catch { }
            for (int i = 0; i < n; i++) {
                if (n > 1) img.SelectActiveFrame(dim, i);
                list.Add(new Bitmap(img));
                int d = raw != null && raw.Length >= (i + 1) * 4 ? BitConverter.ToInt32(raw, i * 4) * 10 : 100;
                delays[i] = Math.Max(50, d <= 0 ? 100 : d);
            }
        }
        return list;
    }
    static Layer KeyLayer(string file, int pos) {
        int[] delays; var frames = Frames(file, out delays);
        var l = new Layer(); l.Delays = delays;
        if (IsSide(pos)) {
            var cells = new Bitmap[frames.Count];
            for (int i = 0; i < frames.Count; i++) { cells[i] = Cell(frames[i]); frames[i].Dispose(); }
            l.Cells[pos] = cells;
            return l;
        }
        var enc = new byte[frames.Count][];
        for (int i = 0; i < frames.Count; i++) { enc[i] = EncodeKey(frames[i], pos); frames[i].Dispose(); }
        l.Tiles[pos] = enc;
        return l;
    }
    // One picture across the whole deck, cropped to fill: the 15 buttons (5 x 3 grid of 95 px keys) plus the tall
    // side screen (the picture's right-hand strip, stretched to its 82 x 400 shape).
    // The deck can't keep up with 15 keys at full speed for long ("A device attached to the system is not
    // functioning" after ~9 s at 15 fps = 225 images/s), so whole-deck frames are held at least this long.
    // 250 ms passed a 2-min soak with 15 keys; with the 3 side strips added it's 300 ms (about 60 images/s).
    public static int SaverMinDelay = 300;
    static Layer DeckLayer(List<Bitmap> frames, int[] delays) {
        for (int i = 0; i < delays.Length; i++) delays[i] = Math.Max(delays[i], SaverMinDelay);
        var l = new Layer(); l.Delays = delays;
        l.Side = new Bitmap[frames.Count];
        foreach (int pos in Buttons) l.Tiles[pos] = new byte[frames.Count][];
        for (int i = 0; i < frames.Count; i++) {
            using (var big = Fit(frames[i], 570, 285, true)) {
                foreach (int pos in Buttons) {
                    int col = pos % 6, row = pos / 6;
                    using (var tile = big.Clone(new Rectangle(col * 95, row * 95, 95, 95), PixelFormat.Format24bppRgb)) l.Tiles[pos][i] = Encode(tile);
                }
                var side = new Bitmap(SideW, SideH);
                using (var g = Graphics.FromImage(side)) {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(big, new Rectangle(0, 0, SideW, SideH), new Rectangle(480, 0, 90, 285), GraphicsUnit.Pixel);
                }
                l.Side[i] = side;
            }
            frames[i].Dispose();
        }
        return l;
    }
    // Built-in screensaver: a still night sky where nothing moves, only brightness changes. Stars fade in and out at
    // their own pace over two softly breathing nebula glows. Whole-deck frames can only change every 250 ms
    // (SaverMinDelay); anything that moved (warp, then a slow sideways drift) looked jittery at that rate on the
    // device (filmed and checked frame by frame), but small brightness steps read as smooth. 240 frames = a seamless loop.
    // 570 wide = the 5 button columns plus a strip for the side screen.
    static List<Bitmap> Starfield(out int[] delays) {
        const int W = 570, H = 285, N = 240, STARS = 380;
        var rnd = new Random(7); var list = new List<Bitmap>();
        // star: x, y, size, base brightness, phase, cycles per loop (whole number keeps the loop seamless)
        var stars = new double[STARS][];
        for (int s = 0; s < STARS; s++) {
            double big = rnd.NextDouble();
            stars[s] = new[] { rnd.NextDouble() * W, rnd.NextDouble() * H, big > 0.93 ? 3.2 : big > 0.7 ? 2.0 : 1.3,
                               big > 0.93 ? 255 : 120 + rnd.NextDouble() * 110, rnd.NextDouble() * Math.PI * 2, 2 + rnd.Next(5) };
        }
        for (int f = 0; f < N; f++) {
            double t = 2 * Math.PI * f / N;
            var bmp = new Bitmap(W, H);
            using (var g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(3, 4, 12));
                // two nebula glows that breathe slowly (one cycle each per loop, out of step)
                DrawGlow(g, new RectangleF(-60, -40, 330, 260), Color.FromArgb((int)(34 + 16 * Math.Sin(t)), 110, 40, 170));
                DrawGlow(g, new RectangleF(260, 60, 340, 260), Color.FromArgb((int)(30 + 14 * Math.Sin(t + 2.1)), 30, 90, 170));
                foreach (var s in stars) {
                    double tw = 0.3 + 0.7 * (0.5 + 0.5 * Math.Sin(s[4] + s[5] * t));
                    int a = (int)(s[3] * tw); float r = (float)s[2];
                    if (r > 3) using (var glow = new SolidBrush(Color.FromArgb(a / 4, 160, 200, 255))) g.FillEllipse(glow, (float)s[0] - r * 2, (float)s[1] - r * 2, r * 4, r * 4);
                    using (var b = new SolidBrush(Color.FromArgb(a, 210, 230, 255))) g.FillEllipse(b, (float)s[0] - r / 2, (float)s[1] - r / 2, r, r);
                }
            }
            list.Add(bmp);
        }
        delays = new int[N]; for (int i = 0; i < N; i++) delays[i] = 250;
        return list;
    }
    static void DrawGlow(Graphics g, RectangleF r, Color c) {
        using (var path = new GraphicsPath()) {
            path.AddEllipse(r);
            using (var br = new PathGradientBrush(path)) {
                br.CenterColor = c; br.SurroundColors = new[] { Color.FromArgb(0, c) };
                g.FillPath(br, path);
            }
        }
    }
    // Flashing red tile with text ('|' = new line), 0.6 s on / 0.6 s off. On a side key it fills that third of the screen.
    static Layer AlertLayer(int pos, string text) {
        bool side = IsSide(pos);
        int sz = SizeOf(pos), w = sz, h = side ? CellH : sz; var lines = text.Split('|');
        var enc = new byte[2][]; var cells = new Bitmap[2];
        for (int f = 0; f < 2; f++) {
            var bmp = new Bitmap(w, h);
            try {
                using (var g = Graphics.FromImage(bmp)) {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(f == 0 ? Color.FromArgb(200, 0, 0) : Color.FromArgb(30, 0, 0));
                    var fg = f == 0 ? Color.White : Color.FromArgb(255, 60, 60);
                    float fs = 26;
                    Font font = null;
                    for (; fs > 9; fs -= 1) {
                        if (font != null) font.Dispose();
                        font = new Font("Bahnschrift SemiBold", fs, FontStyle.Regular, GraphicsUnit.Pixel);
                        float mw = 0; foreach (var ln in lines) mw = Math.Max(mw, g.MeasureString(ln, font).Width);
                        if (mw <= w - 8 && font.Height * lines.Length <= h - 8) break;
                    }
                    using (var br = new SolidBrush(fg)) {
                        float y = (h - font.Height * lines.Length) / 2f;
                        foreach (var ln in lines) { var lw = g.MeasureString(ln, font).Width; g.DrawString(ln, font, br, (w - lw) / 2, y); y += font.Height; }
                    }
                    font.Dispose();
                }
                if (side) { cells[f] = bmp; bmp = null; } else enc[f] = Encode(bmp);
            } finally { if (bmp != null) bmp.Dispose(); }
        }
        var l = new Layer(); l.Delays = new[] { 600, 600 };
        if (side) l.Cells[pos] = cells; else l.Tiles[pos] = enc;
        return l;
    }
    static void Start(Layer l) { l.Frame = 0; l.Next = l.Delays.Length > 1 ? DateTime.UtcNow.AddMilliseconds(l.Delays[0]) : DateTime.MaxValue; }

    // ---------- animation clock ----------
    static void Tick(Layer l, List<int> dirty) {
        if (l == null || l.Next > DateTime.UtcNow) return;
        l.Frame = (l.Frame + 1) % l.Delays.Length;
        l.Next = DateTime.UtcNow.AddMilliseconds(l.Delays[l.Frame]);
        foreach (var pos in l.Positions()) if (!dirty.Contains(pos)) dirty.Add(pos);
    }
    static void StartClock() {
        var t = new Thread(() => {
            var dirty = new List<int>();
            try {
                while (true) {
                    Thread.Sleep(10);
                    lock (sync) {
                        dirty.Clear();
                        Tick(saver, dirty);
                        for (int p = 0; p < 18; p++) { Tick(alert[p], dirty); Tick(anim[p], dirty); }
                        if (dirty.Count == 0) continue;
                        foreach (var p in dirty) Draw(p);
                        Commit();
                    }
                }
            } catch (Exception e) { Emit("GONE clock: " + e.Message.Replace("\r", " ").Replace("\n", " ")); Environment.Exit(3); }
        });
        t.IsBackground = true;
        t.Start();
    }

    // Key presses: report id at byte 0, so device key+1 is byte 10 and the state byte 11
    static void StartReader() {
        var t = new Thread(() => {
            var buf = new byte[inLen];
            try {
                while (true) {
                    int n = rd.Read(buf, 0, buf.Length);
                    if (n <= 0) { Emit("GONE read returned 0"); Environment.Exit(3); }
                    int k = buf[10];
                    if (k >= 1 && k <= 18) Emit("KEY " + ToOpenDeck[k - 1] + " " + (buf[11] != 0 ? 1 : 0));
                }
            } catch (Exception e) { Emit("GONE " + e.Message.Replace("\r", " ").Replace("\n", " ")); Environment.Exit(3); }
        });
        t.IsBackground = true;
        t.Start();
    }

    // Main loop: open the deck, then follow plugin.cjs's commands on stdin until it closes
    public static int Run() {
        if (!Open()) { Emit("NODEV"); return 2; }
        try { lock (sync) Init(50); } catch (Exception e) { Emit("GONE init: " + e.Message); return 3; }
        StartReader();
        StartClock();
        Emit("READY " + Serial);
        string line;
        while ((line = Console.In.ReadLine()) != null) {
            var p = line.Split(new[] { ' ' }, 3);
            try {
                int pos;
                if (p.Length < 2 || !int.TryParse(p[1], out pos)) pos = -1;
                bool keyOk = pos >= 0 && pos < 18;
                switch (p[0]) {
                    case "IMG":
                        if (!keyOk) break;
                        byte[] jpg = null; Bitmap cell = null;
                        using (var src = System.Drawing.Image.FromStream(new MemoryStream(Convert.FromBase64String(p[2]))))
                            if (IsSide(pos)) cell = Cell(src); else jpg = EncodeKey(src, pos);
                        lock (sync) { baseImg[pos] = jpg; baseCell[pos] = cell; Draw(pos); }
                        break;
                    case "CLR": if (keyOk) lock (sync) { baseImg[pos] = null; baseCell[pos] = null; Draw(pos); } break;
                    case "CLRALL": lock (sync) { for (int i = 0; i < 18; i++) { baseImg[i] = null; baseCell[i] = null; } DrawAll(); } break;
                    case "STP": lock (sync) Commit(); break;
                    case "BRI": lock (sync) Brightness(int.Parse(p[1])); break;
                    case "RESET": lock (sync) { Init(int.Parse(p[1])); DrawAll(); } break;
                    case "ANIM": {
                        if (!keyOk) break;
                        var l = KeyLayer(p[2], pos);
                        lock (sync) { Start(l); anim[pos] = l; Draw(pos); Commit(); }
                        Emit("LOADED anim" + pos + " " + l.Delays.Length);
                        break;
                    }
                    case "ANIMOFF": if (keyOk) lock (sync) { anim[pos] = null; Draw(pos); Commit(); } break;
                    case "ALERT": {
                        if (!keyOk) break;
                        var l = AlertLayer(pos, p[2]);
                        lock (sync) { Start(l); alert[pos] = l; Draw(pos); Commit(); }
                        break;
                    }
                    case "ALERTOFF": if (keyOk) lock (sync) { alert[pos] = null; Draw(pos); Commit(); } break;
                    case "SAVER": {
                        string what = line.Substring(6).Trim();
                        int[] delays;
                        var frames = what == "builtin:starfield" ? Starfield(out delays) : Frames(what, out delays);
                        var l = DeckLayer(frames, delays);
                        lock (sync) { Start(l); saver = l; DrawAll(); }
                        Emit("LOADED saver " + delays.Length);
                        break;
                    }
                    case "SAVEROFF": lock (sync) { saver = null; DrawAll(); } break;
                }
            } catch (FileNotFoundException e) { Console.Error.WriteLine("bad command " + p[0] + ": " + e.Message); }   // a missing GIF is not a dead deck
            catch (DirectoryNotFoundException e) { Console.Error.WriteLine("bad command " + p[0] + ": " + e.Message); }
            catch (IOException e) { Emit("GONE write: " + e.Message); return 3; }
            catch (Exception e) { Console.Error.WriteLine("bad command " + p[0] + ": " + e.Message); }   // e.g. an unreadable image: skip it
        }
        return 0;   // stdin closed: plugin.cjs is gone
    }
}
