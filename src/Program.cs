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

            if (args.Length == 1 && args[0] == "--licence")
            {
                using var s = typeof(Program).Assembly.GetManifestResourceStream("NVDA-controllerClient-LICENSE.txt")!;
                using var f = File.Create(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "NVDA-controllerClient-LICENSE.txt"));
                s.CopyTo(f);
                return 0;
            }

            if (args.Length == 1 && args[0] == "--selftest") return SelfTest();

            Speech.Init();
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(autoHost: Array.IndexOf(args, "--host") >= 0));
            return 0;
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
            try { Client.Connect("127.0.0.1", 47999, "nope", Player.NoDevice, 30, log.Enqueue).Dispose(); return Fail("wrong password accepted"); }
            catch (InvalidOperationException e) when (e.Message == "Wrong password.") { }
            using var c = Client.Connect("127.0.0.1", 47999, "secret", Player.NoDevice, 30, log.Enqueue);
            for (int i = 0; i < 40 && c.LastPingMs < 0; i++) System.Threading.Thread.Sleep(100);
            if (c.LastPingMs < 0) return Fail("no ping reply. " + Dump());
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-selftest.txt"), "ok, ping " + c.LastPingMs + " ms. " + Dump());
            return 0;

            int Fail(string why)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "tailremote-selftest.txt"), "FAIL: " + why);
                return 1;
            }
        }
    }
}
