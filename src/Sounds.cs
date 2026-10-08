using System;
using System.Collections.Generic;
using System.IO;
using System.Media;

namespace TailRemote
{
    /// <summary>
    /// Short event sounds, like a phone's text tones, chosen per event in Sounds
    /// (SoundsForm). 12 instruments times 12 little patterns, all in B-flat major,
    /// stereo, with a little room. The piano is a real Steinway (University of Iowa
    /// Electronic Music Studios recordings, free for any use; native\piano); the
    /// rest are made here in code. Built the first time each is used, then kept.
    /// Played on the controlling PC only, never on a host, where they would be sent
    /// along with its sound.
    /// </summary>
    internal static class Sounds
    {
        public enum Tone { Connected, Disconnected, ClipboardSent, ClipboardReceived, FileSent, FileReceived, ControlRemote, ControlLocal, Error }

        /// <summary>What each event is called in the Sounds window.</summary>
        public static string Label(Tone t) => t switch
        {
            Tone.Connected => "&Connected",
            Tone.Disconnected => "&Disconnected",
            Tone.ClipboardSent => "Clipboard &sent",
            Tone.ClipboardReceived => "Clipboard &received",
            Tone.FileSent => "&File sent",
            Tone.FileReceived => "File recei&ved",
            Tone.ControlRemote => "Control re&mote PC",
            Tone.ControlLocal => "Control &this PC",
            _ => "&Error",
        };

        private const int Rate = 48000;
        private const string NoSound = "None";

        public static readonly string[] Instruments =
            { "Piano", "Soft piano", "Chime", "Marimba", "Music box", "Harp", "Electric piano", "Glass", "Kalimba", "Bell", "Text tone", "Vibraphone" };

        // Patterns in B-flat major: (MIDI note, start in seconds). Bb3 58, D4 62, Eb4 63, F4 65, G4 67, A4 69, Bb4 70, C5 72, D5 74.
        private static readonly (string Name, (int Note, double At)[] Notes)[] Patterns =
        {
            ("rising", new[] { (58, 0.0), (62, 0.055), (65, 0.11), (70, 0.165) }),
            ("falling", new[] { (65, 0.0), (62, 0.075), (58, 0.15) }),
            ("two up", new[] { (65, 0.0), (70, 0.05) }),
            ("two down", new[] { (70, 0.0), (65, 0.05) }),
            ("four up", new[] { (62, 0.0), (65, 0.05), (70, 0.10), (74, 0.15) }),
            ("three down", new[] { (74, 0.0), (72, 0.07), (70, 0.14) }),
            ("chord high", new[] { (62, 0.0), (65, 0.0), (70, 0.0) }),
            ("chord low", new[] { (58, 0.0), (62, 0.0), (65, 0.0) }),
            ("sigh", new[] { (63, 0.0), (62, 0.13) }),
            ("single", new[] { (70, 0.0) }),
            ("bounce", new[] { (65, 0.0), (70, 0.06), (65, 0.12) }),
            ("sparkle", new[] { (67, 0.0), (69, 0.035), (70, 0.07), (72, 0.105), (74, 0.14) }),
        };

        /// <summary>Every sound by name, "None" first: "Piano, rising", "Chime, two up" and so on.</summary>
        public static IReadOnlyList<string> All { get; } = BuildNames();

        private static List<string> BuildNames()
        {
            var names = new List<string> { NoSound };
            foreach (var i in Instruments) foreach (var p in Patterns) names.Add(i + ", " + p.Name);
            return names;
        }

        /// <summary>The sound each event gets until it is changed: the piano.</summary>
        public static string Default(Tone t) => "Piano, " + t switch
        {
            Tone.Connected => "rising",
            Tone.Disconnected => "falling",
            Tone.ClipboardSent => "two up",
            Tone.ClipboardReceived => "two down",
            Tone.FileSent => "four up",
            Tone.FileReceived => "three down",
            Tone.ControlRemote => "chord high",
            Tone.ControlLocal => "chord low",
            _ => "sigh",
        };

        /// <summary>The chosen sound for each event (Settings.SoundChoices), looked up by Play.</summary>
        public static Func<Tone, string> Choice = Default;

        private static readonly Dictionary<string, byte[]> Cache = new();
        private static SoundPlayer? _playing; // kept alive while it plays

        /// <summary>Plays the sound chosen for an event. Never throws, never waits.</summary>
        public static void Play(Tone t) => PlayNamed(Choice(t));

        /// <summary>Plays a sound by name (the Sounds window previews with it).</summary>
        public static void PlayNamed(string name)
        {
            try
            {
                byte[]? wav = Get(name);
                if (wav == null) return;
                var player = new SoundPlayer(new MemoryStream(wav));
                player.Play();
                _playing = player;
            }
            catch { }
        }

