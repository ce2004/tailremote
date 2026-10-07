using System;
using Concentus;
using Concentus.Enums;

namespace TailRemote
{
    /// <summary>
    /// The host's Opus encoders: one per bitrate step that someone is using,
    /// shared by everyone on that step, so a hundred listeners cost no more than
    /// one. Each step gathers 5 ms ticks until it has a whole packet (5 to 60 ms).
    ///
    /// Above 16 kbit/s it is tuned for music; 5 ms packets use Opus's low-delay
    /// mode (2.5 ms of look-ahead). From 16 kbit/s down it is tuned for speech and
    /// carries Opus's own loss recovery, which lets the player rebuild a lost
    /// packet from the next one.
    /// </summary>
    internal sealed class OpusBank
    {
        static OpusBank() => OpusCodecFactory.AttemptToUseNativeLibrary = false; // all managed: nothing extra to ship

        private sealed class Step
        {
            public IOpusEncoder? Encoder;
            public short[] Gathered = Array.Empty<short>();
            public int Ticks, Have;
            public uint First, Next;
            public bool InUse;
            public readonly byte[] Packet = new byte[Protocol.MaxOpusBytes];
            public int Length; // this tick's finished packet, or 0
            public uint Seq;
        }

        private readonly Step[] _steps = Array.ConvertAll(Protocol.OpusSteps, _ => new Step());

        public static IOpusEncoder CreateEncoder(int step)
        {
            var (kbps, ms) = Protocol.OpusSteps[step];
            var e = OpusCodecFactory.CreateEncoder(Protocol.AudioRate, 2,
                ms <= 5 ? OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY : OpusApplication.OPUS_APPLICATION_AUDIO);
            e.Bitrate = kbps * 1000;
            e.Complexity = 10;
            e.UseVBR = true;
            e.UseConstrainedVBR = true; // close to the asked-for rate in every packet, so a capped connection is not overrun
            e.SignalType = kbps > 16 ? OpusSignal.OPUS_SIGNAL_MUSIC : OpusSignal.OPUS_SIGNAL_VOICE;
            if (kbps <= 16)
            {
                e.UseInbandFEC = true;
                e.PacketLossPercent = 10;
            }
            return e;
        }

        /// <summary>
        /// One 5 ms tick (null: silence) for every step in 'wanted'. Afterwards Ready
        /// says which steps finished a packet. Called on the capture thread only.
        /// </summary>
        public void Feed(uint seq, short[]? pcm, bool[] wanted)
        {
            for (int i = 0; i < _steps.Length; i++)
            {
                var st = _steps[i];
                st.Length = 0;
                if (!wanted[i]) { st.InUse = false; continue; }
                if (st.Encoder == null)
                {
                    st.Encoder = CreateEncoder(i);
                    st.Ticks = Protocol.OpusSteps[i].Ms / 5;
                    st.Gathered = new short[st.Ticks * Protocol.TickFrames * 2];
                }
                if (!st.InUse) { st.Encoder.ResetState(); st.Have = 0; st.InUse = true; } // coming back into use: start clean
                if (st.Have > 0 && seq != st.Next) st.Have = 0; // a gap: drop the part-gathered packet
                if (st.Have == 0) st.First = seq;
                var into = st.Gathered.AsSpan(st.Have * Protocol.TickFrames * 2, Protocol.TickFrames * 2);
                if (pcm == null) into.Clear(); else pcm.AsSpan(0, into.Length).CopyTo(into);
                st.Next = seq + 1;
                if (++st.Have < st.Ticks) continue;
                st.Have = 0;
                st.Seq = st.First;
                st.Length = st.Encoder.Encode(st.Gathered, st.Ticks * Protocol.TickFrames, st.Packet, st.Packet.Length);
            }
        }

        /// <summary>The packet a step finished this tick: its first tick, its length in ticks, its bytes.</summary>
        public bool Ready(int step, out uint seq, out int ticks, out ReadOnlySpan<byte> packet)
        {
            var st = _steps[step];
            seq = st.Seq;
            ticks = st.Ticks;
            packet = st.Packet.AsSpan(0, Math.Max(0, st.Length));
            return st.Length > 0;
        }
    }
}
