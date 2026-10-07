using System;

namespace TailRemote
{
    /// <summary>
    /// The second, smaller packet coder: FLAC's methods, tuned for one 5.8 ms
    /// packet that has to stand alone (a lost packet never spoils the next).
    ///
    /// For every packet it tries each stereo pairing (left and right, left and
    /// difference, right and difference, middle and difference) and, for each
    /// channel, the fixed predictors (order 0 to 4) and learned predictors (LPC,
    /// order 4, 8 and 12), and keeps whatever costs the fewest bits. Residuals
    /// are Rice-coded in 8 slices, each with its own parameter.
    ///
    /// Quality 0 is exactly lossless. Quality 1 to 4 drop that many of the
    /// lowest bits before coding (15 down to 12 bits), only while the connection
    /// is struggling; the player asks for it, and asks for it back.
    /// </summary>
    internal static class Lossless2
    {
        private const int Frames = Protocol.PacketFrames;
        /// <summary>Rice slices: 8 for a full packet, fewer for the short packets of a low sample rate.</summary>
        private static int Parts(int n) => n >= 128 ? 8 : n >= 64 ? 4 : 2;
        private const int Escape = 24, EscapeBits = 22;
        private const int MaxLpc = 12, CoefBits = 13;
        private static readonly int[] LpcOrders = { 4, 8, 12 };

        /// <summary>Packs one 44.1 kHz packet (the 1.5.0 format); returns the length, or -1 if it would not beat raw.</summary>
        public static int Encode(short[] pcm, int quality, Span<byte> dst)
        {
            var w = new BitWriter(dst[..Math.Min(dst.Length, Frames * 4 - 1)]);
            w.Write((uint)quality, 3);
            return Core(pcm, Frames, quality, ref w);
        }

        /// <summary>
        /// Packs a packet at one of the lower sample rates (Protocol.Rates), any
        /// length up to 256 frames: [rate level 3][frames 9] then the same coding.
        /// </summary>
        public static int EncodeRate(ReadOnlySpan<short> pcm, int frames, int rateLevel, Span<byte> dst)
        {
            var w = new BitWriter(dst[..Math.Min(dst.Length, frames * 4 - 1)]);
            w.Write((uint)rateLevel, 3);
            w.Write((uint)frames, 9);
            return Core(pcm, frames, 0, ref w);
        }

        private static int Core(ReadOnlySpan<short> pcm, int n, int quality, ref BitWriter w)
        {
            if (n < 4) return -1;
            Span<int> l = stackalloc int[n], r = stackalloc int[n], m = stackalloc int[n], sd = stackalloc int[n];
            int half = quality > 0 ? 1 << (quality - 1) : 0;
            for (int i = 0; i < n; i++)
            {
                l[i] = Math.Clamp((pcm[i * 2] + half) >> quality, short.MinValue >> quality, short.MaxValue >> quality);
                r[i] = Math.Clamp((pcm[i * 2 + 1] + half) >> quality, short.MinValue >> quality, short.MaxValue >> quality);
                m[i] = (l[i] + r[i]) >> 1;
                sd[i] = l[i] - r[i];
            }

            Span<int> scratch = stackalloc int[n];
            var pl = Plan(l, scratch); var pr = Plan(r, scratch); var pm = Plan(m, scratch); var ps = Plan(sd, scratch); // scratch is n long
            long lr = pl.Bits + pr.Bits, ls = pl.Bits + ps.Bits, rs = pr.Bits + ps.Bits, ms = pm.Bits + ps.Bits;
            int mode = 0; long best = lr;
            if (ls < best) { best = ls; mode = 1; }
            if (rs < best) { best = rs; mode = 2; }
            if (ms < best) { best = ms; mode = 3; }

            w.Write((uint)mode, 2);
            switch (mode)
            {
                case 0: Write(ref w, l, pl, scratch); Write(ref w, r, pr, scratch); break;
                case 1: Write(ref w, l, pl, scratch); Write(ref w, sd, ps, scratch); break;
                case 2: Write(ref w, r, pr, scratch); Write(ref w, sd, ps, scratch); break;
                default: Write(ref w, m, pm, scratch); Write(ref w, sd, ps, scratch); break;
            }
            return w.Finish();
        }

