using System;
using System.IO;
using System.Security.Cryptography;

namespace TailRemote
{
    /// <summary>
    /// Encrypts everything after the password check, so a connection is private
    /// with or without Tailscale.
    ///
    /// Each direction and each transport gets its own AES-GCM key, derived with
    /// HKDF from the password key and both sides' login nonces, so keys are new
    /// for every connection. TCP frames use a counter as the nonce (TCP keeps
    /// order). Audio packets use their sequence number, which never repeats in
    /// a session; the packet type and number travel in the clear but are
    /// authenticated. A frame that fails to decrypt ends the connection; an
    /// audio packet that fails is dropped.
    /// </summary>
    internal sealed class SecureLink : IDisposable
    {
        public const int TagSize = 16;

        private readonly AesGcm _send, _recv, _audio;
        private readonly object _sendLock = new();
        private ulong _sendCounter, _recvCounter;
        private readonly byte[] _recvHead = new byte[4];

        /// <summary>purpose keeps a second connection (files) on keys of its own.</summary>
        public SecureLink(byte[] key, byte[] hostNonce, byte[] clientNonce, bool isHost, string purpose = "")
        {
            byte[] salt = new byte[32];
            hostNonce.CopyTo(salt, 0);
            clientNonce.CopyTo(salt, 16);
            byte[] Derive(string info) => HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, System.Text.Encoding.ASCII.GetBytes(purpose + info));
            byte[] toHost = Derive("TailRemote tcp to host"), toClient = Derive("TailRemote tcp to client");
            _send = new AesGcm(isHost ? toClient : toHost, TagSize);
            _recv = new AesGcm(isHost ? toHost : toClient, TagSize);
            _audio = new AesGcm(Derive("TailRemote audio"), TagSize);
        }

        public void Dispose()
        {
            _send.Dispose();
            _recv.Dispose();
            _audio.Dispose();
        }

        private static void CounterNonce(Span<byte> nonce, ulong counter)
        {
            nonce.Clear();
            BitConverter.TryWriteBytes(nonce[4..], counter);
        }

        public const int MaxMessage = 8 * 1024 * 1024;

        /// <summary>Sends one message as [u32 length][ciphertext][tag]. Safe from any thread.</summary>
        public void Send(Stream s, ReadOnlySpan<byte> message)
        {
            if (message.Length > MaxMessage) throw new ArgumentException("Message too large.");
            byte[] frame = new byte[4 + message.Length + TagSize];
            BitConverter.TryWriteBytes(frame.AsSpan(0, 4), message.Length);
            Span<byte> nonce = stackalloc byte[12];
            lock (_sendLock)
            {
                CounterNonce(nonce, _sendCounter++);
                _send.Encrypt(nonce, message, frame.AsSpan(4, message.Length), frame.AsSpan(4 + message.Length, TagSize));
                s.Write(frame);
            }
        }

        /// <summary>
        /// Encrypts one message into a ready-to-send frame, without sending it. The
        /// frames must then be written in the order they were sealed, and Send must
        /// not be used on this link at the same time. (Files: one thread seals while
        /// another writes, so the network never waits for the encryption.)
        /// </summary>
        public byte[] Seal(ReadOnlySpan<byte> message)
        {
            if (message.Length > MaxMessage) throw new ArgumentException("Message too large.");
            byte[] frame = new byte[4 + message.Length + TagSize];
            BitConverter.TryWriteBytes(frame.AsSpan(0, 4), message.Length);
            Span<byte> nonce = stackalloc byte[12];
            lock (_sendLock)
            {
                CounterNonce(nonce, _sendCounter++);
                _send.Encrypt(nonce, message, frame.AsSpan(4, message.Length), frame.AsSpan(4 + message.Length, TagSize));
            }
            return frame;
        }

        /// <summary>Reads and decrypts one message. Only one thread may receive.</summary>
        public byte[] Receive(Stream s)
        {
            Protocol.ReadExactly(s, _recvHead);
            int len = BitConverter.ToInt32(_recvHead);
            if (len < 0 || len > MaxMessage) throw new InvalidOperationException("The connection sent something too large.");
            byte[] body = new byte[len + TagSize];
            Protocol.ReadExactly(s, body);
            byte[] message = new byte[len];
            Span<byte> nonce = stackalloc byte[12];
            CounterNonce(nonce, _recvCounter++);
            try { _recv.Decrypt(nonce, body.AsSpan(0, len), body.AsSpan(len, TagSize), message); }
            catch (CryptographicException) { throw new InvalidOperationException("The connection was tampered with, so it was closed."); }
            return message;
        }

        /// <summary>Seals an audio packet in place: [type][u32 seq][payload][tag]. Capture thread only.</summary>
        public void SealAudio(byte[] packet, int payloadLength)
        {
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            packet.AsSpan(1, 4).CopyTo(nonce);
            var payload = packet.AsSpan(5, payloadLength);
            _audio.Encrypt(nonce, payload, payload, packet.AsSpan(5 + payloadLength, TagSize), packet.AsSpan(0, 5));
        }

        /// <summary>Opens an audio packet in place; false if it was forged or damaged. Receive thread only.</summary>
        public bool OpenAudio(byte[] packet, int length)
        {
            int payloadLength = length - 5 - TagSize;
            if (payloadLength < 0) return false;
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            packet.AsSpan(1, 4).CopyTo(nonce);
            var payload = packet.AsSpan(5, payloadLength);
            try { _audio.Decrypt(nonce, payload, packet.AsSpan(5 + payloadLength, TagSize), payload, packet.AsSpan(0, 5)); return true; }
            catch (CryptographicException) { return false; }
        }
    }
}
