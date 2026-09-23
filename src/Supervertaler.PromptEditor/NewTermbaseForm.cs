using System;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Core;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Name and language pair for a termbase about to be created - either empty,
    /// from the New button, or prefilled from a file's own header, from Import.
    ///
    /// <para>The languages are typed as codes, because that is what every file
    /// and every host uses, and the form says back what it understood -
    /// <c>dut-NL</c> is echoed as <c>Dutch</c> - so a typo is visible before it
    /// becomes a termbase nobody's job can match. An unrecognised code is
    /// allowed through, on the same principle as everywhere else in the
    /// language handling: it is kept as typed rather than guessed at.</para>
    /// </summary>
    internal sealed class NewTermbaseForm : Form
    {
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _source = new TextBox();
        private readonly TextBox _target = new TextBox();
        private readonly Label _sourceEcho = new Label();
        private readonly Label _targetEcho = new Label();
        private readonly Button _ok = new Button();

        public string TermbaseName => _name.Text.Trim();
        public string SourceLang => _source.Text.Trim();
        public string TargetLang => _target.Text.Trim();

        internal NewTermbaseForm(string title, string name, string sourceLang, string targetLang)
        {
            Font = Ui.Default;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AppIcon.Apply(this);

            // Width only. The height is measured at the end, from what the
            // controls actually came out as: it was fixed at 214 with the buttons
            // placed 40 up from the bottom, so a font that made a button taller
            // than 40 pushed it through the bottom edge. Reported clipped.
            ClientSize = new Size(440, 214);

            var line = TextRenderer.MeasureText("Termbase", Font).Height;
            var y = 14;

            // Added, then measured. `line` is the height of the TEXT; an AutoSize
            // label is that plus its leading, so advancing by `line` put every
            // heading a couple of pixels into the box beneath it.
            var nameHead = new Label { Text = "Name", AutoSize = true, Location = new Point(12, y) };
            Controls.Add(nameHead);
            y += nameHead.Height + 4;
            _name.Text = name ?? string.Empty;
            _name.Location = new Point(12, y);
            _name.Width = ClientSize.Width - 24;
            Controls.Add(_name);
            y += _name.Height + 14;

            var half = (ClientSize.Width - 24 - 12) / 2;

            var srcHead = new Label { Text = "Source language", AutoSize = true, Location = new Point(12, y) };
            var tgtHead = new Label { Text = "Target language", AutoSize = true, Location = new Point(12 + half + 12, y) };
            Controls.Add(srcHead);
            Controls.Add(tgtHead);
            y += Math.Max(srcHead.Height, tgtHead.Height) + 4;

            _source.Text = sourceLang ?? string.Empty;
            _source.Location = new Point(12, y);
            _source.Width = half;
            _target.Text = targetLang ?? string.Empty;
            _target.Location = new Point(12 + half + 12, y);
            _target.Width = half;
            Controls.Add(_source);
            Controls.Add(_target);
            y += _source.Height + 4;

            // What the code was taken to mean, in words, under each box.
            // Height left to the label rather than set to `line`, which is six
            // pixels short of what one of these needs and clipped the descenders
            // off whatever language name was echoed back.
            //
            // AutoSize with a width cap rather than AutoSize off: the echo is a
            // language name and can be longer than half this dialog.
            _sourceEcho.AutoSize = true;
            _sourceEcho.MaximumSize = new Size(half, 0);
            _sourceEcho.Location = new Point(12, y);
            _sourceEcho.ForeColor = SystemColors.GrayText;
            _targetEcho.AutoSize = true;
            _targetEcho.MaximumSize = new Size(half, 0);
            _targetEcho.Location = new Point(12 + half + 12, y);
            _targetEcho.ForeColor = SystemColors.GrayText;
            Controls.Add(_sourceEcho);
            Controls.Add(_targetEcho);

            y += Math.Max(Math.Max(_sourceEcho.Height, _targetEcho.Height), line) + 16;

            // Measured, not assumed: a button is as tall as its font needs and as
            // wide as its longest word, and "Cancel" is longer in several of the
            // languages this is used in.
            _ok.Text = "OK";
            _ok.DialogResult = DialogResult.OK;

            // See Dialogs.cs: an unparented Button measures itself in the wrong font.
            _ok.Font = Font;
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Font = Font };

            var buttonWidth = Math.Max(80, Math.Max(_ok.PreferredSize.Width, cancel.PreferredSize.Width) + 16);
            var buttonHeight = Math.Max(26, Math.Max(_ok.PreferredSize.Height, cancel.PreferredSize.Height));

            _ok.Size = new Size(buttonWidth, buttonHeight);
            cancel.Size = new Size(buttonWidth, buttonHeight);

            cancel.Location = new Point(ClientSize.Width - 12 - buttonWidth, y);
            _ok.Location = new Point(cancel.Left - 6 - buttonWidth, y);

            Controls.Add(_ok);
            Controls.Add(cancel);

            // The bottom edge follows the buttons rather than the buttons
            // following a guessed edge.
            ClientSize = new Size(ClientSize.Width, y + buttonHeight + 12);
            AcceptButton = _ok;
            CancelButton = cancel;

            _name.TextChanged += (s, e) => Refresh();
            _source.TextChanged += (s, e) => Refresh();
            _target.TextChanged += (s, e) => Refresh();
            Refresh();

            _name.Select();
            _name.SelectAll();
        }

        private new void Refresh()
        {
            _sourceEcho.Text = Echo(_source.Text);
            _targetEcho.Text = Echo(_target.Text);
            _ok.Enabled = TermbaseName.Length > 0 && SourceLang.Length > 0 && TargetLang.Length > 0;
        }

        /// <summary>"Dutch" for something understood; "not a language this knows" otherwise.</summary>
        private static string Echo(string code)
        {
            var value = (code ?? string.Empty).Trim();
            if (value.Length == 0) return "e.g. nl, dut-NL or Dutch";
            if (!LanguageCodes.IsKnown(value)) return "not a language this knows - kept as typed";

            var name = LanguageCodes.EnglishName(value);
            var stored = LanguageCodes.Canonical(value);
            return stored.Equals(value, StringComparison.OrdinalIgnoreCase) ? name : name + ", stored as " + stored;
        }
    }
}
