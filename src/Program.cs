using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TailRemote
{
    internal static class Program
    {
        [STAThread]
        /// <summary>Writes an unexpected error to TailRemote-crash.txt next to the exe (and the log, if on).</summary>
        private static void Crash(Exception? e)
        {
            try
            {
                string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  TailRemote " + Updater.Current + ": " + e + Environment.NewLine;
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "TailRemote-crash.txt"), text);
                DiagLog.Write("unexpected error: " + e);
            }
            catch { }
        }

        private static int Main(string[] args)
        {
            Native.FullSpeed(); // never on power-saving cores: that makes the sound run dry
            // Nothing may ever close TailRemote by surprise: a problem in the window is
            // written down and the app carries on.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
            if (args.Length == 2 && args[0] == "--startup")
                return Startup.Apply(args[1] == "on");

            if (args.Length == 1 && args[0] == "--setup-audio")
            {
                Speech.Init();
                ApplicationConfiguration.Initialize();
                var setup = new SetupForm("Setting up the TailRemote audio device", async (report, ct) =>
                {
                    await AudioSetup.RunAsync(report, ct);
                    return "Done. The TailRemote audio device is ready and is the default output.";
                });
                Application.Run(setup);
                return setup.Result;
            }

            if (args.Length == 3 && args[0] == "--firewall" && int.TryParse(args[2], out int fwPort))
                return Firewall.Apply(args[1] == "open", fwPort);

            if (args.Length == 2 && args[0] == "--service")
                return args[1] == "install" ? ServiceHost.Install() : ServiceHost.Remove();
            if (args.Length == 1 && args[0] == "--service") return ServiceHost.RunService();
            if (args.Length == 1 && args[0] == "--agent") return Agent.Run();

            if (args.Length == 1 && args[0] == "--remove-audio")
            {
                Speech.Init();
                ApplicationConfiguration.Initialize();
                var remove = new SetupForm("Removing the TailRemote audio device", AudioSetup.RemoveAsync);
                Application.Run(remove);
                return remove.Result;
            }

            if (args.Length == 1 && args[0] == "--licence")
            {
                // The licences of what is built in: the NVDA controller client and Concentus (Opus).
                foreach (var name in new[] { "NVDA-controllerClient-LICENSE.txt", "Concentus-LICENSE.txt" })
                {
                    using var s = typeof(Program).Assembly.GetManifestResourceStream(name)!;
                    using var f = File.Create(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, name));
                    s.CopyTo(f);
                }
                return 0;
            }

            if (args.Length == 1 && args[0] == "--selftest") return SelfTest();
            if (args.Length >= 1 && args[0] == "--audiotest")
            {
                if (args.Length > 2 && int.TryParse(args[2], out int jitter)) Client.TestJitterMs = jitter;
                Client.TestHoldQuality = Array.IndexOf(args, "steps") >= 0;
                foreach (var a in args) if (a.StartsWith("drop") && int.TryParse(a[4..], out int drop)) Client.TestDropPercent = drop;
                foreach (var a in args) if (a.StartsWith("lag") && int.TryParse(a[3..], out int lag)) Client.TestLagMs = lag;
                foreach (var a in args) if (a.StartsWith("stall") && int.TryParse(a[5..], out int stall)) Client.TestStallMs = stall;
                foreach (var a in args) if (a.StartsWith("bw") && int.TryParse(a[2..], out int kbps)) Client.TestKbps = kbps;
                foreach (var a in args) if (a.StartsWith("lock") && int.TryParse(a[4..], out int lk)) Client.TestLockStep = lk;
                return AudioTest(args.Length > 1 ? args[1] : null);
            }

            int after = Array.IndexOf(args, "--after-update");
            if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out int oldPid))
                Updater.FinishUpdate(oldPid);

            ClearOldCopies();

            // --resume (used by build.ps1 after swapping in a new build): carry on
            // hosting or connected, whichever was running when the old copy closed.
            string resume = Array.IndexOf(args, "--resume") >= 0 ? Settings.Load().ResumeState : "";

            Speech.Init();
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(
                autoHost: Array.IndexOf(args, "--host") >= 0 || resume == "host",
                autoConnect: Array.IndexOf(args, "--connect") >= 0 || resume == "connect",
                updated: after >= 0));
            return 0;
        }

        /// <summary>Copies that build.ps1 moved aside while they were running; gone once they have exited.</summary>
        private static void ClearOldCopies()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailRemote", "old");
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch { } }
                if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
                string parent = Path.GetDirectoryName(dir)!;
                if (Directory.GetFileSystemEntries(parent).Length == 0) Directory.Delete(parent);
            }
            catch { }
        }

        /// <summary>
        /// The whole audio path on this PC, measured: a 440 Hz tone is played into
        /// one output (default: the first one that is not the default output), a
        /// host captures it, a client receives it and plays it, muted, through the
        /// default output with real device timing. Once a second it writes how
        /// often the buffer ran dry or skipped, and how full it was, to
        /// %TEMP%\tailremote-audiotest.txt.
        /// </summary>
        private static int AudioTest(string? captureName)
        {
            string report = Path.Combine(Path.GetTempPath(), "tailremote-audiotest.txt");
            var lines = new System.Collections.Generic.List<string>();
            void Say(string s) { lines.Add(s); File.WriteAllLines(report, lines); }
            try
            {
                var outputs = Wasapi.OutputDevices();
                Wasapi.Enumerator().GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eConsole, out var def);
                def.GetId(out string defId);
                var src = captureName != null
                    ? outputs.Find(d => d.Name.Contains(captureName, StringComparison.OrdinalIgnoreCase))
                    : outputs.Find(d => d.Id != defId);
                if (src.Id == null) { Say("No output to play the test tone into."); return 1; }
                Say("tone into: " + src.Name + "; playing (muted) through the default output; simulated network delay 0 to " + Client.TestJitterMs + " ms per packet");
                timeBeginPeriod(1);

                // Into the default output (speakers), quiet enough not to hear: loopback records it before the volume.
                using var tone = new ToneSource(src.Id, src.Id == defId ? 0.0003 : 0.3);
                Host.WrongPasswordDelayMs = 0;
                using var host = new Host(47998, "audiotest", null, _ => { }, src.Id);
                using var player = new Player("", s => Say("player: " + s)) { Mute = true, SpeedUp = Array.IndexOf(Environment.GetCommandLineArgs(), "speed") >= 0 };
                using var c = Client.Connect("127.0.0.1", 47998, "audiotest", player, s => Say("client: " + s), Client.TestLockStep);
                System.Threading.Thread.Sleep(1500);
                player.Diagnose();
                for (int i = 1; i <= 15; i++)
                {
                    if (Client.TestHoldQuality) c.TestSetQuality(i % Protocol.OpusSteps.Length); // every bitrate in turn, down and back up
                    System.Threading.Thread.Sleep(1000);
                    int fills = System.Threading.Interlocked.Exchange(ref LoopbackCapture.TestGapFills, 0);
                    int fillMs = System.Threading.Interlocked.Exchange(ref LoopbackCapture.TestGapFillMs, 0);
                    int seqSkips = System.Threading.Interlocked.Exchange(ref LoopbackCapture.TestSeqSkips, 0);
                    int chunk = System.Threading.Interlocked.Exchange(ref LoopbackCapture.TestChunkMax, 0);
                    if (Client.TestLockStep >= 0 && i == 15) Say("locked test: first packet " + Client.TestFirstTicks + " ticks, other lengths " + Client.TestOtherTicks);
                    Say($"{i,2}s  {player.Diagnose()}, quality step {c.AudioQuality} | host: biggest chunk {chunk} ms, silence added {fills}x ({fillMs} ms), count skips {seqSkips}");
                }
                return 0;
            }
            catch (Exception e) { Say("failed: " + e); return 1; }
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);

        /// <summary>Test only: plays a steady 440 Hz tone into one output.</summary>
        private sealed unsafe class ToneSource : IDisposable
        {
            private volatile bool _stop;
            private readonly System.Threading.Thread _t;

            private readonly double _amplitude;

            public ToneSource(string deviceId, double amplitude)
            {
                _amplitude = amplitude;
                _t = new System.Threading.Thread(() => Run(deviceId)) { IsBackground = true };
                _t.Start();
            }

            public void Dispose() { _stop = true; _t.Join(2000); }

            private void Run(string id)
            {
                var dev = Wasapi.OutputDevice(id);
                var client = Wasapi.Activate(dev);
                client.GetMixFormat(out IntPtr fmtPtr);
                var fmt = Wasapi.ReadFormat(fmtPtr);
                client.Initialize(0, Wasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, fmtPtr, IntPtr.Zero);
                using var ev = new System.Threading.AutoResetEvent(false);
                client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
                client.GetBufferSize(out uint buf);
                var iid = Wasapi.IID_IAudioRenderClient;
                client.GetService(ref iid, out object o);
                var render = (Wasapi.IAudioRenderClient)o;
                client.Start();
                double phase = 0, step = 2 * Math.PI * 440 / fmt.Rate;
                while (!_stop)
                {
                    ev.WaitOne(50);
                    client.GetCurrentPadding(out uint pad);
                    uint n = buf - pad;
                    if (n == 0 || render.GetBuffer(n, out IntPtr data) < 0) continue;
                    for (int f = 0; f < n; f++)
                    {
                        float v = (float)(_amplitude * Math.Sin(phase));
                        phase += step;
                        for (int ch = 0; ch < fmt.Channels; ch++)
                        {
                            if (fmt.IsFloat) ((float*)data)[f * fmt.Channels + ch] = v;
                            else ((short*)data)[f * fmt.Channels + ch] = (short)(v * 32767);
                        }
                    }
                    render.ReleaseBuffer(n, 0);
                }
                client.Stop();
            }
        }

        /// <summary>Host and client on this PC: handshake, wrong password, ping. No audio is played.</summary>
        private static int SelfTest()
        {
            string lossless = "";
            // A 1 kHz tone through 48k -> 44.1k -> 48k must keep its level and pitch.
            foreach (var (from, to) in new[] { (48000, 44100), (44100, 48000) })
            {
                var rs = new Resampler(from, to);
                var tone = new float[from * 2];
                for (int i = 0; i < from; i++) tone[i * 2] = tone[i * 2 + 1] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / from);
                var outL = new System.Collections.Generic.List<float>();
                for (int i = 0; i < tone.Length; i += 512) rs.Process(tone.AsSpan(i, Math.Min(512, tone.Length - i)), (l, r) => outL.Add(l));
                var mid = outL.GetRange(1000, to / 2);
                double rms = Math.Sqrt(mid.Average(v => (double)v * v));
                int crossings = 0;
                for (int i = 1; i < mid.Count; i++) if (mid[i - 1] < 0 && mid[i] >= 0) crossings++;
                if (Math.Abs(rms - 0.3536) > 0.005 || Math.Abs(crossings - 500) > 1 || Math.Abs(outL.Count - to) > 40)
                    return Fail($"resampler {from}->{to}: rms {rms:F4}, crossings {crossings}, frames {outL.Count}");
            }

            // Every Opus step: a 1 kHz tone comes back at the same pitch and about the same
            // level, in packets of the step's length, near the step's bitrate.
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int step = 0; step < Protocol.OpusSteps.Length; step++)
                {
                    var (kbps, ms) = Protocol.OpusSteps[step];
                    var enc = OpusBank.CreateEncoder(step);
                    var dec = Concentus.OpusCodecFactory.CreateDecoder(Protocol.AudioRate, 2);
                    int frame = Protocol.AudioRate * ms / 1000;
                    short[] pcm = new short[frame * 2], back = new short[5760 * 2];
                    byte[] packet = new byte[Protocol.MaxOpusBytes];
                    var output = new System.Collections.Generic.List<short>();
                    long bytes = 0;
                    int packets = 1200 / ms, pos = 0;
                    for (int n = 0; n < packets; n++)
                    {
                        for (int f = 0; f < frame; f++, pos++)
                            pcm[f * 2] = pcm[f * 2 + 1] = (short)(0.3 * 32767 * Math.Sin(2 * Math.PI * 1000 * pos / Protocol.AudioRate));
                        int len = enc.Encode(pcm, frame, packet, packet.Length);
                        if (len <= 0 || len > 1200) return Fail("opus " + kbps + ": packet of " + len + " bytes");
                        bytes += len;
                        int decoded = dec.Decode(packet.AsSpan(0, len), back, back.Length / 2, false);
                        if (decoded != frame) return Fail("opus " + kbps + ": decoded " + decoded + " frames, not " + frame);
                        for (int f = 0; f < decoded; f++) output.Add(back[f * 2]);
                    }
                    var mid = output.GetRange(Protocol.AudioRate / 5, Protocol.AudioRate); // one second, after the start
                    double rms = Math.Sqrt(mid.Average(v => (double)v * v)) / 32768;
                    int crossings = 0;
                    for (int k = 1; k < mid.Count; k++) if (mid[k - 1] < 0 && mid[k] >= 0) crossings++;
                    double actual = bytes * 8.0 / (packets * ms);
                    if (Math.Abs(crossings - 1000) > 5 || rms < 0.212 * 0.6 || rms > 0.212 * 1.4 || actual > kbps * 1.3 + 2)
                        return Fail($"opus {kbps} kbit/s: rms {rms:F3}, crossings {crossings}, {actual:0} kbit/s");
                }
                lossless = " | Opus: all " + Protocol.OpusSteps.Length + " steps clean, " + watch.ElapsedMilliseconds + " ms";
            }

            // Audio encryption: a sealed packet opens on the other side; a changed one does not.
            {
                byte[] k = Protocol.DeriveKey("secret"), n1 = new byte[16], n2 = new byte[16];
                n2[0] = 1;
                using var hostLink = new SecureLink(k, n1, n2, isHost: true);
                using var clientLink = new SecureLink(k, n1, n2, isHost: false);
                byte[] pkt = new byte[5 + 256 + SecureLink.TagSize];
                pkt[0] = Protocol.UdpOpus; pkt[1] = 7; pkt[10] = 42;
                hostLink.SealAudio(pkt, 256);
                if (pkt[10] == 42 && pkt[9] == 0 && pkt[11] == 0) return Fail("audio was not encrypted");
                byte[] bad = (byte[])pkt.Clone();
                bad[100] ^= 1;
                if (!clientLink.OpenAudio(pkt, pkt.Length) || pkt[10] != 42) return Fail("audio did not decrypt");
                if (clientLink.OpenAudio(bad, bad.Length)) return Fail("a tampered audio packet was accepted");
            }

            Host.WrongPasswordDelayMs = 0;
            var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
            string Dump() => string.Join(" | ", log);
            using var host = new Host(47999, "secret", "listen", log.Enqueue);
            string? toHost = null;
            host.ClipboardReceived += t => toHost = t;
            if (Protocol.PortProblem("3389", out _) == null || Protocol.PortProblem("abc", out _) == null || Protocol.PortProblem("47120", out _) != null)
                return Fail("port rules");
            try { new Host(47999, "x", null, log.Enqueue).Dispose(); return Fail("a busy port was accepted"); }
            catch (InvalidOperationException e) when (e.Message.Contains("already used")) { }
            using var silent = new Player(Player.NoDevice, log.Enqueue);
            using var silent2 = new Player(Player.NoDevice, log.Enqueue);
            try { Client.Connect("127.0.0.1", 47999, "nope", silent, log.Enqueue).Dispose(); return Fail("wrong password accepted"); }
            catch (InvalidOperationException e) when (e.Message == "Wrong password.") { }
            using var c = Client.Connect("127.0.0.1", 47999, "secret", silent, log.Enqueue);
            for (int i = 0; i < 40 && c.LastPingMs < 0; i++) System.Threading.Thread.Sleep(100);
            if (c.LastPingMs < 0) return Fail("no ping reply. " + Dump());
            if (c.ListenOnly) return Fail("the main password gave a listener");

            // Clipboard both ways, and a listener's clipboard is ignored.
            string? toClient = null;
            c.ClipboardReceived += t => toClient = t;
            c.SendClipboard("from client ✓");
            host.SendClipboard("from host ✓");
            using var listener = Client.Connect("127.0.0.1", 47999, "listen", silent2, log.Enqueue);
            if (!listener.ListenOnly) return Fail("the listen password gave control");
            listener.SendClipboard("from listener");
            for (int i = 0; i < 40 && (toHost == null || toClient == null); i++) System.Threading.Thread.Sleep(50);
            System.Threading.Thread.Sleep(100);
            if (toHost != "from client ✓" || toClient != "from host ✓") return Fail("clipboard: host got " + toHost + ", client got " + toClient);

            // A flood of logins that never finish must not touch a session that is
            // already running, and someone else must still be able to log in.
            {
                var flood = new System.Collections.Generic.List<System.Net.Sockets.TcpClient>();
                for (int i = 0; i < 200; i++)
                {
                    var t = new System.Net.Sockets.TcpClient();
                    try { t.Connect("127.0.0.1", 47999); } catch { }
                    flood.Add(t);
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                toClient = null;
                host.SendClipboard("during the flood");
                while (toClient == null && sw.ElapsedMilliseconds < 3000) System.Threading.Thread.Sleep(10);
                long clipMs = sw.ElapsedMilliseconds;
                foreach (var t in flood) t.Dispose();
                if (toClient != "during the flood") return Fail("the running session froze during a login flood");
                if (clipMs > 500) return Fail("the running session was slow during a login flood: " + clipMs + " ms");
                System.Threading.Thread.Sleep(200);
                using var after = new Player(Player.NoDevice, log.Enqueue);
                using var late = Client.Connect("127.0.0.1", 47999, "listen", after, log.Enqueue);
                if (!late.ListenOnly) return Fail("could not log in after a flood");
                lossless += " | login flood: session answered in " + clipMs + " ms";
            }

            // Files both ways, into a temporary folder, checked byte for byte.
            string dir = Path.Combine(Path.GetTempPath(), "tailremote-selftest-files");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            FileChannel.FolderOverride = Path.Combine(dir, "in");
            Directory.CreateDirectory(dir);
            byte[] content = new byte[700_000];
            new Random(5).NextBytes(content);
            string src = Path.Combine(dir, "test file.bin");
            File.WriteAllBytes(src, content);
            var got = new System.Collections.Concurrent.ConcurrentQueue<string>();
            host.FileMessage += got.Enqueue;
            c.FileMessage += got.Enqueue;
            for (int i = 0; i < 40; i++) { try { c.SendFiles(new[] { src }, (_, _) => { }, default); break; } catch when (i < 39) { System.Threading.Thread.Sleep(50); } }
            host.SendFiles(new[] { src }, (_, _) => { }, default);
            for (int i = 0; i < 100 && got.Count < 2; i++) System.Threading.Thread.Sleep(50);
            string a1 = Path.Combine(FileChannel.Folder, "test file.bin"), a2 = Path.Combine(FileChannel.Folder, "test file (2).bin");
            if (got.Count < 2 || !File.Exists(a2) || !File.ReadAllBytes(a1).AsSpan().SequenceEqual(content) || !File.ReadAllBytes(a2).AsSpan().SequenceEqual(content))
                return Fail("files: " + string.Join(" / ", got));
            Directory.Delete(dir, true);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-selftest.txt"), "ok, ping " + c.LastPingMs + " ms. " + Dump() + lossless + CableReport());
            return 0;

            // Read-only: which virtual cable output setup would rename.
            static string CableReport()
            {
                var d = AudioSetup.FindCable();
                string name = "none";
                if (d != null) { d.GetId(out string id); name = id; }
                string sig = "";
                // TAILREMOTE_VBCHECK: a real VB-Cable installer, and something that is not one.
                if (Environment.GetEnvironmentVariable("TAILREMOTE_VBCHECK") is string vb && File.Exists(vb))
                    sig = ", VB-Audio signature: genuine " + AudioSetup.SignedByVbAudio(vb) + ", other exe " + AudioSetup.SignedByVbAudio(Environment.ProcessPath!);
                return " | cable output: " + name + sig;
            }

            int Fail(string why)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-selftest.txt"), "FAIL: " + why);
                return 1;
            }
        }
    }
}
