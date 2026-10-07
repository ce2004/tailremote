using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TailRemote
{
    /// <summary>The controlling side: one connection to a host.</summary>
    internal sealed class Client : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly UdpClient _udp;
        private readonly byte[] _token;
        private readonly Player _player;
        private readonly SecureLink _link;
        private volatile bool _closed;
        private long _lastPong;
        private int _closing;

        public event Action<string>? Status;
        public event Action<string>? Disconnected;
        /// <summary>Clipboard text from the host. Raised on a network thread.</summary>
        public event Action<string>? ClipboardReceived;
        /// <summary>True when the host let us in with its listen-only password: audio only, no keys.</summary>
        public bool ListenOnly { get; private set; }
        /// <summary>A sentence about a file that arrived. Raised on a network thread.</summary>
        public event Action<string>? FileMessage;
        private FileChannel? _files;
        private readonly byte[] _unpacked = new byte[Protocol.PacketFrames * 4];
        private uint _peerFeatures;
        public int LastPingMs { get; private set; } = -1;
        /// <summary>Buffered audio plus the output device, in ms; -1 while nothing plays.</summary>
        public int AudioDelayMs => _player.DelayMs;

        private Client(TcpClient tcp, NetworkStream stream, UdpClient udp, byte[] token, Player player, SecureLink link)
        {
            _tcp = tcp; _stream = stream; _udp = udp; _token = token; _player = player; _link = link;
        }

        /// <summary>Connects and checks the password; throws with a readable message on failure.</summary>
        public static Client Connect(string address, int port, string password, string? deviceId, Action<string> status)
        {
            var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
            tcp.Client.DualMode = true;
            try
            {
                IPAddress[] addrs;
                try { addrs = Dns.GetHostAddresses(address); }
                catch (SocketException) { addrs = Array.Empty<IPAddress>(); }
                if (addrs.Length == 0)
                    throw new InvalidOperationException(Protocol.LocalTailscaleUp()
                        ? "Could not find " + address + ". Check the name or address."
                        : "Could not find " + address + ". If it is a Tailscale name, open Tailscale on this PC and sign in.");
                bool done;
                try { done = tcp.ConnectAsync(addrs, port).Wait(8000); }
                catch (AggregateException e) when (e.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    throw new InvalidOperationException(address + " is on, but TailRemote is not hosting there. Start hosting on that PC.");
                }
                if (!done)
                    throw new TimeoutException("No answer from " + address + ". It may be off or not hosting, or its port is not open: use Port editor on that PC.");
            }
            catch (AggregateException e) { tcp.Dispose(); throw new InvalidOperationException(e.InnerException?.Message ?? e.Message); }
            catch { tcp.Dispose(); throw; }

            var stream = tcp.GetStream();
            stream.ReadTimeout = 8000;
            try
            {
                byte[] hello = new byte[20];
                Protocol.ReadExactly(stream, hello);
                if (!hello.AsSpan(0, 4).SequenceEqual(Protocol.Magic))
                    throw new InvalidOperationException(hello.AsSpan(0, 3).SequenceEqual("TRM"u8)
                        ? "The other PC has a different TailRemote version. Update both to the latest."
                        : "That is not a TailRemote host.");
                byte[] key = Protocol.DeriveKey(password);
                byte[] hostNonce = hello[4..];
                byte[] myNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                byte[] answer = new byte[52];
                Protocol.Magic.CopyTo(answer, 0);
                myNonce.CopyTo(answer, 4);
                Protocol.Proof(key, 'C', hostNonce, myNonce).CopyTo(answer, 20);
                stream.Write(answer);

                byte[] result = new byte[1];
                Protocol.ReadExactly(stream, result);
                if (result[0] != 1) throw new InvalidOperationException("Wrong password.");
                byte[] role = new byte[1], token = new byte[8], hostProof = new byte[32];
                Protocol.ReadExactly(stream, role);
                Protocol.ReadExactly(stream, token);
                Protocol.ReadExactly(stream, hostProof);
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hostProof, Protocol.Proof(key, 'H', myNonce, hostNonce)))
                    throw new InvalidOperationException("That PC does not know the password, so nothing was sent to it. Check the address.");
                // The host pings back every 2 seconds; 10 silent seconds means it is gone.
                stream.ReadTimeout = 10_000;

                var hostEp = (IPEndPoint)tcp.Client.RemoteEndPoint!;
                var udp = new UdpClient(AddressFamily.InterNetworkV6);
                udp.Client.DualMode = true;
                udp.Client.ReceiveBufferSize = 1 << 20;
                Host.IgnoreUdpResets(udp.Client);
                udp.Connect(hostEp.Address, port);

                var player = new Player(deviceId, status);
                var c = new Client(tcp, stream, udp, token, player, new SecureLink(key, hostNonce, myNonce, isHost: false))
                {
                    ListenOnly = role[0] == Protocol.RoleListen,
                };
                c._link.Send(stream, Protocol.FeaturesMessage());
                if (!c.ListenOnly) c._files = OpenFiles(hostEp, token, key, hostNonce, myNonce, msg => c.FileMessage?.Invoke(msg));
                c.Status += status;
                c.Start();
                return c;
            }
            catch (System.IO.IOException) { tcp.Dispose(); throw new InvalidOperationException("The host closed the connection."); }
            catch { tcp.Dispose(); throw; }
        }

        /// <summary>The second connection, for files. Without it everything else still works.</summary>
        private static FileChannel? OpenFiles(IPEndPoint host, byte[] token, byte[] key, byte[] hostNonce, byte[] myNonce, Action<string> announce)
        {
            var tcp = new TcpClient(AddressFamily.InterNetworkV6);
            tcp.Client.DualMode = true;
            try
            {
                if (!tcp.ConnectAsync(host.Address, host.Port).Wait(5000)) throw new TimeoutException();
                var s = tcp.GetStream();
                s.ReadTimeout = 5000;
                byte[] hello = new byte[20];
                Protocol.ReadExactly(s, hello);
                byte[] answer = new byte[52];
                Protocol.FileMagic.CopyTo(answer, 0);
                token.CopyTo(answer, 4);
                s.Write(answer);
                return new FileChannel(tcp, new SecureLink(key, hostNonce, myNonce, isHost: false, "files "), announce);
            }
            catch { tcp.Dispose(); return null; }
        }

        /// <summary>Sends files to the host. Blocking; throws with a readable message.</summary>
        public string SendFiles(IReadOnlyList<string> paths, Action<string, int> report, CancellationToken ct) =>
            (_files ?? throw new InvalidOperationException(ListenOnly ? "Listeners cannot send files." : "File sending is not available on this connection. Reconnect and try again."))
                .Send(paths, report, ct);

        private void Start()
        {
            new Thread(TcpLoop) { IsBackground = true, Name = "TailRemote tcp" }.Start();
            new Thread(UdpLoop) { IsBackground = true, Name = "TailRemote udp", Priority = ThreadPriority.Highest }.Start();
            new Thread(Heartbeat) { IsBackground = true, Name = "TailRemote heartbeat" }.Start();
        }

        public void Dispose() => Close(null);

        private void Close(string? why)
        {
            if (Interlocked.Exchange(ref _closing, 1) == 1) return;
            _closed = true;
            try { _tcp.Dispose(); } catch { }
            try { _udp.Dispose(); } catch { }
            _files?.Dispose();
            _player.Dispose();
            if (why != null) Disconnected?.Invoke(why);
            // The link is left for the garbage collector: a hook-thread SendKey may still be using it.
        }

        /// <summary>Sends one key. Called from the keyboard hook; never blocks for long.</summary>
        public void SendKey(ushort vk, ushort scan, bool up, bool extended)
        {
            if (_closed) return;
            Span<byte> f = stackalloc byte[6];
            f[0] = Protocol.Key;
            BitConverter.TryWriteBytes(f[1..], vk);
            BitConverter.TryWriteBytes(f[3..], scan);
            f[5] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
            Write(f);
        }

        public void ReleaseAll() => Write(stackalloc byte[] { Protocol.ReleaseAll });

        public bool CanRestart => !ListenOnly && (_peerFeatures & Protocol.FeatureRestart) != 0;

        public bool CanSecureAttention => !ListenOnly && (_peerFeatures & Protocol.FeatureSecureAttention) != 0;

        /// <summary>Asks the host to send Ctrl+Alt+Del (only a host running as the service can).</summary>
        public void SendSecureAttention() => Write(stackalloc byte[] { Protocol.SecureAttention });

        /// <summary>Asks the host PC to restart.</summary>
        public void RestartHost() => Write(stackalloc byte[] { Protocol.RestartPc });

        /// <summary>Sends clipboard text to the host, if it shares the clipboard. Not for listeners.</summary>
        public void SendClipboard(string text)
        {
            if (ListenOnly || (_peerFeatures & Protocol.FeatureClipboard) == 0) return;
            Write(Protocol.TextMessage(Protocol.Clipboard, text));
        }

        private void Write(ReadOnlySpan<byte> f)
        {
            try { _link.Send(_stream, f); }
            catch { }
        }

        private void TcpLoop()
        {
            try
            {
                while (!_closed)
                {
                    byte[] m = _link.Receive(_stream);
                    if (m.Length == 9 && m[0] == Protocol.Pong)
                    {
                        long sent = BitConverter.ToInt64(m, 1);
                        LastPingMs = (int)((Stopwatch.GetTimestamp() - sent) * 1000 / Stopwatch.Frequency);
                        _lastPong = Environment.TickCount64;
                    }
                    else if (m.Length >= 1 && m[0] == Protocol.Message)
                    {
                        Status?.Invoke("Host: " + System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1));
                    }
                    else if (m.Length >= 5 && m[0] == Protocol.Features)
                        _peerFeatures = BitConverter.ToUInt32(m, 1);
                    else if (m.Length >= 1 && m[0] == Protocol.Clipboard && !ListenOnly)
                        ClipboardReceived?.Invoke(System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1));
                    // Anything else is from a newer version: ignore it.
                }
            }
            catch (Exception e)
            {
                if (!_closed) Close("Disconnected: " + (e is System.IO.EndOfStreamException ? "the host closed the connection." : e.Message));
            }
        }

        private void UdpLoop()
        {
            var any = new IPEndPoint(IPAddress.IPv6Any, 0);
            while (!_closed)
            {
                byte[] d;
                try { d = _udp.Receive(ref any); }
                catch { if (_closed) return; continue; }
                // Forged or damaged packets fail to open and are dropped.
                if (d.Length == Protocol.AudioPacketBytes && d[0] == Protocol.UdpAudio && _link.OpenAudio(d, d.Length))
                    _player.Push(BitConverter.ToUInt32(d, 1), d.AsSpan(5, Protocol.PacketFrames * 4));
                else if (d.Length == Protocol.SilencePacketBytes && d[0] == Protocol.UdpSilence && _link.OpenAudio(d, d.Length))
                    _player.Push(BitConverter.ToUInt32(d, 1), ReadOnlySpan<byte>.Empty);
                else if (d[0] == Protocol.UdpPacked && d.Length > Protocol.SilencePacketBytes && d.Length < Protocol.AudioPacketBytes
                         && _link.OpenAudio(d, d.Length) && Lossless.Decode(d.AsSpan(5, d.Length - Protocol.SilencePacketBytes), _unpacked))
                    _player.Push(BitConverter.ToUInt32(d, 1), _unpacked);
            }
        }

        private void Heartbeat()
        {
            byte[] hello = new byte[9];
            hello[0] = Protocol.UdpHello;
            _token.CopyTo(hello, 1);
            byte[] ping = new byte[9];
            int tick = 0;
            _lastPong = Environment.TickCount64;
            while (!_closed)
            {
                if (Environment.TickCount64 - _lastPong > 8000) { Close("Disconnected: the host stopped answering."); return; }
                try { _udp.Send(hello, hello.Length); } catch { }
                if (tick++ % 2 == 0)
                {
                    ping[0] = Protocol.Ping;
                    BitConverter.TryWriteBytes(ping.AsSpan(1), Stopwatch.GetTimestamp());
                    Write(ping);
                }
                Thread.Sleep(1000);
            }
        }
    }
}
