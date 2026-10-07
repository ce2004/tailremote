using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TailRemote
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
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
                using var s = typeof(Program).Assembly.GetManifestResourceStream("NVDA-controllerClient-LICENSE.txt")!;
                using var f = File.Create(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "NVDA-controllerClient-LICENSE.txt"));
                s.CopyTo(f);
                return 0;
            }

            if (args.Length == 1 && args[0] == "--selftest") return SelfTest();

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

        /// <summary>Host and client on this PC: handshake, wrong password, ping. No audio is played.</summary>
        private static int SelfTest()
        {
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

            var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
            string Dump() => string.Join(" | ", log);
            using var host = new Host(47999, "secret", true, log.Enqueue);
            if (Protocol.PortProblem("3389", out _) == null || Protocol.PortProblem("abc", out _) == null || Protocol.PortProblem("47120", out _) != null)
                return Fail("port rules");
            try { new Host(47999, "x", true, log.Enqueue).Dispose(); return Fail("a busy port was accepted"); }
            catch (InvalidOperationException e) when (e.Message.Contains("already used")) { }
            try { Client.Connect("127.0.0.1", 47999, "nope", Player.NoDevice, log.Enqueue).Dispose(); return Fail("wrong password accepted"); }
            catch (InvalidOperationException e) when (e.Message == "Wrong password.") { }
            using var c = Client.Connect("127.0.0.1", 47999, "secret", Player.NoDevice, log.Enqueue);
            for (int i = 0; i < 40 && c.LastPingMs < 0; i++) System.Threading.Thread.Sleep(100);
            if (c.LastPingMs < 0) return Fail("no ping reply. " + Dump());
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-selftest.txt"), "ok, ping " + c.LastPingMs + " ms. " + Dump() + CableReport());
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
