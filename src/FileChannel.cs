using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Files between the two PCs, on a TCP connection of their own so a large
    /// file never holds up a keystroke. Encrypted with keys of its own. Either
    /// side can send; files arrive in Downloads\TailRemote, checked against a
    /// SHA-256 of the original, and never overwrite anything already there.
    /// </summary>
    internal sealed class FileChannel : IDisposable
    {
        private const byte Start = 0x50, Data = 0x51, End = 0x52, Cancel = 0x53;
        private const int ChunkSize = 1024 * 1024; // 1 MB a frame: fewer, bigger writes

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly SecureLink _link;
        private readonly Action<string> _announce;
        private readonly object _sendGate = new();
        private volatile bool _closed;

        /// <summary>Where received files go: Downloads\TailRemote.</summary>
        public static string Folder => FolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TailRemote");

        /// <summary>The self-test receives into a temporary folder instead.</summary>
        public static string? FolderOverride;

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
            try { _tcp.Dispose(); } catch { }
        }

        /// <summary>
        /// Sends files one after another. Blocking: run it off the window's thread.
        /// Two stages run at once so the connection never waits: one reads the file,
        /// fingerprints it and encrypts it, the other only sends. report gets a
        /// sentence and a percentage at most ten times a second.
        /// </summary>
        public string Send(IReadOnlyList<string> paths, Action<string, int> report, CancellationToken ct)
        {
            lock (_sendGate)
            {
                long total = 0;
                foreach (string p in paths) total += new FileInfo(p).Length;
                var frames = new BlockingCollection<byte[]>(8); // 8 MB in flight at most
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Exception? failed = null;
                var producer = new Thread(() =>
                {
                    try { Produce(paths, frames, stop.Token); }
                    catch (Exception e) { failed = e; }
                    finally { try { frames.CompleteAdding(); } catch { } }
                }) { IsBackground = true, Name = "TailRemote files out" };
                producer.Start();

                long sent = 0, lastReport = 0;
                // A steady sentence (NVDA reads it once) and the bar; the speed is said at the end.
                string sending = "Sending " + (paths.Count == 1 ? Path.GetFileName(paths[0]) : paths.Count + " files") + ".";
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    foreach (var frame in frames.GetConsumingEnumerable())
                    {
                        _stream.Write(frame);
                        sent += frame.Length;
                        if (clock.ElapsedMilliseconds - lastReport >= 100)
                        {
                            lastReport = clock.ElapsedMilliseconds;
                            report(sending, total == 0 ? 100 : (int)Math.Min(100, sent * 100 / Math.Max(1, total)));
                        }
                    }
                }
                catch
                {
                    // The connection failed: stop the reading side too, cleanly.
                    stop.Cancel();
                    producer.Join();
                    throw;
                }
                producer.Join();
                if (failed != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
                double secs = Math.Max(0.001, clock.Elapsed.TotalSeconds);
                string speed = ", at " + (total / 1048576.0 / secs).ToString("0.0") + " megabytes a second";
                return paths.Count == 1
                    ? "Sent " + Path.GetFileName(paths[0]) + speed + ". It is in Downloads, TailRemote, on the other PC."
                    : "Sent " + paths.Count + " files" + speed + ". They are in Downloads, TailRemote, on the other PC.";
            }
        }

        /// <summary>Reads, fingerprints and encrypts every file into frames, in order.</summary>
        private void Produce(IReadOnlyList<string> paths, BlockingCollection<byte[]> frames, CancellationToken ct)
        {
            byte[] buf = new byte[1 + ChunkSize];
            buf[0] = Data;
            foreach (string p in paths)
            {
                string name = Path.GetFileName(p);
                using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                byte[] start = new byte[9 + Encoding.UTF8.GetByteCount(name)];
                start[0] = Start;
                BitConverter.TryWriteBytes(start.AsSpan(1), f.Length);
                Encoding.UTF8.GetBytes(name, start.AsSpan(9));
                frames.Add(_link.Seal(start), ct);

                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int read;
                try
                {
                    while ((read = f.Read(buf, 1, ChunkSize)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        sha.AppendData(buf, 1, read);
                        frames.Add(_link.Seal(buf.AsSpan(0, 1 + read)), ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Tell the other PC to throw away the part it has (if the connection still works).
                    try { frames.TryAdd(_link.Seal(new[] { Cancel }), 2000); } catch { }
                    throw;
                }
                byte[] end = new byte[33];
                end[0] = End;
                sha.GetHashAndReset().CopyTo(end, 1);
                frames.Add(_link.Seal(end), ct);
            }
        }

        /// <summary>
        /// Two stages here too: this thread only takes messages off the network and
        /// decrypts them; another writes them to disk and fingerprints them, so a slow
        /// disk moment never stops the network.
        /// </summary>
        private void ReceiveLoop()
        {
            using var queue = new BlockingCollection<byte[]>(8);
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
            FileStream? file = null;
            string? path = null, name = null;
            long expected = 0;
            IncrementalHash? sha = null;
            void Discard()
            {
                try { file?.Dispose(); if (path != null) File.Delete(path); } catch { }
                file = null; path = null; sha?.Dispose(); sha = null;
            }
            foreach (byte[] m in queue.GetConsumingEnumerable())
            {
                try
                {
                    switch (m[0])
                    {
                        case Start when m.Length >= 9:
                            Discard();
                            expected = BitConverter.ToInt64(m, 1);
                            name = SafeName(Encoding.UTF8.GetString(m, 9, m.Length - 9));
                            Directory.CreateDirectory(Folder);
                            path = UniquePath(Folder, name);
                            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
                            sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            break;
                        case Data when file != null:
                            file.Write(m, 1, m.Length - 1);
                            sha!.AppendData(m, 1, m.Length - 1);
                            break;
                        case End when file != null && m.Length == 33:
                            bool ok = file.Length == expected && sha!.GetHashAndReset().AsSpan().SequenceEqual(m.AsSpan(1));
                            file.Dispose();
                            file = null;
                            if (ok)
                            {
                                _announce("Received " + Path.GetFileName(path) + ", " + Size(expected) + ", in Downloads, TailRemote.");
                                path = null;
                            }
                            else
                            {
                                _announce(name + " arrived damaged, so it was deleted. Send it again.");
                                Discard();
                            }
                            break;
                        case Cancel:
                            if (file != null) _announce("The other PC stopped sending " + name + ".");
                            Discard();
                            break;
                    }
                }
                catch (Exception e)
                {
                    // A full disk, a folder that cannot be written: say so, drop this file, and
                    // keep reading, so the network side never waits on a queue nobody empties.
                    try { _announce("Could not save " + name + ": " + e.Message); } catch { }
                    Discard();
                }
            }
            // The network side ended (the connection closed) in the middle of a file.
            if (file != null && !_closed) { try { _announce("The connection dropped while receiving " + name + ", so the part that arrived was deleted."); } catch { } }
            Discard();
        }

        private static string SafeName(string name)
        {
            name = Path.GetFileName(name.Replace('/', '\\'));
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            return name.Length == 0 ? "file" : name;
        }

        private static string UniquePath(string folder, string name)
        {
            string p = Path.Combine(folder, name);
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; File.Exists(p); i++) p = Path.Combine(folder, stem + " (" + i + ")" + ext);
            return p;
        }

        private static string Size(long bytes) =>
            bytes >= 1 << 30 ? (bytes / (double)(1 << 30)).ToString("0.0") + " GB" :
            bytes >= 1 << 20 ? (bytes / (double)(1 << 20)).ToString("0.0") + " MB" :
            bytes >= 1 << 10 ? (bytes / 1024) + " KB" : bytes + " bytes";
    }
}