        /// <summary>Unpacks into raw 16-bit little-endian stereo; false if the data is not valid.</summary>
        public static bool Decode(ReadOnlySpan<byte> src, Span<byte> pcmBytes)
        {
            var rd = new BitReader(src);
            int quality = (int)rd.Read(3);
            return quality <= 4 && DecodeCore(ref rd, Frames, quality, pcmBytes);
        }

        /// <summary>Unpacks a lower-rate packet: its rate level and frame count, and the 16-bit samples.</summary>
        public static bool DecodeRate(ReadOnlySpan<byte> src, Span<byte> pcmBytes, out int rateLevel, out int frames)
        {
            var rd = new BitReader(src);
            rateLevel = (int)rd.Read(3);
            frames = (int)rd.Read(9);
            if (rateLevel >= Protocol.Rates.Length || frames < 4 || frames > Frames || rd.Overrun) return false;
            return DecodeCore(ref rd, frames, 0, pcmBytes);
        }

        private static bool DecodeCore(ref BitReader rd, int n, int quality, Span<byte> pcmBytes)
        {
            Span<int> a = stackalloc int[n], b = stackalloc int[n];
            int mode = (int)rd.Read(2);
            if (!ReadChannel(ref rd, a) || !ReadChannel(ref rd, b)) return false;
            for (int i = 0; i < n; i++)
            {
                int l, r;
                switch (mode)
                {
                    case 0: l = a[i]; r = b[i]; break;
                    case 1: l = a[i]; r = a[i] - b[i]; break;
                    case 2: r = a[i]; l = a[i] + b[i]; break;
                    default:
                        int s = b[i], mm = (a[i] << 1) | (s & 1);
                        l = (mm + s) >> 1; r = (mm - s) >> 1;
                        break;
                }
                l <<= quality; r <<= quality;
                if (l < short.MinValue || l > short.MaxValue || r < short.MinValue || r > short.MaxValue) return false;
                BitConverter.TryWriteBytes(pcmBytes[(i * 4)..], (short)l);
                BitConverter.TryWriteBytes(pcmBytes[(i * 4 + 2)..], (short)r);
            }
            return true;
        }

        // ---------------- Choosing a predictor ----------------

        private struct Choice
        {
            public bool Lpc;
            public int Order, Shift;
            public int[] Coefs; // LPC only
            public long Bits;
        }

        private static Choice Plan(ReadOnlySpan<int> x, Span<int> res)
        {
            var best = new Choice { Bits = long.MaxValue };
            for (int order = 0; order <= 4; order++)
            {
                var c = new Choice { Lpc = false, Order = order };
                if (!Residuals(x, c, res)) continue;
                c.Bits = 4 + RiceBits(res);
                if (c.Bits < best.Bits) best = c;
            }

            Span<double> ac = stackalloc double[MaxLpc + 1];
            Autocorrelate(x, ac);
            if (ac[0] > 0)
            {
                Span<double> lpc = stackalloc double[MaxLpc * MaxLpc];
                Levinson(ac, lpc);
                foreach (int order in LpcOrders)
                {
                    var c = Quantize(lpc.Slice((order - 1) * MaxLpc, order), order);
                    if (!Residuals(x, c, res)) continue;
                    c.Bits = 4 + 4 + 4 + order * CoefBits + RiceBits(res);
                    if (c.Bits < best.Bits) best = c;
                }
            }
            return best;
        }

        private static void Autocorrelate(ReadOnlySpan<int> x, Span<double> ac)
        {
            int len = x.Length;
            Span<double> wx = stackalloc double[len];
            for (int i = 0; i < len; i++)
            {
                double t = (2.0 * i - (len - 1)) / (len - 1);
                wx[i] = x[i] * (1 - t * t); // Welch window
            }
            for (int lag = 0; lag <= MaxLpc; lag++)
            {
                double sum = 0;
                for (int i = lag; i < len; i++) sum += wx[i] * wx[i - lag];
                ac[lag] = sum;
            }
            ac[0] *= 1.0 + 1e-9; // keeps Levinson stable on pure tones
        }

