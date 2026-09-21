using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>Single-line text prompt. WinForms has no InputBox.</summary>
    internal sealed class TextInputDialog : Form
    {
        private readonly TextBox _box;

        public string Value => _box.Text;

        public TextInputDialog(string title, string label, string initial)
        {
            // The shell's own dialog font, before anything else is built: every
            // control created below inherits it. See Ui.Default.
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

            // Width is a choice; height is measured. This was a fixed 420 by 118
            // with the buttons at a fixed y and no height set on them, so a
            // larger interface font pushed them through the bottom edge and a
            // caption of more than a few words ran off the right. Both reported.
            var width = 460;
            var margin = 12;
            var inner = width - margin * 2;

            // AutoSize with a MaximumSize wraps, and the height it reports is the
            // wrapped height - which is what the rest of the layout is built on,
            // so a two-sentence caption pushes the box down instead of running
            // off the edge.
            var caption = new Label
            {
                Text = label,
                AutoSize = true,
                MaximumSize = new Size(inner, 0),
                Location = new Point(margin, 14)
            };
            caption.Size = caption.PreferredSize;

            var y = caption.Bottom + 8;

            _box = new TextBox
            {
                Text = initial ?? "",
                Location = new Point(margin, y),
                Width = inner,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _box.SelectAll();

            y = _box.Bottom + 14;

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };

            var buttonWidth = Math.Max(80, Math.Max(ok.PreferredSize.Width, cancel.PreferredSize.Width) + 16);
            var buttonHeight = Math.Max(26, Math.Max(ok.PreferredSize.Height, cancel.PreferredSize.Height));

            ok.Size = new Size(buttonWidth, buttonHeight);
            cancel.Size = new Size(buttonWidth, buttonHeight);
            cancel.Location = new Point(width - margin - buttonWidth, y);
            ok.Location = new Point(cancel.Left - 6 - buttonWidth, y);

            ClientSize = new Size(width, y + buttonHeight + margin);

            Controls.AddRange(new Control[] { caption, _box, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    /// <summary>Folder chooser for moving a prompt. "" is the library root.</summary>
    internal sealed class FolderPickerDialog : Form
    {
        private readonly ListBox _list;
        private readonly List<string> _folders;

        public string Selected => _folders[Math.Max(0, _list.SelectedIndex)];

        public FolderPickerDialog(List<string> folders, string current)
        {
            // The shell's own dialog font, before anything else is built: every
            // control created below inherits it. See Ui.Default.
            Font = Ui.Default;
            _folders = folders;

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "Move to folder";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 340);

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (var f in folders)
                _list.Items.Add(string.IsNullOrEmpty(f) ? "(library root)" : f);

            var idx = folders.FindIndex(f =>
                string.Equals(f ?? "", current ?? "", StringComparison.OrdinalIgnoreCase));
            _list.SelectedIndex = idx >= 0 ? idx : 0;

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(8)
            };

            var ok = new Button { Text = "Move", DialogResult = DialogResult.OK, Width = 80 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            Controls.Add(_list);
            Controls.Add(buttons);

            AcceptButton = ok;
            CancelButton = cancel;

            _list.DoubleClick += (s, e) => { DialogResult = DialogResult.OK; Close(); };
        }
    }
}
