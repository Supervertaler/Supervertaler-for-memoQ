using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// What the user has selected in the memoQ cell they are typing in.
    ///
    /// <para>memoQ's grid is its own control and tells a plugin nothing about
    /// selections - the one menu item that does, Add Selection As Alternative,
    /// reads them from memoQ's own internals. Reaching into those by reflection
    /// would work until the next memoQ release and then fail quietly, which is
    /// the worst way for a terminology feature to fail. So this asks the way any
    /// program may: it sends Ctrl+C and reads the clipboard.</para>
    ///
    /// <para>The clipboard is the user's, so it is put back. What can be put
    /// back is the ordinary set - text, formatted text, files, a bitmap - and a
    /// format outside that set is reported rather than silently dropped.</para>
    /// </summary>
    internal static class CellSelection
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            // The union is as wide as its widest member - the mouse input - and
            // the struct must be that size or SendInput rejects every call.
            [FieldOffset(0)] public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_C = 0x43;

        /// <summary>How long to wait for the user to let go of Alt before typing
        /// Ctrl+C. Held down, it would arrive as Ctrl+Alt+C.</summary>
        private static readonly TimeSpan AltRelease = TimeSpan.FromMilliseconds(700);

        /// <summary>
        /// How long to wait for memoQ to answer Ctrl+C, and again if it does not.
        ///
        /// <para>Two attempts because the first copy of a session is measurably
        /// slower than the rest: this thread is new, the clipboard has not been
        /// touched from this process yet, and memoQ builds several formats for
        /// one copied word. The first press after memoQ started was the one press
        /// that failed in testing, and it reported "nothing was selected" over a
        /// word that was plainly selected - a wrong answer rather than a slow
        /// one, which is the kind worth spending a second on.</para>
        /// </summary>
        private static readonly TimeSpan[] CopyAnswer =
        {
            TimeSpan.FromMilliseconds(900),
            TimeSpan.FromMilliseconds(1200)
        };

        /// <summary>What a press found, and when it found nothing, why not.</summary>
        internal sealed class Capture
        {
            public string Text;

            /// <summary>Empty when <see cref="Text"/> was found. Written to the log
            /// otherwise: "nothing was selected" and "memoQ was too slow to say"
            /// look identical to the user and must not look identical to us.</summary>
            public string Why = "";
        }

        /// <summary>
        /// The selected text of whichever memoQ cell has the caret. Must be called
        /// on an STA thread.
        /// </summary>
        public static Capture Read()
        {
            // The shortcut is Alt+Up and the hook swallowed it, but Alt itself is
            // still physically down: the user has not let go yet. Ctrl+C sent now
            // is Ctrl+Alt+C, which memoQ may well have a use for.
            if (!WaitForAltRelease())
            {
                // Still held after the grace period - a key repeat, or a hand
                // resting on it. Tell Windows the modifier is up, so what follows
                // is read as a plain Ctrl+C. The user's own key-up afterwards is
                // harmless.
                Send(VK_MENU, up: true);
            }

            using (var clipboard = new ClipboardGuard())
            {
                var answered = false;

                foreach (var patience in CopyAnswer)
                {
                    // The sequence number rather than the content: copying the same
                    // word twice running leaves the text identical, and a comparison
                    // on content would read the second one as "memoQ did not answer".
                    var before = GetClipboardSequenceNumber();

                    Send(VK_CONTROL, up: false);
                    Send(VK_C, up: false);
                    Send(VK_C, up: true);
                    Send(VK_CONTROL, up: true);

                    if (Wait(() => GetClipboardSequenceNumber() != before, patience)) { answered = true; break; }
                }

                if (!answered)
                    return new Capture { Why = "memoQ did not answer Ctrl+C, so most likely nothing was selected" };

                var text = TryGetText();

                if (text == null)
                    return new Capture { Why = "memoQ answered Ctrl+C but put no text on the clipboard" };

                if (string.IsNullOrWhiteSpace(text))
                    return new Capture { Why = "the selection was blank" };

                return new Capture { Text = text.Trim() };
            }
        }

        private static bool WaitForAltRelease()
        {
            return Wait(() => (GetAsyncKeyState(VK_MENU) & 0x8000) == 0, AltRelease);
        }

        /// <summary>Polls <paramref name="until"/> for up to <paramref name="limit"/>.</summary>
        private static bool Wait(Func<bool> until, TimeSpan limit)
        {
            var deadline = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < deadline)
            {
                if (until()) return true;
                Thread.Sleep(15);
            }
            return until();
        }

        private static void Send(ushort key, bool up)
        {
            var input = new INPUT { type = INPUT_KEYBOARD };
            input.u.ki = new KEYBDINPUT { wVk = key, dwFlags = up ? KEYEVENTF_KEYUP : 0 };

            if (SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) == 0)
                PluginLog.Write("Quick term: SendInput refused, error " + Marshal.GetLastWin32Error());
        }

        private static string TryGetText()
        {
            // The clipboard belongs to whatever process last touched it, so any
            // call can fail because someone else has it open for a moment.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
                catch (ExternalException) { Thread.Sleep(20); }
            }
            return null;
        }

        /// <summary>
        /// The clipboard as it was, restored when this is disposed - including
        /// when what happened in between threw.
        /// </summary>
        private sealed class ClipboardGuard : IDisposable
        {
            /// <summary>
            /// The formats worth carrying across. Everything a translator has on
            /// the clipboard mid-job is in this list; a custom format from some
            /// other application is not, and is named in the log rather than
            /// restored, because copying an arbitrary format means holding an
            /// arbitrary amount of memory for it.
            /// </summary>
            private static readonly string[] Carried =
            {
                DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf,
                DataFormats.Html, DataFormats.FileDrop, DataFormats.Bitmap
            };

            private readonly DataObject _saved;

            public ClipboardGuard()
            {
                try
                {
                    var current = Clipboard.GetDataObject();
                    if (current == null) return;

                    var formats = current.GetFormats(false);
                    if (formats == null || formats.Length == 0) return;

                    var kept = new DataObject();
                    var dropped = new List<string>();
                    var any = false;

                    foreach (var format in formats)
                    {
                        if (Array.IndexOf(Carried, format) < 0) { dropped.Add(format); continue; }

                        try
                        {
                            var data = current.GetData(format);
                            if (data == null) continue;
                            kept.SetData(format, data);
                            any = true;
                        }
                        catch (Exception ex) { PluginLog.Write("Quick term: could not hold " + format, ex); }
                    }

                    if (dropped.Count > 0 && !any)
                        PluginLog.Write("Quick term: the clipboard held only " + string.Join(", ", dropped.ToArray())
                            + ", which cannot be put back. It has been replaced by the selected term.");

                    _saved = any ? kept : null;
                }
                catch (Exception ex) { PluginLog.Write("Quick term: could not read the clipboard", ex); }
            }

            public void Dispose()
            {
                try
                {
                    if (_saved != null) Clipboard.SetDataObject(_saved, true, 5, 30);
                    else Clipboard.Clear();
                }
                catch (Exception ex) { PluginLog.Write("Quick term: could not put the clipboard back", ex); }
            }
        }
    }
}
