using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// The optional diagnostic log: Enable logging in the main window (or the
    /// service's settings). Writes TailRemote-log.txt next to the exe, one line
    /// per event and one status line a second from each side, all timestamped
    /// to the millisecond. Never what is typed: key presses are only counted.
    /// At 20 MB the file becomes TailRemote-log.old.txt and a new one starts.
    /// </summary>
    internal static class DiagLog
    {
        private const long MaxBytes = 20L * 1024 * 1024;
        private static readonly object Gate = new();
        private static StreamWriter? _writer;
        private static volatile bool _enabled;
        private static Timer? _flush;

        public static string FilePath => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "TailRemote-log.txt");

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                lock (Gate)
                {
                    if (value == _enabled) return;
                    _enabled = value;
                    if (value)
                    {
                        Open();
                        _flush ??= new Timer(_ => Flush(), null, 1000, 1000);
                        WriteLocked("Logging started. TailRemote " + Updater.Current + ", " +
                            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + " on " +
                            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture + ", " + Environment.OSVersion.VersionString +
                            ", " + Environment.ProcessorCount + " processors.");
                    }
                    else
                    {
                        WriteLocked("Logging stopped.");
                        _writer?.Dispose();
                        _writer = null;
                    }
                }
            }
        }

        public static void Write(string line)
        {
            if (!_enabled) return;
            lock (Gate) WriteLocked(line);
        }

        private static void WriteLocked(string line)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line);
                if (_writer.BaseStream.Length > MaxBytes)
                {
                    _writer.Dispose();
                    string old = Path.ChangeExtension(FilePath, ".old.txt");
                    File.Delete(old);
                    File.Move(FilePath, old);
                    Open();
                }
            }
            catch { }
        }

        private static void Open()
        {
            try
            {
                var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
            }
            catch { _writer = null; }
        }

        private static void Flush()
        {
            lock (Gate) { try { _writer?.Flush(); } catch { } }
        }
    }
}
