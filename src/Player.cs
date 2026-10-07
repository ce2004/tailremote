using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Plays the host's audio with as little delay as the network allows, and
    /// works out for itself how little that is.
    ///
    /// Every packet's arrival is compared with when it should have arrived. The
    /// worst lateness seen in the last 15 seconds sets the buffer: just enough
    /// to ride it out, plus one packet and one device period. A rough patch
    /// raises the buffer at once; when the network calms down it comes back
    /// down over the following seconds.
    ///
    /// The buffer is steered to that size by playing up to half a percent
    /// faster or slower, which nobody hears, never by cutting audio. Only a
    /// pile-up after a long network stall is cut, because hearing the backlog
    /// late would be worse.
    ///
    /// A lost packet fades the last sound out instead of clicking to silence,
    /// and the next one fades back in.
    /// </summary>
    internal sealed class Player : IDisposable
    {
        /// <summary>Device id that plays nothing (the self-test uses it).</summary>
        public const string NoDevice = "-";

        private const double PacketMs = Protocol.PacketFrames * 1000.0 / Protocol.AudioRate;
        private const double MarginMs = 2;
        private const int JitterBuckets = 15; // seconds of history

        private readonly string? _deviceId;
        private readonly Action<string> _status;
        private volatile bool _stop;
        private readonly Thread _thread;

        // Shared, under _gate.
        private readonly object _gate = new();
        private readonly float[] _ring = new float[192000 * 2 * 2]; // 2 s of stereo at up to 192 kHz
        private int _read, _count; // in floats
        private bool _playing, _starved;
        private int _statPackets, _statLost, _statLate, _statStarved; // since the last TakeStats
        private float _gain, _lastL, _lastR;
        private int _fadeOut;
        private double _avgMs;

        private volatile int _deviceRate;
        private double _periodMs = 10;
        private double _speed; // read by the network thread

        // Network-thread state.
        private Resampler? _rs;
        private int _rsRate;
        private uint _expect, _spurtSeq;
        private bool _haveSeq;
        private double _ref;
        private readonly double[] _jitter = new double[JitterBuckets];
        private long _bucketSecond;
        private double _extraMs;
        private volatile float _targetMs = 20;
        private bool _fadeIn;
        private float[] _scratch = new float[8192];
        private int _scratchLen;
        private Action<float, float>? _collect; // made once, not per packet
        private int _rsIn, _lastFrames;
        private double _silenceCarry;

        /// <summary>Frames in one 5.8 ms packet at this rate, carrying the fraction so time never drifts.</summary>
        private int PacketAt(int inRate)
        {
            if (inRate == Protocol.AudioRate) return Protocol.PacketFrames;
            double exact = Protocol.PacketFrames * (double)inRate / Protocol.AudioRate + _silenceCarry;
            int n = (int)exact;
            _silenceCarry = exact - n;
            return n;
        }
        private readonly float[] _in = new float[Protocol.PacketFrames * 2];
        private readonly float[] _last = new float[Protocol.PacketFrames * 2];

        public Player(string? deviceId, Action<string> status)
        {
            _deviceId = deviceId;
            _status = status;
            if (deviceId == NoDevice) _stop = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "TailRemote playback", Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        /// <summary>
        /// Forgets the last connection's audio, ready for the next. The output
        /// stream stays open, so connecting never opens or closes a device.
        /// Call only while no connection is feeding it.
        /// </summary>
        public void Reset()
        {
            lock (_gate)
            {
                _read = 0; _count = 0; _playing = false; _starved = false; _fadeOut = 0; _gain = 0;
            }
            _haveSeq = false;
        }

        /// <summary>Packets, lost, late and ran-dry counts since the last call: how the connection is coping.</summary>
        public (int Packets, int Lost, int Late, int Starved) TakeStats()
        {
            int starved;
            lock (_gate) { starved = _statStarved; _statStarved = 0; }
            return (Interlocked.Exchange(ref _statPackets, 0), Interlocked.Exchange(ref _statLost, 0), Interlocked.Exchange(ref _statLate, 0), starved);
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(2000);
        }

        /// <summary>Audio delay right now: what is buffered plus the device, in ms. -1 when idle.</summary>
        /// <summary>The playback device, for the log.</summary>
        public string DeviceInfo = "none yet";

        /// <summary>Test only: play silence, with all the timing of the real thing.</summary>
        public bool Mute;
        private int _diagDry, _diagSkips, _diagLost, _diagLate, _diagSpurts;
        private readonly uint[] _diagSeqs = new uint[16];
        private int _diagSeqAt;
        private string? _diagOrder;
        private double _diagSkippedMs, _diagLevelMin = double.MaxValue, _diagLevelMax;

        /// <summary>Test only: what happened since the last call.</summary>
        public string Diagnose()
        {
            lock (_gate)
            {
                string s = $"lost {_diagLost}, late {_diagLate}, restarts {_diagSpurts}, dry {_diagDry}, skips {_diagSkips} ({_diagSkippedMs:0} ms), buffer {(_diagLevelMin == double.MaxValue ? 0 : _diagLevelMin):0.0}-{_diagLevelMax:0.0} ms, target {_targetMs + _periodMs:0.0} ms, device period {_periodMs:0.0} ms, delay {DelayMsUnlocked()} ms";
                if (_diagOrder != null) { s += " | arrival order: " + _diagOrder; _diagOrder = null; }
                _diagDry = 0; _diagSkips = 0; _diagLost = 0; _diagLate = 0; _diagSpurts = 0; _diagSkippedMs = 0; _diagLevelMin = double.MaxValue; _diagLevelMax = 0;
                return s;
            }
        }

        private int DelayMsUnlocked() => _playing ? (int)Math.Round(_avgMs + _periodMs) : -1;

        public int DelayMs
        {
            get { lock (_gate) return _playing ? (int)Math.Round(_avgMs + _periodMs) : -1; }
        }

        /// <summary>Called from the network thread with one packet; empty means a silent one.</summary>
        /// <summary>
        /// One packet: always 5.8 ms of sound, at inRate (lower while the
        /// connection struggles), straight to the device's rate in one step.
        /// Empty pcm means a silent packet.
        /// </summary>
        public void Push(uint seq, ReadOnlySpan<byte> pcm, int frames = Protocol.PacketFrames, int inRate = Protocol.AudioRate)
        {
            int rate = _deviceRate;
            if (rate == 0) return;
            if (_rs == null || _rsRate != rate || _rsIn != inRate)
            {
                bool rateChanged = _rs != null && _rsRate == rate;
                _rs = new Resampler(inRate, rate);
                _rsRate = rate;
                _rsIn = inRate;
                _lastFrames = 0; // the old rate's last packet cannot stand in for a lost one
                if (!rateChanged) _haveSeq = false;
                _fadeIn = true;
            }

            double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            int diff = (int)(seq - _expect);
            bool newSpurt = !_haveSeq || diff > 8 || diff < -200;
            if (newSpurt)
            {
                _diagSpurts++;
                _spurtSeq = seq;
                _ref = now;
                diff = 0;
                _fadeIn = true;
            }

            // How late this packet is, against the earliest any packet has been.
            // The reference creeps later at 200 ppm so a host clock that runs
            // slow is not mistaken for growing lateness.
            double raw = now - (int)(seq - _spurtSeq) * PacketMs;
            _ref = Math.Min(raw, _ref + PacketMs * 0.0002);
            RecordJitter(now, raw - _ref);

            lock (_gate)
            {
                if (_starved && !newSpurt && diff == 0) { _extraMs = Math.Min(_extraMs + 3, 60); _statStarved++; } // ran dry mid-sound
                _starved = false;
            }
            _extraMs *= 0.9997; // back down about 5% a second
            UpdateTarget();

            Interlocked.Increment(ref _statPackets);
            _diagSeqs[_diagSeqAt++ & 15] = seq;
            if (diff < 0 && _diagOrder == null)
            {
                var order = new System.Text.StringBuilder();
                for (int i = 0; i < 16; i++) order.Append(_diagSeqs[(_diagSeqAt + i) & 15] % 1000).Append(' ');
                _diagOrder = order.ToString();
            }
            if (diff < 0) { Interlocked.Increment(ref _statLate); _diagLate++; return; } // late; its moment has passed
            if (diff > 0) { Interlocked.Add(ref _statLost, diff); _diagLost += diff; }
            _haveSeq = true;
            _expect = seq + 1;

            _rs.SetSpeed(Volatile.Read(ref _speed));
            _scratchLen = 0;
            for (int lost = 0; lost < diff; lost++)
            {
                // Lost: fade the last packet out over the first gap, then silence,
                // always one packet's worth of time at the current rate.
                int n = lost == 0 && _lastFrames > 0 ? _lastFrames : PacketAt(inRate);
                for (int i = 0; i < n * 2; i += 2)
                {
                    float g = lost == 0 && _lastFrames > 0 ? 1f - (float)i / (n * 2) : 0f;
                    _in[i] = _last[i] * g;
                    _in[i + 1] = _last[i + 1] * g;
                }
                _rs.Process(_in.AsSpan(0, n * 2), _collect ??= Collect);
                _fadeIn = true;
            }

            if (pcm.IsEmpty) frames = PacketAt(inRate);
            var block = _in.AsSpan(0, frames * 2);
            if (pcm.IsEmpty) block.Clear();
            else
                for (int i = 0; i < block.Length; i++)
                    block[i] = BitConverter.ToInt16(pcm.Slice(i * 2, 2)) / 32768f;
            if (_fadeIn)
            {
                int fade = Math.Min(block.Length, 128);
                for (int i = 0; i < fade; i += 2) { float g = (float)i / fade; block[i] *= g; block[i + 1] *= g; }
                _fadeIn = false;
            }
            block.CopyTo(_last);
            _lastFrames = frames;
            _rs.Process(block, _collect ??= Collect);

            lock (_gate)
            {
                int cap = _ring.Length;
                for (int i = 0; i < _scratchLen; i++)
                {
                    if (_count == cap) { _read = (_read + 2) % cap; _count -= 2; }
                    _ring[(_read + _count) % cap] = _scratch[i];
                    _count++;
                }
            }
        }

        private void RecordJitter(double nowMs, double lateMs)
        {
            long sec = (long)(nowMs / 1000);
            if (sec != _bucketSecond)
            {
                // Clear the buckets for the seconds that went by.
                for (long s = Math.Max(_bucketSecond + 1, sec - JitterBuckets + 1); s <= sec; s++)
                    _jitter[s % JitterBuckets] = 0;
                _bucketSecond = sec;
            }
            int b = (int)(sec % JitterBuckets);
            if (lateMs > _jitter[b]) _jitter[b] = Math.Min(lateMs, 400);
        }

        /// <summary>
        /// The buffer is fixed: one packet plus a 2 ms margin, never more, whatever
        /// the network does. Late audio is skipped rather than waited for, so the
        /// delay always stays the same. (Jitter is still measured, for nothing but
        /// interest; it no longer moves the target.)
        /// </summary>
        private void UpdateTarget() => _targetMs = (float)(PacketMs + MarginMs + Math.Max(0, HostBurstMs - 11));

        /// <summary>
        /// The largest chunk the host's capture device hands over at once (ms),
        /// sent by the host. A normal 10 ms device adds nothing; a device that
        /// hands over 30 ms at a time adds 20 ms. Fixed for that device: it never
        /// grows with the network.
        /// </summary>
        public volatile int HostBurstMs;

        private void Collect(float l, float r)
        {
            if (_scratchLen + 2 > _scratch.Length) Array.Resize(ref _scratch, _scratch.Length * 2);
            _scratch[_scratchLen++] = l;
            _scratch[_scratchLen++] = r;
        }

        private void Run()
        {
            Native.ProAudioThread();
            string? lastError = null;
            while (!_stop)
            {
                try
                {
                    PlayUntilDeviceChanges();
                    lastError = null;
                }
                catch (Exception e)
                {
                    string msg = "Playback problem: " + e.Message;
                    DiagLog.Write("player error: " + e);
                    if (msg != lastError) _status(msg);
                    lastError = msg;
                    for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(100);
                }
            }
        }

        private unsafe void PlayUntilDeviceChanges()
        {
            var dev = Wasapi.OutputDevice(_deviceId);
            dev.GetId(out string devId);
            bool followDefault = string.IsNullOrEmpty(_deviceId) || devId != _deviceId;
            var client = Wasapi.Activate(dev);
            client.GetMixFormat(out IntPtr fmtPtr);
            var fmt = Wasapi.ReadFormat(fmtPtr);
            // The normal engine period, on purpose. Asking for the smallest one
            // makes Windows reconfigure the device for every program, which cut
            // all sound on the PC for about a second on connect and disconnect,
            // and a period of 3 ms or less underruns (crackles) on a busy PC.
            client.Initialize(0, Wasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, fmtPtr, IntPtr.Zero);
            client.GetDevicePeriod(out long def, out _);
            double periodMs = def / 10000.0;
            DeviceInfo = (Wasapi.FriendlyName(dev) ?? devId) + ", " + fmt.Rate + " Hz, " + fmt.Channels + " channels, " + fmt.Bits + "-bit" + (fmt.IsFloat ? " float" : "") + ", period " + periodMs.ToString("0.0") + " ms";
            DiagLog.Write("player: opened " + DeviceInfo);
            Marshal.FreeCoTaskMem(fmtPtr);

            using var ev = new AutoResetEvent(false);
            client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
            client.GetBufferSize(out uint bufFrames);
            var iid = Wasapi.IID_IAudioRenderClient;
            client.GetService(ref iid, out object o);
            var render = (Wasapi.IAudioRenderClient)o;

            lock (_gate)
            {
                _read = 0; _count = 0; _playing = false; _starved = false;
                _periodMs = periodMs;
            }
            _deviceRate = fmt.Rate;

            var enumerator = Wasapi.Enumerator();
            long lastDeviceCheck = Environment.TickCount64;
            client.Start();
            try
            {
                while (!_stop)
                {
                    ev.WaitOne(50);
                    client.GetCurrentPadding(out uint pad);
                    uint avail = bufFrames - pad;
                    if (avail > 0)
                    {
                        int hr = render.GetBuffer(avail, out IntPtr data);
                        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
                        Fill(data, (int)avail, fmt);
                        render.ReleaseBuffer(avail, 0);
                    }

                    long now = Environment.TickCount64;
                    if (followDefault && now - lastDeviceCheck > 1000)
                    {
                        lastDeviceCheck = now;
                        enumerator.GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eConsole, out var cur);
                        cur.GetId(out string curId);
                        if (curId != devId) return;
                    }
                }
            }
            finally
            {
                _deviceRate = 0;
                try { client.Stop(); } catch { }
            }
        }

        private unsafe void Fill(IntPtr data, int frames, Wasapi.Format fmt)
        {
            int ch = fmt.Channels;
            int cap = _ring.Length;
            double floatsPerMs = fmt.Rate * 2 / 1000.0;
            lock (_gate)
            {
                double target = _targetMs + _periodMs;
                double levelMs = _count / floatsPerMs;
                _diagLevelMin = Math.Min(_diagLevelMin, levelMs);
                _diagLevelMax = Math.Max(_diagLevelMax, levelMs);
                if (!_playing && levelMs >= target + PacketMs / 2) { _playing = true; _avgMs = levelMs; }

                if (_playing)
                {
                    _avgMs += 0.02 * (levelMs - _avgMs);
                    // Steer toward the target: 10 ms off plays 0.5% fast or slow.
                    // Steer back to the target by playing up to 1% fast or slow (10 ms off = 1%),
                    // which nobody hears. Normal swings are smoothed out this way, never cut.
                    double speed = Math.Clamp((_avgMs - target) * 0.001, -0.01, 0.01);
                    Volatile.Write(ref _speed, speed);

                    // A pile-up after a network bump (20 ms over) is cut back at once, so the
                    // delay never grows. Normal swings on a clean line stay under 15 ms.
                    if (levelMs > target + 20)
                    {
                        int drop = (int)((levelMs - target) * floatsPerMs) & ~1;
                        _read = (_read + drop) % cap;
                        _count -= drop;
                        _avgMs = target;
                        _gain = 0; // fade in after the jump rather than click
                        _diagSkips++;
                        _diagSkippedMs += drop / floatsPerMs;
                    }
                }

                int fadeLen = Math.Max(1, fmt.Rate / 500); // 2 ms
                for (int f = 0; f < frames; f++)
                {
                    float l = 0, r = 0;
                    if (_playing && _count < 2)
                    {
                        // Ran dry: refill to the target. Glide out from the last sample
                        // instead of dropping to zero, which is what clicked.
                        _playing = false;
                        _starved = true;
                        _fadeOut = fadeLen;
                        _gain = 0;
                        _diagDry++;
                    }
                    if (_playing)
                    {
                        l = _ring[_read]; r = _ring[(_read + 1) % cap];
                        _read = (_read + 2) % cap;
                        _count -= 2;
                        if (_gain < 1f) { _gain = Math.Min(1f, _gain + 1f / fadeLen); l *= _gain; r *= _gain; }
                        _lastL = l; _lastR = r;
                    }
                    else if (_fadeOut > 0)
                    {
                        float g = (float)_fadeOut / fadeLen;
                        l = _lastL * g; r = _lastR * g;
                        _fadeOut--;
                    }
                    if (Mute) { l = 0; r = 0; }
                    if (fmt.IsFloat && fmt.Bits == 32)
                    {
                        float* p = (float*)data + f * ch;
                        if (ch == 1) p[0] = (l + r) * 0.5f;
                        else { p[0] = l; p[1] = r; for (int c = 2; c < ch; c++) p[c] = 0; }
                    }
                    else if (fmt.Bits == 16)
                    {
                        short* p = (short*)data + f * ch;
                        short sl = (short)Math.Clamp(l * 32767f, -32768f, 32767f), sr = (short)Math.Clamp(r * 32767f, -32768f, 32767f);
                        if (ch == 1) p[0] = (short)((sl + sr) / 2);
                        else { p[0] = sl; p[1] = sr; for (int c = 2; c < ch; c++) p[c] = 0; }
                    }
                    else throw new NotSupportedException("Unsupported output format: " + fmt.Bits + " bit.");
                }
            }
        }
    }
}
