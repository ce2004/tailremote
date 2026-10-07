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
    /// never waited for or resent.
    ///
    /// Handshake, where key = PBKDF2-SHA256(password, 200000 rounds), slow on
    /// purpose so a recorded handshake cannot be guessed quickly:
    ///   host:   "TRM5" + host nonce (16)
    ///   client: "TRM5" + client nonce (16) + HMAC(key, "C" + host nonce + client nonce)
    ///   host:   0 after a 2 s pause (wrong password), or
    ///           1 + role (1) + session token (8) + HMAC(key, "H" + client nonce + host nonce)
    /// The role is Control or Listen, decided by which of the host's two
    /// passwords the client proved. Then each side sends Features: the extras
    /// it supports. Unknown message types are ignored, so newer versions can
    /// add messages without breaking older ones.
    /// The client checks the host's proof too, so a PC that does not know the
    /// password never receives a single key. After that, everything is
    /// encrypted by SecureLink, so the connection is private without Tailscale.
    /// After that both sides send encrypted frames of [type][payload].
    /// </summary>
    internal static class Protocol
    {
        public const int DefaultPort = 47120;
        public static readonly byte[] Magic = "TRM5"u8.ToArray();
        /// <summary>A client's second connection, for files: "TRF5" + session token (8), padded to 52 bytes.</summary>
        public static readonly byte[] FileMagic = "TRF5"u8.ToArray();

        // Client to host
        public const byte Key = 1;      // vk u16, scan u16, flags u8 (1 = up, 2 = extended)
        public const byte Ping = 2;     // stamp i64
        public const byte ReleaseAll = 3;
        public const byte RestartPc = 4;     // controller asks the host PC to restart
        // Either way
        public const byte Features = 0x40;   // u32 flags
        public const byte Clipboard = 0x41;  // UTF-8 text
        // Host to client
        public const byte Pong = 0x81;  // stamp i64
        public const byte Message = 0x82; // UTF-8 text (the frame gives the length)

        // UDP
        public const byte UdpHello = 0xA0;  // token[8], client to host, every second
        public const byte UdpAudio = 0xA1;  // u32 sequence, 256 stereo int16 frames
        public const byte UdpSilence = 0xA2; // u32 sequence: this packet was silent
        public const byte UdpPacked = 0xA3;  // u32 sequence, losslessly packed audio (Lossless.cs)

        // Roles
        public const byte RoleControl = 1, RoleListen = 2;

        // Feature flags
        public const uint FeatureClipboard = 1;
        public const uint FeatureFiles = 2;
        public const uint FeatureLossless = 4;
        public const uint FeatureRestart = 8;

        /// <summary>What this version supports, sent to the other side after login.</summary>
        public const uint OurFeatures = FeatureClipboard | FeatureFiles | FeatureLossless | FeatureRestart;

        public const int MaxClipboardChars = 1_000_000;

        public static byte[] FeaturesMessage()
        {
            byte[] m = new byte[5];
            m[0] = Features;
            BitConverter.TryWriteBytes(m.AsSpan(1), OurFeatures);
            return m;
        }

        public static byte[] TextMessage(byte type, string text)
        {
            byte[] utf = Encoding.UTF8.GetBytes(text);
            byte[] m = new byte[1 + utf.Length];
            m[0] = type;
            utf.CopyTo(m, 1);
            return m;
        }

        public const int AudioRate = 44100;
        public const int PacketFrames = 256; // 5.8 ms; 1029 bytes, under Tailscale's 1280 MTU
        public const int AudioPacketBytes = 5 + PacketFrames * 4 + SecureLink.TagSize; // 1045, under Tailscale's 1280
        public const int SilencePacketBytes = 5 + SecureLink.TagSize;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Keys = new();

        /// <summary>Slow on purpose (200,000 rounds), so it is worked out once per password and kept.</summary>
        public static byte[] DeriveKey(string password) => Keys.GetOrAdd(password, p =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(p), "TailRemote-v3"u8.ToArray(), 200_000, HashAlgorithmName.SHA256, 32));

        public static byte[] Proof(byte[] key, char side, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            byte[] msg = new byte[1 + first.Length + second.Length];
            msg[0] = (byte)side;
            first.CopyTo(msg.AsSpan(1));
            second.CopyTo(msg.AsSpan(1 + first.Length));
            return HMACSHA256.HashData(key, msg);
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

        public static void SendMessage(SecureLink link, Stream s, string text)
        {
            byte[] utf = Encoding.UTF8.GetBytes(text);
            byte[] f = new byte[1 + Math.Min(utf.Length, 4000)];
            f[0] = Message;
            utf.AsSpan(0, f.Length - 1).CopyTo(f.AsSpan(1));
            link.Send(s, f);
        }

        /// <summary>
        /// Why a port cannot be used, or null if it is fine. Refuses ports that
        /// other remote and network programs depend on, even when they happen to
        /// be free on this PC, so TailRemote never takes over someone else's.
        /// Whether the port is free right now is checked when hosting starts.
        /// </summary>
        public static string? PortProblem(string text, out int port)
        {
            if (!int.TryParse(text, out port) || port < 1 || port > 65535)
                return "The port must be a number from 1024 to 65535. The usual one is " + DefaultPort + ".";
            if (port < 1024)
                return "Port " + port + " is reserved for Windows services. Choose one from 1024 to 65535, such as " + DefaultPort + ".";
            string? owner = port switch
            {
                1900 => "Windows device discovery",
                3389 => "Remote Desktop",
                5353 => "Windows network name lookups",
                5355 => "Windows network name lookups",
                5900 => "VNC",
                6837 => "NVDA Remote",
                41641 => "Tailscale itself",
                _ => null,
            };
            return owner == null ? null : "Port " + port + " belongs to " + owner + ". Choose another, such as " + DefaultPort + ".";
        }

        /// <summary>Whether this PC has a Tailscale address, i.e. Tailscale is up and signed in.</summary>
        public static bool LocalTailscaleUp()
        {
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                        if (!IPAddress.IsLoopback(a.Address) && IsTailscale(a.Address)) return true;
                }
            }
            catch { }
            return false;
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
