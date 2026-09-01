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
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 118);

            var caption = new Label
            {
                Text = label,
                AutoSize = true,
                Location = new Point(12, 14)
            };

            _box = new TextBox
            {
                Text = initial ?? "",
                Location = new Point(12, 36),
                Width = ClientSize.Width - 24,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _box.SelectAll();

            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(ClientSize.Width - 178, 74),
                Width = 80
            };

            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(ClientSize.Width - 92, 74),
                Width = 80
            };

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
