using System;

namespace TailRemote
{
    /// <summary>
    /// Lossless compression of one audio packet (256 stereo 16-bit frames), the
    /// same idea as FLAC: mid/side stereo, a fixed predictor (order 0 to 3,
    /// whichever leaves the smallest error) and Rice-coded residuals. Every
    /// packet stands alone, so a lost packet never spoils the next one. Decoding
    /// gives back exactly the original samples. Costs well under a millisecond.
    /// </summary>
    internal static class Lossless
    {
        private const int Frames = Protocol.PacketFrames;
        private const int Escape = 24, EscapeBits = 21;

        /// <summary>Writes the packed packet; returns its length, or -1 if it would not be smaller than raw.</summary>
        public static int Encode(short[] pcm, Span<byte> dst)
        {
            Span<int> mid = stackalloc int[Frames], side = stackalloc int[Frames];
            for (int i = 0; i < Frames; i++)
            {
                int l = pcm[i * 2], r = pcm[i * 2 + 1];
                mid[i] = (l + r) >> 1;
                side[i] = l - r;
            }
            int limit = Math.Min(dst.Length, Frames * 4 - 1);
            var w = new BitWriter(dst[..limit]);
            if (!EncodeChannel(ref w, mid) || !EncodeChannel(ref w, side)) return -1;
            return w.Finish();
        }

        /// <summary>Unpacks into raw 16-bit little-endian stereo; false if the data is not valid.</summary>
        public static bool Decode(ReadOnlySpan<byte> src, Span<byte> pcmBytes)
        {
            Span<int> mid = stackalloc int[Frames], side = stackalloc int[Frames];
            var r = new BitReader(src);
            if (!DecodeChannel(ref r, mid) || !DecodeChannel(ref r, side)) return false;
            for (int i = 0; i < Frames; i++)
            {
                int s = side[i];
                int m = (mid[i] << 1) | (s & 1);
                int l = (m + s) >> 1, rr = (m - s) >> 1;
                if (l < short.MinValue || l > short.MaxValue || rr < short.MinValue || rr > short.MaxValue) return false;
                BitConverter.TryWriteBytes(pcmBytes[(i * 4)..], (short)l);
                BitConverter.TryWriteBytes(pcmBytes[(i * 4 + 2)..], (short)rr);
            }
            return true;
        }

        private static int Predict(ReadOnlySpan<int> x, int n, int order) => Math.Min(order, n) switch
        {
            0 => 0,
            1 => x[n - 1],
            2 => 2 * x[n - 1] - x[n - 2],
            _ => 3 * x[n - 1] - 3 * x[n - 2] + x[n - 3],
        };

        private static bool EncodeChannel(ref BitWriter w, scoped ReadOnlySpan<int> x)
        {
            int bestOrder = 0;
            long bestSum = long.MaxValue;
            for (int order = 0; order <= 3; order++)
            {
                long sum = 0;
                for (int n = 0; n < Frames; n++) sum += Math.Abs(x[n] - Predict(x, n, order));
                if (sum < bestSum) { bestSum = sum; bestOrder = order; }
            }
            long mean = bestSum / Frames;
            int k = 0;
            while (k < 20 && (1L << (k + 1)) <= mean) k++;

            w.Write((uint)bestOrder, 2);
            w.Write((uint)k, 5);
            for (int n = 0; n < Frames; n++)
            {
                int e = x[n] - Predict(x, n, bestOrder);
                uint u = (uint)((e << 1) ^ (e >> 31)); // zigzag: small magnitudes, small numbers
                uint q = u >> k;
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
                if (w.Overflow) return false;
            }
            return true;
        }

        private static bool DecodeChannel(ref BitReader r, scoped Span<int> x)
        {
            int order = (int)r.Read(2), k = (int)r.Read(5);
            if (k > 20) return false;
            for (int n = 0; n < Frames; n++)
            {
                int q = 0;
                while (q < Escape && r.Read(1) == 1) q++;
                uint u = q == Escape ? r.Read(EscapeBits) : ((uint)q << k) | (k > 0 ? r.Read(k) : 0);
                int e = (int)(u >> 1) ^ -(int)(u & 1);
                x[n] = e + Predict(x, n, order);
                if (r.Overrun) return false;
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