        /// <summary>Plays a sound by name and waits (for --sampler).</summary>
        public static void PlayNamedAndWait(string name)
        {
            try { if (Get(name) is byte[] wav) new SoundPlayer(new MemoryStream(wav)).PlaySync(); } catch { }
        }

        /// <summary>Plays and waits (for --tones).</summary>
        public static void PlayAndWait(Tone t)
        {
            try { if (Get(Choice(t)) is byte[] wav) new SoundPlayer(new MemoryStream(wav)).PlaySync(); } catch { }
        }

        /// <summary>Builds the chosen sounds in the background, so the first one plays at once.</summary>
        public static void Warm() => System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (Tone t in Enum.GetValues<Tone>()) try { Get(Choice(t)); } catch { }
        });

        private static byte[]? Get(string name)
        {
            if (name == NoSound) return null;
            lock (Cache)
            {
                if (Cache.TryGetValue(name, out var have)) return have;
                int comma = name.IndexOf(", ", StringComparison.Ordinal);
                if (comma < 0) return null;
                int instrument = Array.IndexOf(Instruments, name[..comma]);
                int pattern = Array.FindIndex(Patterns, p => p.Name == name[(comma + 2)..]);
                if (instrument < 0 || pattern < 0) return null;
                byte[] wav = Render(instrument, Patterns[pattern].Notes);
                Cache[name] = wav;
                return wav;
            }
        }

        // ---- Making a sound ----

        private static byte[] Render(int instrument, (int Note, double At)[] notes)
        {
            double last = 0;
            foreach (var n in notes) last = Math.Max(last, n.At);
            int length = (int)((last + 0.95) * Rate);
            var left = new double[length];
            var right = new double[length];
            for (int k = 0; k < notes.Length; k++)
            {
                var (note, at) = notes[k];
                int start = (int)(at * Rate);
                // A slight spread across the notes, left to right with the pitch.
                double pan = notes.Length == 1 ? 0 : -0.25 + 0.5 * k / (notes.Length - 1);
                double gl = Math.Sqrt(0.5 * (1 - pan)), gr = Math.Sqrt(0.5 * (1 + pan));
                if (instrument <= 1)
                {
                    var (l, r) = Piano(note);
                    bool soft = instrument == 1;
                    double lp = 0, rp = 0, a = soft ? 0.18 : 1; // the soft piano: rounder and quieter
                    int n = Math.Min(l.Length, length - start);
                    for (int i = 0; i < n; i++)
                    {
                        lp += a * (l[i] - lp); rp += a * (r[i] - rp);
                        double g = soft ? 0.8 : 1;
                        left[start + i] += lp * g;
                        right[start + i] += rp * g;
                    }
                }
                else
                {
                    double[] v = Voice(instrument, 440 * Math.Pow(2, (note - 69) / 12.0), length - start);
                    for (int i = 0; i < v.Length; i++) { left[start + i] += v[i] * gl; right[start + i] += v[i] * gr; }
                }
            }
            Room(left, right, instrument == 1 ? 0.26 : 0.18);

            double peak = 1e-9;
            for (int i = 0; i < length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
            double gain = 0.40 / peak;
            int fade = Rate / 12;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8); w.Write(36 + length * 4); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(length * 4);
            for (int i = 0; i < length; i++)
            {
                double f = i > length - fade ? (length - i) / (double)fade : 1;
                w.Write((short)Math.Clamp(Math.Round(left[i] * gain * f * 32767), -32768, 32767));
                w.Write((short)Math.Clamp(Math.Round(right[i] * gain * f * 32767), -32768, 32767));
            }
            w.Flush();
            return ms.ToArray();
        }

        private static readonly Dictionary<int, (double[] L, double[] R)> PianoNotes = new();

        /// <summary>A Steinway note, quick: it rings 0.42 s, then the damper falls over 0.22 s.</summary>
        private static (double[] L, double[] R) Piano(int note)
        {
            if (PianoNotes.TryGetValue(note, out var have)) return have;
            using var s = typeof(Sounds).Assembly.GetManifestResourceStream("piano." + note + ".wav")
                ?? throw new InvalidOperationException("No piano note " + note);
            using var r = new BinaryReader(s);
            r.ReadBytes(44);
            int frames = (int)((s.Length - 44) / 4);
            int keep = Math.Min(frames, (int)(0.64 * Rate)), release = (int)(0.22 * Rate);
            var l = new double[keep];
            var rr = new double[keep];
            for (int i = 0; i < keep; i++)
            {
                double g = i > keep - release ? Math.Pow(Math.Cos((i - (keep - release)) / (double)release * Math.PI / 2), 2) : 1;
                l[i] = r.ReadInt16() / 32768.0 * g;
                rr[i] = r.ReadInt16() / 32768.0 * g;
            }
            return PianoNotes[note] = (l, rr);
        }

        /// <summary>One note of a made instrument, mono, at most 0.75 s.</summary>
        private static double[] Voice(int instrument, double f, int room)
        {
            int length = Math.Min(room, (int)(0.75 * Rate));
            var v = new double[length];
            var rnd = new Random((int)f);
            double Tau(double seconds, double t) => Math.Exp(-t / seconds);
            switch (Instruments[instrument])
            {
                case "Harp":
                {
                    // A plucked string (Karplus-Strong): a burst of noise through a tuned, damped loop.
                    int period = (int)Math.Round(Rate / f);
                    var line = new double[period];
                    for (int i = 0; i < period; i++) line[i] = rnd.NextDouble() * 2 - 1;
                    for (int i = 0, at = 0; i < length; i++)
                    {
                        int next = (at + 1) % period;
                        double y = line[at];
                        line[at] = 0.996 * 0.5 * (line[at] + line[next]);
                        at = next;
                        v[i] = y * 0.6;
                    }
                    break;
                }
                default:
                    for (int i = 0; i < length; i++)
                    {
                        double t = i / (double)Rate, w = 2 * Math.PI * f * t;
                        double attack = Math.Min(1, i / (Rate * 0.003));
                        v[i] = attack * Instruments[instrument] switch
                        {
                            // Bell-like: a carrier and a modulator 3.5 times higher, the brightness fading.
                            "Chime" => Math.Sin(w + 2.2 * Tau(0.25, t) * Math.Sin(3.5 * w)) * Tau(0.45, t),
                            "Marimba" => (Math.Sin(w) + 0.35 * Math.Sin(4 * w) * Tau(0.03, t)) * Tau(0.18, t),
                            "Music box" => (Math.Sin(2 * w) + 0.3 * Math.Sin(6 * w) * Tau(0.08, t)) * Tau(0.4, t),
                            // A tine: the body at 1:1, a bright knock at 14 times that fades at once.
                            "Electric piano" => (Math.Sin(w + 1.2 * Tau(0.3, t) * Math.Sin(w)) + 0.25 * Math.Sin(14 * w) * Tau(0.015, t)) * Tau(0.5, t),
                            "Glass" => (Math.Sin(w) + 0.4 * Math.Sin(2.76 * w) * Tau(0.3, t)) * Math.Min(1, t / 0.02) * Tau(0.5, t),
                            "Kalimba" => (Math.Sin(w) + 0.5 * Math.Sin(5.4 * w) * Tau(0.012, t)) * Tau(0.25, t),
                            // A small bell: its out-of-tune overtones, an octave up.
                            "Bell" => (Math.Sin(2 * w) + 0.6 * Math.Sin(2 * 2.4 * w) * Tau(0.2, t) + 0.35 * Math.Sin(2 * 3.9 * w) * Tau(0.12, t)) * Tau(0.5, t),
                            // A phone's text tone: a pure blip that dips a hair in pitch.
                            "Text tone" => Math.Sin(2 * Math.PI * f * 2 * (t - 0.02 * t * t)) * Tau(0.09, t),
                            "Vibraphone" => (Math.Sin(w) + 0.25 * Math.Sin(4 * w) * Tau(0.05, t)) * (1 - 0.3 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 5.5 * t))) * Tau(0.55, t),
                            _ => Math.Sin(w) * Tau(0.3, t),
                        };
                    }
                    break;
            }
            int release = Rate / 20; // a soft stop at the end, never a click
            for (int i = 0; i < Math.Min(release, length); i++) v[length - 1 - i] *= i / (double)release;
            return v;
        }

        /// <summary>A little room in stereo (Freeverb: eight damped echoes and four smears each side).</summary>
        private static void Room(double[] left, double[] right, double wet)
        {
            int[] combs = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
            int[] passes = { 556, 441, 341, 225 };
            double scale = Rate / 44100.0;
            double[] Side(double[] input, int spread)
            {
                var output = new double[input.Length];
                foreach (int c in combs)
                {
                    var buf = new double[(int)((c + spread) * scale)];
                    double store = 0;
                    for (int i = 0, at = 0; i < input.Length; i++)
                    {
                        double y = buf[at];
                        store = y * 0.6 + store * 0.4;          // damping
                        buf[at] = input[i] * 0.015 + store * 0.8; // room size
                        at = (at + 1) % buf.Length;
                        output[i] += y;
                    }
                }
                foreach (int p in passes)
                {
                    var buf = new double[(int)((p + spread) * scale)];
                    for (int i = 0, at = 0; i < output.Length; i++)
                    {
                        double b = buf[at];
                        double y = b - output[i];
                        buf[at] = output[i] + b * 0.5;
                        at = (at + 1) % buf.Length;
                        output[i] = y;
                    }
                }
                return output;
            }
            var wl = Side(left, 0);
            var wr = Side(right, 23);
            for (int i = 0; i < left.Length; i++)
            {
                left[i] = left[i] * (1 - wet * 0.5) + wl[i] * wet * 3;
                right[i] = right[i] * (1 - wet * 0.5) + wr[i] * wet * 3;
            }
        }
    }
}
