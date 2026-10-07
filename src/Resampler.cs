using System;

namespace TailRemote
{
    /// <summary>
    /// Streaming stereo windowed-sinc resampler (Kaiser window, 32 taps).
    /// Adds 16 input samples of delay: about a third of a millisecond.
    /// </summary>
    internal sealed class Resampler
    {
        private const int Half = 16;
        private const int Phases = 512;

        private readonly double _step;
        private readonly float[] _table; // (Phases + 1) rows of 2 * Half taps
        private float[] _buf = new float[8192]; // interleaved stereo frames
        private int _frames;
        private double _pos;

        public Resampler(int inRate, int outRate)
        {
            _step = (double)inRate / outRate;
            double fc = Math.Min(1.0, (double)outRate / inRate) * 0.92;
            _table = new float[(Phases + 1) * 2 * Half];
            double i0b = BesselI0(8.0);
            for (int p = 0; p <= Phases; p++)
            {
                double frac = (double)p / Phases;
                for (int k = 0; k < 2 * Half; k++)
                {
                    double x = (k - Half + 1) - frac;
                    double sinc = x == 0 ? 1 : Math.Sin(Math.PI * fc * x) / (Math.PI * fc * x);
                    double w = Math.Abs(x) >= Half ? 0 : BesselI0(8.0 * Math.Sqrt(1 - (x / Half) * (x / Half))) / i0b;
                    _table[p * 2 * Half + k] = (float)(fc * sinc * w);
                }
            }
            Reset();
        }

        /// <summary>
        /// Starts a fresh stream as if the signal had been sitting at (l, r): the
        /// new stream carries on from where the old one stopped instead of rising
        /// out of silence, so a sample-rate change neither dips nor pops.
        /// </summary>
        public void Prime(float l, float r)
        {
            _frames = Half;
            for (int i = 0; i < Half; i++) { _buf[i * 2] = l; _buf[i * 2 + 1] = r; }
            _pos = Half;
        }

        /// <summary>Plays out the last samples still held back by the filter, holding (l, r) after them.</summary>
        public void Flush(float l, float r, Action<float, float> emit)
        {
            Span<float> hold = stackalloc float[Half * 2];
            for (int i = 0; i < Half; i++) { hold[i * 2] = l; hold[i * 2 + 1] = r; }
            Process(hold, emit);
        }

        public void Reset()
        {
            _frames = Half;
            Array.Clear(_buf, 0, Half * 2);
            _pos = Half;
        }


        /// <summary>Feeds interleaved stereo frames; calls emit for each output frame.</summary>
        public void Process(ReadOnlySpan<float> stereo, Action<float, float> emit)
        {
            int add = stereo.Length / 2;
            if ((_frames + add) * 2 > _buf.Length) Array.Resize(ref _buf, (_frames + add) * 4);
            stereo.CopyTo(_buf.AsSpan(_frames * 2));
            _frames += add;

            while (true)
            {
                int i = (int)_pos;
                if (i + Half >= _frames) break;
                double frac = _pos - i;
                double fp = frac * Phases;
                int p = (int)fp;
                float t = (float)(fp - p);
                int row0 = p * 2 * Half, row1 = row0 + 2 * Half;
                float l = 0, r = 0;
                int baseIdx = (i - Half + 1) * 2;
                for (int k = 0; k < 2 * Half; k++)
                {
                    float c = _table[row0 + k] + (_table[row1 + k] - _table[row0 + k]) * t;
                    l += _buf[baseIdx + k * 2] * c;
                    r += _buf[baseIdx + k * 2 + 1] * c;
                }
                emit(l, r);
                _pos += _step;
            }

            // Keep only the history the next call still needs.
            int drop = (int)_pos - Half;
            if (drop > 0)
            {
                Array.Copy(_buf, drop * 2, _buf, 0, (_frames - drop) * 2);
                _frames -= drop;
                _pos -= drop;
            }
        }

        private static double BesselI0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 30; k++) { term *= q / (k * k); sum += term; }
            return sum;
        }
    }
}
