using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// The PC being controlled. One controller at a time (a new controller
    /// replaces the old one, so reconnecting after a dropped network just works),
    /// plus up to 100 listeners who hear everything but cannot type. Which one a
    /// client becomes depends on which password it proved.
    /// </summary>
    internal sealed class Host : IDisposable
    {
        public const int MaxListeners = 100;

        private readonly byte[] _key;
        private readonly byte[]? _listenKey;
        private readonly Action<string> _status;
        private readonly TcpListener _listener;
        private readonly UdpClient _udp;
        private volatile bool _stop;

        /// <summary>The pause before answering a wrong password. The self-test sets it to 0.</summary>
        public static int WrongPasswordDelayMs = 2000;

        /// <summary>Clipboard text from the controller. Raised on a network thread.</summary>
        public event Action<string>? ClipboardReceived;
        /// <summary>A sentence about a file that arrived. Raised on a network thread.</summary>
        public event Action<string>? FileMessage;

        /// <summary>Sends Ctrl+Alt+Del; set only when hosting as the service. Returns false if it could not.</summary>
        public Func<bool>? SecureAttention { get; init; }

        private readonly object _gate = new();
        private Session? _controller;
        private readonly List<Session> _listeners = new();
        private LoopbackCapture? _capture;

        private sealed class Session
        {
            public required TcpClient Tcp;
            public required NetworkStream Stream;
            public required byte[] Token;
            public required IPAddress Address;
            public required SecureLink Link;
            public required byte Role;
            public required byte[] Key, HostNonce, ClientNonce;
            public FileChannel? Files;
            public uint PeerFeatures;
            public volatile int Quality; // 0 = lossless; the client asks for more while its connection struggles
            public volatile IPEndPoint? AudioTo;
            public readonly HashSet<(ushort Vk, bool Ext)> Held = new();
            public readonly byte[] Audio = new byte[Protocol.AudioPacketBytes];
        }

        private string? _captureDevice;

        /// <summary>captureDevice: the output whose sound is sent, or null for Windows' default.</summary>
        public Host(int port, string password, string? listenPassword, Action<string> status, string? captureDevice = null)
        {
            _captureDevice = captureDevice;
            _key = Protocol.DeriveKey(password);
            _listenKey = string.IsNullOrEmpty(listenPassword) ? null : Protocol.DeriveKey(listenPassword);
            _status = msg => { DiagLog.Write("host: " + msg); status(msg); };
            new Thread(LogLoop) { IsBackground = true, Name = "TailRemote host log" }.Start();

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

            lock (_gate) UpdateCapture();
            new Thread(AcceptLoop) { IsBackground = true, Name = "TailRemote accept" }.Start();
            new Thread(UdpLoop) { IsBackground = true, Name = "TailRemote host udp" }.Start();
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
            try { _udp.Dispose(); } catch { }
            lock (_gate)
            {
                foreach (var s in AllSessions()) End(s, null);
                _controller = null;
                _listeners.Clear();
                Rebuild();
                UpdateCapture();
            }
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

        /// <summary>Sends clipboard text to the controller, if it shares the clipboard.</summary>
        public void SendClipboard(string text)
        {
            Session? c;
            lock (_gate) c = _controller;
            if (c == null || (c.PeerFeatures & Protocol.FeatureClipboard) == 0) return;
            // Off the caller's thread: a controller that has stopped reading must not freeze this window.
            ThreadPool.QueueUserWorkItem(_ => { try { c.Link.Send(c.Stream, Protocol.TextMessage(Protocol.Clipboard, text)); } catch { } });
        }

        /// <summary>Sends files to the controller. Blocking; throws with a readable message.</summary>
        public string SendFiles(IReadOnlyList<string> paths, Action<string, int> report, CancellationToken ct)
        {
            Session? c;
            lock (_gate) c = _controller;
            var files = c?.Files ?? throw new InvalidOperationException("No one is controlling this PC, so there is no one to send files to.");
            return files.Send(paths, report, ct);
        }

        /// <summary>Everyone connected, as a ready-made array: the audio thread reads it without waiting for anything.</summary>
        private volatile Session[] _everyone = Array.Empty<Session>();

        private Session[] AllSessions() => _everyone;

        /// <summary>Call under _gate whenever the controller or listeners change.</summary>
        private void Rebuild()
        {
            var all = new List<Session>(_listeners);
            if (_controller != null) all.Add(_controller);
            _everyone = all.ToArray();
        }

        // ---- Nobody can tie the host up ----

        // Logins under way: at most 8 from any one address, 256 in all. A flood
        // from one place cannot crowd out anyone else.
        private const int MaxHandshakes = 256, MaxHandshakesPerAddress = 8;
        private int _handshakes;
        private readonly Dictionary<IPAddress, int> _pending = new();

        private bool BeginHandshake(IPAddress a)
        {
            lock (_guesses)
            {
                if (_handshakes >= MaxHandshakes) return false;
                _pending.TryGetValue(a, out int n);
                if (n >= MaxHandshakesPerAddress) return false;
                _pending[a] = n + 1;
                _handshakes++;
                return true;
            }
        }

        private void EndHandshake(IPAddress a)
        {
            lock (_guesses)
            {
                _handshakes--;
                if (_pending.TryGetValue(a, out int n) && n > 1) _pending[a] = n - 1;
                else _pending.Remove(a);
            }
        }
        private readonly Dictionary<IPAddress, (int Fails, long Since, long BlockedUntil)> _guesses = new();

        /// <summary>After 5 wrong passwords in a minute, an address is refused at once for a minute.</summary>
        private bool Blocked(IPAddress a)
        {
            lock (_guesses) return _guesses.TryGetValue(a, out var g) && g.BlockedUntil > Environment.TickCount64;
        }

        /// <summary>Counts a wrong password; returns what to log, or null. A flood never floods the log.</summary>
        private string? WrongPassword(IPAddress a)
        {
            long now = Environment.TickCount64;
            lock (_guesses)
            {
                if (_guesses.Count > 10_000) _guesses.Clear(); // never grows without bound
                (int Fails, long Since, long BlockedUntil) g = _guesses.TryGetValue(a, out var old) && now - old.Since < 60_000 ? old : (0, now, 0L);
                g.Fails++;
                if (g.Fails >= 5 && g.BlockedUntil <= now) g.BlockedUntil = now + 60_000;
                _guesses[a] = g;
                return g.Fails == 1 ? "Refused " + a + ": wrong password."
                     : g.Fails == 5 ? "Refused " + a + " for a minute: too many wrong passwords."
                     : null;
            }
        }

        /// <summary>Switches to recording another output (null: Windows' default) without stopping hosting.</summary>
        public void SetCaptureDevice(string? id)
        {
            lock (_gate)
            {
                if (_captureDevice == id || _stop) return;
                _captureDevice = id;
                var old = _capture;
                _capture = null;
                if (old != null) ThreadPool.QueueUserWorkItem(_ => old.Dispose());
                UpdateCapture();
            }
        }

        /// <summary>One capture serves everyone; it runs only while someone is connected. Call under _gate.</summary>
        private void UpdateCapture()
        {
            // Runs for as long as this PC hosts, so connecting and disconnecting never
            // start or stop audio capture. With nobody connected it sends nothing.
            bool anyone = !_stop;
            if (anyone && _capture == null)
            {
                _capture = new LoopbackCapture(SendAudio, msg => { _status(msg); Broadcast(msg); }, _captureDevice);
                _capture.Burst += ms => { _burstMs = ms; DiagLog.Write("host: capture device's typical chunk is " + ms + " ms"); SendToAll(BurstMessage(ms)); };
            }
            else if (!anyone && _capture != null)
            {
                var c = _capture;
                _capture = null;
                // Disposing joins the capture thread, which may be waiting on _gate: do it outside.
                ThreadPool.QueueUserWorkItem(_ => c.Dispose());
            }
        }

        private volatile int _burstMs;
        private int _txPackets, _txBytes;

        /// <summary>Once a second while logging is on: what the host is capturing and sending.</summary>
        private void LogLoop()
        {
            while (!_stop)
            {
                Thread.Sleep(1000);
                if (!DiagLog.Enabled) continue;
                int fills = Interlocked.Exchange(ref LoopbackCapture.TestGapFills, 0);
                int fillMs = Interlocked.Exchange(ref LoopbackCapture.TestGapFillMs, 0);
                int skips = Interlocked.Exchange(ref LoopbackCapture.TestSeqSkips, 0);
                int chunk = Interlocked.Exchange(ref LoopbackCapture.TestChunkMax, 0);
                int packets = Interlocked.Exchange(ref _txPackets, 0), bytes = Interlocked.Exchange(ref _txBytes, 0);
                var sb = new System.Text.StringBuilder();
                foreach (var s in AllSessions())
                    sb.Append(" [").Append(s.Address).Append(s.Role == Protocol.RoleControl ? " control" : " listen")
                      .Append(", quality step ").Append(s.Quality).Append(s.AudioTo == null ? ", no audio address yet" : "").Append(']');
                DiagLog.Write("host: capturing " + LoopbackCapture.DeviceInfo + ", biggest chunk " + chunk + " ms, typical " + _burstMs +
                    " ms, silence added " + fills + "x (" + fillMs + " ms), count skips " + skips + ", sent " + packets + " packets, " +
                    (bytes * 8 / 1000) + " kbit/s, sessions" + (sb.Length == 0 ? " none" : sb.ToString()));
            }
        }

        private static byte[] BurstMessage(int ms)
        {
            byte[] m = new byte[3];
            m[0] = Protocol.CaptureBurst;
            BitConverter.TryWriteBytes(m.AsSpan(1), (ushort)Math.Clamp(ms, 0, 1000));
            return m;
        }

        private void SendToAll(byte[] m)
        {
            foreach (var s in AllSessions())
                ThreadPool.QueueUserWorkItem(_ => { try { s.Link.Send(s.Stream, m); } catch { } });
        }

        private void Broadcast(string msg)
        {
            foreach (var s in AllSessions())
                ThreadPool.QueueUserWorkItem(_ => { try { Protocol.SendMessage(s.Link, s.Stream, msg); } catch { } });
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
            bool handedOff = false, counted = false;
            try
            {
                // Refused at once, holding nothing: too many logins under way, or this address keeps guessing.
                if (Blocked(remote) || !BeginHandshake(remote)) { tcp.Dispose(); return; }
                counted = true;
                tcp.NoDelay = true;
                tcp.SendTimeout = 5000; // a PC that stops reading is dropped, never waited on forever
                var stream = tcp.GetStream();
                stream.ReadTimeout = 5_000; // a login that says nothing is dropped after 5 seconds

                byte[] nonce = RandomNumberGenerator.GetBytes(16);
                byte[] hello = new byte[20];
                Protocol.Magic.CopyTo(hello, 0);
                nonce.CopyTo(hello, 4);
                stream.Write(hello);

                byte[] answer = new byte[52];
                Protocol.ReadExactly(stream, answer);
                if (answer.AsSpan(0, 4).SequenceEqual(Protocol.FileMagic))
                {
                    // The controller's second connection, for files.
                    // It can arrive a moment before the main connection is registered.
                    Session? owner = null;
                    for (int i = 0; i < 40 && owner == null; i++)
                    {
                        lock (_gate) owner = _controller != null && _controller.Token.AsSpan().SequenceEqual(answer.AsSpan(4, 8)) && _controller.Address.Equals(remote) ? _controller : null;
                        if (owner == null) Thread.Sleep(50);
                    }
                    if (owner == null) { tcp.Dispose(); return; }
                    owner.Files?.Dispose();
                    owner.Files = new FileChannel(tcp, new SecureLink(owner.Key, owner.HostNonce, owner.ClientNonce, isHost: true, "files "),
                        msg => FileMessage?.Invoke(msg));
                    handedOff = true;
                    return;
                }
                byte[] clientNonce = answer[4..20];
                byte role = 0;
                byte[]? key = null;
                if (answer.AsSpan(0, 4).SequenceEqual(Protocol.Magic))
                {
                    if (CryptographicOperations.FixedTimeEquals(answer.AsSpan(20), Protocol.Proof(_key, 'C', nonce, clientNonce)))
                    { role = Protocol.RoleControl; key = _key; }
                    else if (_listenKey != null && CryptographicOperations.FixedTimeEquals(answer.AsSpan(20), Protocol.Proof(_listenKey, 'C', nonce, clientNonce)))
                    { role = Protocol.RoleListen; key = _listenKey; }
                }
                if (key == null)
                {
                    string? note = WrongPassword(remote);
                    if (note != null) _status(note);
                    Thread.Sleep(WrongPasswordDelayMs); // slows down anyone guessing passwords
                    stream.Write(new byte[] { 0 });
                    tcp.Dispose();
                    return;
                }

                byte[] token = RandomNumberGenerator.GetBytes(8);
                byte[] accept = new byte[42];
                accept[0] = 1;
                accept[1] = role;
                token.CopyTo(accept, 2);
                Protocol.Proof(key, 'H', clientNonce, nonce).CopyTo(accept, 10);
                stream.Write(accept);
                // The client pings every 2 seconds; 10 silent seconds means it is gone,
                // and ending the session lets go of any keys it was holding.
                stream.ReadTimeout = 10_000;

                s = new Session
                {
                    Tcp = tcp, Stream = stream, Token = token, Address = remote, Role = role,
                    Key = key, HostNonce = nonce, ClientNonce = clientNonce,
                    Link = new SecureLink(key, nonce, clientNonce, isHost: true),
                };
                s.Link.Send(s.Stream, Protocol.FeaturesMessage(SecureAttention != null ? Protocol.FeatureSecureAttention : 0));
                if (_burstMs > 0) s.Link.Send(s.Stream, BurstMessage(_burstMs));
                EndHandshake(remote);
                counted = false;
                lock (_gate)
                {
                    if (_stop) { End(s, null); return; }
                    if (role == Protocol.RoleControl)
                    {
                        if (_controller != null) End(_controller, "Replaced by a new connection.");
                        _controller = s;
                    }
                    else
                    {
                        if (_listeners.Count >= MaxListeners)
                        {
                            End(s, "There are already " + MaxListeners + " people listening.");
                            s = null;
                            return;
                        }
                        _listeners.Add(s);
                    }
                    Rebuild();
                    UpdateCapture();
                }
                _status((role == Protocol.RoleControl ? "Connected: " : "Listening: ") + remote + ".");

                ReadLoop(s);
            }
            catch
            {
                // A login that was abandoned or was not TailRemote: dropped without a word,
                // so a flood of them cannot flood the log.
            }
            finally
            {
                if (counted) EndHandshake(remote);
                if (s != null)
                {
                    bool was = false;
                    lock (_gate)
                    {
                        if (_controller == s) { _controller = null; was = true; }
                        else if (_listeners.Remove(s)) was = true;
                        if (was) { End(s, null); Rebuild(); UpdateCapture(); }
                    }
                    if (was) _status((s.Role == Protocol.RoleControl ? "Disconnected: " : "Stopped listening: ") + remote + ".");
                }
                else if (!handedOff) tcp.Dispose();
            }
        }

        private void ReadLoop(Session s)
        {
            while (!_stop)
            {
                byte[] m = s.Link.Receive(s.Stream);
                if (m.Length == 0) continue;
                switch (m[0])
                {
                    case Protocol.Key when m.Length == 6:
                        ushort vk = BitConverter.ToUInt16(m, 1);
                        ushort scan = BitConverter.ToUInt16(m, 3);
                        bool up = (m[5] & 1) != 0, ext = (m[5] & 2) != 0;
                        lock (_gate)
                        {
                            // Only the current controller types. A replaced or closed session
                            // must not press anything after its keys were released.
                            if (_controller != s) break;
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
                    case Protocol.SecureAttention when _controller == s:
                        if (SecureAttention == null) Protocol.SendMessage(s.Link, s.Stream, "Control Alt Delete needs the TailRemote service on the remote PC.");
                        else if (!SecureAttention()) Protocol.SendMessage(s.Link, s.Stream, "The remote PC could not send Control Alt Delete.");
                        break;
                    case Protocol.RestartPc when _controller == s:
                        _status("Restarting this PC, as the controlling PC asked.");
                        Broadcast("The remote PC is restarting.");
                        // A normal restart: programs can still ask to save their work.
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false }); }
                        catch (Exception e) { Broadcast("The remote PC could not restart: " + e.Message); }
                        break;
                    case Protocol.AudioQuality when m.Length == 2:
                        // Sample-rate steps for PCs that understand them, bit steps for 1.5.0.
                        s.Quality = Math.Min((int)m[1], (s.PeerFeatures & Protocol.FeatureRate) != 0 ? Protocol.Rates.Length - 1 : 4);
                        DiagLog.Write("host: " + s.Address + " asked for quality step " + s.Quality);
                        break;
                    case Protocol.Features when m.Length >= 5:
                        s.PeerFeatures = BitConverter.ToUInt32(m, 1);
                        break;
                    case Protocol.Clipboard when s.Role == Protocol.RoleControl:
                        ClipboardReceived?.Invoke(System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1));
                        break;
                    // Anything else is from a newer version: ignore it.
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

        /// <summary>
        /// Lets go of the session's keys at once, then says goodbye and closes it
        /// in the background: a PC that has stopped reading must never hold up
        /// the host, and End is called while holding _gate.
        /// </summary>
        private static void End(Session s, string? why)
        {
            ReleaseHeld(s);
            s.Files?.Dispose();
            if (why == null) { try { s.Tcp.Dispose(); } catch { } return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Protocol.SendMessage(s.Link, s.Stream, why); } catch { }
                try { s.Tcp.Dispose(); } catch { }
            });
        }

        /// <summary>Called on the capture thread with each packet; sent to every session that said where.</summary>
        private readonly byte[] _packed = new byte[Protocol.PacketFrames * 4];
        private readonly byte[][] _packed2 = { new byte[Protocol.PacketFrames * 4], new byte[Protocol.PacketFrames * 4], new byte[Protocol.PacketFrames * 4], new byte[Protocol.PacketFrames * 4], new byte[Protocol.PacketFrames * 4] };
        private readonly int[] _packed2Length = new int[5];

        // Lower sample rates: one streaming downsampler per rate, so there is no seam between packets.
        private readonly Resampler?[] _down = new Resampler?[Protocol.Rates.Length];
        private readonly uint[] _downNext = new uint[Protocol.Rates.Length];
        private readonly short[][] _downPcm = Array.ConvertAll(Protocol.Rates, _ => new short[Protocol.PacketFrames * 2 + 16]);
        private readonly int[] _downFrames = new int[Protocol.Rates.Length];
        private readonly byte[][] _rated = Array.ConvertAll(Protocol.Rates, _ => new byte[Protocol.PacketFrames * 4]);
        private readonly int[] _ratedLength = new int[Protocol.Rates.Length];
        private readonly float[] _downIn = new float[Protocol.PacketFrames * 2];
        private int _downLevel;
        private Action<float, float>? _downEmit;

        private void DownEmit(float l, float r)
        {
            int f = _downFrames[_downLevel];
            if (f >= Protocol.PacketFrames) return;
            short[] d = _downPcm[_downLevel];
            d[f * 2] = (short)Math.Clamp(MathF.Round(l * 32767f), -32768f, 32767f);
            d[f * 2 + 1] = (short)Math.Clamp(MathF.Round(r * 32767f), -32768f, 32767f);
            _downFrames[_downLevel] = f + 1;
        }

        /// <summary>This packet at a lower rate, packed once for everyone on that rate. -1 if it would not pack.</summary>
        private int Rated(int level, uint seq, short[] pcm)
        {
            if (_ratedLength[level] != -2) return _ratedLength[level];
            var rs = _down[level] ??= new Resampler(Protocol.AudioRate, Protocol.Rates[level]);
            if (_downNext[level] != seq) rs.Reset(); // not used for a while: start clean
            _downNext[level] = seq + 1;
            for (int i = 0; i < _downIn.Length; i++) _downIn[i] = pcm[i] / 32768f;
            _downLevel = level;
            _downFrames[level] = 0;
            rs.Process(_downIn, _downEmit ??= DownEmit);
            int n = _downFrames[level];
            _ratedLength[level] = Lossless2.EncodeRate(_downPcm[level].AsSpan(0, n * 2), n, level, _rated[level]);
            return _ratedLength[level];
        }

        private void SendAudio(uint seq, short[]? pcm)
        {
            var all = AllSessions();
            if (all.Length == 0) return;
            // Packed once, losslessly, for everyone whose PC can unpack it: about half the data.
            int packedLength = pcm != null && Array.Exists(all, x => (x.PeerFeatures & (Protocol.FeatureLossless | Protocol.FeatureLossless2)) == Protocol.FeatureLossless)
                ? Lossless.Encode(pcm, _packed) : -1;
            Array.Fill(_packed2Length, -2); // -2: not packed yet at that quality; each is packed once, for all who want it
            Array.Fill(_ratedLength, -2);
            foreach (var s in all)
            {
                var to = s.AudioTo;
                if (to == null) continue;
                byte[] a = s.Audio; // each session seals its own copy with its own keys
                BitConverter.TryWriteBytes(a.AsSpan(1), seq);
                int payload;
                bool byRate = (s.PeerFeatures & Protocol.FeatureRate) != 0;
                int q = s.Quality;
                int bits = byRate ? 0 : Math.Min(q, 4); // a rate-capable PC steps by rate, never by bits
                if (pcm != null && (s.PeerFeatures & Protocol.FeatureLossless2) != 0 && _packed2Length[bits] == -2)
                    _packed2Length[bits] = Lossless2.Encode(pcm, bits, _packed2[bits]);
                int ratedLength = pcm != null && byRate && q > 0 ? Rated(q, seq, pcm) : -1;
                if (pcm == null) { a[0] = Protocol.UdpSilence; payload = 0; }
                else if (ratedLength > 0)
                {
                    a[0] = Protocol.UdpPackedRate;
                    payload = ratedLength;
                    _rated[q].AsSpan(0, payload).CopyTo(a.AsSpan(5));
                }
                else if ((s.PeerFeatures & Protocol.FeatureLossless2) != 0 && _packed2Length[bits] > 0)
                {
                    a[0] = Protocol.UdpPacked2;
                    payload = _packed2Length[bits];
                    _packed2[bits].AsSpan(0, payload).CopyTo(a.AsSpan(5));
                }
                else if (packedLength > 0 && (s.PeerFeatures & Protocol.FeatureLossless) != 0)
                {
                    a[0] = Protocol.UdpPacked;
                    payload = packedLength;
                    _packed.AsSpan(0, payload).CopyTo(a.AsSpan(5));
                }
                else
                {
                    a[0] = Protocol.UdpAudio;
                    payload = pcm.Length * 2;
                    Buffer.BlockCopy(pcm, 0, a, 5, payload);
                }
                try
                {
                    s.Link.SealAudio(a, payload);
                    _udp.Send(a, 5 + payload + SecureLink.TagSize, to);
                    Interlocked.Increment(ref _txPackets);
                    Interlocked.Add(ref _txBytes, 5 + payload + SecureLink.TagSize);
                }
                catch { }
            }
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
                s = Array.Find(AllSessions(), x => d.AsSpan(1).SequenceEqual(x.Token));
                if (s == null) continue;
                var from = any.Address.IsIPv4MappedToIPv6 ? any.Address.MapToIPv4() : any.Address;
                if (!from.Equals(s.Address)) continue;
                s.AudioTo = new IPEndPoint(any.Address, any.Port);
            }
        }
    }
}
