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

        public ChooserForm(string title, string caption, string filterHint,
                           IReadOnlyList<Row> rows, string current)
        {
            _all = rows ?? new List<Row>();

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = title;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 460);
            MinimumSize = new Size(460, 320);
            AppIcon.Apply(this);

            var head = new Label
            {
                Text = caption,
                Left = 12, Top = 10, Width = 580, AutoSize = false, Height = 18,
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(head);

            _filter.Left = 12; _filter.Top = 34; _filter.Width = 596;
            _filter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _filter.TextChanged += (s, e) => Populate();
            Controls.Add(_filter);

            Controls.Add(new Label
            {
                Text = filterHint,
                Left = 14, Top = 58, AutoSize = true, ForeColor = SystemColors.GrayText
            });

            _list.Left = 12; _list.Top = 78; _list.Width = 596; _list.Height = 296;
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _list.IntegralHeight = false;
            _list.SelectedIndexChanged += (s, e) => ShowDetail();
            _list.DoubleClick += (s, e) => Accept();
            Controls.Add(_list);

            _detail.Left = 14; _detail.Top = 380; _detail.Width = 594; _detail.Height = 34;
            _detail.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _detail.ForeColor = SystemColors.GrayText;
            Controls.Add(_detail);

            var ok = new Button
            {
                Text = "OK", DialogResult = DialogResult.OK,
                Left = ClientSize.Width - 184, Top = ClientSize.Height - 34, Width = 84, Height = 26,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            ok.Click += (s, e) => Accept();
            Controls.Add(ok);

            var cancel = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel,
                Left = ClientSize.Width - 94, Top = ClientSize.Height - 34, Width = 84, Height = 26,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            Controls.Add(cancel);

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
            if (!(_list.SelectedItem is Row row)) { DialogResult = DialogResult.Cancel; return; }

            SelectedValue = row.Value ?? string.Empty;
            DialogResult = DialogResult.OK;
        }
    }
}
