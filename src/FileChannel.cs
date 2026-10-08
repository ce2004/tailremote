using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TailRemote
{
    /// <summary>
    /// Send the clipboard and Send files between the two PCs, on up to 16 TCP connections
    /// ("lanes") of their own, so they never hold up a keystroke.
    ///
    /// Every lane has its own encryption keys (fresh random values from both PCs go into
    /// them) and its own two threads: one reads, encrypts and sends, one receives, decrypts
    /// and writes to disk, so a transfer uses every core. Files are cut into pieces that
    /// all lanes share, so several files, or several parts of one big file, travel at once.
    ///
    /// Nothing is lost when a connection breaks. The receiver confirms every piece once it
    /// is on disk; a piece not confirmed goes again on another lane. A lane that dies, or
    /// stalls for 10 seconds, is closed and reopened (the controlling PC dials; the
    /// host only answers), and the transfer carries on where it was. Even the main
    /// connection dropping does not stop it: the channel outlives it and its lanes join
    /// the next connection. Only a whole minute with nothing getting through gives up.
    /// The sender says "sent" only when the receiver has confirmed the whole batch, so the
    /// two PCs always agree on how it ended.
    ///
    /// Sending is paced (Rate): the controlling PC watches the audio path's ping and
    /// slows file data the moment it starts hurting the sound, then speeds back up. At low
    /// speeds fewer lanes and smaller pieces are used, so nothing queues up anywhere.
    /// A new batch replaces the one still going.
    /// </summary>
    internal sealed class FileChannel : IDisposable
    {
        // Wire messages, each one encrypted frame, on any lane:
        private const byte Ping = 0x60;     // nothing: this quiet lane still works
        private const byte Offer = 0x61;    // u32 id, u8 kind, u32 entries, u64 total, u32 first entry, then per entry: i64 length (-1: a folder), u16 path bytes, UTF-8 path
        private const byte Ready = 0x62;    // u32 id: the receiver has made the files; send the pieces
        private const byte Piece = 0x63;    // u32 id, u32 entry, i64 offset, then the bytes
        private const byte Got = 0x64;      // u32 id, u32 entry, i64 offset, u32 length: that piece is on disk
        private const byte Finished = 0x65; // u32 id, u8 outcome, UTF-8 why (for the sender to show)
        private const byte Stop = 0x66;     // u32 id: the sender stopped it
        private const byte Ask = 0x67;      // u32 id: how did it end? (the answer was lost with a lane)
        private const byte KindFiles = 1, KindText = 2, KindDownloads = 3; // clipboard files, clipboard text, Send files
        private const byte EndedWell = 0, EndedBadly = 1, EndedStopped = 2;

        /// <summary>The most lanes at once.</summary>
        public const int MaxLanes = 16;
        /// <summary>The most text a clipboard may carry: 512 MB.</summary>
        public const long MaxText = 512L << 20;
        private const int GiveUpMs = 60_000, StallMs = 10_000, QuietMs = 12_000, PingMs = 3_000, OfferPartBytes = 1 << 20;

        private static long Now => Environment.TickCount64;

        /// <summary>Text that arrived for the clipboard.</summary>
        public Action<string>? TextReceived;
        /// <summary>Files that arrived for the clipboard: the top-level files and folders, in the holding folder.</summary>
        public Action<string[]>? FilesReceived;
        /// <summary>How a transfer is going, either way; at most four times a second, and once at the end.</summary>
        public Action<Transfer>? Progress;
        /// <summary>Bytes a second this side may send right now (set from the audio ping).</summary>
        public Func<double>? Rate;

        /// <summary>Identifies this channel to the host, so a new main connection gets the same transfers back.</summary>
        public readonly byte[] Id;
        /// <summary>Closed for good.</summary>
        public bool Gone => _closed;

        /// <summary>True while a batch is going either way (the client then measures its ping more often).</summary>
        public bool Busy => _out != null || _in != null;

        public sealed class Transfer
        {
            public bool Outgoing, Finished, Failed;
            /// <summary>For the clipboard (Send clipboard), not Send files.</summary>
            public bool Clipboard;
            /// <summary>Stopped on purpose (Stop, or something newer sent), not a real failure.</summary>
            public bool Cancelled;
            /// <summary>No connection to the other PC right now: waiting for it to come back.</summary>
            public bool Waiting;
            public string What = "";
            public long Done, Total;
            /// <summary>Each file moving right now, with how far it has got (a fresh list each report).</summary>
            public IReadOnlyList<(string Name, long Done, long Total)> Files = Array.Empty<(string, long, long)>();
            public double BytesPerSecond;
            public string? Result;
            public TimeSpan? Left => BytesPerSecond > 1 && Total > Done ? TimeSpan.FromSeconds((Total - Done) / BytesPerSecond) : null;
        }

        /// <summary>Where arriving clipboard files are held until pasted: %LOCALAPPDATA%\TailRemote\Clipboard.</summary>
        public static string Staging => StagingOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailRemote", "Clipboard");
        /// <summary>The self-test receives into a temporary folder instead.</summary>
        public static string? StagingOverride;
        /// <summary>Where Send files puts what arrives: Downloads\TailRemote.</summary>
        public static string Downloads => DownloadsOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TailRemote");
        public static string? DownloadsOverride;

        /// <summary>The keys' purpose for one lane: both PCs' fresh values for it, so no two lanes ever share keys.</summary>
        public static string LanePurpose(ReadOnlySpan<byte> hostValue, ReadOnlySpan<byte> clientValue) =>
            "files " + Convert.ToHexString(hostValue) + Convert.ToHexString(clientValue) + " ";

        private sealed class Lane
        {
            public required TcpClient Tcp;
            public required NetworkStream Stream;
            public required SecureLink Link;
            public int Slot;
            public readonly ConcurrentQueue<byte[]> Control = new();
            public readonly AutoResetEvent Wake = new(false);
            public long LastIn = Now, LastOut = Now;
            public volatile bool Dead;
        }

        private readonly object _lanesGate = new();
        private volatile Lane[] _lanes = Array.Empty<Lane>(); // by slot; replaced, never changed
        private volatile Func<(TcpClient Tcp, SecureLink Link)?>? _dial;
        private readonly AutoResetEvent _dialWake = new(false);
        private volatile bool _closed;
        private long _detachedAt;

        public FileChannel(byte[]? id = null)
        {
            Id = id ?? RandomNumberGenerator.GetBytes(16);
            _nextId = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
            new Thread(WatchLoop) { IsBackground = true, Name = "TailRemote files watch" }.Start();
            new Thread(DialLoop) { IsBackground = true, Name = "TailRemote files dial" }.Start();
        }

        /// <summary>The controlling PC only: opens one more lane to the host, or returns null.</summary>
        public Func<(TcpClient Tcp, SecureLink Link)?>? Dial
        {
            set { _dial = value; Interlocked.Exchange(ref _detachedAt, 0); _dialWake.Set(); }
        }

        /// <summary>
        /// The main connection is gone. The lanes stay (they may still work) and so do the
        /// transfers: a new connection picks them up. Closed after a minute without one.
        /// </summary>
        public void Detach()
        {
            _dial = null;
            Interlocked.Exchange(ref _detachedAt, Now);
        }

        /// <summary>Closed on purpose: whatever is going is stopped, and the other PC told so.</summary>
        public void Dispose()
        {
            if (_closed) return;
            if (_out is Out o) EndOut(o, EndedStopped, "Stopped sending " + o.What + ": the connection was closed.", tell: true);
            if (_in is In b) EndIn(b, EndedStopped, "Stopped receiving " + b.T.What + ": the connection was closed.", "The other PC closed the connection.", null, tell: true);
            _closed = true;
            _dial = null;
            _dialWake.Set();
            var lanes = _lanes;
            // A moment for the goodbyes above to go out, then the lanes close.
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(300); foreach (var l in lanes) Kill(l); });
        }

        // ---- Lanes ----

        /// <summary>How many lanes this speed can use: one per 2 MB/s, from 1 to 16.</summary>
        private int Wanted() => Rate?.Invoke() is double r && r > 0 ? (int)Math.Clamp(r / (2 << 20), 1, MaxLanes) : MaxLanes;

        public void AddLane(TcpClient tcp, SecureLink link)
        {
            if (_closed) { tcp.Dispose(); return; }
            tcp.NoDelay = true;
            // No timeouts: a lane that stops is found by the watch (nothing heard for 12 s, or
            // pieces not confirmed for 10 s) and closed, which ends any wait on it.
            tcp.SendTimeout = 0;
            tcp.ReceiveTimeout = 0;
            var stream = tcp.GetStream();
            stream.ReadTimeout = Timeout.Infinite;
            var lane = new Lane { Tcp = tcp, Stream = stream, Link = link };
            Lane? extra = null;
            lock (_lanesGate)
            {
                var list = _lanes.ToList();
                if (list.Count >= MaxLanes) { extra = list.OrderBy(l => l.LastIn).First(); list.Remove(extra); }
                for (int s = 0; ; s++) if (list.All(l => l.Slot != s)) { lane.Slot = s; break; }
                list.Add(lane);
                _lanes = list.OrderBy(l => l.Slot).ToArray();
            }
            if (extra != null) Kill(extra);
            Interlocked.Exchange(ref _detachedAt, 0);
            // Stops that may not have reached the other PC yet (sent while it was unreachable).
            lock (_stops) foreach (var (id, at) in _stops) if (Now - at < 2 * GiveUpMs) lane.Control.Enqueue(IdMessage(Stop, id));
            // Below normal: keys and sound run on their own threads at higher priority, so even a
            // transfer using every core can never hold up a keystroke or the audio.
            new Thread(() => ReadLoop(lane), 256 << 10) { IsBackground = true, Name = "TailRemote files in", Priority = ThreadPriority.BelowNormal }.Start();
            new Thread(() => WriteLoop(lane), 256 << 10) { IsBackground = true, Name = "TailRemote files out", Priority = ThreadPriority.BelowNormal }.Start();
            _out?.Changed.Set();
        }

        private void Kill(Lane lane)
        {
            lock (_lanesGate)
            {
                if (lane.Dead) return;
                lane.Dead = true;
                _lanes = _lanes.Where(l => l != lane).ToArray();
            }
            try { lane.Tcp.Dispose(); } catch { }
            lane.Wake.Set();
            // Its pieces not confirmed yet go again, on the other lanes.
            if (_out is Out o)
            {
                lock (o.Gate)
                {
                    foreach (var p in o.InFlight.Values.Where(p => p.On == lane).ToList())
                    {
                        o.InFlight.Remove((p.Entry, p.Offset));
                        p.On = null;
                        o.Retry.Enqueue(p);
                    }
                    o.LaneUnacked.Remove(lane);
                    o.LaneHeard.Remove(lane);
                }
                foreach (var l in _lanes) l.Wake.Set();
                o.Changed.Set();
            }
            _dialWake.Set();
        }

        private void DialLoop()
        {
            int failures = 0;
            while (!_closed)
            {
                var dial = _dial;
                if (dial == null || _lanes.Length >= Wanted()) { _dialWake.WaitOne(500); continue; }
                (TcpClient Tcp, SecureLink Link)? lane = null;
                try { lane = dial(); } catch { }
                if (lane is { } l)
                {
                    failures = 0;
                    if (_dial == null || _closed) l.Tcp.Dispose();
                    else AddLane(l.Tcp, l.Link);
                }
                else _dialWake.WaitOne(Math.Min(250 << Math.Min(++failures, 3), 2000));
            }
        }

        /// <summary>Four times a second: closes lanes that went quiet or stalled, and gives up on what cannot finish.</summary>
        private void WatchLoop()
        {
            while (!_closed)
            {
                Thread.Sleep(250);
                long now = Now;
                foreach (var l in _lanes)
                    if (now - l.LastIn > QuietMs) { DiagLog.Write("files: a lane went quiet; closing it"); Kill(l); }
                if (_out is Out o)
                {
                    List<Lane> stalled;
                    lock (o.Gate) stalled = o.LaneUnacked.Where(kv => kv.Value > 0 && now - o.LaneHeard[kv.Key] > StallMs).Select(kv => kv.Key).ToList();
                    foreach (var l in stalled) { DiagLog.Write("files: a lane stalled; closing it, its pieces go again"); Kill(l); }
                }
                if (_in is In b && now - b.LastActivity > GiveUpMs)
                    EndIn(b, EndedBadly, "Nothing came from the other PC for a minute, so " + b.T.What + " was stopped. Send it again.",
                        "The other PC heard nothing for a minute and gave up.", null, tell: true);
                long d = Interlocked.Read(ref _detachedAt);
                if (d != 0 && now - d > GiveUpMs + 10_000 && (_lanes.Length == 0 || (_out == null && _in == null))) { Dispose(); return; }
            }
        }

        // The loops' steps are methods of their own: a variable in a long-lived loop inside a try
        // stays alive for the whole thread, and an idle lane then kept the last batch it sent
        // (clipboard text and all) in memory for good.
        private void WriteLoop(Lane lane)
        {
            try { while (!lane.Dead) if (!WriteOne(lane)) lane.Wake.WaitOne(200); }
            catch (Exception e) { DiagLog.Write("files: a lane could not send: " + e.Message); }
            Kill(lane);
        }

        /// <summary>Sends one thing on the lane; false if there was nothing to send.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool WriteOne(Lane lane)
        {
            if (lane.Control.TryDequeue(out var c)) { Send(lane, c); return true; }
            if (_out is Out o && NextPiece(o, lane) is PieceRef p) { SendPiece(o, lane, p); return true; }
            if (Now - lane.LastOut >= PingMs) Send(lane, new[] { Ping });
            return false;
        }

        private static void Send(Lane lane, byte[] m)
        {
            lane.Link.Send(lane.Stream, m);
            lane.LastOut = Now;
        }

        private static void Tell(Lane lane, byte[] m)
        {
            lane.Control.Enqueue(m);
            lane.Wake.Set();
        }

        private long _pauseUntil;

        /// <summary>Test only (--chaostest): stops reading the network for a while, like a stalled PC.</summary>
        internal void TestPause(int ms) => Interlocked.Exchange(ref _pauseUntil, Now + ms);

        /// <summary>Test only (--chaostest): cuts every lane at once, as a broken connection would.</summary>
        internal void TestCutLanes() { foreach (var l in _lanes) Kill(l); }

        /// <summary>Test only: how many lanes are open.</summary>
        internal int TestLanes => _lanes.Length;

        private void ReadLoop(Lane lane)
        {
            try
            {
                while (!lane.Dead)
                {
                    while (Now < Interlocked.Read(ref _pauseUntil) && !lane.Dead) Thread.Sleep(20); // test only
                    ReceiveOne(lane);
                }
            }
            catch (Exception e) { DiagLog.Write("files: a lane closed: " + e.Message); }
            Kill(lane);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ReceiveOne(Lane lane)
        {
            byte[] m = lane.Link.Receive(lane.Stream);
            lane.LastIn = Now;
            if (m.Length == 0) return;
            switch (m[0])
            {
                case Offer when m.Length >= 22: OnOffer(lane, m); break;
                case Piece when m.Length > 17: OnPiece(lane, m); break;
                case Ready when m.Length == 5: OnReady(BitConverter.ToUInt32(m, 1)); break;
                case Got when m.Length == 21: OnGot(lane, m); break;
                case Finished when m.Length >= 6:
                    if (_out is Out o && o.Id == BitConverter.ToUInt32(m, 1)) EndOut(o, m[5], Encoding.UTF8.GetString(m, 6, m.Length - 6), tell: false);
                    break;
                case Stop when m.Length == 5: OnStop(BitConverter.ToUInt32(m, 1)); break;
                case Ask when m.Length == 5: OnAsk(lane, BitConverter.ToUInt32(m, 1)); break;
            }
        }

        private static byte[] IdMessage(byte type, uint id)
        {
            byte[] m = new byte[5];
            m[0] = type;
            BitConverter.TryWriteBytes(m.AsSpan(1), id);
            return m;
        }

        private static byte[] FinishedMessage(uint id, byte outcome, string why)
        {
            byte[] w = Encoding.UTF8.GetBytes(why);
            byte[] m = new byte[6 + Math.Min(w.Length, 2000)];
            m[0] = Finished;
            BitConverter.TryWriteBytes(m.AsSpan(1), id);
            m[5] = outcome;
            w.AsSpan(0, m.Length - 6).CopyTo(m.AsSpan(6));
            return m;
        }

        // ---- Sending ----

        private sealed record Entry(string Rel, long Length, string? Full, byte[]? Data);

        private sealed class PieceRef
        {
            public readonly int Entry, Length;
            public readonly long Offset;
            public Lane? On;
            public PieceRef(int entry, long offset, int length) { Entry = entry; Offset = offset; Length = length; }
        }

        private sealed class Out
        {
            public required uint Id;
            public required byte Kind;
            public required string What;
            public required volatile List<Entry> Entries;
            public long Total;
            public readonly object Gate = new();
            public readonly ManualResetEventSlim Changed = new();
            public volatile bool Ready, Ended;
            public byte Outcome;
            public string Why = "";
            public long LastProgress = Now;
            // Under Gate:
            public readonly Queue<PieceRef> Retry = new();
            public long[] Next = Array.Empty<long>();         // per file: the next offset to hand out
            public int Fresh;                                 // the first file not started yet
            public readonly HashSet<int> Open = new();         // started, with pieces still to hand out
            public readonly SortedSet<int> Active = new();     // started, not all confirmed (for the per-file progress)
            public readonly Dictionary<Lane, int> LaneFile = new(); // each lane keeps to its own file
            public readonly Dictionary<(int, long), PieceRef> InFlight = new();
            public readonly Dictionary<Lane, long> LaneUnacked = new(), LaneHeard = new();
            public long Acked;
            public long[] EntryAcked = Array.Empty<long>();
            // Under Handles:
            public SafeFileHandle?[] Handles = Array.Empty<SafeFileHandle?>();

            public void Read(int i, long offset, Span<byte> into)
            {
                var e = Entries[i];
                if (e.Data != null) { e.Data.AsSpan((int)offset, into.Length).CopyTo(into); return; }
                SafeFileHandle h;
                lock (Handles)
                {
                    if (Ended) throw new OperationCanceledException();
                    h = Handles[i] ??= File.OpenHandle(e.Full!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                for (int got = 0; got < into.Length;)
                {
                    int n = RandomAccess.Read(h, into[got..], offset + got);
                    if (n <= 0) throw new IOException(Path.GetFileName(e.Full) + " got shorter while it was being sent.");
                    got += n;
                }
            }

            public void Close(int i)
            {
                lock (Handles) { Handles[i]?.Dispose(); Handles[i] = null; }
            }

            public void CloseAll()
            {
                lock (Handles) for (int i = 0; i < Handles.Length; i++) { Handles[i]?.Dispose(); Handles[i] = null; }
            }

            public IEnumerable<byte[]> OfferParts()
            {
                int i = 0;
                do
                {
                    var part = new MemoryStream();
                    var w = new BinaryWriter(part);
                    w.Write(Offer); w.Write(Id); w.Write(Kind); w.Write(Entries.Count); w.Write(Total); w.Write(i);
                    for (; i < Entries.Count && part.Length < OfferPartBytes; i++)
                    {
                        byte[] path = Encoding.UTF8.GetBytes(Entries[i].Rel);
                        w.Write(Entries[i].Length);
                        w.Write((ushort)path.Length);
                        w.Write(path);
                    }
                    yield return part.ToArray();
                } while (i < Entries.Count);
            }
        }

        private readonly object _sendGate = new(), _latestGate = new();
        private volatile Out? _out;
        private Out? _latest;
        private uint _nextId;
        private readonly List<(uint Id, long At)> _stops = new();

        /// <summary>
        /// Sends files and folders. toClipboard: onto the other PC's clipboard (Send clipboard), ready for
        /// Control V; otherwise into its Downloads\TailRemote (Send files). Blocking: run it off the
        /// window's thread. Replaces any batch still going.
        /// </summary>
        public void SendFiles(IReadOnlyList<string> items, bool toClipboard = true)
        {
            var entries = new List<Entry>();
            var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in items)
            {
                bool dir = Directory.Exists(item);
                if (!dir && !File.Exists(item)) continue;
                // Two things with the same name (from different folders): the second becomes "name (2)".
                string name = Path.GetFileName(item.TrimEnd('\\', '/'));
                string root = name;
                for (int n = 2; !tops.Add(root); n++) root = Path.GetFileNameWithoutExtension(name) + " (" + n + ")" + Path.GetExtension(name);
                if (dir)
                {
                    entries.Add(new Entry(root, -1, item, null));
                    foreach (string d in SafeEnumerate(item, true)) entries.Add(new Entry(Path.Combine(root, Path.GetRelativePath(item, d)), -1, d, null));
                    foreach (string f in SafeEnumerate(item, false))
                    {
                        long length;
                        try { length = new FileInfo(f).Length; } catch { continue; } // gone, or not readable: left out
                        entries.Add(new Entry(Path.Combine(root, Path.GetRelativePath(item, f)), length, f, null));
                    }
                }
                else entries.Add(new Entry(root, new FileInfo(item).Length, item, null));
            }
            if (entries.Count == 0) return;
            string what = items.Count == 1 ? Path.GetFileName(items[0].TrimEnd('\\', '/')) : items.Count + " items";
            SendBatch(toClipboard ? KindFiles : KindDownloads, what, entries);
        }

        /// <summary>Sends clipboard text. Blocking. Replaces any batch still going.</summary>
        public void SendText(string text) =>
            SendBatch(KindText, "clipboard text", new List<Entry> { new("", 0, null, Encoding.UTF8.GetBytes(text)) });

        private static IEnumerable<string> SafeEnumerate(string dir, bool dirs)
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            try { return dirs ? Directory.EnumerateDirectories(dir, "*", opts).ToList() : Directory.EnumerateFiles(dir, "*", opts).ToList(); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>Test only (--chaostest): sends a batch with any paths at all, as a hostile PC could.</summary>
        internal void TestSendEntries(List<(string Rel, byte[]? Data)> entries) =>
            SendBatch(KindFiles, "test", entries.Select(e => new Entry(e.Rel, e.Data?.Length ?? -1, null, e.Data)).ToList());

        /// <summary>Stops what is being sent and what is being received, if anything.</summary>
        public void CancelSending()
        {
            lock (_latestGate) _latest = null;
            if (_out is Out o) EndOut(o, EndedStopped, "Stopped sending " + o.What + ".", tell: true);
            if (_in is In b) EndIn(b, EndedStopped, "Stopped receiving " + b.T.What + ".", "The other PC stopped receiving " + b.T.What + ".", null, tell: true);
        }

        private void SendBatch(byte kind, string what, List<Entry> entries)
        {
            // Text's length is its bytes'.
            for (int i = 0; i < entries.Count; i++) if (entries[i].Data is byte[] d) entries[i] = entries[i] with { Length = d.Length };
            var o = new Out
            {
                Id = Interlocked.Increment(ref _nextId), Kind = kind, What = what, Entries = entries,
                Total = entries.Sum(e => Math.Max(0, e.Length)),
            };
            o.EntryAcked = new long[entries.Count];
            o.Next = new long[entries.Count];
            o.Handles = new SafeFileHandle?[entries.Count];
            // Sending something new replaces what is still going.
            lock (_latestGate) _latest = o;
            if (_out is Out old) EndOut(old, EndedStopped, "Stopped sending " + old.What + ": something newer was sent.", tell: true);
            lock (_sendGate)
            {
                lock (_latestGate) if (_latest != o) return; // replaced before it even started
                if (_closed) return;
                _out = o;
                try { Run(o); }
                finally
                {
                    _out = null;
                    o.CloseAll();
                    lock (o.Gate) o.Entries = new List<Entry>(); // lets go of the data (a lane still reading finds nothing, and stops)
                    lock (_latestGate) if (_latest == o) _latest = null; // never keeps what was sent (clipboard text can be 512 MB)
                }
            }
        }

        private void Run(Out o)
        {
            var t = new Transfer { Outgoing = true, What = o.What, Total = o.Total, Clipboard = o.Kind != KindDownloads };
            var clock = Stopwatch.StartNew();
            double lastReport = 0;
            Lane? offeredOn = null, askedOn = null;
            long offeredAt = 0, askedAt = 0, allAt = 0;
            DiagLog.Write("files: sending " + o.What + ", " + o.Entries.Count + " entries, " + o.Total + " bytes");
            Progress?.Invoke(t);
            while (true)
            {
                o.Changed.Wait(100);
                o.Changed.Reset();
                if (o.Ended) break;
                long now = Now;
                var lanes = _lanes;
                if (!o.Ready)
                {
                    // The list of what is coming, again on another lane if the first one died.
                    if (lanes.Length > 0 && (offeredOn == null || offeredOn.Dead || now - offeredAt > 5000))
                    {
                        offeredOn = lanes[0];
                        offeredAt = now;
                        foreach (var part in o.OfferParts()) offeredOn.Control.Enqueue(part);
                        offeredOn.Wake.Set();
                    }
                }
                else if (Interlocked.Read(ref o.Acked) == o.Total)
                {
                    // Everything confirmed: the receiver's word on how it ended normally follows at
                    // once. If that was lost with a lane, ask.
                    if (allAt == 0) allAt = now;
                    if (lanes.Length > 0 && now - allAt > 3000 && (askedOn == null || askedOn.Dead || now - askedAt > 3000))
                    {
                        askedOn = lanes[0];
                        askedAt = now;
                        Tell(askedOn, IdMessage(Ask, o.Id));
                    }
                }
                if (now - Interlocked.Read(ref o.LastProgress) > GiveUpMs)
                {
                    EndOut(o, EndedBadly, "Nothing got through to the other PC for a minute, so " + o.What + " was not sent. Send it again.", tell: true);
                    break;
                }
                double s = clock.Elapsed.TotalSeconds;
                if (s - lastReport >= 0.25)
                {
                    lastReport = s;
                    t.Done = Interlocked.Read(ref o.Acked);
                    t.BytesPerSecond = t.Done / Math.Max(0.001, s);
                    t.Waiting = lanes.Length == 0;
                    lock (o.Gate) if (!o.Ended) t.Files = o.Active.Take(32).Select(i => (o.Entries[i].Rel, o.EntryAcked[i], o.Entries[i].Length)).ToArray();
                    Progress?.Invoke(t);
                }
            }
            t.Done = o.Outcome == EndedWell ? o.Total : Interlocked.Read(ref o.Acked); // the receiver has it all, whatever confirmations are still on their way
            t.BytesPerSecond = t.Done / Math.Max(0.001, clock.Elapsed.TotalSeconds);
            t.Finished = true;
            t.Waiting = false;
            t.Files = Array.Empty<(string, long, long)>();
            if (o.Outcome == EndedWell) t.Result = "Sent " + o.What + ", at " + Speed(t.BytesPerSecond) + ".";
            else { t.Failed = true; t.Cancelled = o.Outcome == EndedStopped; t.Result = o.Why; }
            DiagLog.Write("files: " + o.What + ": " + t.Result);
            Progress?.Invoke(t);
        }

        private void EndOut(Out o, byte outcome, string why, bool tell)
        {
            lock (o.Gate)
            {
                if (o.Ended) return;
                o.Ended = true;
                o.Outcome = outcome;
                o.Why = why;
            }
            o.CloseAll();
            if (tell)
            {
                lock (_stops)
                {
                    _stops.Add((o.Id, Now));
                    if (_stops.Count > 32) _stops.RemoveAt(0);
                }
                foreach (var l in _lanes) Tell(l, IdMessage(Stop, o.Id));
            }
            o.Changed.Set();
        }

        private void OnReady(uint id)
        {
            if (_out is not Out o || o.Id != id || o.Ready) return;
            Interlocked.Exchange(ref o.LastProgress, Now);
            o.Ready = true;
            o.Changed.Set();
            foreach (var l in _lanes) l.Wake.Set();
        }

        /// <summary>The next piece for this lane to send, or null (none left, this lane not needed at this speed, or its share already on the way).</summary>
        private PieceRef? NextPiece(Out o, Lane lane)
        {
            if (!o.Ready || o.Ended) return null;
            double rate = Rate?.Invoke() is double r && r > 0 ? r : double.PositiveInfinity;
            int wanted = Wanted();
            int rank = Array.IndexOf(_lanes, lane);
            if (rank < 0 || rank >= wanted) return null;
            // Pieces of about a sixteenth of a second, so pacing is smooth; never more than half a
            // second of data unconfirmed, so nothing queues up anywhere along the way.
            int size = (int)Math.Clamp(rate / 16, 16 << 10, 1 << 20);
            long window = (long)Math.Clamp(rate * 0.5 / wanted, 256 << 10, 32 << 20);
            lock (o.Gate)
            {
                if (o.Ended) return null; // checked again in here: an ended batch lets go of its list of files
                o.LaneUnacked.TryGetValue(lane, out long unacked);
                if (unacked > 0 && unacked + size > window) return null;
                PieceRef p;
                if (o.Retry.Count > 0) p = o.Retry.Dequeue();
                else
                {
                    // Each lane keeps to a file of its own, so many files move side by side, each on
                    // its own thread. With no new file left, it helps the one with the most to go
                    // (one big file is shared by every lane).
                    var entries = o.Entries;
                    int e = o.LaneFile.TryGetValue(lane, out int mine) && mine < entries.Count && o.Next[mine] < entries[mine].Length ? mine : -1;
                    if (e < 0)
                    {
                        while (o.Fresh < entries.Count && entries[o.Fresh].Length <= 0) o.Fresh++;
                        if (o.Fresh < entries.Count) e = o.Fresh++;
                        else
                        {
                            long most = 0;
                            foreach (int i in o.Open) if (entries[i].Length - o.Next[i] > most) { most = entries[i].Length - o.Next[i]; e = i; }
                        }
                        if (e < 0) return null;
                        o.LaneFile[lane] = e;
                    }
                    int n = (int)Math.Min(size, entries[e].Length - o.Next[e]);
                    p = new PieceRef(e, o.Next[e], n);
                    o.Next[e] += n;
                    if (o.Next[e] < entries[e].Length) o.Open.Add(e); else o.Open.Remove(e);
                    o.Active.Add(e);
                }
                p.On = lane;
                if (unacked == 0) o.LaneHeard[lane] = Now;
                o.LaneUnacked[lane] = unacked + p.Length;
                o.InFlight[(p.Entry, p.Offset)] = p;
                return p;
            }
        }

        private void SendPiece(Out o, Lane lane, PieceRef p)
        {
            byte[] m = new byte[17 + p.Length];
            m[0] = Piece;
            BitConverter.TryWriteBytes(m.AsSpan(1), o.Id);
            BitConverter.TryWriteBytes(m.AsSpan(5), p.Entry);
            BitConverter.TryWriteBytes(m.AsSpan(9), p.Offset);
            try { o.Read(p.Entry, p.Offset, m.AsSpan(17)); }
            catch (Exception e)
            {
                if (!o.Ended) EndOut(o, EndedBadly, "Could not send " + o.What + ": " + e.Message, tell: true);
                return;
            }
            // Paced: never faster than the controlling PC says the sound can bear. Short waits, so
            // a new rate or a stop is seen at once.
            for (int wait; (wait = PaceWait(m.Length)) > 0;)
            {
                if (o.Ended || lane.Dead) return;
                Thread.Sleep(wait);
            }
            if (o.Ended) return;
            Send(lane, m);
        }

        private void OnGot(Lane lane, byte[] m)
        {
            if (_out is not Out o || o.Id != BitConverter.ToUInt32(m, 1)) return;
            int e = BitConverter.ToInt32(m, 5);
            long offset = BitConverter.ToInt64(m, 9);
            bool all, entryDone;
            lock (o.Gate)
            {
                if (!o.InFlight.Remove((e, offset), out var p)) return; // already counted (sent twice)
                if (o.LaneUnacked.TryGetValue(lane, out long u)) o.LaneUnacked[lane] = u - p.Length;
                o.LaneHeard[lane] = Now;
                o.Acked += p.Length;
                entryDone = (o.EntryAcked[e] += p.Length) == o.Entries[e].Length;
                if (entryDone) o.Active.Remove(e);
                all = o.Acked == o.Total;
            }
            Interlocked.Exchange(ref o.LastProgress, Now);
            if (entryDone) o.Close(e);
            lane.Wake.Set();
            if (all) o.Changed.Set();
        }

        private readonly object _bucketGate = new();
        private double _allowance;
        private long _bucketAt = Stopwatch.GetTimestamp();

        /// <summary>0 when this many bytes may go now (and takes them), otherwise how many ms to wait first.</summary>
        private int PaceWait(int bytes)
        {
            if (Rate?.Invoke() is not double rate || rate <= 0) return 0;
            lock (_bucketGate)
            {
                long now = Stopwatch.GetTimestamp();
                _allowance = Math.Min(_allowance + (now - _bucketAt) / (double)Stopwatch.Frequency * rate, rate * 0.05 + bytes);
                _bucketAt = now;
                if (_allowance >= bytes) { _allowance -= bytes; return 0; }
                return Math.Clamp((int)Math.Ceiling((bytes - _allowance) / rate * 1000), 1, 50);
            }
        }

        // ---- Receiving ----

        private sealed class In
        {
            public required uint Id;
            public required byte Kind;
            public required (string Rel, long Length)?[] Entries;
            public readonly object Gate = new();
            public readonly Transfer T = new() { What = "files" };
            public readonly Stopwatch Clock = new();
            public long LastActivity = Now;
            public int Have;
            public bool Prepared, Ended, Completing;
            public string? Folder;
            public string[] Paths = Array.Empty<string>();
            public readonly List<string> Tops = new();
            public SafeFileHandle?[] Handles = Array.Empty<SafeFileHandle?>();
            public long[] Got = Array.Empty<long>();
            public HashSet<long>?[] Claimed = Array.Empty<HashSet<long>?>();
            public byte[]? Text;
            public readonly SortedSet<int> Active = new(); // files partly arrived (for the per-file progress)
            public long Total, Done;
            public double LastReport;

            public long Length(int i) => Entries[i]!.Value.Length;
        }

        private readonly object _inGate = new();
        private volatile In? _in;
        private readonly Dictionary<uint, (byte Outcome, string Why)> _results = new();
        private readonly Queue<uint> _resultOrder = new();

        /// <summary>How a batch ended, kept so a sender that missed the answer can ask again. Under _inGate.</summary>
        private void Remember(uint id, byte outcome, string why)
        {
            if (_results.ContainsKey(id)) return;
            _results[id] = (outcome, why);
            _resultOrder.Enqueue(id);
            while (_resultOrder.Count > 64) _results.Remove(_resultOrder.Dequeue());
        }

        private void OnOffer(Lane lane, byte[] m)
        {
            uint id = BitConverter.ToUInt32(m, 1);
            byte kind = m[5];
            int count = BitConverter.ToInt32(m, 6);
            int first = BitConverter.ToInt32(m, 18);
            if (kind is < KindFiles or > KindDownloads || count < 1 || count > 1_000_000 || first < 0 || first >= count) return;
            In b;
            In? old = null;
            lock (_inGate)
            {
                if (_results.TryGetValue(id, out var r)) { Tell(lane, FinishedMessage(id, r.Outcome, r.Why)); return; }
                if (_in is In cur && cur.Id == id) b = cur;
                else
                {
                    old = _in;
                    b = new In { Id = id, Kind = kind, Entries = new (string, long)?[count] };
                    b.T.Clipboard = kind != KindDownloads;
                    if (kind == KindText) b.T.What = "clipboard text";
                    _in = b;
                }
            }
            if (old != null) EndIn(old, EndedStopped, "The other PC sent something newer, so " + old.T.What + " was stopped.", "Something newer was sent.", null, tell: true);
            bool ready = false;
            try
            {
                lock (b.Gate)
                {
                    if (b.Ended || b.Kind != kind) return;
                    b.LastActivity = Now;
                    if (b.Prepared) ready = true;
                    else
                    {
                        int pos = 22;
                        for (int i = first; pos < m.Length; i++)
                        {
                            if (i >= count || pos + 10 > m.Length) throw new InvalidDataException("a damaged list of files");
                            long length = BitConverter.ToInt64(m, pos);
                            int bytes = BitConverter.ToUInt16(m, pos + 8);
                            if (pos + 10 + bytes > m.Length) throw new InvalidDataException("a damaged list of files");
                            string rel = Encoding.UTF8.GetString(m, pos + 10, bytes);
                            pos += 10 + bytes;
                            if (b.Entries[i] == null) { b.Entries[i] = (rel, length); b.Have++; }
                        }
                        if (b.Have == count) { Prepare(b); ready = true; }
                    }
                }
            }
            catch (Exception e)
            {
                EndIn(b, EndedBadly, "Could not receive " + b.T.What + ": " + e.Message, "The other PC could not receive " + b.T.What + ": " + e.Message, lane, tell: true);
                return;
            }
            if (!ready) return;
            Tell(lane, IdMessage(Ready, id));
            Progress?.Invoke(b.T);
            bool empty;
            lock (b.Gate) { empty = b.Total == 0 && !b.Completing && !b.Ended; if (empty) b.Completing = true; }
            if (empty) Complete(b, lane);
        }

        /// <summary>Makes the folders and empty files (under b.Gate). Throws if anything is wrong, such as a path that leaves the folder.</summary>
        private static void Prepare(In b)
        {
            int count = b.Entries.Length;
            b.Handles = new SafeFileHandle?[count];
            b.Got = new long[count];
            b.Claimed = new HashSet<long>?[count];
            b.Paths = new string[count];
            b.Total = b.Entries.Sum(e => Math.Max(0, e!.Value.Length));
            if (b.Kind == KindText)
            {
                if (count != 1 || b.Length(0) < 0 || b.Length(0) > MaxText) throw new InvalidDataException("The other PC copied more text than TailRemote can carry (512 MB).");
                b.Text = new byte[b.Length(0)];
            }
            else
            {
                if (b.Kind == KindFiles)
                {
                    CleanStaging();
                    b.Folder = Path.Combine(Staging, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                }
                else b.Folder = Downloads;
                Directory.CreateDirectory(b.Folder);
                var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    var (rel, length) = b.Entries[i]!.Value;
                    if (SafePath(b.Folder, rel) == null) throw new InvalidDataException("a path that leaves the folder");
                    string top = rel.Split('\\', '/')[0];
                    if (b.Kind == KindDownloads)
                    {
                        // Never over anything already in Downloads: "name (2).ext" and so on.
                        if (!renamed.TryGetValue(top, out var fresh)) renamed[top] = fresh = Unique(b.Folder, top);
                        rel = fresh + rel[top.Length..];
                        top = fresh;
                    }
                    string path = SafePath(b.Folder, rel)!;
                    if (length < 0) Directory.CreateDirectory(path);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using (File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write)) { }
                    }
                    if (tops.Add(top)) b.Tops.Add(top); // made by this batch, so its to throw away if it fails
                    b.Paths[i] = path;
                }
                b.T.What = b.Tops.Count == 1 ? b.Tops[0] : b.Tops.Count + " items";
            }
            b.T.Total = b.Total;
            b.Prepared = true;
            b.Clock.Start();
        }

        private void OnPiece(Lane lane, byte[] m)
        {
            uint id = BitConverter.ToUInt32(m, 1);
            int e = BitConverter.ToInt32(m, 5);
            long offset = BitConverter.ToInt64(m, 9);
            int n = m.Length - 17;
            if (_in is not In b || b.Id != id)
            {
                lock (_inGate)
                    Tell(lane, _results.TryGetValue(id, out var r) ? FinishedMessage(id, r.Outcome, r.Why)
                        : FinishedMessage(id, EndedBadly, "The other PC no longer had this transfer. Send it again."));
                return;
            }
            SafeFileHandle? h = null;
            bool repeat = false;
            try
            {
                lock (b.Gate)
                {
                    if (b.Ended || !b.Prepared) return;
                    if ((uint)e >= (uint)b.Entries.Length || offset < 0 || offset + n > b.Length(e)) throw new InvalidDataException("a piece that does not fit");
                    // A piece sent again (its confirmation was lost with a lane): confirmed, not written twice.
                    repeat = b.Got[e] >= b.Length(e) || !(b.Claimed[e] ??= new HashSet<long>()).Add(offset);
                    if (!repeat && b.Text == null) h = b.Handles[e] ??= File.OpenHandle(b.Paths[e], FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete);
                }
                if (!repeat)
                {
                    if (b.Text != null) m.AsSpan(17).CopyTo(b.Text.AsSpan((int)offset));
                    else RandomAccess.Write(h!, m.AsSpan(17), offset);
                }
            }
            catch (Exception ex)
            {
                if (!b.Ended) EndIn(b, EndedBadly, "Could not receive " + b.T.What + ": " + ex.Message, "The other PC could not receive " + b.T.What + ": " + ex.Message, lane, tell: true);
                return;
            }
            bool report = false, complete = false;
            lock (b.Gate)
            {
                if (b.Ended) return;
                b.LastActivity = Now;
                if (!repeat)
                {
                    if ((b.Got[e] += n) == b.Length(e))
                    {
                        b.Handles[e]?.Dispose();
                        b.Handles[e] = null;
                        b.Claimed[e] = null;
                        b.Active.Remove(e);
                    }
                    else b.Active.Add(e);
                    b.Done += n;
                    b.T.Done = b.Done;
                    double s = b.Clock.Elapsed.TotalSeconds;
                    if (s - b.LastReport >= 0.25)
                    {
                        b.LastReport = s;
                        b.T.BytesPerSecond = b.Done / Math.Max(0.001, s);
                        b.T.Files = b.Active.Take(32).Select(i => (b.Text != null ? "clipboard text" : Path.GetRelativePath(b.Folder!, b.Paths[i]), b.Got[i], b.Length(i))).ToArray();
                        report = true;
                    }
                    if (b.Done == b.Total && !b.Completing) complete = b.Completing = true;
                }
            }
            byte[] got = new byte[21];
            got[0] = Got;
            Buffer.BlockCopy(m, 1, got, 1, 16);
            BitConverter.TryWriteBytes(got.AsSpan(17), n);
            Tell(lane, got);
            if (report) Progress?.Invoke(b.T);
            if (complete) Complete(b, lane);
        }

        /// <summary>Everything arrived: onto the clipboard, or announced in Downloads.</summary>
        private void Complete(In b, Lane lane)
        {
            lock (b.Gate)
            {
                if (b.Ended) return;
                b.Ended = true;
                for (int i = 0; i < b.Handles.Length; i++) { b.Handles[i]?.Dispose(); b.Handles[i] = null; }
            }
            var t = b.T;
            t.Finished = true;
            t.Files = Array.Empty<(string, long, long)>();
            t.Done = b.Done;
            t.BytesPerSecond = b.Done / Math.Max(0.001, b.Clock.Elapsed.TotalSeconds);
            string? text = null;
            string[]? paths = null;
            if (b.Kind == KindFiles)
            {
                paths = b.Tops.Select(x => Path.Combine(b.Folder!, x)).ToArray();
                t.Result = "The other PC sent " + t.What + " to your clipboard, at " + Speed(t.BytesPerSecond) + ". Press Control V to paste.";
            }
            else if (b.Kind == KindDownloads) t.Result = "Received " + t.What + ", at " + Speed(t.BytesPerSecond) + ". It is in Downloads, TailRemote.";
            else
            {
                text = Encoding.UTF8.GetString(b.Text!);
                b.Text = null;
                t.Result = "Received the clipboard text.";
            }
            lock (_inGate)
            {
                if (_in == b) _in = null;
                Remember(b.Id, EndedWell, "");
            }
            Tell(lane, FinishedMessage(b.Id, EndedWell, ""));
            DiagLog.Write("files: " + t.Result);
            Progress?.Invoke(t);
            if (paths != null) FilesReceived?.Invoke(paths);
            if (text != null) TextReceived?.Invoke(text);
        }

        /// <summary>
        /// A batch being received ends without arriving: what it made is thrown away and both PCs
        /// say so. why: for this PC; whyThere: for the sender. tell: send the sender the outcome.
        /// </summary>
        private void EndIn(In b, byte outcome, string why, string whyThere, Lane? lane, bool tell)
        {
            lock (b.Gate)
            {
                if (b.Ended) return;
                b.Ended = true;
                for (int i = 0; i < b.Handles.Length; i++) { b.Handles[i]?.Dispose(); b.Handles[i] = null; }
                b.Text = null;
            }
            ThrowAway(b, 0);
            lock (_inGate)
            {
                if (_in == b) _in = null;
                Remember(b.Id, outcome, whyThere);
            }
            if (tell)
            {
                var m = FinishedMessage(b.Id, outcome, whyThere);
                if (lane != null && !lane.Dead) Tell(lane, m);
                else foreach (var l in _lanes) Tell(l, m);
            }
            DiagLog.Write("files: " + why);
            var t = b.T;
            t.Files = Array.Empty<(string, long, long)>();
            t.Finished = t.Failed = true;
            t.Cancelled = outcome == EndedStopped;
            t.Result = why;
            try { Progress?.Invoke(t); } catch { }
        }

        /// <summary>
        /// Deletes what a batch that did not arrive made: its own holding folder, or in Downloads only
        /// what it made. A lane may still be finishing a write, so it tries again a few times.
        /// </summary>
        private static void ThrowAway(In b, int attempt)
        {
            if (b.Folder == null) return;
            bool left = false;
            var targets = b.Kind == KindDownloads ? b.Tops.Select(t => Path.Combine(b.Folder, t)).ToList() : new List<string> { b.Folder };
            foreach (string p in targets)
            {
                try { if (Directory.Exists(p)) Directory.Delete(p, true); else if (File.Exists(p)) File.Delete(p); }
                catch { left = true; }
            }
            if (left && attempt < 10) ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(200); ThrowAway(b, attempt + 1); });
        }

        private void OnStop(uint id)
        {
            if (_in is In b && b.Id == id) EndIn(b, EndedStopped, "The other PC stopped sending " + b.T.What + ".", "Stopped.", null, tell: false);
            else lock (_inGate) Remember(id, EndedStopped, "Stopped.");
        }

        private void OnAsk(Lane lane, uint id)
        {
            lock (_inGate)
            {
                if (_results.TryGetValue(id, out var r)) Tell(lane, FinishedMessage(id, r.Outcome, r.Why));
                else if (_in?.Id != id) Tell(lane, FinishedMessage(id, EndedBadly, "The other PC no longer had this transfer. Send it again."));
            }
        }

        /// <summary>A name that is not taken in the folder yet: name, name (2), name (3)...</summary>
        private static string Unique(string folder, string name)
        {
            if (!File.Exists(Path.Combine(folder, name)) && !Directory.Exists(Path.Combine(folder, name))) return name;
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; ; i++)
            {
                string n = stem + " (" + i + ")" + ext;
                if (!File.Exists(Path.Combine(folder, n)) && !Directory.Exists(Path.Combine(folder, n))) return n;
            }
        }

        /// <summary>A path inside the batch folder, or null if it would leave it.</summary>
        private static string? SafePath(string folder, string rel)
        {
            if (rel.Length == 0 || Path.IsPathRooted(rel)) return null;
            foreach (string part in rel.Split('\\', '/'))
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            string full = Path.GetFullPath(Path.Combine(folder, rel));
            return full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        /// <summary>Keeps only the newest batch in the holding folder (the clipboard still points at it).</summary>
        private static void CleanStaging()
        {
            try
            {
                if (!Directory.Exists(Staging)) return;
                foreach (var old in new DirectoryInfo(Staging).GetDirectories().OrderByDescending(d => d.Name).Skip(1))
                    try { old.Delete(true); } catch { }
            }
            catch { }
        }

        public static string Speed(double bytesPerSecond) =>
            bytesPerSecond >= 1 << 20 ? (bytesPerSecond / (1 << 20)).ToString(bytesPerSecond < 10 << 20 ? "0.0" : "0") + " megabytes a second"
            : (bytesPerSecond / 1024).ToString("0") + " kilobytes a second";

        public static string Size(long bytes) =>
            bytes >= 1 << 30 ? (bytes / (double)(1 << 30)).ToString("0.0") + " GB" :
            bytes >= 1 << 20 ? (bytes / (double)(1 << 20)).ToString("0.0") + " MB" :
            bytes >= 1 << 10 ? (bytes / 1024) + " KB" : bytes + " bytes";
    }
}
