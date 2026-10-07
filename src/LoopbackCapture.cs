using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Records what the host plays (WASAPI loopback on the default output),
    /// converts it to 16-bit 44.1 kHz stereo and hands out 256-frame packets.
    /// All-silent packets become 5-byte markers, then stop, so a quiet PC costs nothing.
    /// </summary>
    internal sealed class LoopbackCapture : IDisposable
    {
        private readonly Action<uint, short[]?> _onPacket;
        private readonly Action<string> _status;
        private volatile bool _stop;
        private readonly Thread _thread;

        private readonly short[] _packet = new short[Protocol.PacketFrames * 2];
        private int _fill;
        private readonly Action<float, float> _emit; // made once: a new one per packet was garbage every 5.8 ms
        private uint _seq;

        private readonly string? _deviceId;

        /// <summary>
        /// The largest chunk (ms) the device hands over at once, measured every 2
        /// seconds and raised when it changes by 2 ms or more. A normal output
        /// gives 10 ms; Bluetooth, USB and some virtual devices give 20 to 40,
        /// which arrive at the other PC as a burst its buffer has to cover.
        /// </summary>
        public event Action<int>? Burst;
        private int _burstWindow, _burstReported;
        private long _burstCheckedAt;

        /// <summary>deviceId: the output to record, or null for Windows' default (followed when it changes).</summary>
        public LoopbackCapture(Action<uint, short[]?> onPacket, Action<string> status, string? deviceId = null)
        {
            _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
            _onPacket = onPacket;
            _emit = Emit;
            _status = status;
            _thread = new Thread(Run) { IsBackground = true, Name = "TailRemote capture", Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(2000);
        }

        private void Run()
        {
            Native.ProAudioThread();
            string? lastError = null;
            while (!_stop)
            {
                try
                {
                    CaptureUntilDeviceChanges();
                    lastError = null;
                }
                catch (Exception e)
                {
                    string msg = "Host audio is not available: " + e.Message +
                        " A cloud PC usually has no sound card; install a virtual audio device such as VB-Cable and make it the default output.";
                    if (msg != lastError) _status(msg);
                    lastError = msg;
                    for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
                }
            }
        }

        private void CaptureUntilDeviceChanges()
        {
            var enumerator = Wasapi.Enumerator();
            // The chosen output, or Windows' default when none is chosen or it has gone.
            var dev = Wasapi.OutputDevice(_deviceId);
            dev.GetId(out string devId);
            bool followDefault = _deviceId == null || devId != _deviceId;
            var client = Wasapi.Activate(dev);
            client.GetMixFormat(out IntPtr fmtPtr);
            var fmt = Wasapi.ReadFormat(fmtPtr);
            client.Initialize(0, Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK | Wasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                200_000, 0, fmtPtr, IntPtr.Zero);
            Marshal.FreeCoTaskMem(fmtPtr);

            using var ev = new AutoResetEvent(false);
            client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
            var iid = Wasapi.IID_IAudioCaptureClient;
            client.GetService(ref iid, out object o);
            var cap = (Wasapi.IAudioCaptureClient)o;

            var rs = new Resampler(fmt.Rate, Protocol.AudioRate);
            float[] stereo = new float[4096];
            client.Start();
            long lastDataAt = Environment.TickCount64, lastDeviceCheck = lastDataAt;
            long expectedQpc = -1; // when the next audio should have been played, in 100 ns units
            float[] silence = new float[2 * 4096];
            try
            {
                while (!_stop)
                {
                    ev.WaitOne(5); // loopback events are not guaranteed on every build; poll too
                    int drained = 0;
                    while (true)
                    {
                        int nhr = cap.GetNextPacketSize(out uint next);
                        if (nhr < 0) throw Marshal.GetExceptionForHR(nhr)!; // device reset: reopen
                        if (next == 0) break;
                        int hr = cap.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpc);
                        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;

                        // Gaps are measured by when Windows says the audio was played,
                        // never by when it reached us. On a busy PC audio often arrives
                        // late in a burst; treating that as silence is what crackled.
                        if (expectedQpc >= 0)
                        {
                            long gap100ns = (long)qpc - expectedQpc;
                            double gapFrames = gap100ns * (double)Protocol.AudioRate / 10_000_000;
                            if (gapFrames > Protocol.PacketFrames * 8)
                            {
                                // A real pause: finish the part-filled packet and move the
                                // count on, so the player starts fresh.
                                while (_fill > 0) Emit(0, 0);
                                _seq += (uint)(gapFrames / Protocol.PacketFrames);
                            }
                            else if (gap100ns > 20_000) // over 2 ms of real silence: keep the timing exact
                            {
                                int n = Math.Min((int)(gap100ns * fmt.Rate / 10_000_000), silence.Length / 2);
                                rs.Process(silence.AsSpan(0, n * 2), _emit);
                            }
                        }
                        expectedQpc = (long)qpc + (long)frames * 10_000_000 / fmt.Rate;
                        if (stereo.Length < frames * 2) stereo = new float[frames * 4];
                        ToStereo(data, (int)frames, fmt, (flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0, stereo);
                        cap.ReleaseBuffer(frames);
                        rs.Process(stereo.AsSpan(0, (int)frames * 2), _emit);
                        lastDataAt = Environment.TickCount64;
                        drained += (int)frames;
                    }

                    long now = Environment.TickCount64;
                    if (drained > 0) _burstWindow = Math.Max(_burstWindow, drained * 1000 / fmt.Rate);
                    if (now - _burstCheckedAt >= 2000)
                    {
                        _burstCheckedAt = now;
                        if (_burstWindow > 0 && Math.Abs(_burstWindow - _burstReported) >= 2)
                        {
                            _burstReported = _burstWindow;
                            try { Burst?.Invoke(_burstWindow); } catch { }
                        }
                        _burstWindow = 0;
                    }
                    // The tail of a sound: after a real stop, send the part-filled packet
                    // rather than hold it. Not sooner: late audio is not the end of a sound.
                    if (_fill > 0 && now - lastDataAt > 100)
                    {
                        while (_fill > 0) Emit(0, 0);
                    }
                    if (now - lastDeviceCheck > 1000)
                    {
                        lastDeviceCheck = now;
                        // Reopen when the default output changes (if following it), or when
                        // the chosen output that was missing comes back.
                        var want = Wasapi.OutputDevice(_deviceId);
                        want.GetId(out string curId);
                        if (curId != devId && (followDefault || curId == _deviceId)) return;
                    }
                }
            }
            finally
            {
                try { client.Stop(); } catch { }
            }
        }

        private void Emit(float l, float r)
        {
            _packet[_fill++] = ToShort(l);
            _packet[_fill++] = ToShort(r);
            if (_fill < _packet.Length) return;
            _fill = 0;
            uint seq = _seq++;
            foreach (short s in _packet)
            {
                if (s != 0) { _silentRun = 0; _onPacket(seq, _packet); return; }
            }
            // Silence: a 5-byte marker for the first second, so the player can
            // tell silence from a lost packet; after that, nothing at all.
            if (_silentRun++ < 172) _onPacket(seq, null);
        }

        private int _silentRun;

        private static short ToShort(float f)
        {
            float v = f * 32767f;
            if (v > 32767f) return 32767;
            if (v < -32768f) return -32768;
            return (short)MathF.Round(v);
        }

        private static unsafe void ToStereo(IntPtr data, int frames, Wasapi.Format fmt, bool silent, float[] dst)
        {
            if (silent) { Array.Clear(dst, 0, frames * 2); return; }
            int ch = fmt.Channels;
            for (int i = 0; i < frames; i++)
            {
                float l, r;
                if (fmt.IsFloat && fmt.Bits == 32)
                {
                    float* p = (float*)data + i * ch;
                    l = p[0]; r = ch > 1 ? p[1] : l;
                }
                else if (fmt.Bits == 16)
                {
                    short* p = (short*)data + i * ch;
                    l = p[0] / 32768f; r = ch > 1 ? p[1] / 32768f : l;
                }
                else if (fmt.Bits == 32)
                {
                    int* p = (int*)data + i * ch;
                    l = p[0] / 2147483648f; r = ch > 1 ? p[1] / 2147483648f : l;
                }
                else throw new NotSupportedException("Unsupported audio format: " + fmt.Bits + " bit.");
                dst[i * 2] = l;
                dst[i * 2 + 1] = r;
            }
        }
    }
}
