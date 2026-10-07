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
        private const uint WM_APP_REHOOK = 0x8001, WM_QUIT = 0x0012, WM_TIMER = 0x0113;

        [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
        private bool _away;

        private readonly Func<IntPtr> _ourWindow;
        private readonly Native.HookProc _proc; // kept alive for the hook's lifetime
        private readonly Thread _thread;
        private uint _threadId;
        private IntPtr _hook;
        private readonly ManualResetEventSlim _ready = new();

        private volatile Client? _client;
        private volatile bool _remote;
        private readonly HashSet<(uint Vk, bool Ext)> _held = new();
        private bool _swallowEnterUp, _enterHeld, _swallowEndUp;
        private const int VK_END = 0x23;

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
            SetTimer(IntPtr.Zero, IntPtr.Zero, 250, IntPtr.Zero);
            _ready.Set();
            while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP_REHOOK) { Unhook(); Hook(); }
                else if (msg.message == WM_TIMER && _remote) CheckDesktop();
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

        /// <summary>
        /// Control Alt Delete or locking this PC switches it to the secure desktop,
        /// where the hook sees nothing, not even the keys being let go. Whatever
        /// was held (Control and Alt, at least) is released on the remote PC, or
        /// they would stay down there.
        /// </summary>
        private void CheckDesktop()
        {
            IntPtr d = NativeService.OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
            bool ours = d != IntPtr.Zero && NativeService.DesktopName(d) == "Default";
            if (d != IntPtr.Zero) NativeService.CloseDesktop(d);
            if (ours) { _away = false; return; }
            if (_away) return;
            _away = true;
            var c = _client;
            lock (_held)
            {
                if (c != null) foreach (var (vk, ext) in _held) c.SendKey((ushort)vk, 0, true, ext);
                _held.Clear();
            }
            c?.ReleaseAll();
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

            bool enterRepeat = false;
            if (k.vkCode == VK_RETURN)
            {
                enterRepeat = !up && _enterHeld;
                _enterHeld = !up;
            }
            if (k.vkCode == VK_RETURN && up && _swallowEnterUp) { _swallowEnterUp = false; return 1; }
            // Holding Ctrl+Shift+Enter must not flip back and forth with every repeat.
            if (enterRepeat && _swallowEnterUp) return 1;

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

            // Ctrl+Alt+End stands for Ctrl+Alt+Del, which never reaches a hook (as in Remote Desktop).
            if (k.vkCode == VK_END && up && _swallowEndUp) { _swallowEndUp = false; return 1; }
            if (k.vkCode == VK_END && !up && ctrl && alt && !shift && !win && _client is Client sas)
            {
                _swallowEndUp = true;
                if (!_held.Contains((VK_END, true))) sas.SendSecureAttention();
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
