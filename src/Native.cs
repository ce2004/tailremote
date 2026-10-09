using System;
using System.Runtime.InteropServices;

namespace TailRemote
{
    internal static class Native
    {
        // ---- Audio thread priority ----

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);

        /// <summary>
        /// Opts this process out of Windows' power throttling (EcoQoS), which would
        /// otherwise put its audio and network threads on slow, power-saving cores
        /// and make the sound run dry. Also asks for 1 ms timer precision.
        /// </summary>
        public static void FullSpeed()
        {
            try
            {
                var s = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1 /* EXECUTION_SPEED */, StateMask = 0 /* off */ };
                SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref s, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                timeBeginPeriod(1);
            }
            catch { }
        }

        /// <summary>Puts the calling thread in the "Pro Audio" scheduling class.</summary>
        public static void ProAudioThread()
        {
            uint i = 0;
            try { AvSetMmThreadCharacteristicsW("Pro Audio", ref i); } catch { }
        }

        // ---- Keyboard hook ----

        public const int WH_KEYBOARD_LL = 13;
        public const int LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

        // ---- Key injection ----

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        private const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;

        /// <summary>Set by the service's agent: send keys to whichever desktop is showing, secure desktop included.</summary>
        public static bool FollowInputDesktop;

        [ThreadStatic] private static IntPtr _desktop, _originalDesktop;
        [ThreadStatic] private static string? _desktopName;

        /// <summary>Moves this thread to the desktop that has the keyboard (Default, Winlogon for the lock screen and UAC).</summary>
        private static void FollowDesktop()
        {
            IntPtr d = NativeService.OpenInputDesktop(0, false, 0x02000000 /* MAXIMUM_ALLOWED */);
            if (d == IntPtr.Zero) return;
            string name = NativeService.DesktopName(d);
            if (_desktop != IntPtr.Zero && name == _desktopName) { NativeService.CloseDesktop(d); return; }
            if (_originalDesktop == IntPtr.Zero) _originalDesktop = NativeService.GetThreadDesktop(GetCurrentThreadId()); // never closed
            if (NativeService.SetThreadDesktop(d))
            {
                if (_desktop != IntPtr.Zero) NativeService.CloseDesktop(_desktop);
                _desktop = d;
                _desktopName = name;
            }
            else NativeService.CloseDesktop(d);
        }

        /// <summary>Call before a thread that sent keys ends: puts it back on its own desktop and closes the one it followed.</summary>
        public static void LeaveDesktop()
        {
            if (_desktop == IntPtr.Zero) return;
            if (!NativeService.SetThreadDesktop(_originalDesktop)) return; // still in use: leave it
            NativeService.CloseDesktop(_desktop);
            _desktop = IntPtr.Zero;
            _desktopName = null;
        }

        /// <summary>Runs a console program with no window; its exit code, or -1 if it would not start.</summary>
        public static int RunHidden(string file, string args)
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args)
                    { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
                var errors = p.StandardError.ReadToEndAsync(); // both pipes are read, so a chatty program never blocks on a full one
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                errors.Wait();
                return p.ExitCode;
            }
            catch { return -1; }
        }

        public static bool SendKey(ushort vk, ushort scan, bool up, bool extended)
        {
            if (FollowInputDesktop) FollowDesktop();
            var input = new INPUT { type = 1 };
            input.u.ki.wVk = vk;
            input.u.ki.wScan = scan;
            input.u.ki.dwFlags = (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
            return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
        }

        // ---- DPAPI ----

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

        /// <summary>DPAPI. machine: any process on this PC can open it (the service's settings, kept in an admin-only folder).</summary>
        public static byte[] Protect(byte[] data, bool protect, bool machine = false)
        {
            int flags = 1 | (machine ? 4 : 0); // UI forbidden, local machine
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var inBlob = new DATA_BLOB { cbData = data.Length, pbData = h.AddrOfPinnedObject() };
                bool ok = protect
                    ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out var outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out outBlob);
                if (!ok) throw new InvalidOperationException("DPAPI failed: " + Marshal.GetLastWin32Error());
                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, result.Length);
                LocalFree(outBlob.pbData);
                return result;
            }
            finally { h.Free(); }
        }
    }
}
