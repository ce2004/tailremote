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

        /// <summary>Sends Ctrl+Alt+Del; set only when hosting as the service. Returns false if it could not.</summary>
        public Func<bool>? SecureAttention { get; init; }

        /// <summary>
        /// A controlling PC asked to update TailRemote here to this version. Raised on a pool
        /// thread; the second argument sends a message back to that PC alone.
        /// </summary>
        public event Action<string, Action<string>>? UpdateRequested;

        private readonly object _gate = new();
        // Any number of controllers up to MaxControllers, all at once: a second one used to replace
        // the first, which reconnected and replaced the second, back and forth for ever.
        private readonly List<Session> _controllers = new();
        public const int MaxControllers = 100; // the same password lets many PCs control at once; this only stops a runaway
        private readonly List<FileChannel> _kept = new(); // file channels of controllers that dropped, for their next connection; under _gate
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
            public FileChannel? Files; // under _gate
            public double Pace = 8 << 20; // bytes a second the host may send this controller files at (FilePace)
            public uint PeerFeatures;
            public volatile bool Ready; // its features message came: it has said everything it wants first (a locked bitrate)
            public volatile int Quality; // the bitrate step (0 = the best); the client asks for lower while its connection struggles
            public volatile IPEndPoint? AudioTo;
            public long HeardAt; // when its last UDP hello came (every second): no hello for 3 s, no audio
            public readonly long Since = Environment.TickCount64;
            public long LastMessage = Environment.TickCount64; // anything at all from it (it pings several times a second while holding keys)
            public bool WarnedNoUdp;
            public readonly HashSet<(ushort Vk, bool Ext)> Held = new();
            public readonly byte[] Audio = new byte[5 + 1 + Protocol.MaxOpusBytes + SecureLink.TagSize];
            public uint SentUntil; // the tick after the last audio sent
            public bool SentAny;
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
            new Thread(KeySafety) { IsBackground = true, Name = "TailRemote key safety" }.Start();

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

            lock (_gate) StartCapture();
            _wifi = new WlanStreaming("host"); // steady Wi-Fi while hosting
            new Thread(AcceptLoop) { IsBackground = true, Name = "TailRemote accept" }.Start();
            new Thread(UdpLoop) { IsBackground = true, Name = "TailRemote host udp" }.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _wifi?.Dispose();
            try { _listener.Stop(); } catch { }
            try { _udp.Dispose(); } catch { }
            LoopbackCapture? c;
            lock (_gate)
            {
                foreach (var s in AllSessions()) End(s, null);
                foreach (var k in _kept) k.Dispose();
                _kept.Clear();
                _controllers.Clear();
                _listeners.Clear();
                Rebuild();
                c = _capture;
                _capture = null;
            }
            // Stopping the capture waits for its thread (up to 2 seconds): never on the caller's (window's) thread.
            if (c != null) ThreadPool.QueueUserWorkItem(_ => c.Dispose());
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

        /// <summary>Every controller's file channel (each controlling PC gets what this PC sends).</summary>
        private FileChannel[] ControllerChannels()
        {
            lock (_gate) return _controllers.Select(c => c.Files).OfType<FileChannel>().ToArray();
        }

        /// <summary>Sends clipboard text to every controller's clipboard. Never waits.</summary>
        public void SendClipboard(string text)
        {
            // Over the file lanes: confirmed on arrival, and sent again if a connection breaks on the way.
            foreach (var files in ControllerChannels())
                ThreadPool.QueueUserWorkItem(_ => { try { files.SendText(text); } catch { } });
        }

        /// <summary>Copied files from here to every controller's clipboard. Never waits.</summary>
        public void SendClipboardFiles(IReadOnlyList<string> paths, System.Security.Principal.WindowsIdentity? asUser = null)
        {
            foreach (var files in ControllerChannels())
                ThreadPool.QueueUserWorkItem(_ => { try { files.SendFiles(paths, toClipboard: true, asUser); } catch { } });
        }

        /// <summary>Send files: into every controller's Downloads\TailRemote. Never waits. asUser: the service's agent opens the files as the window's user, never as SYSTEM.</summary>
        public void SendFiles(IReadOnlyList<string> paths, System.Security.Principal.WindowsIdentity? asUser = null)
        {
            foreach (var files in ControllerChannels())
                ThreadPool.QueueUserWorkItem(_ => { try { files.SendFiles(paths, toClipboard: false, asUser); } catch { } });
        }

        /// <summary>How many PCs are controlling and listening right now (for the window's title).</summary>
        public (int Controlling, int Listening) Connected { get { lock (_gate) return (_controllers.Count, _listeners.Count); } }

        /// <summary>True while someone is controlling this PC (the only ones files and the clipboard can go to).</summary>
        public bool HasController => ControllerChannels().Length > 0;

        /// <summary>Stops what is going to or coming from the controllers (also while one is reconnecting).</summary>
        public void CancelTransfer()
        {
            FileChannel[] all;
            lock (_gate) all = ControllerChannels().Concat(_kept).ToArray();
            foreach (var files in all) files.CancelSending();
        }

        /// <summary>Test only: the newest controller's file channel.</summary>
        internal FileChannel? ControllerFiles { get { lock (_gate) return _controllers.LastOrDefault()?.Files; } }

        /// <summary>Files from the controller's clipboard, in the holding folder. Raised on a network thread.</summary>
        public event Action<string[]>? ClipboardFilesReceived;
        /// <summary>How a clipboard batch is going, either way. Raised on a network thread.</summary>
        public event Action<FileChannel.Transfer>? TransferProgress;

        /// <summary>Everyone connected, as a ready-made array: the audio thread reads it without waiting for anything.</summary>
        private volatile Session[] _everyone = Array.Empty<Session>();

        private Session[] AllSessions() => _everyone;

        /// <summary>Test only (--chaostest): how many controllers and listeners are connected.</summary>
        internal int TestSessions => _everyone.Length;

        /// <summary>Call under _gate whenever the controller or listeners change.</summary>
        private void Rebuild()
        {
            var all = new List<Session>(_listeners);
            all.AddRange(_controllers);
            _everyone = all.ToArray();
        }

        // ---- Nobody can tie the host up ----

        // Logins under way: at most 32 from any one address, 256 in all. A flood from one
        // place cannot crowd out anyone else, and from one address the NEWEST login always
        // gets in: the oldest silent one is dropped to make room. (Refusing the new one let
        // 8 silent connections lock a real login out for as long as they kept coming.)
        // 32 from one address: many controllers behind one router each open file lanes, and with 8
        // a lane's login could push a real one out.
        private const int MaxHandshakes = 256, MaxHandshakesPerAddress = 32;
        private int _handshakes;
        private readonly Dictionary<IPAddress, LinkedList<TcpClient>> _pending = new();

        private bool BeginHandshake(IPAddress a, TcpClient tcp)
        {
            TcpClient? drop = null;
            lock (_guesses)
            {
                if (!_pending.TryGetValue(a, out var list)) _pending[a] = list = new LinkedList<TcpClient>();
                if (list.Count >= MaxHandshakesPerAddress)
                {
                    drop = list.First!.Value; // the oldest from this address makes room
                    list.RemoveFirst();
                    _handshakes--;
                }
                if (_handshakes >= MaxHandshakes) { if (list.Count == 0) _pending.Remove(a); return false; }
                list.AddLast(tcp);
                _handshakes++;
            }
            try { drop?.Dispose(); } catch { } // its login thread ends on its own
            return true;
        }

        private void EndHandshake(IPAddress a, TcpClient tcp)
        {
            lock (_guesses)
            {
                if (!_pending.TryGetValue(a, out var list) || !list.Remove(tcp)) return; // already dropped to make room
                _handshakes--;
                if (list.Count == 0) _pending.Remove(a);
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
            lock (_switching)
            {
                LoopbackCapture? old;
                lock (_gate)
                {
                    if (_captureDevice == id || _stop) return;
                    _captureDevice = id;
                    old = _capture;
                    _capture = null;
                }
                // The old capture must stop before the new one starts: the packet number is
                // the audio nonce, and two captures counting at once would repeat one. Outside
                // _gate, because stopping waits for the capture thread.
                StopCapture(old);
                lock (_gate) StartCapture();
            }
        }

        private readonly object _switching = new();
        private uint _nextSeq;

        /// <summary>Capture runs for as long as this PC hosts; with nobody connected it sends nothing. Call under _gate.</summary>
        private void StartCapture()
        {
            if (!_stop && _capture == null)
                _capture = new LoopbackCapture(SendAudio, msg => { _status(msg); Broadcast(msg); }, _captureDevice, _nextSeq);
        }

        private void StopCapture(LoopbackCapture? c)
        {
            if (c == null) return;
            bool stopped = c.Stop();
            // A second's gap (the player starts fresh); far more if the thread would not stop.
            _nextSeq = c.NextSeq + (stopped ? 172u : 1u << 24);
        }

        private readonly WlanStreaming _wifi;
        private int _txPackets, _txBytes;

        /// <summary>Once a second while logging is on: what the host is capturing and sending.</summary>
        private void LogLoop()
        {
            while (!_stop)
            {
                Thread.Sleep(1000);
                // Connected, but its UDP hello never came: this PC cannot send it any sound. Said once.
                foreach (var s in AllSessions())
                    if (s.AudioTo == null && !s.WarnedNoUdp && Environment.TickCount64 - s.Since > 5000)
                    {
                        s.WarnedNoUdp = true;
                        _status("No sound can reach " + s.Address + ": nothing from it arrives here over UDP port " + ((IPEndPoint)_listener.LocalEndpoint).Port +
                            ", though its keys do. Open the port with Port editor on this PC, or check the firewall and the network.");
                    }
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
                DiagLog.Write("host: capturing " + LoopbackCapture.DeviceInfo + ", biggest chunk " + chunk +
                    " ms, silence added " + fills + "x (" + fillMs + " ms), count skips " + skips + ", sent " + packets + " packets, " +
                    (bytes * 8 / 1000) + " kbit/s, sessions" + (sb.Length == 0 ? " none" : sb.ToString()));
            }
        }

        /// <summary>
        /// Tells every connected PC why this host is about to go away (updating, restarting,
        /// stopped), so they say that instead of "the connection was forcibly closed", and an
        /// update or restart is waited out quietly. Waits up to a second for the sends; the
        /// connections stay open, so a restart that fails changes nothing.
        /// </summary>
        public void Leave(byte why, string detail = "")
        {
            byte[] utf = System.Text.Encoding.UTF8.GetBytes(detail);
            byte[] m = new byte[2 + utf.Length];
            m[0] = Protocol.Leaving;
            m[1] = why;
            utf.CopyTo(m, 2);
            var sends = AllSessions().Select(s => System.Threading.Tasks.Task.Run(() => { try { s.Link.Send(s.Stream, m); } catch { } })).ToArray();
            try { System.Threading.Tasks.Task.WaitAll(sends, 1000); } catch { }
            _status(why switch
            {
                Protocol.LeavingUpdating => "Told the connected PCs this PC is updating TailRemote.",
                Protocol.LeavingRestarting => "Told the connected PCs this PC is restarting.",
                _ => "Told the connected PCs hosting is stopping.",
            });
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
                if (Blocked(remote) || !BeginHandshake(remote, tcp)) { tcp.Dispose(); return; }
                counted = true;
                tcp.NoDelay = true;
                tcp.SendTimeout = 5000; // a PC that stops reading is dropped, never waited on forever
                var stream = tcp.GetStream();
                stream.ReadTimeout = 3_000; // a login that says nothing is dropped after 3 seconds

                byte[] nonce = RandomNumberGenerator.GetBytes(16);
                byte[] hello = new byte[Protocol.HelloBytes];
                Protocol.Magic.CopyTo(hello, 0);
                nonce.CopyTo(hello, 4);
                Protocol.AddCheck(hello);
                stream.Write(hello);

                byte[] answer = new byte[Protocol.AnswerBytes];
                Protocol.ReadExactly(stream, answer);
                // Damaged on the way (or junk): hang up. Never counted as a wrong password, so
                // a bad link cannot get a PC blocked, and the client simply tries again.
                if (!Protocol.CheckOk(answer)) { tcp.Dispose(); return; }
                if (answer.AsSpan(0, 4).SequenceEqual(Protocol.FileMagic))
                {
                    // The controller's second connection, for files.
                    // It can arrive a moment before the main connection is registered.
                    Session? owner = null;
                    for (int i = 0; i < 40 && owner == null; i++)
                    {
                        lock (_gate) owner = _controllers.Find(c => c.Token.AsSpan().SequenceEqual(answer.AsSpan(4, 8)) && c.Address.Equals(remote));
                        if (owner == null) Thread.Sleep(50);
                    }
                    if (owner == null) { tcp.Dispose(); return; }
                    // One more lane for the controller's channel. A channel this PC already has
                    // (the controller's main connection dropped and came back: kept, or still on
                    // its old session that has not timed out yet) carries on with what it was doing.
                    byte[] channel = answer[12..28];
                    FileChannel files;
                    lock (_gate)
                    {
                        if (owner.Files is FileChannel mine && !mine.Gone && mine.Id.AsSpan().SequenceEqual(channel)) files = mine;
                        else
                        {
                            bool Same(FileChannel f) => !f.Gone && f.Id.AsSpan().SequenceEqual(channel);
                            var stale = _controllers.Find(c => c != owner && c.Files is FileChannel f && Same(f));
                            var kept = _kept.Find(Same);
                            if (stale != null) { files = stale.Files!; stale.Files = null; }
                            else if (kept != null) { files = kept; _kept.Remove(kept); }
                            else
                            {
                                files = new FileChannel(channel)
                                {
                                    TextReceived = text => ClipboardReceived?.Invoke(text),
                                    FilesReceived = paths => ClipboardFilesReceived?.Invoke(paths),
                                    Progress = t => TransferProgress?.Invoke(t),
                                };
                            }
                            if (owner.Files != files) owner.Files?.Dispose();
                            owner.Files = files;
                            var o = owner;
                            files.Rate = () => Volatile.Read(ref o.Pace);
                            files.PeerName = owner.Address.ToString();
                        }
                    }
                    files.AddLane(tcp, new SecureLink(owner.Key, owner.HostNonce, owner.ClientNonce, isHost: true, FileChannel.LanePurpose(nonce, answer.AsSpan(28, 16))));
                    handedOff = true;
                    return;
                }
                byte[] clientNonce = answer[4..20];
                byte role = 0;
                byte[]? key = null;
                if (answer.AsSpan(0, 4).SequenceEqual(Protocol.Magic))
                {
                    if (CryptographicOperations.FixedTimeEquals(answer.AsSpan(20, 32), Protocol.Proof(_key, 'C', nonce, clientNonce)))
                    { role = Protocol.RoleControl; key = _key; }
                    else if (_listenKey != null && CryptographicOperations.FixedTimeEquals(answer.AsSpan(20, 32), Protocol.Proof(_listenKey, 'C', nonce, clientNonce)))
                    { role = Protocol.RoleListen; key = _listenKey; }
                }
                if (key == null)
                {
                    string? note = WrongPassword(remote);
                    if (note != null) _status(note);
                    Thread.Sleep(WrongPasswordDelayMs); // slows down anyone guessing passwords
                    byte[] refuse = new byte[Protocol.ReplyBytes]; // the same size as an acceptance
                    Protocol.AddCheck(refuse);
                    stream.Write(refuse);
                    tcp.Dispose();
                    return;
                }

                byte[] token = RandomNumberGenerator.GetBytes(8);
                byte[] accept = new byte[Protocol.ReplyBytes];
                accept[0] = 1;
                accept[1] = role;
                token.CopyTo(accept, 2);
                Protocol.Proof(key, 'H', clientNonce, nonce).CopyTo(accept, 10);
                Protocol.AddCheck(accept);
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
                EndHandshake(remote, tcp);
                counted = false;
                lock (_gate)
                {
                    if (_stop) { End(s, null); return; }
                    if (role == Protocol.RoleControl)
                    {
                        // The same PC coming back after its connection broke: its old session, silent for
                        // a while now, is let go at once (keys released), not after its 10 seconds.
                        long now = Environment.TickCount64;
                        foreach (var old in _controllers.Where(c => c.Address.Equals(remote) && now - Volatile.Read(ref c.LastMessage) > 3000).ToList())
                        {
                            _controllers.Remove(old);
                            End(old, null);
                        }
                        if (_controllers.Count >= MaxControllers)
                        {
                            End(s, "There are already " + MaxControllers + " PCs controlling this one.");
                            s = null;
                            return;
                        }
                        _controllers.Add(s);
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
                if (counted) EndHandshake(remote, tcp);
                if (s != null)
                {
                    bool was = false;
                    lock (_gate)
                    {
                        if (_controllers.Remove(s) || _listeners.Remove(s)) was = true;
                        if (was) { End(s, null); Rebuild(); }
                    }
                    if (was) _status((s.Role == Protocol.RoleControl ? "Disconnected: " : "Stopped listening: ") + remote + ".");
                    else s.Tcp.Dispose(); // never registered (the host was stopping, or full)
                }
                else if (!handedOff) tcp.Dispose();
                Native.LeaveDesktop();
            }
        }

        private bool IsController(Session s) { lock (_gate) return _controllers.Contains(s); }

        private void ReadLoop(Session s)
        {
            while (!_stop)
            {
                byte[] m = s.Link.Receive(s.Stream);
                Volatile.Write(ref s.LastMessage, Environment.TickCount64);
                if (m.Length == 0) continue;
                switch (m[0])
                {
                    case Protocol.Key when m.Length == 6:
                        ushort vk = BitConverter.ToUInt16(m, 1);
                        ushort scan = BitConverter.ToUInt16(m, 3);
                        bool up = (m[5] & 1) != 0, ext = (m[5] & 2) != 0;
                        lock (_gate)
                        {
                            // Only connected controllers type. A closed session must not press
                            // anything after its keys were released.
                            if (!_controllers.Contains(s)) break;
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
                    case Protocol.SecureAttention when IsController(s):
                        if (SecureAttention == null) Protocol.SendMessage(s.Link, s.Stream, "Control Alt Delete needs the TailRemote service on the remote PC.");
                        else if (!SecureAttention()) Protocol.SendMessage(s.Link, s.Stream, "The remote PC could not send Control Alt Delete.");
                        break;
                    case Protocol.UpdateTo when IsController(s):
                        {
                            string version = System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1);
                            void Reply(string msg) { try { Protocol.SendMessage(s.Link, s.Stream, msg); } catch { } }
                            if (UpdateRequested == null) Reply("TailRemote on the remote PC cannot update itself from here.");
                            else ThreadPool.QueueUserWorkItem(_ => UpdateRequested(version, Reply));
                        }
                        break;
                    case Protocol.ListFolder when m.Length >= 5 && IsController(s):
                        {
                            byte[] id = m[1..5];
                            string path = System.Text.Encoding.UTF8.GetString(m, 5, m.Length - 5);
                            // Off this thread: a slow drive must not hold up the keys behind it.
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                // Never let a problem here end the host (or the service): it is said instead.
                                string listing;
                                try { listing = RemoteTools.ListFolder(path); }
                                catch (Exception e) { listing = "E\t" + e.Message; }
                                byte[] body = System.Text.Encoding.UTF8.GetBytes(listing);
                                byte[] r = new byte[5 + body.Length];
                                r[0] = Protocol.FolderList;
                                id.CopyTo(r, 1);
                                body.CopyTo(r, 5);
                                try { s.Link.Send(s.Stream, r); } catch { }
                            });
                        }
                        break;
                    case Protocol.Fetch when IsController(s):
                        {
                            string[] paths = System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            FileChannel? files;
                            lock (_gate) files = s.Files;
                            if (files == null) Protocol.SendMessage(s.Link, s.Stream, "The file connection is not open yet. Try again in a moment.");
                            else if (paths.Length > 0) ThreadPool.QueueUserWorkItem(_ => { try { files.SendFiles(paths, toClipboard: false); } catch { } });
                        }
                        break;
                    case Protocol.InfoRequest when IsController(s):
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            string text;
                            try
                            {
                                var (controlling, listening) = Connected;
                                text = RemoteTools.Info(Updater.Current + (SecureAttention != null ? ", running as the Windows service" : "")) +
                                    "\nConnected: " + controlling + " controlling" + (listening > 0 ? ", " + listening + " listening" : "");
                            }
                            catch (Exception e) { text = "Problem: could not gather this PC's information: " + e.Message; } // never ends the host
                            try { s.Link.Send(s.Stream, Protocol.TextMessage(Protocol.Info, text)); } catch { }
                        });
                        break;
                    case Protocol.SpeedTestRequest when IsController(s):
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            _status("Running an internet speed test, as the controlling PC asked.");
                            string text = await SpeedTest.RunAsync();
                            try { s.Link.Send(s.Stream, Protocol.TextMessage(Protocol.SpeedResult, text)); } catch { }
                        });
                        break;
                    case Protocol.RestartPc when IsController(s):
                        _status("Restarting this PC, as the controlling PC asked.");
                        Leave(Protocol.LeavingRestarting);
                        // A normal restart: programs can still ask to save their work.
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false }); }
                        catch (Exception e) { Broadcast("The remote PC could not restart: " + e.Message); }
                        break;
                    case Protocol.AudioQuality when m.Length == 2:
                        s.Quality = Math.Min((int)m[1], Protocol.OpusSteps.Length - 1);
                        DiagLog.Write("host: " + s.Address + " asked for quality step " + s.Quality);
                        break;
                    case Protocol.FilePace when m.Length == 5:
                        Volatile.Write(ref s.Pace, Math.Max(16 << 10, BitConverter.ToUInt32(m, 1) * 1024.0));
                        break;
                    case Protocol.Features when m.Length >= 5:
                        s.PeerFeatures = BitConverter.ToUInt32(m, 1);
                        s.Ready = true;
                        break;
                    // Anything else is from a newer version: ignore it.
                }
            }
        }

        /// <summary>
        /// Never a stuck key. A controller holding keys tells this PC it is still there several times a
        /// second; if it goes silent for 1.5 seconds (the connection broke, or is badly delayed) its keys
        /// are let go at once, without waiting the 10 seconds it takes to call the connection dead.
        /// </summary>
        private void KeySafety()
        {
            while (!_stop)
            {
                Thread.Sleep(250);
                long now = Environment.TickCount64;
                foreach (var s in AllSessions())
                {
                    bool holding;
                    lock (s.Held) holding = s.Held.Count > 0;
                    if (holding && now - Volatile.Read(ref s.LastMessage) > 1500)
                    {
                        ReleaseHeld(s);
                        DiagLog.Write("host: " + s.Address + " went quiet while holding keys; let them go");
                    }
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
        private void End(Session s, string? why)
        {
            ReleaseHeld(s);
            if (s.Files is FileChannel f)
            {
                // Kept for the controller's next connection (a dropped one comes straight back),
                // so its transfers carry on; stopping hosting ends them. With nothing going there
                // is nothing to carry on: closed at once (it would otherwise hold two threads for a minute).
                if (_stop || !f.Busy) f.Dispose();
                else
                {
                    f.Detach();
                    if (!_kept.Contains(f)) _kept.Add(f);
                    while (_kept.Count > MaxControllers) { _kept[0].Dispose(); _kept.RemoveAt(0); }
                }
            }
            if (why == null) { try { s.Tcp.Dispose(); } catch { } return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Protocol.SendMessage(s.Link, s.Stream, why); } catch { }
                try { s.Tcp.Dispose(); } catch { }
            });
        }

        private readonly OpusBank _opus = new();
        private readonly bool[] _wanted = new bool[Protocol.OpusSteps.Length];

        /// <summary>
        /// Called on the capture thread with every 5 ms tick (null: silence). Each
        /// step someone uses is encoded once; every session gets its own step's
        /// packets, sealed with its own keys.
        /// </summary>
        private void SendAudio(uint seq, short[]? pcm)
        {
            var all = AllSessions();
            if (all.Length == 0) return;
            // Only to PCs that are really there: one that has gone quiet (no hello for 3
            // seconds) gets nothing, rather than a stream nobody hears until it times out.
            long now = Environment.TickCount64;
            Array.Clear(_wanted);
            foreach (var s in all) if (s.Ready && s.AudioTo != null && now - Volatile.Read(ref s.HeardAt) < 3000) _wanted[s.Quality] = true;
            _opus.Feed(seq, pcm, _wanted);
            foreach (var s in all)
            {
                var to = s.AudioTo;
                if (to == null || !s.Ready || now - Volatile.Read(ref s.HeardAt) >= 3000 || !_opus.Ready(s.Quality, out uint first, out int ticks, out var packet)) continue;
                // The packet number is also the audio nonce: never send one at or before what
                // this session already has (after a change of step, the new step's packet can
                // start earlier). The player fills the gap.
                if (s.SentAny && (int)(first - s.SentUntil) < 0) continue;
                s.SentUntil = first + (uint)ticks;
                s.SentAny = true;
                byte[] a = s.Audio;
                a[0] = Protocol.UdpOpus;
                BitConverter.TryWriteBytes(a.AsSpan(1), first);
                a[5] = (byte)ticks;
                packet.CopyTo(a.AsSpan(6));
                int payload = 1 + packet.Length;
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
                if ((d.Length != 9 && d.Length != 17) || d[0] != Protocol.UdpHello) continue;
                Session? s;
                s = Array.Find(AllSessions(), x => d.AsSpan(1, 8).SequenceEqual(x.Token));
                if (s == null) continue;
                var from = any.Address.IsIPv4MappedToIPv6 ? any.Address.MapToIPv4() : any.Address;
                if (!from.Equals(s.Address)) continue;
                s.AudioTo = new IPEndPoint(any.Address, any.Port);
                Volatile.Write(ref s.HeardAt, Environment.TickCount64);
                if (d.Length == 17)
                {
                    // Straight back, on the same path the sound takes: the client's audio ping.
                    d[8] = Protocol.UdpPong;
                    try { _udp.Send(d.AsSpan(8, 9), s.AudioTo); } catch { }
                }
            }
        }
    }
}
