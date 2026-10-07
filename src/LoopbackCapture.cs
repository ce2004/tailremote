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
        private uint _seq;

        public LoopbackCapture(Action<uint, short[]?> onPacket, Action<string> status)
        {
            _onPacket = onPacket;
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
            enumerator.GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eConsole, out var dev);
            dev.GetId(out string devId);
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
            try
            {
                while (!_stop)
                {
                    ev.WaitOne(5); // loopback events are not guaranteed on every build; poll too
                    while (true)
                    {
                        int nhr = cap.GetNextPacketSize(out uint next);
                        if (nhr < 0) throw Marshal.GetExceptionForHR(nhr)!; // device reset: reopen
                        if (next == 0) break;
                        // Nothing played for a while: move the packet count on by the
                        // gap, so the player sees a fresh start, not a very late packet.
                        long gap = Environment.TickCount64 - lastDataAt;
                        if (gap > 20 && _fill == 0) _seq += (uint)(gap * Protocol.AudioRate / 1000 / Protocol.PacketFrames);
                        int hr = cap.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
                        if (stereo.Length < frames * 2) stereo = new float[frames * 4];
                        ToStereo(data, (int)frames, fmt, (flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0, stereo);
                        cap.ReleaseBuffer(frames);
                        rs.Process(stereo.AsSpan(0, (int)frames * 2), Emit);
                        lastDataAt = Environment.TickCount64;
                    }

                    long now = Environment.TickCount64;
                    // The tail of a sound: send the part-filled packet rather than hold it.
                    if (_fill > 0 && now - lastDataAt > 15)
                    {
                        while (_fill > 0) Emit(0, 0);
                    }
                    if (now - lastDeviceCheck > 1000)
                    {
                        lastDeviceCheck = now;
                        enumerator.GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eConsole, out var cur);
                        cur.GetId(out string curId);
                        if (curId != devId) return; // default output changed: reopen on the new one
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
