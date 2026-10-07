using System;
using Concentus;
using System.Collections.Concurrent;
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
        private FileChannel? _files;
        private WlanStreaming? _wifi;
        private uint _peerFeatures;

        public event Action<string>? Status;
        public event Action<string>? Disconnected;
        /// <summary>Clipboard text from the host. Raised on a network thread.</summary>
        public event Action<string>? ClipboardReceived;
        /// <summary>A sentence about a file that arrived. Raised on a network thread.</summary>
        public event Action<string>? FileMessage;
        /// <summary>True when the host let us in with its listen-only password: audio only, no keys.</summary>
        public bool ListenOnly { get; private set; }
        /// <summary>0 = full quality; above that, a lower sample rate while the connection struggles.</summary>
        public int AudioQuality { get; private set; }
        public int LastPingMs { get; private set; } = -1;
        /// <summary>Buffered audio plus the output device, in ms; -1 while nothing plays.</summary>
        public int AudioDelayMs => _player.DelayMs;

        private Client(TcpClient tcp, NetworkStream stream, UdpClient udp, byte[] token, Player player, SecureLink link)
        {
            _tcp = tcp; _stream = stream; _udp = udp; _token = token; _player = player; _link = link;
        }

        /// <summary>
        /// Connects and checks the password; throws with a readable message on failure.
        /// The player is the caller's and outlives the connection, so connecting never
        /// opens or closes an audio device.
        /// </summary>
        public static Client Connect(string address, int port, string password, Player player, Action<string> status)
        {
            var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
            tcp.Client.DualMode = true;
            tcp.Client.SendTimeout = 5000; // a host that stops taking data is dropped, never waited on forever
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
            UdpClient? udp = null;
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
                udp = new UdpClient(AddressFamily.InterNetworkV6);
                udp.Client.DualMode = true;
                udp.Client.ReceiveBufferSize = 1 << 20;
                Host.IgnoreUdpResets(udp.Client);
                udp.Connect(hostEp.Address, port);

                player.Reset();
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
            catch (System.IO.IOException) { udp?.Dispose(); tcp.Dispose(); throw new InvalidOperationException("The host closed the connection."); }
            catch { udp?.Dispose(); tcp.Dispose(); throw; }
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
            new Thread(SendLoop) { IsBackground = true, Name = "TailRemote send", Priority = ThreadPriority.AboveNormal }.Start();
            new Thread(TcpLoop) { IsBackground = true, Name = "TailRemote tcp" }.Start();
            new Thread(UdpLoop) { IsBackground = true, Name = "TailRemote udp", Priority = ThreadPriority.Highest }.Start();
            new Thread(Heartbeat) { IsBackground = true, Name = "TailRemote heartbeat" }.Start();
            new Thread(QualityLoop) { IsBackground = true, Name = "TailRemote quality" }.Start();
            _wifi = new WlanStreaming("client"); // steady Wi-Fi while connected
        }

        public void Dispose() => Close(null);

        private void Close(string? why)
        {
            if (Interlocked.Exchange(ref _closing, 1) == 1) return;
            DiagLog.Write("client: connection closed" + (why != null ? ": " + why : " by this PC"));
            _closed = true;
            _toSend.Release();
            try { _tcp.Dispose(); } catch { }
            try { _udp.Dispose(); } catch { }
            _files?.Dispose();
            _wifi?.Dispose();
            if (why != null) Disconnected?.Invoke(why);
        }

        // ---- Sending: one thread, keys first ----
        // Every send goes through this queue, so the keyboard hook and the window
        // never wait on the network. Keys and commands always go before clipboard
        // text: a large copy can never hold up a keystroke, and a hook that waits
        // gets removed by Windows, after which keys silently stay on this PC.

        private readonly ConcurrentQueue<byte[]> _urgent = new(), _bulk = new();
        private readonly SemaphoreSlim _toSend = new(0);

        private void Write(ReadOnlySpan<byte> message)
        {
            if (_closed) return;
            _urgent.Enqueue(message.ToArray());
            _toSend.Release();
        }

        private void SendLoop()
        {
            while (!_closed)
            {
                _toSend.Wait();
                if (_closed) return;
                if (!_urgent.TryDequeue(out var m) && !_bulk.TryDequeue(out m)) continue;
                try { _link.Send(_stream, m); }
                catch
                {
                    Close("Disconnected: the host stopped taking data.");
                    return;
                }
            }
        }

        /// <summary>Sends one key. Called from the keyboard hook: only queues it.</summary>
        public void SendKey(ushort vk, ushort scan, bool up, bool extended)
        {
            if (_closed) return;
            if (!up) Interlocked.Increment(ref _keysSent); // counted only: what is typed is never logged
            Span<byte> f = stackalloc byte[6];
            f[0] = Protocol.Key;
            BitConverter.TryWriteBytes(f[1..], vk);
            BitConverter.TryWriteBytes(f[3..], scan);
            f[5] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
            Write(f);
        }

        public void ReleaseAll() => Write(stackalloc byte[] { Protocol.ReleaseAll });

        /// <summary>How the sound is reduced right now, for the title; null at full quality.</summary>
        public string? ReducedSound => AudioQuality == 0 ? null : Protocol.OpusSteps[AudioQuality].Kbps + " kbit/s";

        public bool CanRestart => !ListenOnly && (_peerFeatures & Protocol.FeatureRestart) != 0;

        public bool CanSecureAttention => !ListenOnly && (_peerFeatures & Protocol.FeatureSecureAttention) != 0;

        /// <summary>Asks the host to send Ctrl+Alt+Del (only a host running as the service can).</summary>
        public void SendSecureAttention() => Write(stackalloc byte[] { Protocol.SecureAttention });

        /// <summary>Asks the host PC to restart.</summary>
        public void RestartHost() => Write(stackalloc byte[] { Protocol.RestartPc });

        /// <summary>Sends clipboard text to the host, behind any keys. Not for listeners.</summary>
        public void SendClipboard(string text)
        {
            if (_closed || ListenOnly || (_peerFeatures & Protocol.FeatureClipboard) == 0) return;
            _bulk.Enqueue(Protocol.TextMessage(Protocol.Clipboard, text));
            _toSend.Release();
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
                        Status?.Invoke("Host: " + System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1));
                    else if (m.Length >= 5 && m[0] == Protocol.Features)
                    {
                        _peerFeatures = BitConverter.ToUInt32(m, 1);
                        DiagLog.Write("client: host features " + _peerFeatures + (ListenOnly ? ", listen only" : ", control"));
                    }
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

        // ---- Receiving audio ----

        /// <summary>Test only (--audiotest): delay each audio packet by a random 0 to N ms, like a bumpy network.</summary>
        public static int TestJitterMs;
        /// <summary>Test only (--audiotest ... lagN): from 5 seconds in, every packet arrives N ms later, like Clumsy's lag.</summary>
        public static int TestLagMs;
        /// <summary>Test only (--audiotest ... stallN): 5 seconds in, nothing arrives for N ms, then all of it at once.</summary>
        public static int TestStallMs;
        /// <summary>Test only (--audiotest ... dropN): throws away N percent of audio packets at random, like Clumsy.</summary>
        public static int TestDropPercent;
        /// <summary>Test only (--audiotest ... bwN): from 3 seconds in, audio over N kbit/s is thrown away, like Clumsy's bandwidth limit.</summary>
        public static int TestKbps;
        private double _bucket;
        private long _bucketAt;
        private readonly SortedList<(long Due, long N), byte[]> _jitterQueue = new();
        private readonly Stopwatch _jitterClock = Stopwatch.StartNew();
        private long _jitterN;

        private void JitterLoop()
        {
            while (!_closed)
            {
                byte[]? next = null;
                lock (_jitterQueue)
                {
                    if (_jitterQueue.Count > 0 && _jitterQueue.Keys[0].Due <= _jitterClock.ElapsedMilliseconds)
                    {
                        next = _jitterQueue.Values[0];
                        _jitterQueue.RemoveAt(0);
                    }
                }
                if (next != null) SafeHandle(next); else Thread.Sleep(1);
            }
        }

        private void UdpLoop()
        {
            if (TestJitterMs > 0 || TestLagMs > 0 || TestStallMs > 0) new Thread(JitterLoop) { IsBackground = true, Name = "TailRemote test jitter" }.Start();
            var any = new IPEndPoint(IPAddress.IPv6Any, 0);
            while (!_closed)
            {
                byte[] d;
                try { d = _udp.Receive(ref any); }
                catch { if (_closed) return; continue; }
                Interlocked.Increment(ref _rxPackets);
                Interlocked.Add(ref _rxBytes, d.Length);
                if (TestDropPercent > 0 && Random.Shared.Next(100) < TestDropPercent) continue;
                if (TestKbps > 0 && _jitterClock.ElapsedMilliseconds > 3000)
                {
                    long t = _jitterClock.ElapsedMilliseconds;
                    _bucket = Math.Min(TestKbps * 1000 / 8 * 0.05, _bucket + (t - _bucketAt) * TestKbps / 8.0); // bytes; 50 ms of burst
                    _bucketAt = t;
                    if (_bucket < d.Length) continue;
                    _bucket -= d.Length;
                }
                if (TestJitterMs > 0 || TestLagMs > 0 || TestStallMs > 0)
                {
                    long at = _jitterClock.ElapsedMilliseconds;
                    if (at >= 5000 && at < 5000 + TestStallMs) at = 5000 + TestStallMs;
                    at += Random.Shared.Next(TestJitterMs + 1) + (at > 5000 ? TestLagMs : 0);
                    lock (_jitterQueue) _jitterQueue.Add((at, _jitterN++), d);
                    continue;
                }
                SafeHandle(d);
            }
        }

        /// <summary>A bad packet or a player hiccup must never take the whole app down.</summary>
        private void SafeHandle(byte[] d)
        {
            try { HandlePacket(d); }
            catch (Exception e) { DiagLog.Write("client: audio packet failed: " + e.Message); }
        }

        // ---- Opus, in order ----
        // Packets can arrive out of order (Wi-Fi, Windows' own loopback, Clumsy). They
        // wait in a small queue and are decoded strictly in order. When one is missing,
        // the queue waits for it as long as the buffer's jitter allowance (never more
        // than 40 ms): that time is already in the buffer, so waiting adds no delay.
        // Only if it still has not come is the gap filled in by Opus: rebuilt from the
        // next packet where Opus sent recovery data for it (the speech steps),
        // otherwise its own concealment, which continues the sound smoothly. A packet
        // that comes after its gap was filled is too late and is dropped.

        private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(Protocol.AudioRate, 2);
        private readonly short[] _pcm = new short[5760 * 2];
        private readonly SortedList<uint, (byte[] Data, double At)> _queue = new();
        private double _gapSince;
        private uint _next;
        private bool _haveNext;

        private void HandlePacket(byte[] d)
        {
            if (d.Length < Protocol.MinAudioPacketBytes || d[0] != Protocol.UdpOpus) return;
            if (!_link.OpenAudio(d, d.Length)) { Interlocked.Increment(ref _rxDamaged); return; } // changed on the way (or not for us): counts as lost
            Interlocked.Increment(ref _qPackets);
            Interlocked.Add(ref _qBytes, d.Length);
            int ticks = d[5];
            if (ticks < 1 || ticks > 24) return;
            uint seq = BitConverter.ToUInt32(d, 1);
            double now = Player.Now;
            if (_haveNext && (int)(seq - _next) < 0) { _player.CountLate(ticks); return; } // its moment has passed
            if (_haveNext && (int)(seq - _next) > 400) { _queue.Clear(); _haveNext = false; } // after a pause: start again from here
            _queue.TryAdd(seq, (d, now));
            while (_queue.Count > 0)
            {
                uint first = _queue.Keys[0];
                var (data, at) = _queue.Values[0];
                if (_haveNext && first != _next)
                {
                    // A gap before the first one waiting: wait for it, a little.
                    if (_gapSince == 0) _gapSince = at;
                    if (now - _gapSince < _player.ReorderWaitMs && _queue.Count < 32) break;
                }
                _queue.RemoveAt(0);
                _gapSince = 0;
                Deliver(data, at);
            }
        }

        /// <summary>Decodes and plays one packet that has already been opened (decrypted).</summary>
        private void Deliver(byte[] d, double arrivedAt)
        {
            uint seq = BitConverter.ToUInt32(d, 1);
            int ticks = d[5];
            var opus = d.AsSpan(6, d.Length - 6 - SecureLink.TagSize);
            if (_haveNext)
            {
                int gap = (int)(seq - _next);
                if (gap < 0) { _player.CountLate(ticks); return; } // its moment has passed
                if (gap > 0 && gap <= 24)
                {
                    if (gap == ticks && ticks >= 8)
                    {
                        // Only the packet before this one is missing: Opus's recovery data in this one rebuilds it.
                        int n = _decoder.Decode(opus, _pcm, gap * Protocol.TickFrames, true);
                        if (n > 0) _player.Push(_next, _pcm.AsSpan(0, n * 2), gap, concealed: true);
                    }
                    else
                    {
                        for (int left = gap; left > 0;)
                        {
                            int t = Math.Min(left, 12);
                            int n = _decoder.Decode(ReadOnlySpan<byte>.Empty, _pcm, t * Protocol.TickFrames, false);
                            if (n > 0) _player.Push(seq - (uint)left, _pcm.AsSpan(0, n * 2), t, concealed: true);
                            left -= t;
                        }
                    }
                }
            }
            int frames = _decoder.Decode(opus, _pcm, _pcm.Length / 2, false);
            _next = seq + (uint)ticks;
            _haveNext = true;
            if (frames > 0) _player.Push(seq, _pcm.AsSpan(0, frames * 2), ticks, arrivedAt: arrivedAt);
        }

        // ---- Bitrate steps ----

        private const int QualityTickMs = 200;
        private const int StepEveryMs = 750; // never faster than this: 128 to 32 kbit/s takes 3 seconds
        private readonly int[] _bad = new int[5], _sent = new int[5], _winBytes = new int[5], _winPackets = new int[5]; // the last second, by 0.2 s tick
        private int _badAt, _cleanTicks, _target, _ceiling, _failures;
        private long _noHelpUntil, _lastTargetAt, _lastMoveAt, _ceilingUntil, _lastFailAt;
        private double _lossBefore, _noHelpLoss;
        private int _qPackets, _qBytes;

        /// <summary>The bitrate step to hold no matter what, or -1 to follow the connection (Variable).</summary>
        public volatile int LockedStep = -1;

        /// <summary>Test only (--audiotest ... steps): hold the quality where the test puts it.</summary>
        public static bool TestHoldQuality;

        public void TestSetQuality(int q) => SetStep(q);

        private void SetStep(int q)
        {
            if (q == AudioQuality) return;
            DiagLog.Write("client: bitrate step " + AudioQuality + " to " + q + " (" + Protocol.OpusSteps[q].Kbps + " kbit/s)");
            AudioQuality = q;
            Write(stackalloc byte[] { Protocol.AudioQuality, (byte)q });
        }

        private void QualityLoop()
        {
            int tick = 0;
            while (!_closed)
            {
                Thread.Sleep(QualityTickMs);
                try { AdaptQuality(); } catch { }
                if (++tick % (1000 / QualityTickMs) == 0 && DiagLog.Enabled) LogSecond();
            }
        }

        /// <summary>
        /// Five times a second, looking at the last second.
        ///
        /// Locked: the chosen step, always. Variable: a target step, and the bitrate
        /// moves toward it one step at a time, never faster than one step every
        /// 0.75 s, so it changes over seconds rather than all at once.
        ///
        /// More than 10 percent of the sound never arriving (a capped or slow
        /// connection) sets the target to the best step that fits in what is really
        /// arriving, headers included. While it is that bad, it is judged again every
        /// second. A lower step that does not help (2 seconds on, the loss has not
        /// fallen) means random loss, which no bitrate fixes: back to the best, and
        /// that loss cannot lower it again for 30 seconds. Over 40 percent is always
        /// a starved connection.
        ///
        /// Clean for 0.6 s: the target goes back to the best and it climbs, step by
        /// step. Trouble on the way up sets a ceiling at the last step that worked,
        /// held 5 seconds the first time, then 10, 20, up to a minute, so it does not
        /// keep breaking up trying to go higher.
        /// </summary>
        private void AdaptQuality()
        {
            var (packets, lost, late) = _player.TakeStats();
            int bytes = Interlocked.Exchange(ref _qBytes, 0), arrived = Interlocked.Exchange(ref _qPackets, 0);
            if (TestHoldQuality) return;
            int locked = LockedStep;
            if (locked >= 0) { SetStep(Math.Min(locked, Protocol.OpusSteps.Length - 1)); return; }
            if (packets == 0) return; // silence: nothing to judge
            // Only sound that never came: what arrived late was counted lost first, then
            // late, and lateness is not a bandwidth problem a lower bitrate could fix.
            int bad = Math.Max(0, lost - late);
            _badAt = (_badAt + 1) % _bad.Length;
            _bad[_badAt] = bad;
            _sent[_badAt] = packets + bad;
            _winBytes[_badAt] = bytes;
            _winPackets[_badAt] = arrived;
            int badSecond = 0, sentSecond = 0, bytesSecond = 0, packetsSecond = 0;
            for (int i = 0; i < _bad.Length; i++) { badSecond += _bad[i]; sentSecond += _sent[i]; bytesSecond += _winBytes[i]; packetsSecond += _winPackets[i]; }
            double loss = sentSecond == 0 ? 0 : (double)badSecond / sentSecond;
            bool heavy = bad >= 4 && bad * 4 >= packets + bad;
            bool struggling = heavy || loss > 0.10;
            bool clean = loss < 0.07;
            long now = Environment.TickCount64;
            int q = AudioQuality, lowest = Protocol.OpusSteps.Length - 1;

            if (struggling && now < _noHelpUntil && loss < _noHelpLoss * 1.5 && loss < 0.4) struggling = false; // lowering did not help last time
            if (now - _lastFailAt > 30_000) _failures = 0;

            bool noHelp = _target > 0 && q > 0 && _lossBefore > 0.05 && loss < 0.4 && now - _lastTargetAt >= 2000 && loss >= _lossBefore * 0.9;
            if (noHelp)
            {
                // Lower did not help: random loss, not a full connection.
                _noHelpUntil = now + 30_000;
                _noHelpLoss = _lossBefore;
                _target = 0;
                _ceilingUntil = 0;
                _cleanTicks = 0;
            }
            else if (struggling && now - _lastTargetAt >= 1000 && q < lowest)
            {
                if (_target == 0) _lossBefore = loss;
                // The best step that fits in what really arrives (headers included), with room to spare.
                double wire = (bytesSecond + packetsSecond * (Protocol.PacketOverheadBytes - 22)) * 8 / 1000.0;
                int fit = lowest;
                for (int i = q + 1; i <= lowest; i++) if (Protocol.WireKbps(i) <= wire * 0.85) { fit = i; break; }
                _target = Math.Max(_target, fit);
                // Trouble: the step before this one is the most to try for a while.
                _ceiling = Math.Min(lowest, q + 1);
                _ceilingUntil = now + Math.Min(60_000, 5000 << Math.Min(_failures, 4));
                _failures++;
                _lastFailAt = now;
                _lastTargetAt = now;
                _cleanTicks = 0;
                Array.Clear(_bad); Array.Clear(_sent); Array.Clear(_winBytes); Array.Clear(_winPackets); // judge from here on
            }
            else if (!clean) _cleanTicks = 0;
            else if (++_cleanTicks >= 3) _target = now < _ceilingUntil ? Math.Max(_ceiling, 0) : 0;

            // One step at a time toward the target, never faster than every 0.75 s.
            if (_target != q && now - _lastMoveAt >= StepEveryMs)
            {
                _lastMoveAt = now;
                SetStep(q + Math.Sign(_target - q));
            }
        }

        // ---- The once-a-second log line (only while logging is on) ----
        private int _rxPackets, _rxBytes, _keysSent, _rxDamaged;

        private void LogSecond()
        {
            int packets = Interlocked.Exchange(ref _rxPackets, 0), bytes = Interlocked.Exchange(ref _rxBytes, 0), keys = Interlocked.Exchange(ref _keysSent, 0);
            int damaged = Interlocked.Exchange(ref _rxDamaged, 0);
            DiagLog.Write("client: " + _player.Diagnose() + ", ping " + LastPingMs + " ms, bitrate step " + AudioQuality +
                " (" + Protocol.OpusSteps[AudioQuality].Kbps + " kbit/s" + (LockedStep >= 0 ? ", locked" : "") + ")" +
                ", received " + packets + " packets, " + (bytes * 8 / 1000) + " kbit/s, damaged " + damaged +
                ", keys sent " + keys + ", playing on " + _player.DeviceInfo);
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
