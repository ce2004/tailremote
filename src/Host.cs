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
        private readonly string _password;
        private readonly bool _tailscaleOnly;
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
            public readonly object WriteLock = new();
            public volatile IPEndPoint? AudioTo;
            public readonly HashSet<(ushort Vk, bool Ext)> Held = new();
            public LoopbackCapture? Capture;
        }

        public Host(int port, string password, bool tailscaleOnly, Action<string> status)
        {
            _password = password;
            _tailscaleOnly = tailscaleOnly;
            _status = status;

            _listener = new TcpListener(IPAddress.IPv6Any, port);
            _listener.Server.DualMode = true;
            _listener.Start();

            _udp = new UdpClient(AddressFamily.InterNetworkV6);
            _udp.Client.DualMode = true;
            _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            IgnoreUdpResets(_udp.Client);

            new Thread(AcceptLoop) { IsBackground = true, Name = "TailRemote accept" }.Start();
            new Thread(UdpLoop) { IsBackground = true, Name = "TailRemote host udp" }.Start();
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
            try { _udp.Dispose(); } catch { }
            lock (_gate) { if (_session != null) End(_session, null); }
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
                if (_tailscaleOnly && !Protocol.IsTailscale(remote))
                {
                    _status("Refused a connection from " + remote + ": not a Tailscale address.");
                    tcp.Dispose();
                    return;
                }
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                stream.ReadTimeout = 10_000;

                byte[] nonce = RandomNumberGenerator.GetBytes(16);
                byte[] hello = new byte[20];
                Protocol.Magic.CopyTo(hello, 0);
                nonce.CopyTo(hello, 4);
                stream.Write(hello);

                byte[] answer = new byte[36];
                Protocol.ReadExactly(stream, answer);
                bool ok = answer.AsSpan(0, 4).SequenceEqual(Protocol.Magic) &&
                          CryptographicOperations.FixedTimeEquals(answer.AsSpan(4), Protocol.Proof(_password, nonce));
                if (!ok)
                {
                    stream.Write(new byte[] { 0 });
                    _status("Refused " + remote + ": wrong password.");
                    tcp.Dispose();
                    return;
                }

                byte[] token = RandomNumberGenerator.GetBytes(8);
                byte[] accept = new byte[9];
                accept[0] = 1;
                token.CopyTo(accept, 1);
                stream.Write(accept);
                stream.ReadTimeout = Timeout.Infinite;

                s = new Session { Tcp = tcp, Stream = stream, Token = token, Address = remote };
                lock (_gate)
                {
                    if (_session != null) End(_session, "Replaced by a new connection.");
                    _session = s;
                }
                _status("Connected: " + remote + ".");
                var session = s;
                s.Capture = new LoopbackCapture(
                    (seq, pcm) => SendAudio(session, seq, pcm),
                    msg => { _status(msg); try { Protocol.SendMessage(session.Stream, session.WriteLock, msg); } catch { } });

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
            byte[] head = new byte[1];
            byte[] key = new byte[5];
            byte[] stamp = new byte[8];
            while (!_stop)
            {
                Protocol.ReadExactly(s.Stream, head);
                switch (head[0])
                {
                    case Protocol.Key:
                        Protocol.ReadExactly(s.Stream, key);
                        ushort vk = BitConverter.ToUInt16(key, 0);
                        ushort scan = BitConverter.ToUInt16(key, 2);
                        bool up = (key[4] & 1) != 0, ext = (key[4] & 2) != 0;
                        lock (s.Held) { if (up) s.Held.Remove((vk, ext)); else s.Held.Add((vk, ext)); }
                        Native.SendKey(vk, scan, up, ext);
                        break;
                    case Protocol.Ping:
                        Protocol.ReadExactly(s.Stream, stamp);
                        byte[] pong = new byte[9];
                        pong[0] = Protocol.Pong;
                        stamp.CopyTo(pong, 1);
                        lock (s.WriteLock) s.Stream.Write(pong);
                        break;
                    case Protocol.ReleaseAll:
                        ReleaseHeld(s);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown message " + head[0]);
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
            if (why != null) { try { Protocol.SendMessage(s.Stream, s.WriteLock, why); } catch { } }
            ReleaseHeld(s);
            s.Capture?.Dispose();
            try { s.Tcp.Dispose(); } catch { }
        }

        private readonly byte[] _audio = new byte[Protocol.AudioPacketBytes];

        private void SendAudio(Session s, uint seq, short[] pcm)
        {
            var to = s.AudioTo;
            if (to == null) return;
            _audio[0] = Protocol.UdpAudio;
            BitConverter.TryWriteBytes(_audio.AsSpan(1), seq);
            Buffer.BlockCopy(pcm, 0, _audio, 5, pcm.Length * 2);
            try { _udp.Send(_audio, _audio.Length, to); } catch { }
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
