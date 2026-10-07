using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TailRemote
{
    /// <summary>
    /// The wire format.
    ///
    /// TCP (no Nagle) carries the handshake and the keys: every key must arrive,
    /// in order. UDP carries the audio: a late audio packet is worthless, so it is
    /// never waited for or resent. Tailscale already encrypts both.
    ///
    /// Handshake: host sends "TRM1" + 16-byte nonce. Client answers "TRM1" +
    /// HMAC-SHA256(password, nonce). Host answers 1 + 8-byte session token, or 0.
    /// After that both sides send frames of [type][payload].
    /// </summary>
    internal static class Protocol
    {
        public const int DefaultPort = 47120;
        public static readonly byte[] Magic = "TRM1"u8.ToArray();

        // Client to host
        public const byte Key = 1;      // vk u16, scan u16, flags u8 (1 = up, 2 = extended)
        public const byte Ping = 2;     // stamp i64
        public const byte ReleaseAll = 3;
        // Host to client
        public const byte Pong = 0x81;  // stamp i64
        public const byte Message = 0x82; // u16 length, UTF-8 text

        // UDP
        public const byte UdpHello = 0xA0;  // token[8], client to host, every second
        public const byte UdpAudio = 0xA1;  // u32 sequence, 256 stereo int16 frames

        public const int AudioRate = 44100;
        public const int PacketFrames = 256; // 5.8 ms; 1029 bytes, under Tailscale's 1280 MTU
        public const int AudioPacketBytes = 5 + PacketFrames * 4;

        public static byte[] Proof(string password, byte[] nonce)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(password));
            return h.ComputeHash(nonce);
        }

        public static void ReadExactly(Stream s, Span<byte> buf)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = s.Read(buf[got..]);
                if (n <= 0) throw new EndOfStreamException("The connection closed.");
                got += n;
            }
        }

        public static void SendMessage(Stream s, object writeLock, string text)
        {
            byte[] utf = Encoding.UTF8.GetBytes(text);
            byte[] f = new byte[3 + utf.Length];
            f[0] = Message;
            BitConverter.TryWriteBytes(f.AsSpan(1), (ushort)utf.Length);
            utf.CopyTo(f, 3);
            lock (writeLock) s.Write(f);
        }

        /// <summary>True for Tailscale's 100.64.0.0/10 and fd7a:115c:a1e0::/48, and loopback.</summary>
        public static bool IsTailscale(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            byte[] b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
                return b[0] == 100 && (b[1] & 0xC0) == 64;
            return b.Length == 16 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
        }
    }
}
