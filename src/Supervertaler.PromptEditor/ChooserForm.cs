using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Pick one thing out of a list that may be long.
    ///
    /// <para>Every choice on the context bar - the prompt, the memory bank -
    /// picks a named thing out of a folder that grows with the work. The prompt
    /// library is already past forty across several jobs; there are twenty-three
    /// memory banks. A dropdown menu of either is unreadable long before it is
    /// wrong, and both would need a filter box eventually, so both get the same
    /// one now rather than one growing a private version later.</para>
    ///
    /// <para>Rows are built once by the caller and filtered in memory, so the
    /// cost of typing is a substring scan over a few hundred short strings
    /// rather than a re-read of a folder per keystroke.</para>
    /// </summary>
    internal sealed class ChooserForm : Form
    {
        internal sealed class Row
        {
            /// <summary>What the caller stores when this row is chosen.</summary>
            public string Value;

            /// <summary>The line shown in the list.</summary>
            public string Display;

            /// <summary>The grey line under the list when this row is selected.</summary>
            public string Detail;

            /// <summary>
            /// Extra text the filter also matches - a relative path, say - so
            /// typing a folder name finds things whose displayed line does not
            /// carry it.
            /// </summary>
            public string Search;

            public override string ToString() => Display;
        }

        private readonly IReadOnlyList<Row> _all;
        private readonly TextBox _filter = new TextBox();
        private readonly ListBox _list = new ListBox();
        private readonly Label _detail = new Label();

        /// <summary>The chosen row's value, or null when the dialog was cancelled.</summary>
        public string SelectedValue { get; private set; }

        /// <summary>
        /// Set when the dialog closed because the user asked to make a new one
        /// rather than pick an existing one. Only offered when the caller passes
        /// <c>extraButton</c>.
        /// </summary>
        public bool CreateRequested { get; private set; }

        public ChooserForm(string title, string caption, string filterHint,
                           IReadOnlyList<Row> rows, string current)
            : this(title, caption, filterHint, rows, current, null) { }

        /// <summary>
        /// <paramref name="extraButton"/> puts one more button on the left of the
        /// row - "New memory bank...", say - and closing through it sets
        /// <see cref="CreateRequested"/>. A chooser that can only choose from what
        /// exists is a dead end the first time somebody has nothing to choose
        /// from, which is exactly when a new project starts.
        /// </summary>
        public ChooserForm(string title, string caption, string filterHint,
                           IReadOnlyList<Row> rows, string current, string extraButton)
        {
            // The shell's own dialog font, before anything else is built: every
            // control created below inherits it. See Ui.Default.
            Font = Ui.Default;
            _all = rows ?? new List<Row>();

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = title;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 460);
            MinimumSize = new Size(460, 320);
            AppIcon.Apply(this);

            // Measured, not one fixed line. It was 18 pixels high, which clipped
            // the memory bank chooser's own caption to its first line - and then
            // silently swallowed the note added for issue #8, which says which
            // project a bank will be filed against BEFORE the choice is made. A
            // warning nobody can see is no warning. Everything below moves down by
            // however much taller than one line the caption turned out to be.
            var head = new Label
            {
                Text = caption,
                Left = 12, Top = 10, AutoSize = true, MaximumSize = new Size(596, 0),
                ForeColor = SystemColors.GrayText,
            };
            Controls.Add(head);
            var extra = Math.Max(0, head.Height - 18);

            // Grown now, before anything else is placed, so every position below
            // is simply "where it was, plus extra". Growing it at the end instead
            // would move the bottom-anchored controls a second time.
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + extra);

            _filter.Left = 12; _filter.Top = 34 + extra; _filter.Width = 596;
            _filter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _filter.TextChanged += (s, e) => Populate();
            Controls.Add(_filter);

            var hint = new Label
            {
                Text = filterHint,
                Left = 14, Top = 58 + extra, AutoSize = true, ForeColor = SystemColors.GrayText
            };
            Controls.Add(hint);

            // Below the hint as it measured, keeping the list's bottom edge where
            // it was. It sat at a fixed 78, one pixel inside a hint that turned
            // out 21 high - found the first time this dialog was measured.
            _list.Left = 12; _list.Top = hint.Bottom + 2; _list.Width = 596;
            _list.Height = (374 + extra) - _list.Top;
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _list.IntegralHeight = false;
            _list.SelectedIndexChanged += (s, e) => ShowDetail();
            _list.DoubleClick += (s, e) => Accept();
            Controls.Add(_list);

            _detail.Left = 14; _detail.Top = 380 + extra; _detail.Width = 594; _detail.Height = 34;
            _detail.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _detail.ForeColor = SystemColors.GrayText;
            Controls.Add(_detail);

            // Font set before measuring - an unparented Button measures in the
            // default font - and heights taken from the text, not a fixed 26,
            // which was two pixels short of it in this font. The buttons share
            // one height and sit the same distance from the bottom as before.
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Font = Font };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Font = Font };
            var buttonHeight = Math.Max(26, Math.Max(ok.PreferredSize.Height, cancel.PreferredSize.Height));
            var buttonTop = ClientSize.Height - 8 - buttonHeight;

            ok.Size = new Size(Math.Max(84, ok.PreferredSize.Width + 16), buttonHeight);
            cancel.Size = new Size(Math.Max(84, cancel.PreferredSize.Width + 16), buttonHeight);
            cancel.Location = new Point(ClientSize.Width - 10 - cancel.Width, buttonTop);
            ok.Location = new Point(cancel.Left - 6 - ok.Width, buttonTop);
            ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ok.Click += (s, e) => Accept();
            Controls.Add(ok);

            Controls.Add(cancel);

            if (!string.IsNullOrEmpty(extraButton))
            {
                // Width measured from the text, not guessed at. A fixed 170 is
                // fine in this font and clips in a larger one, which is the bug
                // that was reported against the new-termbase dialog the same day
                // this button was written.
                var create = new Button
                {
                    Text = extraButton, Font = Font,
                    Left = 12, Top = buttonTop, Height = buttonHeight,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Left
                };
                create.Width = Math.Max(170, create.PreferredSize.Width + 16);
                create.Height = Math.Max(buttonHeight, create.PreferredSize.Height);
                create.Click += (s, e) =>
                {
                    CreateRequested = true;
                    DialogResult = DialogResult.OK;
                    Close();
                };
                Controls.Add(create);
            }

            AcceptButton = ok;
            CancelButton = cancel;

            // Typing goes to the filter, because that is what this dialog is for.
            ActiveControl = _filter;

            Populate();
            SelectCurrent(current);
        }

        /// <summary>
        /// The rows matching the filter. A row matches when the needle appears in
        /// its displayed line or in its search text; an empty needle matches all.
        /// </summary>
        private IEnumerable<Row> Matching()
        {
            var needle = _filter.Text?.Trim();
            if (string.IsNullOrEmpty(needle)) return _all;

            return _all.Where(r =>
                (r.Display ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || (r.Search ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void Populate()
        {
            // What was selected before the keystroke, so that narrowing the list
            // does not silently move the selection onto a different answer.
            var was = (_list.SelectedItem as Row)?.Value;

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var r in Matching()) _list.Items.Add(r);
            }
            finally
            {
                _list.EndUpdate();
            }

            if (_list.Items.Count == 0) { ShowDetail(); return; }

            SelectCurrent(was);
            if (_list.SelectedIndex < 0) _list.SelectedIndex = 0;
            ShowDetail();
        }

        private void SelectCurrent(string value)
        {
            if (value == null) { _list.SelectedIndex = _list.Items.Count > 0 ? 0 : -1; return; }

            for (var i = 0; i < _list.Items.Count; i++)
            {
                if (_list.Items[i] is Row r
                    && string.Equals(r.Value ?? "", value, StringComparison.OrdinalIgnoreCase))
                {
                    _list.SelectedIndex = i;
                    return;
                }
            }
        }

        private void ShowDetail()
        {
            _detail.Text = (_list.SelectedItem as Row)?.Detail ?? string.Empty;
        }

        private void Accept()
        {
            // Nothing selected - an empty filter result, say - must not read as a
            // deliberate choice of the first thing in the underlying list.
            if (CreateRequested) return;   // closing to create, not to pick
            if (!(_list.SelectedItem is Row row)) { DialogResult = DialogResult.Cancel; return; }

            SelectedValue = row.Value ?? string.Empty;
            DialogResult = DialogResult.OK;
        }
    }
}
