using System;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The dialog behind memoQ's own Add Term button when Supervertaler is the
    /// term base being added to.
    ///
    /// <para>memoQ hands the plugin the text selected in the source and target
    /// cells and expects a URL to open. This is shown instead, on a thread of
    /// its own, and the URL memoQ gets back is nothing - the whole point is that
    /// a term decided in the grid lands in the project termbase without a
    /// browser, a second window or a trip to the editor.</para>
    ///
    /// <para>Deliberately small: the two terms, forbidden or not, a note, and
    /// the name of the termbase it is going into. Anything more is what the
    /// Terms window in the editor is for.</para>
    /// </summary>
    internal sealed class QuickAddForm : Form
    {
        private readonly TextBox _source = new TextBox();
        private readonly TextBox _target = new TextBox();
        private readonly CheckBox _forbidden = new CheckBox { Text = "Forbidden – this rendering must not be used", AutoSize = true };
        private readonly TextBox _notes = new TextBox();
        private readonly Label _into = new Label { AutoSize = false };
        private readonly Label _guessedNote = new Label { AutoSize = false };
        private readonly Label _swapped = new Label { AutoSize = false };
        private readonly Button _add = new Button { Text = "Add", DialogResult = DialogResult.OK, Width = 84, Height = 26 };

        public string Source => _source.Text.Trim();
        public string Target => _target.Text.Trim();
        public bool Forbidden => _forbidden.Checked;
        public string Notes => _notes.Text.Trim();

        /// <param name="termbaseName">Where the term will go, or null when no project termbase is ticked.</param>
        internal QuickAddForm(string sourceLang, string targetLang, string source, string target, string termbaseName)
            : this(sourceLang, targetLang, source, target, termbaseName, false) { }

        /// <summary>
        /// <paramref name="guessed"/> means the two halves were inferred rather
        /// than read off the segment, so they may be the wrong way round. The
        /// dialog then says so, in colour, and Swap is one click - because a term
        /// stored backwards matches nothing ever again and looks like a term that
        /// is simply never used.
        /// </summary>
        internal QuickAddForm(string sourceLang, string targetLang, string source, string target,
                              string termbaseName, bool guessed)
        {
            Font = SystemFonts.MessageBoxFont;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Add term – Supervertaler";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;

            // Shown from a thread memoQ does not own, so memoQ cannot parent it;
            // TopMost is what keeps it from opening behind the grid.
            TopMost = true;

            ClientSize = new Size(480, 250);
            var line = TextRenderer.MeasureText("Xg", Font).Height;
            var y = 12;

            Controls.Add(new Label { Text = Heading(sourceLang), AutoSize = true, Location = new Point(12, y), ForeColor = SystemColors.GrayText });
            Controls.Add(new Label { Text = Heading(targetLang), AutoSize = true, Location = new Point(246, y), ForeColor = SystemColors.GrayText });
            y += line + 3;

            _source.Text = source ?? string.Empty;
            _source.Location = new Point(12, y); _source.Width = 222;
            _target.Text = target ?? string.Empty;
            _target.Location = new Point(246, y); _target.Width = 222;
            Controls.Add(_source);
            Controls.Add(_target);

            // Between the two boxes, where it reads as "these two, the other way
            // round" rather than as an action on the dialog.
            var swap = new Button
            {
                Text = "\u21c4",
                Width = 26,
                Height = _source.Height,
                Location = new Point(234, y),
                TabStop = false
            };
            swap.Click += (s2, e2) =>
            {
                var was = _source.Text;
                _source.Text = _target.Text;
                _target.Text = was;
                _swapped.Visible = true;
            };
            new ToolTip().SetToolTip(swap, "Swap: put the " + Heading(sourceLang) + " term on the left.");
            Controls.Add(swap);

            y += _source.Height + 4;

            // Said only when it is true. A warning on every add is a warning
            // nobody reads, and most adds are read off the segment with certainty.
            _guessedNote.Location = new Point(12, y);
            _guessedNote.Width = 456;
            _guessedNote.Height = line + 2;
            _guessedNote.ForeColor = Color.Firebrick;
            _guessedNote.Text = "Which word is which was inferred, not read from the segment - check the order.";
            _guessedNote.Visible = guessed;
            Controls.Add(_guessedNote);

            _swapped.Location = new Point(12, y);
            _swapped.Width = 456;
            _swapped.Height = line + 2;
            _swapped.ForeColor = SystemColors.GrayText;
            _swapped.Text = "Swapped.";
            _swapped.Visible = false;
            Controls.Add(_swapped);

            y += line + 8;

            _forbidden.Location = new Point(12, y);
            Controls.Add(_forbidden);
            y += line + 10;

            Controls.Add(new Label { Text = "Note (optional)", AutoSize = true, Location = new Point(12, y), ForeColor = SystemColors.GrayText });
            y += line + 3;
            _notes.Location = new Point(12, y); _notes.Width = 456;
            Controls.Add(_notes);
            y += _notes.Height + 12;

            _into.Location = new Point(12, y);
            _into.Width = 456;
            _into.Height = line * 2;
            if (termbaseName != null)
            {
                _into.Text = "Into the project termbase: " + termbaseName;
                _into.ForeColor = SystemColors.ControlText;
            }
            else
            {
                _into.Text = "No project termbase is ticked for this project. Tick one under Termbases… in the prompt editor, then try again.";
                _into.ForeColor = Color.Firebrick;
                _add.Enabled = false;
            }
            Controls.Add(_into);

            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 84, Height = 26 };
            _add.Location = new Point(ClientSize.Width - 12 - 84 - 8 - 84, ClientSize.Height - 12 - 26);
            cancel.Location = new Point(ClientSize.Width - 12 - 84, ClientSize.Height - 12 - 26);
            Controls.Add(_add);
            Controls.Add(cancel);
            AcceptButton = _add;
            CancelButton = cancel;

            _source.TextChanged += (s, e) => Refresh();
            _target.TextChanged += (s, e) => Refresh();
            Refresh();

            // The target is the half the translator usually types: memoQ fills
            // the source from the selection and the target is often empty.
            (Target.Length == 0 ? _target : (Control)_source).Select();
        }

        private new void Refresh()
        {
            if (_into.ForeColor == Color.Firebrick) return;
            _add.Enabled = Source.Length > 0 && Target.Length > 0;
        }

        private static string Heading(string lang)
        {
            var code = (lang ?? string.Empty).Trim();
            if (code.Length == 0) return "Term";
            var name = global::Supervertaler.Core.LanguageCodes.EnglishName(code);
            return name.Equals(code, StringComparison.OrdinalIgnoreCase) ? code : name;
        }
    }
}
