using System;
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
        private readonly object _writeLock = new();
        private volatile bool _closed;
        private long _lastPong;
        private int _closing;

        public event Action<string>? Status;
        public event Action<string>? Disconnected;
        public int LastPingMs { get; private set; } = -1;
        /// <summary>Buffered audio plus the output device, in ms; -1 while nothing plays.</summary>
        public int AudioDelayMs => _player.DelayMs;

        private Client(TcpClient tcp, NetworkStream stream, UdpClient udp, byte[] token, Player player)
        {
            _tcp = tcp; _stream = stream; _udp = udp; _token = token; _player = player;
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
                        ? "Could not find " + address + ". Check the name in the Tailscale app, or use its 100 address."
                        : "Tailscale is not connected on this PC. Open Tailscale and sign in.");
                bool done;
                try { done = tcp.ConnectAsync(addrs, port).Wait(8000); }
                catch (AggregateException e) when (e.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    throw new InvalidOperationException(address + " is on, but TailRemote is not hosting there. Start hosting on that PC.");
                }
                if (!done)
                    throw new TimeoutException(!Protocol.LocalTailscaleUp()
                        ? "Tailscale is not connected on this PC. Open Tailscale and sign in."
                        : "No answer from " + address + ". It may be off, or its Tailscale is not connected.");
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
                byte[] token = new byte[8], hostProof = new byte[32];
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
                var c = new Client(tcp, stream, udp, token, player);
                c.Status += status;
                c.Start();
                return c;
            }
            catch (System.IO.IOException) { tcp.Dispose(); throw new InvalidOperationException("The host closed the connection."); }
            catch { tcp.Dispose(); throw; }
        }

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
            _player.Dispose();
            if (why != null) Disconnected?.Invoke(why);
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

        private void Write(ReadOnlySpan<byte> f)
        {
            try { lock (_writeLock) _stream.Write(f); }
            catch { }
        }

        private void TcpLoop()
        {
            byte[] head = new byte[1], stamp = new byte[8], len = new byte[2];
            try
            {
                while (!_closed)
                {
                    Protocol.ReadExactly(_stream, head);
                    if (head[0] == Protocol.Pong)
                    {
                        Protocol.ReadExactly(_stream, stamp);
                        long sent = BitConverter.ToInt64(stamp);
                        LastPingMs = (int)((Stopwatch.GetTimestamp() - sent) * 1000 / Stopwatch.Frequency);
                        _lastPong = Environment.TickCount64;
                    }
                    else if (head[0] == Protocol.Message)
                    {
                        Protocol.ReadExactly(_stream, len);
                        byte[] text = new byte[BitConverter.ToUInt16(len)];
                        Protocol.ReadExactly(_stream, text);
                        Status?.Invoke("Host: " + System.Text.Encoding.UTF8.GetString(text));
                    }
                    else throw new InvalidOperationException("Unknown message from host.");
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
                if (d.Length == Protocol.AudioPacketBytes && d[0] == Protocol.UdpAudio)
                    _player.Push(BitConverter.ToUInt32(d, 1), d.AsSpan(5));
                else if (d.Length == 5 && d[0] == Protocol.UdpSilence)
                    _player.Push(BitConverter.ToUInt32(d, 1), ReadOnlySpan<byte>.Empty);
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