        /// <summary>Levinson-Durbin: row (order-1) of lpc holds the coefficients for that order.</summary>
        private static void Levinson(ReadOnlySpan<double> ac, Span<double> lpc)
        {
            Span<double> a = stackalloc double[MaxLpc + 1], tmp = stackalloc double[MaxLpc + 1];
            double err = ac[0];
            for (int i = 1; i <= MaxLpc; i++)
            {
                double acc = ac[i];
                for (int j = 1; j < i; j++) acc -= a[j] * ac[i - j];
                double k = err == 0 ? 0 : acc / err;
                a.CopyTo(tmp);
                a[i] = k;
                for (int j = 1; j < i; j++) a[j] = tmp[j] - k * tmp[i - j];
                err *= 1 - k * k;
                for (int j = 0; j < i; j++) lpc[(i - 1) * MaxLpc + j] = a[j + 1];
                if (err <= 0) err = 1e-9;
            }
        }

        private static Choice Quantize(ReadOnlySpan<double> c, int order)
        {
            double max = 0;
            for (int i = 0; i < order; i++) max = Math.Max(max, Math.Abs(c[i]));
            int shift = 15;
            int limit = (1 << (CoefBits - 1)) - 1;
            while (shift > 0 && max * (1 << shift) > limit) shift--;
            var q = new int[order];
            for (int i = 0; i < order; i++) q[i] = (int)Math.Clamp(Math.Round(c[i] * (1 << shift)), -limit - 1, limit);
            return new Choice { Lpc = true, Order = order, Shift = shift, Coefs = q };
        }

        private static long Predict(ReadOnlySpan<int> x, int n, in Choice c)
        {
            if (!c.Lpc || n < c.Order)
            {
                // Fixed predictors; LPC's first samples use the best fixed one that fits.
                int order = c.Lpc ? Math.Min(n, 2) : Math.Min(c.Order, n);
                return order switch
                {
                    0 => 0,
                    1 => x[n - 1],
                    2 => 2L * x[n - 1] - x[n - 2],
                    3 => 3L * x[n - 1] - 3L * x[n - 2] + x[n - 3],
                    _ => 4L * x[n - 1] - 6L * x[n - 2] + 4L * x[n - 3] - x[n - 4],
                };
            }
            long sum = 0;
            for (int j = 0; j < c.Order; j++) sum += (long)c.Coefs[j] * x[n - 1 - j];
            return sum >> c.Shift;
        }

        private static bool Residuals(ReadOnlySpan<int> x, in Choice c, Span<int> res)
        {
            for (int n = 0; n < x.Length; n++)
            {
                long e = x[n] - Predict(x, n, c);
                if (e >= 1 << 20 || e <= -(1 << 20)) return false;
                res[n] = (int)e;
            }
            return true;
        }

        private static uint Zig(int e) => (uint)((e << 1) ^ (e >> 31));

        private static int BestK(ReadOnlySpan<int> part, out long bits)
        {
            long sum = 0;
            foreach (int e in part) sum += Zig(e);
            long mean = sum / part.Length;
            int guess = 0;
            while (guess < 20 && (1L << (guess + 1)) <= mean) guess++;
            int bestK = guess; bits = long.MaxValue;
            for (int k = Math.Max(0, guess - 1); k <= Math.Min(21, guess + 1); k++)
            {
                long b = 0;
                foreach (int e in part)
                {
                    uint q = Zig(e) >> k;
                    b += q < Escape ? q + 1 + k : Escape + EscapeBits;
                }
                if (b < bits) { bits = b; bestK = k; }
            }
            return bestK;
        }

        private static long RiceBits(ReadOnlySpan<int> res)
        {
            long total = 0;
            int parts = Parts(res.Length);
            for (int p = 0; p < parts; p++)
            {
                int from = p * res.Length / parts, to = (p + 1) * res.Length / parts;
                BestK(res[from..to], out long b);
                total += 5 + b;
            }
            return total;
        }

        // ---------------- Writing and reading ----------------

