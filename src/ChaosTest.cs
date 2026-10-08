using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TailRemote
{
    /// <summary>
    /// --chaostest: a host and its clients on this PC, put through everything nasty a
    /// network, a broken or hostile PC, or a user could do. No keys are ever sent (the
    /// host would type them into this PC) and the real clipboard is never touched (this
    /// tests the connection, not Windows). Each scenario has a time limit, so a freeze
    /// shows as FROZE rather than hanging the test. Results: %TEMP%\tailremote-chaostest.txt.
    /// </summary>
    internal static class ChaosTest
    {
        private const int Port = 48100;
        private const string Password = "chaos-pass", ListenPassword = "listen-pass";
        private static readonly List<string> Report = new();
        private static readonly List<string> Log = new();
        private static int _failures;
        private static string Root = Path.Combine(Path.GetTempPath(), "tailremote-chaos");

        public static int Run()
        {
            Host.WrongPasswordDelayMs = 0;
            if (Environment.GetEnvironmentVariable("CHAOS_TRACE") != null) DiagLog.Enabled = true;
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Directory.CreateDirectory(Root);
            FileChannel.StagingOverride = Path.Combine(Root, "stage");
            var host = NewHost();
            Thread.Sleep(500);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memBefore = GC.GetTotalMemory(true);
            int threadsBefore = Process.GetCurrentProcess().Threads.Count;

            Scenario("junk instead of a login, 50 times", 30, () =>
            {
                var rnd = new Random(1);
                for (int i = 0; i < 50; i++)
                {
                    using var t = new TcpClient("127.0.0.1", Port);
                    var junk = new byte[rnd.Next(1, 4000)];
                    rnd.NextBytes(junk);
                    try { t.GetStream().Write(junk); } catch { }
                }
                return Healthy(host, "after the junk");
            });

            Scenario("300 connections that never say anything, then a real login", 30, () =>
            {
                var silent = new List<TcpClient>();
                for (int i = 0; i < 300; i++) try { silent.Add(new TcpClient("127.0.0.1", Port)); } catch { }
                var clock = Stopwatch.StartNew();
                string result;
                try { using var c = Connect(Password); result = "logged in after " + clock.ElapsedMilliseconds + " ms"; }
                catch (Exception e) { result = "FAIL: " + e.Message + " after " + clock.ElapsedMilliseconds + " ms"; }
                foreach (var t in silent) t.Dispose();
                Thread.Sleep(300);
                return result.StartsWith("FAIL") ? result : result + "; " + Healthy(host, "after they went");
            });

            Scenario("10,000 junk audio packets at the host", 30, () =>
            {
                using var u = new UdpClient();
                var rnd = new Random(2);
                for (int i = 0; i < 10_000; i++)
                {
                    var d = new byte[rnd.Next(0, 1400)];
                    rnd.NextBytes(d);
                    if (i % 3 == 0 && d.Length > 0) d[0] = Protocol.UdpHello; // look almost right
                    try { u.Send(d, d.Length, "127.0.0.1", Port); } catch { }
                }
                return Healthy(host, "after the flood");
            });

            Scenario("30 connects and disconnects as fast as possible", 60, () =>
            {
                for (int i = 0; i < 30; i++) { using var c = Connect(Password); }
                Thread.Sleep(1500);
                return "sessions left: " + host.TestSessions + "; " + Healthy(host, "after the storm");
            });

            Scenario("two controllers kicking each other off, 20 times", 60, () =>
            {
                int kicked = 0;
                Client? previous = null;
                for (int i = 0; i < 20; i++)
                {
                    var c = Connect(Password);
                    if (previous != null)
                    {
                        var p = previous;
                        for (int w = 0; w < 40 && !p.TestClosed; w++) Thread.Sleep(25);
                        if (p.TestClosed) kicked++;
                        p.Dispose();
                    }
                    previous = c;
                }
                previous?.Dispose();
                return kicked == 19 ? "every old controller was told it was replaced" : "FAIL: only " + kicked + " of 19 replaced controllers noticed";
            });

            Scenario("100 listeners at once, plus one too many", 120, () =>
            {
                var listeners = new List<Client>();
                Exception? extra = null;
                for (int i = 0; i < 100; i++) listeners.Add(Connect(ListenPassword));
                Client? hundredFirst = null;
                try { hundredFirst = Connect(ListenPassword); } catch (Exception e) { extra = e; }
                Thread.Sleep(500);
                bool refused = extra != null || (hundredFirst != null && WaitClosed(hundredFirst, 3000));
                hundredFirst?.Dispose();
                int sessions = host.TestSessions;
                foreach (var l in listeners) l.Dispose();
                Thread.Sleep(1500);
                return (refused ? "the 101st was turned away" : "FAIL: a 101st listener got in") + "; sessions at the peak " + sessions + ", after " + host.TestSessions;
            });

            Scenario("nonsense messages after login", 30, () =>
            {
                using var c = Connect(Password);
                c.TestWrite(new byte[] { Protocol.AudioQuality, 255 });       // an impossible bitrate step
                c.TestWrite(new byte[] { Protocol.FilePace, 0, 0, 0, 0 });    // a file speed of zero
                c.TestWrite(new byte[] { Protocol.FilePace, 255, 255, 255, 255 });
                c.TestWrite(new byte[] { 0xEE, 1, 2, 3 });                     // a message type nobody knows
                c.TestWrite(new byte[] { Protocol.AudioQuality });            // too short
                c.TestWrite(new byte[] { Protocol.Features });
                for (int i = 0; i < 1000; i++) c.TestWrite(new byte[] { Protocol.ClipboardSharing, (byte)(i & 1) });
                c.TestWrite(new byte[] { Protocol.ClipboardSharing, 1 });
                Thread.Sleep(1500);
                return c.TestClosed ? "FAIL: the host dropped the connection over nonsense" : "the connection stayed up and ignored all of it";
            });

            Scenario("a listener sending clipboard and Control Alt Delete", 20, () =>
            {
                string? got = null;
                host.ClipboardReceived += t => got = t;
                using var l = Connect(ListenPassword);
                l.TestWrite(Protocol.TextMessage(Protocol.Clipboard, "a listener should never set the clipboard"));
                l.TestWrite(new byte[] { Protocol.SecureAttention });
                l.TestWrite(new byte[] { Protocol.RestartPc }); // must never restart this PC
                Thread.Sleep(1000);
                return got == null ? "ignored, as it should be (and nothing restarted)" : "FAIL: a listener set the clipboard";
            });

            Scenario("damaged data in the middle of a connection, then reconnecting", 30, () =>
            {
                var c = Connect(Password);
                string? why = null;
                c.Disconnected += w => why = w;
                c.TestRaw(new byte[] { 0x55, 0x55, 0x55, 0x55, 0x12, 0x34, 0x56, 0x78, 1, 2, 3 }); // a frame header that does not check out
                bool dropped = WaitClosed(c, 12_000);
                c.Dispose();
                using var again = Connect(Password);
                return dropped ? "the host hung up at once (" + (why ?? "") + ") and reconnecting worked" : "FAIL: the damaged connection was not noticed";
            });

            using (var ctrl = Connect(Password))
            {
                for (int i = 0; i < 100 && (ctrl.Files == null || host.ControllerFiles == null); i++) Thread.Sleep(50);

                Scenario("2,000 tiny files, nested folders, empty files, emoji, Arabic, Chinese and very long names", 120, () =>
                {
                    string src = Path.Combine(Root, "src", "Mess");
                    var names = new[] { "plain.txt", "empty.bin", "\U0001F600 smile.txt", "مرحبا.txt", "你好.txt", new string('x', 180) + ".txt", "spaces   in name.txt", "dots...txt" };
                    long bytes = 0;
                    int files = 0;
                    for (int d = 0; d < 20; d++)
                    {
                        string dir = Path.Combine(src, "level" + d, d % 3 == 0 ? "deeper" : "");
                        Directory.CreateDirectory(dir);
                        for (int f = 0; f < 100; f++)
                        {
                            string name = f < names.Length ? names[f] : "file" + f + ".dat";
                            byte[] data = name == "empty.bin" ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(name + d + f);
                            File.WriteAllBytes(Path.Combine(dir, name), data);
                            bytes += data.Length; files++;
                        }
                    }
                    Directory.CreateDirectory(Path.Combine(src, "empty folder"));
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    ctrl.Files!.SendFiles(new[] { src });
                    for (int i = 0; i < 1000 && landed == null; i++) Thread.Sleep(20);
                    if (landed == null) return "FAIL: nothing arrived";
                    var got = Directory.EnumerateFiles(landed[0], "*", SearchOption.AllDirectories).ToList();
                    long gotBytes = got.Sum(f => new FileInfo(f).Length);
                    bool emptyFolder = Directory.Exists(Path.Combine(landed[0], "empty folder"));
                    return got.Count == files && gotBytes == bytes && emptyFolder ? files + " files and every folder arrived exactly" : "FAIL: " + got.Count + " of " + files + " files, " + gotBytes + " of " + bytes + " bytes, empty folder " + emptyFolder;
                });

                Scenario("64 MB each way at the same moment", 120, () =>
                {
                    string a = Big("both-a.bin", 64), b = Big("both-b.bin", 64);
                    string[]? toHost = null, toClient = null;
                    host.ClipboardFilesReceived += p => toHost = p;
                    ctrl.ClipboardFilesReceived += p => toClient = p;
                    var t1 = Task.Run(() => ctrl.Files!.SendFiles(new[] { a }));
                    var t2 = Task.Run(() => host.ControllerFiles!.SendFiles(new[] { b }));
                    Task.WaitAll(t1, t2);
                    for (int i = 0; i < 500 && (toHost == null || toClient == null); i++) Thread.Sleep(20);
                    bool ok = toHost != null && toClient != null && Same(a, toHost[0]) && Same(b, toClient[0]);
                    return ok ? "both arrived whole" : "FAIL: to host " + (toHost != null) + ", to client " + (toClient != null);
                });

                Scenario("copying something new while 256 MB is still going", 120, () =>
                {
                    string big = Big("replaced.bin", 256), small = Big("replacement.bin", 1);
                    var results = new List<FileChannel.Transfer>();
                    ctrl.TransferProgress += t => { if (t.Finished) lock (results) results.Add(t); };
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    var first = Task.Run(() => ctrl.Files!.SendFiles(new[] { big }));
                    Thread.Sleep(300);
                    ctrl.Files!.SendFiles(new[] { small });
                    first.Wait();
                    for (int i = 0; i < 300 && landed == null; i++) Thread.Sleep(20);
                    bool replaced = landed != null && Path.GetFileName(landed[0]) == "replacement.bin" && Same(small, landed[0]);
                    bool stopped = results.Any(r => r.Failed && r.What == "replaced.bin");
                    return replaced && stopped ? "the old one stopped, the new one arrived" : "FAIL: replaced " + replaced + ", old one stopped " + stopped;
                });

                Scenario("a file another program has locked", 30, () =>
                {
                    string locked = Big("locked.bin", 1);
                    using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    FileChannel.Transfer? result = null;
                    ctrl.TransferProgress += t => { if (t.Finished && t.What == "locked.bin") result = t; };
                    ctrl.Files!.SendFiles(new[] { locked });
                    hold.Dispose();
                    string after = Big("after-lock.bin", 1);
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    ctrl.Files.SendFiles(new[] { after });
                    for (int i = 0; i < 300 && landed == null; i++) Thread.Sleep(20);
                    return result?.Failed == true && landed != null ? "said it could not send it (" + result.Result + "), and the next copy still worked" : "FAIL: result " + result?.Result + ", next copy arrived " + (landed != null);
                });

                Scenario("50 MB of clipboard text", 60, () =>
                {
                    string text = string.Concat(Enumerable.Repeat("Fifty megabytes of clipboard, every character checked. ", 950_000));
                    string? got = null;
                    Action<string> took = t => got = t;
                    host.ClipboardReceived += took;
                    ctrl.SendClipboard(text);
                    for (int i = 0; i < 1500 && got == null; i++) Thread.Sleep(20);
                    host.ClipboardReceived -= took;
                    string result = got == text ? (text.Length / 1_000_000) + " million characters arrived exactly" : "FAIL: " + (got?.Length.ToString() ?? "nothing");
                    got = null; // let it go, so the leak check sees TailRemote's own memory
                    return result;
                });

                Scenario("50 MB of clipboard text three times: memory must not grow", 120, () =>
                {
                    // Nothing here keeps the text: only its length is noted. If TailRemote held on to
                    // what it shared, memory would climb by about 100 MB a round.
                    var sizes = new List<long>();
                    for (int round = 0; round < 3; round++)
                    {
                        int length = -1;
                        Action<string> took = t => length = t.Length;
                        host.ClipboardReceived += took;
                        ctrl.SendClipboard(string.Concat(Enumerable.Repeat("Round " + round + " of clipboard text. ", 2_600_000)));
                        for (int i = 0; i < 1500 && length < 0; i++) Thread.Sleep(20);
                        host.ClipboardReceived -= took;
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        sizes.Add(GC.GetTotalMemory(true) >> 20);
                    }
                    long growth = sizes[2] - sizes[0];
                    return growth < 30 ? "memory after each round: " + string.Join(", ", sizes) + " MB, so nothing is kept" : "FAIL: memory grew " + growth + " MB over three rounds (" + string.Join(", ", sizes) + ")";
                });

                Scenario("a hostile PC sending files that try to escape the holding folder", 30, () =>
                {
                    string outside = Path.Combine(Root, "escaped.txt");
                    string? arrived = null;
                    host.ClipboardFilesReceived += p => arrived = string.Join(",", p);
                    foreach (string evil in new[] { "..\\escaped.txt", "../escaped.txt", "C:\\Windows\\escaped.txt", "\\\\server\\share\\x.txt", "a\\..\\..\\escaped.txt", "con.txt\u0000x", "" })
                        ctrl.Files!.TestSendEntries(new List<(string, byte[]?)> { (evil, Encoding.UTF8.GetBytes("pwned")) });
                    Thread.Sleep(1500);
                    string? acceptedEvil = arrived; // before the normal copy below, which should arrive
                    bool escaped = File.Exists(outside) || File.Exists("C:\\Windows\\escaped.txt");
                    string next = Big("still-fine.bin", 1);
                    string[]? landed = null;
                    host.ClipboardFilesReceived += p => landed = p;
                    ctrl.Files!.SendFiles(new[] { next });
                    for (int i = 0; i < 300 && landed == null; i++) Thread.Sleep(20);
                    return !escaped && acceptedEvil == null && landed != null ? "every escape was refused, and normal copies still work" : "FAIL: escaped " + escaped + ", accepted " + acceptedEvil;
                });

                Scenario("sharing turned off: nothing may arrive", 30, () =>
                {
                    string? text = null;
                    string[]? files = null;
                    ctrl.ClipboardReceived += t => text = t;
                    ctrl.ClipboardFilesReceived += p => files = p;
                    ctrl.SetClipboardSharing(false);
                    Thread.Sleep(300);
                    host.SendClipboard("should never arrive");
                    host.SendClipboard(new string('z', 100_000));
                    host.SendClipboardFiles(new[] { Big("never.bin", 1) });
                    Thread.Sleep(2000);
                    bool quiet = text == null && files == null;
                    ctrl.SetClipboardSharing(true);
                    Thread.Sleep(300);
                    host.SendClipboard("now it should");
                    for (int i = 0; i < 200 && text == null; i++) Thread.Sleep(20);
                    return quiet && text == "now it should" ? "nothing arrived while off; it worked again when back on" : "FAIL: while off text " + (text != null) + ", files " + (files != null);
                });

                Scenario("the connection dying in the middle of 256 MB", 60, () =>
                {
                    string big = Big("dies.bin", 256);
                    var c2 = Connect(Password); // a second controller, to be cut off
                    for (int i = 0; i < 100 && c2.Files == null; i++) Thread.Sleep(50);
                    var send = Task.Run(() => c2.Files!.SendFiles(new[] { big }));
                    Thread.Sleep(400);
                    c2.Dispose();
                    send.Wait(10_000);
                    Thread.Sleep(1500);
                    var partial = Directory.Exists(FileChannel.Staging) ? Directory.EnumerateFiles(FileChannel.Staging, "dies.bin", SearchOption.AllDirectories).ToList() : new List<string>();
                    return partial.Count == 0 ? "the half-arrived file was thrown away, nothing left behind" : "FAIL: a partial file was left: " + partial[0];
                });
            }

            Scenario("stopping the host in the middle of everything, then starting it again", 60, () =>
            {
                var c = Connect(Password);
                var ls = Enumerable.Range(0, 10).Select(_ => Connect(ListenPassword)).ToList();
                for (int i = 0; i < 100 && c.Files == null; i++) Thread.Sleep(50);
                var send = Task.Run(() => { try { c.Files?.SendFiles(new[] { Big("mid-stop.bin", 128) }); } catch { } });
                Thread.Sleep(300);
                host.Dispose();
                bool allNoticed = WaitClosed(c, 12_000) && ls.All(l => WaitClosed(l, 12_000));
                send.Wait(10_000);
                c.Dispose();
                foreach (var l in ls) l.Dispose();
                Thread.Sleep(500);
                host = NewHost();
                Thread.Sleep(500);
                using var back = Connect(Password);
                return allNoticed ? "every PC noticed, the port was freed, and hosting again worked" : "FAIL: not every PC noticed the host stopping";
            });

            Scenario("six wrong passwords, then the right one", 30, () =>
            {
                int refused = 0;
                for (int i = 0; i < 6; i++) try { using var c = Connect("wrong" + i); } catch (Exception e) when (e.Message.StartsWith("Wrong password") || e.Message == Protocol.DamagedLogin || e.Message.Contains("closed")) { refused++; }
                string right;
                try { using var c = Connect(Password); right = "the right one got in (it should have been blocked for a minute)"; }
                catch (Exception e) { right = "the right one was blocked for now: " + e.Message; }
                return refused == 6 ? "all 6 refused; " + right : "FAIL: only " + refused + " refused";
            });

            // Leaks: memory and threads back where they started.
            Thread.Sleep(3000);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memAfter = GC.GetTotalMemory(true);
            int threadsAfter = Process.GetCurrentProcess().Threads.Count;
            // Memory is checked by the three-round clipboard scenario; here only threads (the test itself holds its own data).
            Add("leaks", threadsAfter - threadsBefore < 30,
                "memory " + (memBefore >> 20) + " MB before, " + (memAfter >> 20) + " MB after; threads " + threadsBefore + " before, " + threadsAfter + " after");
            host.Dispose();

            string text = (_failures == 0 ? "ALL PASSED" : _failures + " FAILED") + Environment.NewLine + string.Join(Environment.NewLine, Report);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-chaostest.txt"), text);
            Console.WriteLine(text);
            try { Directory.Delete(Root, true); } catch { }
            return _failures == 0 ? 0 : 1;
        }

        private static Host NewHost() => new(Port, Password, ListenPassword, m => { lock (Log) Log.Add(m); });

        private static Client Connect(string password)
        {
            var p = new Player(Player.NoDevice, _ => { });
            return Client.Connect("127.0.0.1", Port, password, p, _ => { });
        }

        private static bool WaitClosed(Client c, int ms)
        {
            for (int i = 0; i < ms / 25 && !c.TestClosed; i++) Thread.Sleep(25);
            return c.TestClosed;
        }

        private static string Healthy(Host host, string when)
        {
            try { using var c = Connect(Password); return "a real login still works " + when; }
            catch (Exception e) { return "FAIL: no login " + when + ": " + e.Message; }
        }

        private static string Big(string name, int mb)
        {
            string path = Path.Combine(Root, "src", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = new byte[1 << 20];
            new Random(name.GetHashCode()).NextBytes(data);
            using var f = File.Create(path);
            for (int i = 0; i < mb; i++) f.Write(data);
            return path;
        }

        private static bool Same(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            return System.Security.Cryptography.SHA256.HashData(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb));
        }

        private static readonly string? Only = Environment.GetEnvironmentVariable("CHAOS_ONLY");

        private static void Scenario(string name, int seconds, Func<string> body)
        {
            if (Only != null && !name.Contains(Only, StringComparison.OrdinalIgnoreCase)) return;
            var clock = Stopwatch.StartNew();
            var task = Task.Run(() => { try { return body(); } catch (Exception e) { return "FAIL: " + e.GetType().Name + ": " + e.Message; } });
            string result = task.Wait(TimeSpan.FromSeconds(seconds)) ? task.Result : "FAIL: FROZE (no answer in " + seconds + " seconds)";
            Add(name, !result.Contains("FAIL"), result + " (" + clock.Elapsed.TotalSeconds.ToString("0.0") + " s)");
        }

        private static void Add(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            string line = (ok ? "ok    " : "FAIL  ") + name + ": " + detail;
            lock (Report) Report.Add(line);
            Console.WriteLine(line);
        }
    }
}
