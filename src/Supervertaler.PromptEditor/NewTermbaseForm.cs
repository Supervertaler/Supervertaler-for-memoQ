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
            ClientSize = new Size(440, 214);

            var line = TextRenderer.MeasureText("Termbase", Font).Height;
            var y = 14;

            Controls.Add(new Label { Text = "Name", AutoSize = true, Location = new Point(12, y) });
            y += line + 4;
            _name.Text = name ?? string.Empty;
            _name.Location = new Point(12, y);
            _name.Width = ClientSize.Width - 24;
            Controls.Add(_name);
            y += _name.Height + 14;

            var half = (ClientSize.Width - 24 - 12) / 2;

            Controls.Add(new Label { Text = "Source language", AutoSize = true, Location = new Point(12, y) });
            Controls.Add(new Label { Text = "Target language", AutoSize = true, Location = new Point(12 + half + 12, y) });
            y += line + 4;

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
            _sourceEcho.AutoSize = false;
            _sourceEcho.Location = new Point(12, y);
            _sourceEcho.Width = half;
            _sourceEcho.Height = line;
            _sourceEcho.ForeColor = SystemColors.GrayText;
            _targetEcho.AutoSize = false;
            _targetEcho.Location = new Point(12 + half + 12, y);
            _targetEcho.Width = half;
            _targetEcho.Height = line;
            _targetEcho.ForeColor = SystemColors.GrayText;
            Controls.Add(_sourceEcho);
            Controls.Add(_targetEcho);

            _ok.Text = "OK";
            _ok.DialogResult = DialogResult.OK;
            _ok.Width = 80;
            _ok.Location = new Point(ClientSize.Width - 178, ClientSize.Height - 40);

            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Width = 80,
                Location = new Point(ClientSize.Width - 92, ClientSize.Height - 40)
            };

            Controls.Add(_ok);
            Controls.Add(cancel);
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
