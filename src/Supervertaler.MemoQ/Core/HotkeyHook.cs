using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// One keyboard shortcut, live only while memoQ is the window in front.
    ///
    /// <para>memoQ gives a plugin no way to register a shortcut of its own, so
    /// this is a low-level keyboard hook - the same mechanism that once ate the
    /// Escape key on this machine system-wide. Everything below is written
    /// against that memory: the hook swallows a key only when every one of its
    /// conditions holds, passes everything else on untouched, does no work of
    /// its own inside the callback, and dies with the process whatever happens.
    /// If this class has a bug, the worst it should do is fail to fire.</para>
    ///
    /// <list type="bullet">
    /// <item>The key is swallowed only when the foreground window belongs to
    /// this process. In every other application - Explorer, where Alt+Up is the
    /// parent folder - the key is passed straight on.</item>
    /// <item>The modifiers must match exactly. Alt+Up is not Ctrl+Alt+Up.</item>
    /// <item>The callback posts and returns. Windows silently unhooks a callback
    /// that takes too long, which would leave the shortcut dead with nothing to
    /// show for it, so the work happens on a worker thread.</item>
    /// <item>The delegate is held in a field. A collected delegate is the classic
    /// way for a hook to stop working later for no visible reason.</item>
    /// </list>
    /// </summary>
    internal static class HotkeyHook
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // Held for the lifetime of the process so the delegate is never collected
        // out from under the unmanaged hook.
        private static HookProc _proc;
        private static IntPtr _hook;
        private static Thread _thread;
        private static uint _ownProcess;

        private static int _key;
        private static Action _onPressed;
        private static Func<bool> _enabled;

        /// <summary>0 while nothing is being handled. The guard against a second
        /// press arriving while the first still has its dialog open.</summary>
        private static int _busy;

        /// <summary>
        /// Starts watching for <paramref name="key"/> with Alt held, calling
        /// <paramref name="onPressed"/> on a private STA thread each time it is
        /// pressed while memoQ is in front. <paramref name="enabled"/> is asked on
        /// every press, so the shortcut can be switched off without a restart.
        /// Calling this twice does nothing the second time.
        /// </summary>
        public static void Start(Keys key, Func<bool> enabled, Action onPressed)
        {
            if (_thread != null) return;
            if (onPressed == null) throw new ArgumentNullException(nameof(onPressed));

            _key = (int)key;
            _enabled = enabled ?? (() => true);
            _onPressed = onPressed;

            using (var me = Process.GetCurrentProcess()) _ownProcess = (uint)me.Id;

            // Its own thread, with its own message loop. A low-level hook is called
            // on the thread that installed it and only while that thread pumps
            // messages, so borrowing memoQ's UI thread would mean a shortcut that
            // stops answering whenever memoQ is busy - and would put this code in
            // the path of every keystroke typed into the grid.
            _thread = new Thread(Pump) { IsBackground = true, Name = "Supervertaler hotkey" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private static void Pump()
        {
            try
            {
                _proc = Callback;
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

                if (_hook == IntPtr.Zero)
                {
                    PluginLog.Write("Hotkey: SetWindowsHookEx failed, error " + Marshal.GetLastWin32Error()
                        + ". The shortcut is off for this session; nothing else is affected.");
                    return;
                }

                PluginLog.Write("Hotkey: watching Alt+" + (Keys)_key + " while memoQ is in front");
                Application.Run();
            }
            catch (ThreadInterruptedException) { }
            catch (Exception ex)
            {
                PluginLog.Write("Hotkey: the watcher stopped", ex);
            }
            finally
            {
                // Windows takes a hook down with the process, but memoQ can unload
                // and reload a plugin, and without this each cycle would leave the
                // previous hook behind in the chain.
                if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            }
        }

        /// <summary>Takes the hook down. Safe to call when it never started.</summary>
        public static void Stop()
        {
            var thread = _thread;
            if (thread == null) return;
            _thread = null;

            try
            {
                if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                thread.Interrupt();
            }
            catch (Exception ex) { PluginLog.Write("Hotkey: stopping failed", ex); }
        }

        private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // Nothing but the fast path until every condition is met. An exception
            // escaping here would travel into unmanaged code, so the body is
            // guarded and failure is read as "not our key".
            try
            {
                if (nCode >= 0 && IsOurs(wParam, lParam))
                {
                    Fire();
                    return (IntPtr)1;   // swallowed, so memoQ never sees it
                }
            }
            catch { }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsOurs(IntPtr wParam, IntPtr lParam)
        {
            var message = (int)wParam;

            // Alt+Up arrives as a system key. Both are checked because what counts
            // as a system key is not worth depending on.
            if (message != WM_KEYDOWN && message != WM_SYSKEYDOWN) return false;

            var key = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            if (key.vkCode != (uint)_key) return false;

            if (!Down(VK_MENU)) return false;
            if (Down(VK_CONTROL) || Down(VK_SHIFT) || Down(VK_LWIN) || Down(VK_RWIN)) return false;

            // Last, because it is the most expensive - and first in importance:
            // outside memoQ this hook does nothing at all.
            return IsMemoQInFront();
        }

        private static bool Down(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private static bool IsMemoQInFront()
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;

            uint pid;
            GetWindowThreadProcessId(window, out pid);
            return pid == _ownProcess;
        }

        private static void Fire()
        {
            if (!_enabled()) return;

            // One at a time. A second press while the dialog is open is dropped
            // rather than queued, and the flag is cleared in a finally, so a press
            // that throws cannot wedge the shortcut for the rest of the session.
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

            var worker = new Thread(() =>
            {
                try { _onPressed(); }
                catch (Exception ex) { PluginLog.Write("Hotkey: handling the press failed", ex); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            })
            { IsBackground = true, Name = "Supervertaler quick term" };

            worker.SetApartmentState(ApartmentState.STA);   // the clipboard and the dialog both need it
            worker.Start();
        }
    }
}
