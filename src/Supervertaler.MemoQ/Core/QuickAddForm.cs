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

            // Width is a choice; the height is measured at the end from where the
            // controls actually ended up.
            ClientSize = new Size(480, 250);
            var line = TextRenderer.MeasureText("Xg", Font).Height;
            var y = 12;

            Controls.Add(new Label { Text = Heading(sourceLang), AutoSize = true, Location = new Point(12, y), ForeColor = SystemColors.GrayText });
            Controls.Add(new Label { Text = Heading(targetLang), AutoSize = true, Location = new Point(246, y), ForeColor = SystemColors.GrayText });
            y += line + 3;

            // Measured from the client width rather than written as four fixed
            // numbers. The first version put the swap button at x=234 with a
            // width of 26 while the right-hand box began at 246, so it sat on
            // top of it.
            var margin = 12;
            var gap = 34;                                   // room for the swap button
            var box = (ClientSize.Width - margin * 2 - gap) / 2;
            var rightBox = margin + box + gap;

            _source.Text = source ?? string.Empty;
            _source.Location = new Point(margin, y); _source.Width = box;
            _target.Text = target ?? string.Empty;
            _target.Location = new Point(rightBox, y); _target.Width = box;
            Controls.Add(_source);
            Controls.Add(_target);

            // Between the two boxes, where it reads as "these two, the other way
            // round" rather than as an action on the dialog.
            var swap = new Button { Text = "\u21c4", TabStop = false };

            // Even one glyph has to be measured. A button is not as wide as the
            // number somebody typed; it is as wide as its own text plus its
            // border, and an arrow in this font is wider than 28 pixels.
            swap.Width = Math.Max(gap - 6, swap.PreferredSize.Width);
            swap.Height = Math.Max(_source.Height, swap.PreferredSize.Height);
            swap.Location = new Point(margin + box + 3, y);
            swap.Click += (s2, e2) =>
            {
                var was = _source.Text;
                _source.Text = _target.Text;
                _target.Text = was;
                _guessedNote.Visible = false;
                _swapped.Visible = true;
            };
            new ToolTip().SetToolTip(swap, "Swap: put the " + Heading(sourceLang) + " term on the left.");
            Controls.Add(swap);

            y += _source.Height + 4;

            // Said only when it is true. A warning on every add is a warning
            // nobody reads, and most adds are read off the segment with certainty.
            // Wrapped and measured. The first wording was both confusing and too
            // long for one line, so it was cut off mid-sentence - which is a poor
            // way to ask somebody to check something.
            //
            // It says what to DO first. "Inferred, not read from the segment" is
            // an accurate description of the cause and meant nothing to the person
            // reading it, who wanted to know whether to act.
            _guessedNote.AutoSize = true;
            _guessedNote.MaximumSize = new Size(ClientSize.Width - margin * 2, 0);
            _guessedNote.Location = new Point(margin, y);
            _guessedNote.ForeColor = Color.Firebrick;
            _guessedNote.Text = "Check these are the right way round. This segment did not say which word "
                              + "was which, so the order was worked out rather than read.";
            _guessedNote.Visible = guessed;
            Controls.Add(_guessedNote);
            var noteHeight = _guessedNote.PreferredSize.Height;

            _swapped.AutoSize = true;
            _swapped.Location = new Point(margin, y);
            _swapped.ForeColor = SystemColors.GrayText;
            _swapped.Text = "Swapped \u2013 now " + Heading(sourceLang) + " on the left.";
            _swapped.Visible = false;
            Controls.Add(_swapped);

            // The warning is the taller of the two and only shown sometimes, so
            // the space below follows whichever is actually there.
            y += (guessed ? noteHeight : 0) + 8;

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

            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };

            // Measured, and the bottom edge follows them. The fixed 84 by 26 with
            // the dialog fixed at 250 tall clipped both buttons' text at a larger
            // interface font - the third time that same shape was reported.
            var buttonWidth = Math.Max(84, Math.Max(_add.PreferredSize.Width, cancel.PreferredSize.Width) + 16);
            var buttonHeight = Math.Max(26, Math.Max(_add.PreferredSize.Height, cancel.PreferredSize.Height));

            _add.Size = new Size(buttonWidth, buttonHeight);
            cancel.Size = new Size(buttonWidth, buttonHeight);

            y += 6;
            cancel.Location = new Point(ClientSize.Width - margin - buttonWidth, y);
            _add.Location = new Point(cancel.Left - 8 - buttonWidth, y);

            ClientSize = new Size(ClientSize.Width, y + buttonHeight + margin);
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
