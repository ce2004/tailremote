using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Plays the host's audio with as little delay as the network allows.
    ///
    /// Packets are resampled to the device's rate as they arrive and kept in a
    /// small jitter buffer. Playback starts once the buffer holds the chosen
    /// number of milliseconds; if the buffer ever grows well past that (a burst
    /// after a network stall, or clock drift), the excess is dropped so the
    /// delay never creeps up.
    /// </summary>
    internal sealed class Player : IDisposable
    {
        private readonly string? _deviceId;
        private readonly int _bufferMs;
        private readonly Action<string> _status;
        private volatile bool _stop;
        private readonly Thread _thread;

        private readonly object _gate = new();
        private readonly float[] _ring = new float[48000 * 4 * 2]; // 4 s of stereo at up to 96 kHz/2
        private int _read, _count; // in floats
        private bool _playing;
        private volatile int _deviceRate;
        private int _targetFloats, _maxFloats;

        // Network-thread state.
        private Resampler? _rs;
        private int _rsRate;
        private uint _expect;
        private bool _haveSeq;
        private float[] _scratch = new float[8192];
        private int _scratchLen;
        private readonly float[] _in = new float[Protocol.PacketFrames * 2];

        /// <summary>Device id that plays nothing (the self-test uses it).</summary>
        public const string NoDevice = "-";

        public Player(string? deviceId, int bufferMs, Action<string> status)
        {
            _deviceId = deviceId;
            _bufferMs = Math.Clamp(bufferMs, 5, 1000);
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

        /// <summary>Called from the network thread with one audio packet.</summary>
        public void Push(uint seq, ReadOnlySpan<byte> pcm)
        {
            int rate = _deviceRate;
            if (rate == 0) return;
            if (_rs == null || _rsRate != rate) { _rs = new Resampler(Protocol.AudioRate, rate); _rsRate = rate; _haveSeq = false; }

            _scratchLen = 0;
            if (_haveSeq)
            {
                int diff = (int)(seq - _expect);
                if (diff < 0) return; // late; its moment has passed
                if (diff > 0 && diff <= 8)
                {
                    // Lost packets, or silence the host did not send: keep the timing.
                    Array.Clear(_in);
                    for (int i = 0; i < diff; i++) _rs.Process(_in, Collect);
                }
            }
            _haveSeq = true;
            _expect = seq + 1;

            for (int i = 0; i < _in.Length; i++)
                _in[i] = BitConverter.ToInt16(pcm.Slice(i * 2, 2)) / 32768f;
            _rs.Process(_in, Collect);

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
            try
            {
                // The smallest engine period the driver offers (often 2.7 ms or less).
                client.GetSharedModeEnginePeriod(fmtPtr, out _, out _, out uint minFrames, out _);
                client.InitializeSharedAudioStream(Wasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, minFrames, fmtPtr, IntPtr.Zero);
            }
            catch
            {
                client = Wasapi.Activate(dev);
                client.Initialize(0, Wasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, fmtPtr, IntPtr.Zero);
            }
            Marshal.FreeCoTaskMem(fmtPtr);

            using var ev = new AutoResetEvent(false);
            client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
            client.GetBufferSize(out uint bufFrames);
            var iid = Wasapi.IID_IAudioRenderClient;
            client.GetService(ref iid, out object o);
            var render = (Wasapi.IAudioRenderClient)o;

            lock (_gate)
            {
                _targetFloats = (int)((long)fmt.Rate * _bufferMs / 1000) * 2;
                // Allow two packets' worth of wobble above the target before trimming.
                _maxFloats = _targetFloats + (int)((long)fmt.Rate * 12 / 1000) * 2;
                _read = 0; _count = 0; _playing = false;
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
            lock (_gate)
            {
                if (!_playing && _count >= _targetFloats) _playing = true;
                if (_playing && _count > _maxFloats)
                {
                    int drop = _count - _targetFloats;
                    _read = (_read + drop) % cap;
                    _count -= drop;
                }
                for (int f = 0; f < frames; f++)
                {
                    float l = 0, r = 0;
                    if (_playing)
                    {
                        if (_count >= 2)
                        {
                            l = _ring[_read]; r = _ring[(_read + 1) % cap];
                            _read = (_read + 2) % cap;
                            _count -= 2;
                        }
                        else _playing = false; // ran dry: wait for the buffer to refill
                    }
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
