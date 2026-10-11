using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>How one picture is sent: its widest (0: as it is), quality (1 to 100 for JPEG; Lossless for PNG, every pixel exact), and the most pictures a second.</summary>
    internal readonly record struct VideoSettings(int MaxWidth, int Quality, int Fps)
    {
        public const int Lossless = 101;

        /// <summary>
        /// The steps the picture moves along by itself, best first, like the sound's: as good as the line
        /// carries. There is no setting: it starts at Start (quick to arrive) and climbs while pictures
        /// are confirmed quickly, down a step the moment they are not.
        /// </summary>
        public static readonly VideoSettings[] Ladder =
        {
            new(0, Lossless, 15), // every pixel exactly as on the remote screen
            new(0, 92, 15),
            new(0, 82, 15),
            new(1920, 72, 12),
            new(1600, 62, 10),
            new(1280, 52, 8),
            new(960, 42, 6),
        };
        public const int Start = 2;
    }

    /// <summary>One picture of the screen: 32-bit BGRX, top row first, no padding.</summary>
    internal sealed class Frame
    {
        public required int Width, Height;
        public required byte[] Pixels;
    }

    /// <summary>
    /// The screen, taken with GDI (in Windows, no packages). Run on the thread that asks: the agent's
    /// own, which follows the desktop that has the keyboard, so the lock screen and UAC prompts are
    /// seen from the service as well as the desktop. Null when there is nothing to take (a desktop
    /// this process may not see, such as the lock screen from the window).
    /// </summary>
    internal static class ScreenCapture
    {
        public static Frame? Capture(int maxWidth)
        {
            // Real pixels: a thread not aware of the display's scaling gets a blurred, smaller copy.
            IntPtr dpi = SetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
            IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero, mem2 = IntPtr.Zero, bmp2 = IntPtr.Zero;
            try
            {
                if (Native.FollowInputDesktop) Native.FollowInputDesktopNow();
                int w = GetSystemMetrics(0), h = GetSystemMetrics(1); // the main screen
                if (w <= 0 || h <= 0) return null;
                int ow = w, oh = h;
                if (maxWidth > 0 && w > maxWidth) { ow = maxWidth; oh = Math.Max(1, (int)((long)h * maxWidth / w)); }
                screen = GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return null;
                mem = CreateCompatibleDC(screen);
                bmp = CreateCompatibleBitmap(screen, w, h);
                IntPtr was = SelectObject(mem, bmp);
                bool ok = BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY | CAPTUREBLT);
                if (ok) DrawCursor(mem);
                SelectObject(mem, was);
                if (!ok) return null;
                IntPtr take = bmp;
                if (ow != w)
                {
                    mem2 = CreateCompatibleDC(screen);
                    bmp2 = CreateCompatibleBitmap(screen, ow, oh);
                    IntPtr was2 = SelectObject(mem2, bmp2);
                    was = SelectObject(mem, bmp);
                    SetStretchBltMode(mem2, 4); // HALFTONE: smaller, still readable
                    SetBrushOrgEx(mem2, 0, 0, IntPtr.Zero);
                    StretchBlt(mem2, 0, 0, ow, oh, mem, 0, 0, w, h, SRCCOPY);
                    SelectObject(mem, was);
                    SelectObject(mem2, was2);
                    take = bmp2;
                }
                var info = new BITMAPINFOHEADER { biSize = 40, biWidth = ow, biHeight = -oh, biPlanes = 1, biBitCount = 32 };
                byte[] pixels = new byte[ow * oh * 4];
                if (GetDIBits(mem, take, 0, (uint)oh, pixels, ref info, 0) != oh) return null;
                return new Frame { Width = ow, Height = oh, Pixels = pixels };
            }
            catch { return null; }
            finally
            {
                if (bmp2 != IntPtr.Zero) DeleteObject(bmp2);
                if (mem2 != IntPtr.Zero) DeleteDC(mem2);
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                SetThreadDpiAwarenessContext(dpi);
            }
        }

        /// <summary>The mouse pointer, which a screen copy leaves out: whoever watches sees where it is.</summary>
        private static void DrawCursor(IntPtr dc)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
            int hx = 0, hy = 0;
            if (GetIconInfo(ci.hCursor, out var ii))
            {
                hx = ii.xHotspot; hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            DrawIconEx(dc, ci.x - hx, ci.y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
        }

        private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
        [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    }

    /// <summary>
    /// The screen as a stream of updates, one per viewer. Each update holds only what changed since
    /// the last one that viewer got (64-pixel tiles, those side by side joined into strips, each a
    /// JPEG), or everything for a fresh start: a still screen costs nothing, typing costs a few kB.
    ///
    /// An update: u8 1, u16 width, u16 height, u8 flags (1 = everything), u16 strips, then per strip
    /// u16 x, u16 y, u16 width, u16 height, i32 length, JPEG (or PNG, when lossless).
    /// </summary>
    internal sealed class ScreenVideo
    {
        public const int Tile = 64;
        private readonly Func<int, Frame?> _capture;
        private readonly Dictionary<int, Frame> _sent = new(); // what each viewer has now
        private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        public ScreenVideo(Func<int, Frame?> capture) => _capture = capture;

        public int Viewers { get { lock (_sent) return _sent.Count; } }

        /// <summary>The next update for this viewer: null if there is no picture to take, empty if nothing changed.</summary>
        public byte[]? Update(int viewer, bool everything, VideoSettings settings)
        {
            var f = _capture(settings.MaxWidth);
            if (f == null) return null;
            Frame? had;
            lock (_sent) _sent.TryGetValue(viewer, out had);
            bool all = everything || had == null || had.Width != f.Width || had.Height != f.Height;
            var strips = new List<Rectangle>();
            for (int y = 0; y < f.Height; y += Tile)
            {
                int h = Math.Min(Tile, f.Height - y), runFrom = -1;
                for (int x = 0; ; x += Tile)
                {
                    bool changed = x < f.Width && (all || Changed(had!, f, x, y, Math.Min(Tile, f.Width - x), h));
                    if (changed && runFrom < 0) runFrom = x;
                    if (!changed && runFrom >= 0) { strips.Add(new Rectangle(runFrom, y, Math.Min(x, f.Width) - runFrom, h)); runFrom = -1; }
                    if (x >= f.Width) break;
                }
            }
            if (strips.Count == 0) return Array.Empty<byte>();
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write((byte)1); w.Write((ushort)f.Width); w.Write((ushort)f.Height); w.Write((byte)(all ? 1 : 0)); w.Write((ushort)strips.Count);
                bool lossless = settings.Quality >= VideoSettings.Lossless;
                using var q = new EncoderParameters(1);
                q.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(settings.Quality, 1, 100));
                foreach (var r in strips)
                {
                    w.Write((ushort)r.X); w.Write((ushort)r.Y); w.Write((ushort)r.Width); w.Write((ushort)r.Height);
                    long lengthAt = ms.Position;
                    w.Write(0);
                    w.Flush();
                    long start = ms.Position;
                    using (var b = Piece(f, r)) { if (lossless) b.Save(ms, ImageFormat.Png); else b.Save(ms, Jpeg, q); }
                    long end = ms.Position;
                    ms.Position = lengthAt;
                    w.Write((int)(end - start));
                    w.Flush();
                    ms.Position = end;
                }
            }
            lock (_sent) _sent[viewer] = f;
            return ms.ToArray();
        }

        public void Forget(int viewer) { lock (_sent) _sent.Remove(viewer); }

        private static bool Changed(Frame a, Frame b, int x, int y, int w, int h)
        {
            for (int row = y; row < y + h; row++)
            {
                int at = (row * a.Width + x) * 4;
                if (!a.Pixels.AsSpan(at, w * 4).SequenceEqual(b.Pixels.AsSpan(at, w * 4))) return true;
            }
            return false;
        }

        private static Bitmap Piece(Frame f, Rectangle r)
        {
            var b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
            var d = b.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int row = 0; row < r.Height; row++)
                    Marshal.Copy(f.Pixels, ((r.Y + row) * f.Width + r.X) * 4, d.Scan0 + row * d.Stride, r.Width * 4);
            }
            finally { b.UnlockBits(d); }
            return b;
        }
    }

    /// <summary>The viewer's copy of the remote screen, kept up to date from the updates. Lock Gate to read Picture.</summary>
    internal sealed class VideoCanvas : IDisposable
    {
        public readonly object Gate = new();
        public Bitmap? Picture { get; private set; }

        /// <summary>
        /// Applies one update; returns how many strips it held. Throws on a damaged one. Decoded before
        /// the lock is taken, so a big picture never holds up the window drawing the last one. Only JPEG
        /// and PNG of exactly the size given are taken: anything else (another image kind, a size that
        /// does not fit, a length past the end) is damage.
        /// </summary>
        public int Apply(byte[] m, int offset)
        {
            using var r = new BinaryReader(new MemoryStream(m, offset, m.Length - offset));
            if (r.ReadByte() != 1) throw new InvalidDataException("a picture of a kind this version does not know");
            int width = r.ReadUInt16(), height = r.ReadUInt16();
            bool all = (r.ReadByte() & 1) != 0;
            int count = r.ReadUInt16();
            if (width == 0 || height == 0) throw new InvalidDataException("an empty picture");
            var pieces = new List<(Rectangle At, Image Image)>(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    int x = r.ReadUInt16(), y = r.ReadUInt16(), w = r.ReadUInt16(), h = r.ReadUInt16(), n = r.ReadInt32();
                    long left = r.BaseStream.Length - r.BaseStream.Position;
                    if (n < 8 || n > left || w == 0 || h == 0 || x + w > width || y + h > height) throw new InvalidDataException("a damaged picture");
                    byte[] data = r.ReadBytes(n);
                    bool jpeg = data[0] == 0xFF && data[1] == 0xD8, png = data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
                    if (!jpeg && !png) throw new InvalidDataException("a picture of a kind this version does not take");
                    var img = Image.FromStream(new MemoryStream(data));
                    pieces.Add((new Rectangle(x, y, w, h), img));
                    if (img.Width != w || img.Height != h) throw new InvalidDataException("a picture of the wrong size");
                }
                lock (Gate)
                {
                    if (Picture == null || all || Picture.Width != width || Picture.Height != height)
                    {
                        if (!all) throw new InvalidDataException("a change to a picture this PC does not have");
                        Picture?.Dispose();
                        Picture = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                    }
                    using var g = Graphics.FromImage(Picture);
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    foreach (var (at, img) in pieces) g.DrawImage(img, at, new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                }
            }
            finally { foreach (var (_, img) in pieces) img.Dispose(); }
            return count;
        }

        /// <summary>A pixel of the copy (tests), or Empty if there is none yet.</summary>
        public Color At(int x, int y) { lock (Gate) return Picture == null || x >= Picture.Width || y >= Picture.Height ? Color.Empty : Picture.GetPixel(x, y); }

        public void Dispose() { lock (Gate) { Picture?.Dispose(); Picture = null; } }
    }

    /// <summary>
    /// Watching the remote screen: its own connection (a video lane: encrypted, proven like a file
    /// lane), which comes back by itself if it drops, starting from a whole picture. Each update is
    /// confirmed once it is on the canvas, and the host sends the next only then, so pictures never
    /// queue up on a slow connection: what is shown is always the newest the line can carry.
    /// </summary>
    internal sealed class VideoStream : IDisposable
    {
        public readonly VideoCanvas Canvas = new();
        /// <summary>A new picture is on the canvas. Raised on the stream's own thread.</summary>
        public event Action? Updated;
        /// <summary>What the host says about the picture (none to show, and why). Raised on the stream's own thread.</summary>
        public event Action<string>? Note;
        /// <summary>Raised once, when this stream is disposed.</summary>
        public event Action? Stopped;
        public int Pictures, LastStrips, Reconnects;
        public long Bytes;
        private readonly Func<(TcpClient, SecureLink)?> _dial;
        private volatile bool _stop;
        private volatile TcpClient? _tcp;
        private volatile SecureLink? _link;
        private readonly object _sendGate = new();

        public VideoStream(Func<(TcpClient, SecureLink)?> dial)
        {
            _dial = dial;
            new Thread(Loop) { IsBackground = true, Name = "Kova remote screen" }.Start();
        }

        /// <summary>Nothing to look at (the window is minimized): the host sends nothing, and takes no pictures, until resumed.</summary>
        public void Pause(bool paused)
        {
            _paused = paused;
            Send(new[] { Protocol.VideoPause, (byte)(paused ? 1 : 0) });
        }
        private volatile bool _paused;

        /// <summary>A whole new picture, rather than only what changes.</summary>
        public void Refresh() => Send(new[] { Protocol.VideoAgain });

        private void Send(byte[] m)
        {
            var tcp = _tcp; var link = _link;
            if (tcp == null || link == null) return;
            try { lock (_sendGate) link.Send(tcp.GetStream(), m); } catch { }
        }

        private void Loop()
        {
            bool first = true;
            while (!_stop)
            {
                var lane = _dial();
                if (lane is not var (tcp, link)) { Sleep(2000); continue; }
                if (!first) Interlocked.Increment(ref Reconnects);
                first = false;
                _tcp = tcp; _link = link;
                bool toldWhy = false, saw = false;
                try
                {
                    var stream = tcp.GetStream();
                    stream.ReadTimeout = 15_000; // the host says something at least every few seconds
                    if (_paused) Send(new[] { Protocol.VideoPause, (byte)1 });
                    byte[]? buffer = null;
                    while (!_stop)
                    {
                        int n = link.Receive(stream, ref buffer);
                        if (n < 1) continue;
                        switch (buffer![0])
                        {
                            case Protocol.VideoPicture when n >= 5:
                                {
                                    uint number = BitConverter.ToUInt32(buffer, 1);
                                    byte[] update = buffer.AsSpan(5, n - 5).ToArray();
                                    LastStrips = Canvas.Apply(update, 0);
                                    saw = true;
                                    Interlocked.Increment(ref Pictures);
                                    Interlocked.Add(ref Bytes, n);
                                    Updated?.Invoke();
                                    byte[] ack = new byte[5];
                                    ack[0] = Protocol.VideoGot;
                                    BitConverter.TryWriteBytes(ack.AsSpan(1), number);
                                    Send(ack);
                                    break;
                                }
                            case Protocol.VideoNote:
                                toldWhy = true;
                                Note?.Invoke(System.Text.Encoding.UTF8.GetString(buffer, 1, n - 1));
                                break;
                        }
                    }
                }
                catch { }
                finally
                {
                    _tcp = null; _link = null;
                    try { tcp.Dispose(); } catch { }
                    link.Dispose();
                }
                // Turned away with a reason and nothing shown (8 PCs already watching): asking again every
                // second would only cost both PCs a login each time. Every 20 seconds is plenty.
                Sleep(toldWhy && !saw ? 20_000 : 1000);
            }
        }

        private void Sleep(int ms) { for (int i = 0; i < ms / 50 && !_stop; i++) Thread.Sleep(50); }

        /// <summary>Test only: cuts the line, as a dropped connection would.</summary>
        internal void TestCut() { try { _tcp?.Dispose(); } catch { } }

        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            try { _tcp?.Dispose(); } catch { }
            Canvas.Dispose();
            Stopped?.Invoke();
        }
    }

    /// <summary>Test only: a made-up screen (a gradient with a square on it) the tests can move the square on.</summary>
    internal sealed class TestScreen
    {
        public int Width = 640, Height = 360;
        public volatile int SquareX = 100, SquareY = 100;
        public const int Square = 40;
        public Frame? Take(int maxWidth)
        {
            var p = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int i = (y * Width + x) * 4;
                    bool sq = x >= SquareX && x < SquareX + Square && y >= SquareY && y < SquareY + Square;
                    p[i] = (byte)(sq ? 0 : x * 255 / Width);      // blue
                    p[i + 1] = (byte)(sq ? 0 : y * 255 / Height); // green
                    p[i + 2] = (byte)(sq ? 255 : 128);            // red
                }
            return new Frame { Width = Width, Height = Height, Pixels = p };
        }

        /// <summary>The colour the made-up screen has here.</summary>
        public Color At(int x, int y)
        {
            bool sq = x >= SquareX && x < SquareX + Square && y >= SquareY && y < SquareY + Square;
            return sq ? Color.FromArgb(255, 0, 0) : Color.FromArgb(128, y * 255 / Height, x * 255 / Width);
        }

        /// <summary>Close enough for a JPEG: each colour within the given distance.</summary>
        public static bool Near(Color a, Color b, int by) => Math.Abs(a.R - b.R) <= by && Math.Abs(a.G - b.G) <= by && Math.Abs(a.B - b.B) <= by;
    }
}
