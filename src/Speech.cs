using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// NVDA output through the controller client. NVDA only, no SAPI fallback.
    /// Calls go through a background thread because the client is a synchronous
    /// RPC into NVDA, and the keyboard hook must never wait on it.
    /// </summary>
    internal static unsafe class Speech
    {
        private static int _init; // 0 = not yet started; CompareExchange below makes the check-then-set atomic
        private static delegate* unmanaged[Stdcall]<char*, int> _speakText;
        private static delegate* unmanaged[Stdcall]<int> _cancelSpeech;

        private readonly record struct Utterance(string Message, bool Interrupt);
        private static readonly Queue<Utterance> Pending = new();
        private static readonly object Gate = new();

        public static void Init()
        {
            // Atomic check-then-set: two near-simultaneous callers must never both pass this,
            // which would double-load the native DLL and start two Pump threads on one queue.
            if (Interlocked.CompareExchange(ref _init, 1, 0) != 0) return;
            Load();
            if (_speakText == null) return;
            new Thread(Pump) { IsBackground = true, Name = "TailRemote speech" }.Start();
        }

        public static void Speak(string message, bool interrupt = true)
        {
            if (_speakText == null || string.IsNullOrEmpty(message)) return;
            lock (Gate)
            {
                if (interrupt) Pending.Clear();
                while (Pending.Count >= 8) Pending.Dequeue();
                Pending.Enqueue(new Utterance(message, interrupt));
                Monitor.Pulse(Gate);
            }
        }

        private static void Pump()
        {
            while (true)
            {
                Utterance u;
                lock (Gate)
                {
                    while (Pending.Count == 0) Monitor.Wait(Gate);
                    u = Pending.Dequeue();
                }
                try
                {
                    if (u.Interrupt && _cancelSpeech != null) _cancelSpeech();
                    fixed (char* p = u.Message) _speakText(p);
                }
                catch { }
            }
        }

        private static void Load()
        {
            string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "Arm64" : "64";
            string exact = "nvdaControllerClient" + arch + ".dll";
            var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, exact) };

            // A single-file build unpacks native libraries to its own folder.
            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string search)
                foreach (var dir in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    candidates.Add(Path.Combine(dir, exact));
            candidates.Add(exact);

            foreach (string c in candidates)
            {
                if (!NativeLibrary.TryLoad(c, out IntPtr lib)) continue;
                if (!NativeLibrary.TryGetExport(lib, "nvdaController_speakText", out IntPtr speak)) continue;
                NativeLibrary.TryGetExport(lib, "nvdaController_cancelSpeech", out IntPtr cancel);
                _speakText = (delegate* unmanaged[Stdcall]<char*, int>)speak;
                _cancelSpeech = cancel == IntPtr.Zero ? null : (delegate* unmanaged[Stdcall]<int>)cancel;
                return;
            }
        }
    }
}
