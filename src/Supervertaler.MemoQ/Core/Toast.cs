using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// A line of text near the cursor for a second and a half.
    ///
    /// <para>The first press of the term shortcut has nothing to show for itself
    /// - the word is held, not added - and a shortcut that appears to do nothing
    /// is a shortcut nobody presses twice. This says what was caught.</para>
    ///
    /// <para>It must never take the focus: the user is typing in the grid and
    /// about to select the other half of the term. So it is a borderless window
    /// that refuses activation, on a thread of its own, closing itself on a
    /// timer.</para>
    /// </summary>
    internal static class Toast
    {
        private static readonly TimeSpan Lasts = TimeSpan.FromMilliseconds(1600);

        public static void Show(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // Its own thread with its own loop, so the caller - the thread
            // handling the key press - returns at once and the next press is not
            // held up behind this.
            var thread = new Thread(() =>
            {
                try
                {
                    using (var form = new ToastForm(text)) Application.Run(form);
                }
                catch (Exception ex) { PluginLog.Write("Toast failed", ex); }
            })
            { IsBackground = true, Name = "Supervertaler toast" };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private sealed class ToastForm : Form
        {
            private const int WS_EX_NOACTIVATE = 0x08000000;
            private const int WS_EX_TOOLWINDOW = 0x00000080;

            private readonly System.Windows.Forms.Timer _close;

            public ToastForm(string text)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.FromArgb(32, 32, 32);
                ForeColor = Color.White;
                Padding = new Padding(12, 9, 12, 9);

                var label = new Label
                {
                    Text = text,
                    AutoSize = true,
                    Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.5f),
                    ForeColor = Color.White
                };
                Controls.Add(label);

                // Measured, not guessed: the term is the user's word and may be
                // any length, and a fixed width would cut it off.
                using (var g = CreateGraphics())
                {
                    var size = TextRenderer.MeasureText(g, text, label.Font,
                        new Size(520, int.MaxValue), TextFormatFlags.WordBreak);
                    label.MaximumSize = new Size(520, 0);
                    ClientSize = new Size(size.Width + Padding.Horizontal, size.Height + Padding.Vertical);
                }

                PlaceNearCursor();

                _close = new System.Windows.Forms.Timer { Interval = (int)Lasts.TotalMilliseconds };
                _close.Tick += (s, e) => Close();
                _close.Start();
            }

            /// <summary>Below and right of the pointer, kept inside that screen.</summary>
            private void PlaceNearCursor()
            {
                var at = Cursor.Position;
                var screen = Screen.FromPoint(at).WorkingArea;

                var x = Math.Min(at.X + 16, screen.Right - Width - 8);
                var y = Math.Min(at.Y + 22, screen.Bottom - Height - 8);

                Location = new Point(Math.Max(screen.Left + 8, x), Math.Max(screen.Top + 8, y));
            }

            /// <summary>The whole point: memoQ keeps the caret and the keyboard.</summary>
            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var p = base.CreateParams;
                    p.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    return p;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _close != null) { _close.Stop(); _close.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