        private static void Write(ref BitWriter w, scoped ReadOnlySpan<int> x, in Choice c, scoped Span<int> res)
        {
            Residuals(x, c, res);
            w.Write(c.Lpc ? 1u : 0u, 1);
            if (!c.Lpc) w.Write((uint)c.Order, 3);
            else
            {
                w.Write((uint)c.Order, 4);
                w.Write((uint)c.Shift, 4);
                foreach (int q in c.Coefs) w.Write((uint)q, CoefBits);
            }
            int parts = Parts(res.Length);
            for (int p = 0; p < parts; p++)
            {
                var part = res[(p * res.Length / parts)..((p + 1) * res.Length / parts)];
                int k = BestK(part, out _);
                w.Write((uint)k, 5);
                foreach (int e in part)
                {
                    uint u = Zig(e), q = u >> k;
                    if (q < Escape)
                    {
                        w.Ones((int)q);
                        w.Write(0, 1);
                        if (k > 0) w.Write(u & ((1u << k) - 1), k);
                    }
                    else
                    {
                        w.Ones(Escape);
                        w.Write(u, EscapeBits);
                    }
                }
            }
        }

        private static bool ReadChannel(ref BitReader r, scoped Span<int> x)
        {
            var c = new Choice { Lpc = r.Read(1) == 1 };
            if (!c.Lpc)
            {
                c.Order = (int)r.Read(3);
                if (c.Order > 4) return false;
            }
            else
            {
                c.Order = (int)r.Read(4);
                c.Shift = (int)r.Read(4);
                if (c.Order < 1 || c.Order > MaxLpc) return false;
                c.Coefs = new int[c.Order];
                for (int j = 0; j < c.Order; j++)
                {
                    uint v = r.Read(CoefBits);
                    c.Coefs[j] = (int)(v << (32 - CoefBits)) >> (32 - CoefBits); // sign-extend
                }
            }
            int parts = Parts(x.Length);
            for (int p = 0; p < parts; p++)
            {
                int k = (int)r.Read(5);
                if (k > 21) return false;
                int from = p * x.Length / parts, to = (p + 1) * x.Length / parts;
                for (int n = from; n < to; n++)
                {
                    int q = 0;
                    while (q < Escape && r.Read(1) == 1) q++;
                    uint u = q == Escape ? r.Read(EscapeBits) : ((uint)q << k) | (k > 0 ? r.Read(k) : 0);
                    int e = (int)(u >> 1) ^ -(int)(u & 1);
                    x[n] = (int)(e + Predict(x, n, c));
                    if (r.Overrun) return false;
                }
            }
            return true;
        }

        private ref struct BitWriter
        {
            private readonly Span<byte> _buf;
            private int _pos;
            private ulong _acc;
            private int _bits;
            public bool Overflow;

            public BitWriter(Span<byte> buf) { _buf = buf; _pos = 0; _acc = 0; _bits = 0; Overflow = false; }

            public void Write(uint value, int count)
            {
                _acc = (_acc << count) | (value & (count == 32 ? uint.MaxValue : (1u << count) - 1));
                _bits += count;
                while (_bits >= 8)
                {
                    _bits -= 8;
                    if (_pos >= _buf.Length) { Overflow = true; return; }
                    _buf[_pos++] = (byte)(_acc >> _bits);
                }
            }

            public void Ones(int count)
            {
                while (count > 16) { Write(0xFFFF, 16); count -= 16; }
                if (count > 0) Write((1u << count) - 1, count);
            }

            public int Finish()
            {
                if (_bits > 0) Write(0, 8 - _bits);
                return Overflow ? -1 : _pos;
            }
        }

        private ref struct BitReader
        {
            private readonly ReadOnlySpan<byte> _buf;
            private int _bitPos;
            public bool Overrun;

            public BitReader(ReadOnlySpan<byte> buf) { _buf = buf; _bitPos = 0; Overrun = false; }

            public uint Read(int count)
            {
                uint v = 0;
                for (int i = 0; i < count; i++)
                {
                    int byteIndex = _bitPos >> 3;
                    if (byteIndex >= _buf.Length) { Overrun = true; return 0; }
                    v = (v << 1) | (uint)((_buf[byteIndex] >> (7 - (_bitPos & 7))) & 1);
                    _bitPos++;
                }
                return v;
            }
        }
    }
}
