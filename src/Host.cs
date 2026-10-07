using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// The PC being controlled. One controller at a time; a new connection
    /// replaces the old one, so reconnecting after a dropped network just works.
    /// </summary>
    internal sealed class Host : IDisposable
    {
        private readonly byte[] _key;
        private readonly Action<string> _status;
        private readonly TcpListener _listener;
        private readonly UdpClient _udp;
        private volatile bool _stop;

        private readonly object _gate = new();
        private Session? _session;

        private sealed class Session
        {
            public required TcpClient Tcp;
            public required NetworkStream Stream;
            public required byte[] Token;
            public required IPAddress Address;
            public required SecureLink Link;
            public volatile IPEndPoint? AudioTo;
            public readonly HashSet<(ushort Vk, bool Ext)> Held = new();
            public LoopbackCapture? Capture;
        }

        public Host(int port, string password, Action<string> status)
        {
            _key = Protocol.DeriveKey(password);
            _status = status;

            _listener = new TcpListener(IPAddress.IPv6Any, port);
            _listener.Server.DualMode = true;
            _udp = new UdpClient(AddressFamily.InterNetworkV6);
            _udp.Client.DualMode = true;
            // Exclusive: no other program may share the port while TailRemote holds it.
            _listener.ExclusiveAddressUse = true;
            _udp.Client.ExclusiveAddressUse = true;
            try
            {
                _listener.Start();
                _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            catch (SocketException e)
            {
                _listener.Stop();
                _udp.Dispose();
                throw new InvalidOperationException(e.SocketErrorCode == SocketError.AccessDenied
                    ? "Port " + port + " is reserved by Windows on this PC. Choose another port."
                    : "Port " + port + " is already used by another program on this PC. Choose another port.");
            }
            IgnoreUdpResets(_udp.Client);

            new Thread(AcceptLoop) { IsBackground = true, Name = "TailRemote accept" }.Start();
            new Thread(UdpLoop) { IsBackground = true, Name = "TailRemote host udp" }.Start();
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
            try { _udp.Dispose(); } catch { }
            lock (_gate) { if (_session != null) End(_session, null); _session = null; }
        }

        /// <summary>
        /// Windows reports an ICMP "port unreachable" from an earlier send as an
        /// error on the next receive, which would end the receive loop.
        /// </summary>
        public static void IgnoreUdpResets(Socket s)
        {
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { }
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { if (_stop) return; continue; }
                new Thread(() => Serve(tcp)) { IsBackground = true, Name = "TailRemote session" }.Start();
            }
        }

        private void Serve(TcpClient tcp)
        {
            var remote = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            Session? s = null;
            try
            {
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                stream.ReadTimeout = 10_000;

                byte[] nonce = RandomNumberGenerator.GetBytes(16);
                byte[] hello = new byte[20];
                Protocol.Magic.CopyTo(hello, 0);
                nonce.CopyTo(hello, 4);
                stream.Write(hello);

                byte[] answer = new byte[52];
                Protocol.ReadExactly(stream, answer);
                byte[] clientNonce = answer[4..20];
                bool ok = answer.AsSpan(0, 4).SequenceEqual(Protocol.Magic) &&
                          CryptographicOperations.FixedTimeEquals(answer.AsSpan(20), Protocol.Proof(_key, 'C', nonce, clientNonce));
                if (!ok)
                {
                    Thread.Sleep(2000); // slows down anyone guessing passwords
                    stream.Write(new byte[] { 0 });
                    _status("Refused " + remote + ": wrong password.");
                    tcp.Dispose();
                    return;
                }

                byte[] token = RandomNumberGenerator.GetBytes(8);
                byte[] accept = new byte[41];
                accept[0] = 1;
                token.CopyTo(accept, 1);
                Protocol.Proof(_key, 'H', clientNonce, nonce).CopyTo(accept, 9);
                stream.Write(accept);
                // The client pings every 2 seconds; 10 silent seconds means it is gone,
                // and ending the session lets go of any keys it was holding.
                stream.ReadTimeout = 10_000;

                s = new Session { Tcp = tcp, Stream = stream, Token = token, Address = remote, Link = new SecureLink(_key, nonce, clientNonce, isHost: true) };
                var session = s;
                s.Capture = new LoopbackCapture(
                    (seq, pcm) => SendAudio(session, seq, pcm),
                    msg => { _status(msg); try { Protocol.SendMessage(session.Link, session.Stream, msg); } catch { } });
                lock (_gate)
                {
                    if (_stop) { End(s, null); return; }
                    if (_session != null) End(_session, "Replaced by a new connection.");
                    _session = s;
                }
                _status("Connected: " + remote + ".");

                ReadLoop(s);
            }
            catch (Exception e)
            {
                if (s == null) _status("Connection from " + remote + " failed: " + e.Message);
            }
            finally
            {
                if (s != null)
                {
                    lock (_gate)
                    {
                        if (_session == s) { End(s, null); _session = null; _status("Disconnected: " + remote + "."); }
                    }
                }
                else tcp.Dispose();
            }
        }

        private void ReadLoop(Session s)
        {
            while (!_stop)
            {
                byte[] m = s.Link.Receive(s.Stream);
                if (m.Length == 0) throw new InvalidOperationException("Empty message.");
                switch (m[0])
                {
                    case Protocol.Key when m.Length == 6:
                        ushort vk = BitConverter.ToUInt16(m, 1);
                        ushort scan = BitConverter.ToUInt16(m, 3);
                        bool up = (m[5] & 1) != 0, ext = (m[5] & 2) != 0;
                        lock (_gate)
                        {
                            // A replaced or closed session must not press anything after its keys were released.
                            if (_session != s) return;
                            lock (s.Held) { if (up) s.Held.Remove((vk, ext)); else s.Held.Add((vk, ext)); }
                            Native.SendKey(vk, scan, up, ext);
                        }
                        break;
                    case Protocol.Ping when m.Length == 9:
                        m[0] = Protocol.Pong;
                        s.Link.Send(s.Stream, m);
                        break;
                    case Protocol.ReleaseAll:
                        ReleaseHeld(s);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown message " + m[0]);
                }
            }
        }

        private static void ReleaseHeld(Session s)
        {
            lock (s.Held)
            {
                foreach (var (vk, ext) in s.Held) Native.SendKey(vk, 0, true, ext);
                s.Held.Clear();
            }
        }

        private static void End(Session s, string? why)
        {
            if (why != null) { try { Protocol.SendMessage(s.Link, s.Stream, why); } catch { } }
            ReleaseHeld(s);
            s.Capture?.Dispose();
            try { s.Tcp.Dispose(); } catch { }
            s.Link.Dispose();
        }

        private readonly byte[] _audio = new byte[Protocol.AudioPacketBytes];

        private void SendAudio(Session s, uint seq, short[]? pcm)
        {
            var to = s.AudioTo;
            if (to == null) return;
            _audio[0] = pcm == null ? Protocol.UdpSilence : Protocol.UdpAudio;
            BitConverter.TryWriteBytes(_audio.AsSpan(1), seq);
            int payload = pcm == null ? 0 : pcm.Length * 2;
            if (pcm != null) Buffer.BlockCopy(pcm, 0, _audio, 5, payload);
            try
            {
                s.Link.SealAudio(_audio, payload);
                _udp.Send(_audio, 5 + payload + SecureLink.TagSize, to);
            }
            catch { }
        }

        private void UdpLoop()
        {
            var any = new IPEndPoint(IPAddress.IPv6Any, 0);
            while (!_stop)
            {
                byte[] d;
                try { d = _udp.Receive(ref any); }
                catch { if (_stop) return; continue; }
                if (d.Length != 9 || d[0] != Protocol.UdpHello) continue;
                Session? s;
                lock (_gate) s = _session;
                if (s == null || !d.AsSpan(1).SequenceEqual(s.Token)) continue;
                var from = any.Address.IsIPv4MappedToIPv6 ? any.Address.MapToIPv4() : any.Address;
                if (!from.Equals(s.Address)) continue;
                s.AudioTo = new IPEndPoint(any.Address, any.Port);
            }
        }
    }
}
