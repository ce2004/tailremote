using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TailRemote
{
    /// <summary>
    /// Grabs the whole keyboard while controlling the remote PC: Windows key,
    /// Alt+Tab, the NVDA key, everything Windows lets a hook see. Ctrl+Alt+Del
    /// and Windows+L are the two it never sees; Windows keeps those for itself.
    ///
    /// Ctrl+Shift+Enter switches between the remote PC and this one. From this
    /// PC it only works while the TailRemote window has focus.
    ///
    /// The hook runs on its own thread with its own message loop, so a busy UI
    /// can never make Windows time the hook out. It is reinstalled each time
    /// remote control starts, which puts it ahead of NVDA's own hook.
    /// </summary>
    internal sealed class KeyCapture : IDisposable
    {
        private const int VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        private const uint WM_APP_REHOOK = 0x8001, WM_QUIT = 0x0012;

        private readonly Func<IntPtr> _ourWindow;
        private readonly Native.HookProc _proc; // kept alive for the hook's lifetime
        private readonly Thread _thread;
        private uint _threadId;
        private IntPtr _hook;
        private readonly ManualResetEventSlim _ready = new();

        private volatile Client? _client;
        private volatile bool _remote;
        private readonly HashSet<(uint Vk, bool Ext)> _held = new();
        private bool _swallowEnterUp;

        /// <summary>Raised on the hook thread with true when remote control starts, false when it stops.</summary>
        public event Action<bool>? ModeChanged;
        /// <summary>Raised when Ctrl+Shift+Enter is pressed with no connection.</summary>
        public event Action? NotConnected;

        public bool Remote => _remote;

        public KeyCapture(Func<IntPtr> ourWindow)
        {
            _ourWindow = ourWindow;
            _proc = Callback;
            _thread = new Thread(Loop) { IsBackground = true, Name = "TailRemote keyboard", Priority = ThreadPriority.Highest };
            _thread.Start();
            _ready.Wait();
        }

        public void SetClient(Client? c)
        {
            _client = c;
            if (c == null && _remote) Leave(announce: true);
        }

        private void Loop()
        {
            _threadId = Native.GetCurrentThreadId();
            Hook();
            _ready.Set();
            while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP_REHOOK) { Unhook(); Hook(); }
            }
            Unhook();
        }

        private void Hook() => _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(null), 0);
        private void Unhook() { if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }

        public void Dispose() => Native.PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        /// <summary>Starts or stops remote control, as if Ctrl+Shift+Enter were pressed.</summary>
        public void Toggle()
        {
            if (_remote) Leave(announce: true);
            else Enter();
        }

        private void Enter()
        {
            if (_client == null) { NotConnected?.Invoke(); return; }
            _remote = true;
            Native.PostThreadMessageW(_threadId, WM_APP_REHOOK, IntPtr.Zero, IntPtr.Zero);
            ModeChanged?.Invoke(true);
        }

        private void Leave(bool announce)
        {
            _remote = false;
            var c = _client;
            lock (_held)
            {
                // Let go of everything the remote PC still thinks is held down.
                if (c != null) foreach (var (vk, ext) in _held) c.SendKey((ushort)vk, 0, true, ext);
                _held.Clear();
            }
            c?.ReleaseAll();
            if (announce) ModeChanged?.Invoke(false);
        }

        private static bool Down(int vk) => Native.GetAsyncKeyState(vk) < 0;

        private bool HeldRemote(uint a, uint b) => _held.Contains((a, false)) || _held.Contains((b, false)) || _held.Contains((b, true));

        private unsafe IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0) return Native.CallNextHookEx(_hook, code, wParam, lParam);
            var k = *(Native.KBDLLHOOKSTRUCT*)lParam;
            bool up = (k.flags & Native.LLKHF_UP) != 0;
            bool ext = (k.flags & Native.LLKHF_EXTENDED) != 0;

            // Keys typed by software on this PC (NVDA itself, for one) stay here.
            if ((k.flags & Native.LLKHF_INJECTED) != 0) return Native.CallNextHookEx(_hook, code, wParam, lParam);

            if (k.vkCode == VK_RETURN && up && _swallowEnterUp) { _swallowEnterUp = false; return 1; }

            bool ctrl, shift, alt, win;
            lock (_held)
            {
                ctrl = Down(VK_CONTROL) || HeldRemote(0xA2, 0xA3);
                shift = Down(VK_SHIFT) || HeldRemote(0xA0, 0xA1);
                alt = Down(VK_MENU) || HeldRemote(0xA4, 0xA5);
                win = Down(VK_LWIN) || Down(VK_RWIN) || _held.Contains((VK_LWIN, true)) || _held.Contains((VK_RWIN, true));
            }
            bool toggle = k.vkCode == VK_RETURN && !up && ctrl && shift && !alt && !win;

            if (!_remote)
            {
                if (toggle && IsOurWindowFocused())
                {
                    _swallowEnterUp = true;
                    Enter();
                    return 1;
                }
                return Native.CallNextHookEx(_hook, code, wParam, lParam);
            }

            if (toggle)
            {
                _swallowEnterUp = true;
                Leave(announce: true);
                return 1;
            }

            var c = _client;
            if (c == null) return Native.CallNextHookEx(_hook, code, wParam, lParam);

            lock (_held)
            {
                if (up)
                {
                    // A key that went down before remote control started is
                    // released here, or this PC would think it is still held.
                    if (!_held.Remove((k.vkCode, ext))) return Native.CallNextHookEx(_hook, code, wParam, lParam);
                }
                else _held.Add((k.vkCode, ext));
            }
            c.SendKey((ushort)k.vkCode, (ushort)k.scanCode, up, ext);
            return 1;
        }

        private bool IsOurWindowFocused()
        {
            IntPtr fg = Native.GetForegroundWindow();
            return fg != IntPtr.Zero && Native.GetAncestor(fg, 3) == _ourWindow();
        }
    }
}
