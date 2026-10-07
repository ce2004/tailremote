using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Plays the host's audio. The pitch never changes.
    ///
    /// The buffer is sized to the connection and always heads back to live: it
    /// covers how unevenly packets arrived over the last 3 seconds (98% of them,
    /// measured from the earliest, so a network that simply got slower costs
    /// nothing once 3 seconds have passed), plus one packet, a 2 ms margin and
    /// the device period, never more than 40 ms. A stall never raises it: what
    /// piles up behind a stall is skipped (or fast-forwarded) the moment it lands. It rises at once and halves
    /// back each half second.
    ///
    /// When the buffer is too full (clock drift, or a burst after a stall) it
    /// catches up one of two ways, never by changing the pitch: by default it
    /// skips straight back; with Catch up by fast-forwarding it plays 30 ms
    /// pieces and jumps ahead between them (1.5x, 2x or 4x) until it is back on
    /// time. Every jump crossfades over 10 ms into the point, within 5 ms of the
    /// aim, where the waveform lines up best, so there is no click, gap or
    /// warble: the way podcast players speed up speech.
    ///
    /// A lost packet fades out instead of clicking, and a change of sample rate
    /// glides: the old rate plays out and the new one starts from its last value.
    /// </summary>
    internal sealed class Player : IDisposable
    {
        /// <summary>Device id that plays nothing (the self-test uses it).</summary>
        public const string NoDevice = "-";

        private const double PacketMs = Protocol.PacketFrames * 1000.0 / Protocol.AudioRate;
        private const double MarginMs = 2;

        private readonly string? _deviceId;
        private readonly Action<string> _status;
        private volatile bool _stop;
        private readonly Thread _thread;

        // Shared, under _gate.
        private readonly object _gate = new();
        private readonly float[] _ring = new float[192000 * 2 * 2]; // 2 s of stereo at up to 192 kHz
        private int _read, _count; // in floats
        private bool _playing;
        private float _gain, _lastL, _lastR;
        private int _fadeOut;
        private double _avgMs;
        private double _ff; // fast-forward speed: 0 = off, else 1.5, 2 or 4
        private int _grainPos; // frames played since the last jump
        private int _xfLeft, _xfLen, _xfFrom, _xfJump; // a jump under way: frames left, length, where the new sound reads, how far ahead

        private volatile int _deviceRate;
        private double _periodMs = 10;
        private volatile float _targetMs = 20;

        // Network-thread state.
        private Resampler? _rs;
        private int _rsRate, _rsIn, _lastFrames;
        private uint _expect, _spurtSeq;
        private bool _haveSeq, _fadeIn;
        private double _ref, _silenceCarry;
        private float[] _scratch = new float[8192];
        private int _scratchLen;
        private Action<float, float>? _collect; // made once, not per packet
        private readonly float[] _in = new float[Protocol.PacketFrames * 2];
        private readonly float[] _last = new float[Protocol.PacketFrames * 2];

        // Lateness of the last 516 packets (3 seconds), and the buffer worked out from it.
        private readonly float[] _late = new float[516];
        private readonly float[] _lateSorted = new float[516];
        private const float MaxCoverMs = 40; // live at all costs: past this, a rare gap rather than more delay
        private int _stallRun; // packets in a row that came after a stall
        private int _lateAt, _lateCount, _sinceTarget;
        private float _coverMs;
        private bool _measured;

        // How the connection is coping, for the quality steps (since the last TakeStats).
        private int _statPackets, _statLost, _statLate;

        /// <summary>
        /// Catch up by speeding up the sound (a setting, off by default): fast-forward
        /// in pieces at the same pitch instead of skipping.
        /// </summary>
        public volatile bool SpeedUp;

        /// <summary>Test only: play silence, with all the timing of the real thing.</summary>
        public bool Mute;

        /// <summary>The playback device, for the log.</summary>
        public string DeviceInfo = "none yet";

        public Player(string? deviceId, Action<string> status)
        {
            _deviceId = deviceId;
            _status = status;
            if (deviceId == NoDevice) _stop = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "TailRemote playback", Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(2000);
        }

        /// <summary>
        /// Forgets the last connection, ready for the next. The output stream stays
        /// open, so connecting never opens or closes a device. Call only while no
        /// connection is feeding it.
        /// </summary>
        public void Reset()
        {
            lock (_gate)
            {
                _read = 0; _count = 0; _playing = false; _fadeOut = 0; _gain = 0; _ff = 0; _xfLeft = 0;
            }
            _haveSeq = false;
            _lateCount = 0; _lateAt = 0; _coverMs = 0; _measured = false; // the old connection's lateness must not size the new buffer
        }

        /// <summary>Packets, lost and late since the last call: how the connection is coping.</summary>
        public (int Packets, int Lost, int Late) TakeStats() =>
            (Interlocked.Exchange(ref _statPackets, 0), Interlocked.Exchange(ref _statLost, 0), Interlocked.Exchange(ref _statLate, 0));

        /// <summary>Audio delay right now: what is buffered plus the device, in ms. -1 when idle.</summary>
        public int DelayMs { get { lock (_gate) return DelayUnlocked(); } }
        private int DelayUnlocked() => _playing ? (int)Math.Round(_avgMs + _periodMs) : -1;

        // ---- Diagnostics for the log and the audio test ----
        private int _diagDry, _diagSkips, _diagLost, _diagLate, _diagSpurts;
        private double _diagSkippedMs, _diagFastMs, _diagLevelMin = double.MaxValue, _diagLevelMax;
        private float _diagJump, _prevOut; // biggest sample-to-sample step: a pop shows up as a big one

        /// <summary>What happened since the last call.</summary>
        public string Diagnose()
        {
            lock (_gate)
            {
                string s = $"lost {_diagLost}, late {_diagLate}, restarts {_diagSpurts}, dry {_diagDry}, skips {_diagSkips} ({_diagSkippedMs:0} ms)" +
                    $", fast-forwarded {_diagFastMs:0} ms, buffer {(_diagLevelMin == double.MaxValue ? 0 : _diagLevelMin):0.0}-{_diagLevelMax:0.0} ms" +
                    $", target {_targetMs + _periodMs:0.0} ms, device period {_periodMs:0.0} ms, delay {DelayUnlocked()} ms, biggest step {_diagJump:0.0000000}";
                _diagDry = 0; _diagSkips = 0; _diagLost = 0; _diagLate = 0; _diagSpurts = 0; _diagJump = 0;
                _diagSkippedMs = 0; _diagFastMs = 0; _diagLevelMin = double.MaxValue; _diagLevelMax = 0;
                return s;
            }
        }

        /// <summary>
        /// One packet: always 5.8 ms of sound, at inRate (lower while the connection
        /// struggles), straight to the device's rate in one step. Empty pcm means a
        /// silent packet. Called from the network thread only.
        /// </summary>
        public void Push(uint seq, ReadOnlySpan<byte> pcm, int frames = Protocol.PacketFrames, int inRate = Protocol.AudioRate)
        {
            int rate = _deviceRate;
            if (rate == 0) return;
            if (pcm.IsEmpty && _rsIn != 0) inRate = _rsIn; // silence at the current rate: no needless rate change

            Resampler? finishing = null;
            float holdL = 0, holdR = 0;
            if (_rs == null || _rsRate != rate || _rsIn != inRate)
            {
                bool rateChanged = _rs != null && _rsRate == rate;
                if (rateChanged)
                {
                    // The host changed sample rate. Glide: play out what the old rate still
                    // holds, and start the new one from its last value. No gap, dip or pop.
                    finishing = _rs;
                    if (_lastFrames > 0) { holdL = _last[(_lastFrames - 1) * 2]; holdR = _last[(_lastFrames - 1) * 2 + 1]; }
                }
                _rs = new Resampler(inRate, rate);
                if (rateChanged) _rs.Prime(holdL, holdR);
                else { _haveSeq = false; _fadeIn = true; }
                _rsRate = rate;
                _rsIn = inRate;
                _lastFrames = 0; // the old rate's last packet cannot stand in for a lost one
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

            // How late this packet is, against the earliest any packet has been. The
            // reference creeps later at 200 ppm so a slow host clock is not mistaken
            // for growing lateness.
            double raw = now - (int)(seq - _spurtSeq) * PacketMs;
            _ref = Math.Min(raw, _ref + PacketMs * 0.0002);
            float lateMs = (float)Math.Min(raw - _ref, 400);
            if (_lateCount >= 86 && lateMs > _coverMs + 40)
            {
                // After a stall: never a reason to build delay. The backlog is skipped (or
                // fast-forwarded) when it lands, and the buffer stays as it was. If every
                // packet stays this late for a second, the network has simply got slower:
                // measure from here instead.
                if (++_stallRun >= 172) { _ref = raw; _stallRun = 0; }
            }
            else
            {
                _stallRun = 0;
                _late[_lateAt++ % _late.Length] = lateMs;
                if (_lateCount < _late.Length) _lateCount++;
            }
            UpdateTarget();

            Interlocked.Increment(ref _statPackets);
            if (diff < 0) { Interlocked.Increment(ref _statLate); _diagLate++; return; } // late: its moment has passed
            if (diff > 0) { Interlocked.Add(ref _statLost, diff); _diagLost += diff; }
            _haveSeq = true;
            _expect = seq + 1;

            _scratchLen = 0;
            finishing?.Flush(holdL, holdR, _collect ??= Collect);
            for (int lost = 0; lost < diff; lost++)
            {
                // Lost: the last packet played backwards, fading out, then silence;
                // always one packet's worth of time at the current rate. Backwards
                // starts exactly where the sound stopped, so there is no click (playing
                // it forwards again jumped back to its start).
                int n = lost == 0 && _lastFrames > 0 ? _lastFrames : PacketAt(inRate);
                for (int i = 0; i < n * 2; i += 2)
                {
                    float g = lost == 0 && _lastFrames > 0 ? 1f - (float)i / (n * 2) : 0f;
                    int from = (n - 1) * 2 - i;
                    _in[i] = _last[from] * g;
                    _in[i + 1] = _last[from + 1] * g;
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

        /// <summary>Frames in one 5.8 ms packet at this rate, carrying the fraction so time never drifts.</summary>
        private int PacketAt(int inRate)
        {
            if (inRate == Protocol.AudioRate) return Protocol.PacketFrames;
            double exact = Protocol.PacketFrames * (double)inRate / Protocol.AudioRate + _silenceCarry;
            int n = (int)exact;
            _silenceCarry = exact - n;
            return n;
        }

        /// <summary>
        /// The buffer covers 98% of the last 10 seconds' lateness, worked out every
        /// half second: up at once, down 20% of the way each time. A rare spike (a
        /// Wi-Fi scan) costs one short gap rather than raising the delay.
        /// </summary>
        private void UpdateTarget()
        {
            if (_lateCount < 86)
            {
                // The first half second: no 98% yet, so cover the worst lateness so far
                // at once. Starting too small ran dry and rebuilt just after connecting.
                float l = _late[(_lateAt - 1) % _late.Length];
                if (l > _coverMs) _coverMs = Math.Min(l, 100);
            }
            else if (++_sinceTarget >= 86)
            {
                _sinceTarget = 0;
                Array.Copy(_late, _lateSorted, _lateCount);
                Array.Sort(_lateSorted, 0, _lateCount);
                // How uneven, not how late: from the earliest packet in the window. A delay
                // that every packet shares (Clumsy's lag, a slower route) is not unevenness;
                // measured from the start it held the buffer at 300 ms for good.
                float spread = Math.Min(_lateSorted[(int)(_lateCount * 0.98) - 1] - _lateSorted[0], MaxCoverMs);
                // Up at once, half way back down each time; the first real measurement simply
                // replaces the start-up guess, which the connect burst made too big.
                _coverMs = spread > _coverMs || !_measured ? spread : _coverMs + (spread - _coverMs) * 0.5f;
                _measured = true;
            }
            _targetMs = (float)(PacketMs + MarginMs + _coverMs);
        }

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
                _read = 0; _count = 0; _playing = false; _ff = 0; _xfLeft = 0;
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

        private float Mono(int i) => _ring[i % _ring.Length] + _ring[(i + 1) % _ring.Length];

        /// <summary>
        /// Starts a jump about 'want' floats ahead: it lands within 'search' frames of
        /// that, wherever the next 'xfLen' frames best match what is playing now
        /// (normalised cross-correlation), and crossfades over them. Call under _gate.
        /// </summary>
        private bool StartJump(int want, int xfLen, int search)
        {
            if (_count < want + (xfLen + search) * 2 + 2) return false;
            int best = want;
            double bestScore = double.MinValue;
            for (int d = -search; d <= search; d++)
            {
                int b = want + d * 2;
                if (b < 2) continue;
                double corr = 0, energy = 1e-12;
                for (int k = 0; k < xfLen; k += 2) // every other frame is plenty
                {
                    float x = Mono(_read + k * 2), y = Mono(_read + b + k * 2);
                    corr += x * y;
                    energy += y * y;
                }
                double score = corr / Math.Sqrt(energy);
                if (score > bestScore) { bestScore = score; best = b; }
            }
            _xfJump = best;
            _xfFrom = (_read + best) % _ring.Length;
            _xfLen = _xfLeft = xfLen;
            return true;
        }

        private unsafe void Fill(IntPtr data, int frames, Wasapi.Format fmt)
        {
            int ch = fmt.Channels;
            int cap = _ring.Length;
            double floatsPerMs = fmt.Rate * 2 / 1000.0;
            int fadeLen = Math.Max(1, fmt.Rate / 500); // 2 ms
            int grainLen = fmt.Rate * 3 / 100;          // 30 ms fast-forward pieces
            int xfLen = fmt.Rate / 100;                 // 10 ms crossfade at every jump
            int search = fmt.Rate / 200;                // the jump lands within 5 ms of its aim
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
                    double over = levelMs - target;
                    if (SpeedUp && over < 300)
                    {
                        // Fast-forward: on at 4 ms behind on average (or 10 at once); 1.5x,
                        // 2x from 20 ms behind, 4x from 60; off once back within 3 ms.
                        // Judged on the smoothed level as well: the level dips by a device period
                        // at every refill, and those dips kept switching fast-forward off.
                        double behind = Math.Max(over, _avgMs - target);
                        if (_ff == 0 && (_avgMs > target + 4 || over > 10)) { _ff = 1.5; _grainPos = grainLen; }
                        if (_ff > 0) _ff = _avgMs - target <= 2 || over <= -5 ? 0 : behind > 60 ? 4 : behind > 20 ? 2 : 1.5;
                    }
                    else if (_xfLeft == 0 && (_avgMs > target + 4 || over > 10))
                    {
                        // Skip straight back to the target, crossfaded.
                        _ff = 0;
                        int drop = Math.Min((int)(over * floatsPerMs), _count - (xfLen + search) * 2 - 2) & ~1;
                        if (drop >= fadeLen * 2 && StartJump(drop, xfLen, search))
                        {
                            _avgMs = target;
                            _diagSkips++;
                            _diagSkippedMs += _xfJump / floatsPerMs;
                        }
                    }
                }

                int keep = (int)(target * floatsPerMs) & ~1;
                for (int f = 0; f < frames; f++)
                {
                    float l = 0, r = 0;
                    if (_playing && _count < 2)
                    {
                        // Ran dry: refill to the target. Glide out from the last sample
                        // instead of dropping to zero, which is what clicked.
                        _playing = false;
                        _fadeOut = fadeLen;
                        _gain = 0;
                        _ff = 0;
                        _xfLeft = 0;
                        _diagDry++;
                    }
                    if (_playing)
                    {
                        if (_xfLeft == 0 && _ff > 0 && ++_grainPos >= grainLen)
                        {
                            // Fast-forward: after each 30 ms piece, jump over (speed - 1) pieces,
                            // never below the target.
                            _grainPos = 0;
                            int want = Math.Min((int)((_ff - 1) * grainLen) * 2, _count - keep - (xfLen + search) * 2) & ~1;
                            if (want >= fadeLen * 2 && StartJump(want, xfLen, search))
                            {
                                _diagFastMs += _xfJump / floatsPerMs;
                                _avgMs -= _xfJump / floatsPerMs; // the average knows at once what was cut
                            }
                        }
                        l = _ring[_read]; r = _ring[(_read + 1) % cap];
                        if (_xfLeft > 0)
                        {
                            // Crossfading into the sound further ahead, which was lined up to match.
                            float w = 1f - (float)_xfLeft / _xfLen;
                            l += (_ring[_xfFrom] - l) * w;
                            r += (_ring[(_xfFrom + 1) % cap] - r) * w;
                            _xfFrom = (_xfFrom + 2) % cap;
                        }
                        _read = (_read + 2) % cap;
                        _count -= 2;
                        if (_xfLeft > 0 && --_xfLeft == 0)
                        {
                            _read = _xfFrom;
                            _count -= _xfJump;
                        }
                        if (_gain < 1f) { _gain = Math.Min(1f, _gain + 1f / fadeLen); l *= _gain; r *= _gain; }
                        _lastL = l; _lastR = r;
                    }
                    else if (_fadeOut > 0)
                    {
                        float g = (float)_fadeOut / fadeLen;
                        l = _lastL * g; r = _lastR * g;
                        _fadeOut--;
                    }
                    if (_playing || _fadeOut > 0)
                    {
                        float step = Math.Abs(l - _prevOut);
                        if (step > _diagJump) _diagJump = step;
                    }
                    _prevOut = l;
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
