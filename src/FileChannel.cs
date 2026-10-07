using System;
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
        private const int ChunkSize = 256 * 1024;

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

        /// <summary>Sends files one after another. Blocking: run it off the window's thread.</summary>
        public string Send(IReadOnlyList<string> paths, Action<string, int> report, CancellationToken ct)
        {
            lock (_sendGate)
            {
                long total = 0, done = 0;
                foreach (string p in paths) total += new FileInfo(p).Length;
                int n = 0;
                foreach (string p in paths)
                {
                    string name = Path.GetFileName(p);
                    n++;
                    using var f = File.OpenRead(p);
                    byte[] start = new byte[9 + Encoding.UTF8.GetByteCount(name)];
                    start[0] = Start;
                    BitConverter.TryWriteBytes(start.AsSpan(1), f.Length);
                    Encoding.UTF8.GetBytes(name, start.AsSpan(9));
                    _link.Send(_stream, start);

                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buf = new byte[1 + ChunkSize];
                    buf[0] = Data;
                    int read;
                    while ((read = f.Read(buf, 1, ChunkSize)) > 0)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            _link.Send(_stream, new[] { Cancel });
                            throw new OperationCanceledException();
                        }
                        sha.AppendData(buf, 1, read);
                        _link.Send(_stream, buf.AsSpan(0, 1 + read));
                        done += read;
                        report("Sending " + name + (paths.Count > 1 ? ", file " + n + " of " + paths.Count : "") + ".",
                            total == 0 ? 100 : (int)(done * 100 / total));
                    }
                    byte[] end = new byte[33];
                    end[0] = End;
                    sha.GetHashAndReset().CopyTo(end, 1);
                    _link.Send(_stream, end);
                }
                return paths.Count == 1
                    ? "Sent " + Path.GetFileName(paths[0]) + ". It is in Downloads, TailRemote, on the other PC."
                    : "Sent " + paths.Count + " files. They are in Downloads, TailRemote, on the other PC.";
            }
        }

        private void ReceiveLoop()
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
            try
            {
                while (!_closed)
                {
                    byte[] m = _link.Receive(_stream);
                    if (m.Length == 0) continue;
                    switch (m[0])
                    {
                        case Start when m.Length >= 9:
                            Discard();
                            expected = BitConverter.ToInt64(m, 1);
                            name = SafeName(Encoding.UTF8.GetString(m, 9, m.Length - 9));
                            Directory.CreateDirectory(Folder);
                            path = UniquePath(Folder, name);
                            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
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
            }
            catch
            {
                if (file != null && !_closed) _announce("The connection dropped while receiving " + name + ", so the part that arrived was deleted.");
            }
            finally { Discard(); }
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
