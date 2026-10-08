using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// The clipboard's big things between the two PCs, on a TCP connection of its
    /// own so they never hold up a keystroke, encrypted with keys of its own.
    ///
    /// Copied files and folders (and text too large for the keyboard connection) go
    /// over as a batch. Files land in a holding folder (Staging) and, once all have
    /// arrived and passed their SHA-256 checks, are put on that PC's clipboard, so
    /// Control V pastes them anywhere. Text goes straight onto the clipboard.
    ///
    /// Sending is paced (Rate): the controlling PC watches the audio path's ping and
    /// slows file data the moment it starts queueing up the sound, then speeds back up.
    /// A new batch cancels the one still going: copying something new replaces it.
    /// </summary>
    internal sealed class FileChannel : IDisposable
    {
        // Wire messages (each one encrypted frame):
        private const byte Start = 0x50;      // u64 length (-1: a folder), UTF-8 path inside the batch
        private const byte Data = 0x51;       // bytes of the current file
        private const byte End = 0x52;        // SHA-256 of the current file
        private const byte Cancel = 0x53;     // the batch was stopped: throw away what came
        private const byte BatchStart = 0x54; // u8 kind, u32 id, u32 entries, u64 total bytes
        private const byte BatchEnd = 0x55;   // u32 id: everything arrived
        private const byte KindFiles = 1, KindText = 2;
        private const int ChunkSize = 256 * 1024; // small enough that pacing is smooth, big enough to be fast

        /// <summary>The most text a clipboard may carry: 512 MB.</summary>
        public const long MaxText = 512L << 20;

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly SecureLink _link;
        private readonly Action<string> _announce;
        private readonly object _sendGate = new();
        private volatile bool _closed;
        private CancellationTokenSource? _sending;
        private uint _nextId = 1;

        /// <summary>Text that arrived for the clipboard.</summary>
        public Action<string>? TextReceived;
        /// <summary>Files that arrived for the clipboard: the top-level files and folders, in the holding folder.</summary>
        public Action<string[]>? FilesReceived;
        /// <summary>How a transfer is going, either way; at most four times a second, and once at the end.</summary>
        public Action<Transfer>? Progress;
        /// <summary>Bytes a second this side may send right now (set from the audio ping), or null for no limit.</summary>
        public Func<double>? Rate;

        /// <summary>True while a batch is going either way (the client then measures its ping more often).</summary>
        public bool Busy => _outgoing || _incoming;
        private volatile bool _outgoing, _incoming;

        public sealed class Transfer
        {
            public bool Outgoing, Finished, Failed;
            public string What = "";
            public long Done, Total;
            public double BytesPerSecond;
            public string? Result;
            public TimeSpan? Left => BytesPerSecond > 1 && Total > Done ? TimeSpan.FromSeconds((Total - Done) / BytesPerSecond) : null;
        }

        /// <summary>Where arriving clipboard files are held until pasted: %LOCALAPPDATA%\TailRemote\Clipboard.</summary>
        public static string Staging => StagingOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailRemote", "Clipboard");

        /// <summary>The self-test receives into a temporary folder instead.</summary>
        public static string? StagingOverride;

        public FileChannel(TcpClient tcp, SecureLink link, Action<string> announce)
        {
            _tcp = tcp;
            _tcp.NoDelay = true;
            _stream = tcp.GetStream();
            _stream.ReadTimeout = Timeout.Infinite;
            _link = link;
            _announce = announce;
            new Thread(ReceiveLoop) { IsBackground = true, Name = "TailRemote files" }.Start();
        }

        public void Dispose()
        {
            _closed = true;
            try { _sending?.Cancel(); } catch { }
            try { _tcp.Dispose(); } catch { }
        }

        /// <summary>Stops the batch being sent, if any.</summary>
        public void CancelSending()
        {
            try { _sending?.Cancel(); } catch { }
        }

        // ---- Sending ----

        /// <summary>Sends copied files and folders. Blocking: run it off the window's thread. Replaces any batch still going.</summary>
        public void SendFiles(IReadOnlyList<string> items)
        {
            var entries = new List<(string Rel, string Full, long Length)>();
            foreach (string item in items)
            {
                if (Directory.Exists(item))
                {
                    string root = Path.GetFileName(item.TrimEnd('\\', '/'));
                    entries.Add((root, item, -1));
                    foreach (string d in SafeEnumerate(item, true)) entries.Add((Path.Combine(root, Path.GetRelativePath(item, d)), d, -1));
                    foreach (string f in SafeEnumerate(item, false)) entries.Add((Path.Combine(root, Path.GetRelativePath(item, f)), f, new FileInfo(f).Length));
                }
                else if (File.Exists(item)) entries.Add((Path.GetFileName(item), item, new FileInfo(item).Length));
            }
            if (entries.Count == 0) return;
            int files = entries.Count(e => e.Length >= 0);
            string what = items.Count == 1 ? Path.GetFileName(items[0].TrimEnd('\\', '/')) : items.Count + " items";
            SendBatch(KindFiles, what, entries.Select(e => (e.Rel, (Func<Stream>?)(e.Length < 0 ? null : () => OpenRead(e.Full)), e.Length)).ToList());
        }

        /// <summary>Sends clipboard text too large for the keyboard connection. Blocking. Replaces any batch still going.</summary>
        public void SendText(string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            SendBatch(KindText, "clipboard text", new List<(string, Func<Stream>?, long)> { ("", () => new MemoryStream(utf8, false), utf8.Length) });
        }

        private static Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);

        private static IEnumerable<string> SafeEnumerate(string dir, bool dirs)
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            try { return dirs ? Directory.EnumerateDirectories(dir, "*", opts).ToList() : Directory.EnumerateFiles(dir, "*", opts).ToList(); }
            catch { return Array.Empty<string>(); }
        }

        private void SendBatch(byte kind, string what, List<(string Rel, Func<Stream>? Open, long Length)> entries)
        {
            // Copying something new replaces what is still going.
            var mine = new CancellationTokenSource();
            Interlocked.Exchange(ref _sending, mine)?.Cancel();
            lock (_sendGate)
            {
                if (mine.IsCancellationRequested || _closed) return;
                _outgoing = true;
                var t = new Transfer { Outgoing = true, What = what, Total = entries.Sum(e => Math.Max(0, e.Length)) };
                var frames = new BlockingCollection<(byte[] Frame, int Payload)>(16);
                Exception? failed = null;
                uint id = _nextId++;
                var producer = new Thread(() =>
                {
                    try { Produce(kind, id, entries, t.Total, frames, mine.Token); }
                    catch (Exception e) { failed = e; }
                    finally { try { frames.CompleteAdding(); } catch { } }
                }) { IsBackground = true, Name = "TailRemote files out" };
                producer.Start();

                var clock = System.Diagnostics.Stopwatch.StartNew();
                double allowance = 0, lastTick = 0, lastReport = 0;
                try
                {
                    foreach (var (frame, payload) in frames.GetConsumingEnumerable())
                    {
                        // Paced: never faster than the controlling PC says the sound can bear.
                        if (Rate?.Invoke() is double rate && rate > 0)
                        {
                            double now = clock.Elapsed.TotalSeconds;
                            allowance = Math.Min(allowance + (now - lastTick) * rate, rate * 0.05 + frame.Length);
                            lastTick = now;
                            if (allowance < frame.Length) Thread.Sleep((int)Math.Ceiling((frame.Length - allowance) / rate * 1000));
                            allowance -= frame.Length;
                        }
                        _stream.Write(frame);
                        t.Done += payload;
                        double s = clock.Elapsed.TotalSeconds;
                        if (s - lastReport >= 0.25)
                        {
                            lastReport = s;
                            t.BytesPerSecond = t.Done / Math.Max(0.001, s);
                            Progress?.Invoke(t);
                        }
                    }
                }
                catch
                {
                    mine.Cancel();
                    producer.Join();
                    _outgoing = false;
                    t.Finished = t.Failed = true;
                    t.Result = "The connection dropped while sending " + what + ".";
                    Progress?.Invoke(t);
                    return;
                }
                producer.Join();
                _outgoing = false;
                t.BytesPerSecond = t.Done / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                t.Finished = true;
                if (failed is OperationCanceledException) { t.Failed = true; t.Result = "Stopped sending " + what + "."; }
                else if (failed != null) { t.Failed = true; t.Result = "Could not send " + what + ": " + failed.Message; }
                else t.Result = "Sent " + what + ", at " + Speed(t.BytesPerSecond) + ".";
                Progress?.Invoke(t);
            }
        }

        /// <summary>Reads, fingerprints and encrypts the batch into frames, in order.</summary>
        private void Produce(byte kind, uint id, List<(string Rel, Func<Stream>? Open, long Length)> entries, long total,
            BlockingCollection<(byte[], int)> frames, CancellationToken ct)
        {
            byte[] head = new byte[18];
            head[0] = BatchStart;
            head[1] = kind;
            BitConverter.TryWriteBytes(head.AsSpan(2), id);
            BitConverter.TryWriteBytes(head.AsSpan(6), entries.Count);
            BitConverter.TryWriteBytes(head.AsSpan(10), total);
            frames.Add((_link.Seal(head), 0), ct);
            byte[] buf = new byte[1 + ChunkSize];
            buf[0] = Data;
            try
            {
                foreach (var (rel, open, length) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    byte[] start = new byte[9 + Encoding.UTF8.GetByteCount(rel)];
                    start[0] = Start;
                    BitConverter.TryWriteBytes(start.AsSpan(1), length);
                    Encoding.UTF8.GetBytes(rel, start.AsSpan(9));
                    frames.Add((_link.Seal(start), 0), ct);
                    if (open == null) continue; // a folder
                    using var f = open();
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    int read;
                    while ((read = f.Read(buf, 1, ChunkSize)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        sha.AppendData(buf, 1, read);
                        frames.Add((_link.Seal(buf.AsSpan(0, 1 + read)), read), ct);
                    }
                    byte[] end = new byte[33];
                    end[0] = End;
                    sha.GetHashAndReset().CopyTo(end, 1);
                    frames.Add((_link.Seal(end), 0), ct);
                }
                byte[] done = new byte[5];
                done[0] = BatchEnd;
                BitConverter.TryWriteBytes(done.AsSpan(1), id);
                frames.Add((_link.Seal(done), 0), ct);
            }
            catch (OperationCanceledException)
            {
                // Tell the other PC to throw away the part it has (if the connection still works).
                try { frames.TryAdd((_link.Seal(new[] { Cancel }), 0), 2000); } catch { }
                throw;
            }
        }

        // ---- Receiving ----

        /// <summary>This thread only takes frames off the network and decrypts them; another writes them to disk.</summary>
        private void ReceiveLoop()
        {
            using var queue = new BlockingCollection<byte[]>(16);
            var writer = new Thread(() => WriteLoop(queue)) { IsBackground = true, Name = "TailRemote files in" };
            writer.Start();
            try
            {
                while (!_closed)
                {
                    byte[] m = _link.Receive(_stream);
                    if (m.Length > 0) queue.Add(m);
                }
            }
            catch { }
            finally
            {
                queue.CompleteAdding();
                writer.Join();
            }
        }

        private void WriteLoop(BlockingCollection<byte[]> queue)
        {
            byte kind = 0;
            string? folder = null;
            var t = new Transfer();
            var clock = new System.Diagnostics.Stopwatch();
            double lastReport = 0;
            Stream? file = null;
            string? rel = null;
            long expected = 0;
            IncrementalHash? sha = null;
            var tops = new List<string>();
            bool broken = false;

            void Abandon(string? why)
            {
                try { file?.Dispose(); } catch { }
                file = null; sha?.Dispose(); sha = null;
                if (folder != null) { try { Directory.Delete(folder, true); } catch { } }
                if (kind != 0 && why != null)
                {
                    t.Finished = t.Failed = true;
                    t.Result = why;
                    try { Progress?.Invoke(t); } catch { }
                }
                folder = null; kind = 0; _incoming = false; broken = false;
            }

            foreach (byte[] m in queue.GetConsumingEnumerable())
            {
                try
                {
                    switch (m[0])
                    {
                        case BatchStart when m.Length >= 18:
                            Abandon(null);
                            kind = m[1];
                            t = new Transfer { Outgoing = false, Total = BitConverter.ToInt64(m, 10), What = kind == KindText ? "clipboard text" : "files" };
                            if (kind == KindText && t.Total > MaxText) { broken = true; t.Result = "The other PC copied more text than TailRemote can carry (512 MB)."; break; }
                            if (kind == KindFiles)
                            {
                                CleanStaging();
                                folder = Path.Combine(Staging, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                                Directory.CreateDirectory(folder);
                            }
                            tops.Clear();
                            _incoming = true;
                            clock.Restart();
                            lastReport = 0;
                            break;
                        case Start when kind != 0 && !broken && m.Length >= 9:
                            expected = BitConverter.ToInt64(m, 1);
                            rel = Encoding.UTF8.GetString(m, 9, m.Length - 9);
                            if (kind == KindText) { file = new MemoryStream((int)Math.Min(expected, int.MaxValue)); sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); break; }
                            string path = SafePath(folder!, rel) ?? throw new InvalidDataException("a path that leaves the folder");
                            string top = rel.Split('\\', '/')[0];
                            if (!tops.Contains(top)) tops.Add(top);
                            if (t.What == "files") t.What = top;
                            else if (tops.Count > 1) t.What = tops.Count + " items";
                            if (expected < 0) { Directory.CreateDirectory(path); break; }
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
                            sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            break;
                        case Data when file != null && !broken:
                            file.Write(m, 1, m.Length - 1);
                            sha!.AppendData(m, 1, m.Length - 1);
                            t.Done += m.Length - 1;
                            double s = clock.Elapsed.TotalSeconds;
                            if (s - lastReport >= 0.25)
                            {
                                lastReport = s;
                                t.BytesPerSecond = t.Done / Math.Max(0.001, s);
                                Progress?.Invoke(t);
                            }
                            break;
                        case End when file != null && !broken && m.Length == 33:
                            bool ok = file.Length == expected && sha!.GetHashAndReset().AsSpan().SequenceEqual(m.AsSpan(1));
                            if (!ok) { Abandon((rel == "" ? "The clipboard text" : rel) + " arrived damaged, so it was thrown away. Copy it again."); break; }
                            if (kind == KindText)
                            {
                                string text = Encoding.UTF8.GetString(((MemoryStream)file).GetBuffer(), 0, (int)file.Length);
                                file.Dispose(); file = null;
                                TextReceived?.Invoke(text);
                            }
                            else { file.Dispose(); file = null; }
                            sha.Dispose(); sha = null;
                            break;
                        case BatchEnd when kind != 0:
                            if (broken) { Abandon(t.Result); break; }
                            t.Finished = true;
                            t.BytesPerSecond = t.Done / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                            if (kind == KindFiles)
                            {
                                var paths = tops.Select(x => Path.Combine(folder!, x)).ToArray();
                                t.Result = "Received " + t.What + ", at " + Speed(t.BytesPerSecond) + ". Press Control V to paste.";
                                Progress?.Invoke(t);
                                FilesReceived?.Invoke(paths);
                            }
                            else { t.Result = "Received the clipboard text."; Progress?.Invoke(t); }
                            folder = null; kind = 0; _incoming = false;
                            break;
                        case Cancel:
                            Abandon(kind != 0 ? "The other PC stopped sending " + t.What + "." : null);
                            break;
                    }
                }
                catch (Exception e)
                {
                    // A full disk, a folder that cannot be written: say so, drop this batch, and
                    // keep reading, so the network side never waits on a queue nobody empties.
                    Abandon("Could not receive " + t.What + ": " + e.Message);
                }
            }
            if (kind != 0 && !_closed) Abandon("The connection dropped while receiving " + t.What + ".");
            else Abandon(null);
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

        /// <summary>Keeps only the two newest batches in the holding folder (the clipboard still points at the newest).</summary>
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
