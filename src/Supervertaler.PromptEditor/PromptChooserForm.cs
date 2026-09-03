using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Core.Models;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Picks the prompt memoQ will use.
    ///
    /// A list rather than a dropdown on the bar: the library already holds twenty
    /// prompts across several jobs and grows with every project, and a flat
    /// dropdown of that becomes unreadable long before it becomes wrong. The
    /// filter box is the point of the dialog.
    ///
    /// Only prompts memoQ can actually run are offered. Prompts marked for Trados
    /// are filtered out by the caller, because memoQ would fall back to its own
    /// Instructions box without saying so.
    /// </summary>
    internal sealed class PromptChooserForm : Form
    {
        private readonly List<PromptTemplate> _all;
        private readonly TextBox _filter = new TextBox();
        private readonly ListBox _list = new ListBox();
        private readonly Label _detail = new Label();

        /// <summary>The chosen prompt's relative path, or empty for "use memoQ's own instructions".</summary>
        public string SelectedPath { get; private set; }

        public PromptChooserForm(List<PromptTemplate> prompts, string current)
        {
            _all = prompts ?? new List<PromptTemplate>();

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "Choose the active prompt";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 460);
            MinimumSize = new Size(460, 320);

            var caption = new Label
            {
                Text = "memoQ will send this prompt with every translation request.",
                Left = 12, Top = 10, Width = 580, AutoSize = false, Height = 18,
                ForeColor = SystemColors.GrayText,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(caption);

            _filter.Left = 12; _filter.Top = 34; _filter.Width = 596;
            _filter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _filter.TextChanged += (s, e) => Populate();
            Controls.Add(_filter);

            var filterHint = new Label
            {
                Text = "Type to filter by name or folder",
                Left = 14, Top = 58, AutoSize = true, ForeColor = SystemColors.GrayText
            };
            Controls.Add(filterHint);

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

            Populate();
            SelectCurrent(current);
        }

        private sealed class Row
        {
            public string Path;
            public string Display;
            public PromptTemplate Prompt;
            public override string ToString() => Display;
        }

        private void Populate()
        {
            var needle = _filter.Text?.Trim();

            var rows = new List<Row>
            {
                // First, because it is the state a new install is in and the only
                // way back to memoQ's own Instructions box.
                new Row { Path = "", Display = "(use the instructions in memoQ's settings)" }
            };

            foreach (var p in _all)
            {
                var folder = string.IsNullOrWhiteSpace(p.Category) ? "" : p.Category + "  /  ";
                var display = folder + p.Name;

                var pair = string.IsNullOrWhiteSpace(p.SourceLang) || string.IsNullOrWhiteSpace(p.TargetLang)
                    ? null
                    : p.SourceLang + " to " + p.TargetLang;

                if (pair != null) display += "      " + pair;

                if (!string.IsNullOrEmpty(needle)
                    && display.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                    && (p.RelativePath ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                rows.Add(new Row { Path = p.RelativePath, Display = display, Prompt = p });
            }

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var r in rows) _list.Items.Add(r);
            _list.EndUpdate();

            if (_list.Items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
            ShowDetail();
        }

        private void SelectCurrent(string current)
        {
            if (string.IsNullOrWhiteSpace(current)) { _list.SelectedIndex = 0; return; }

            for (var i = 0; i < _list.Items.Count; i++)
            {
                if (_list.Items[i] is Row r
                    && string.Equals(r.Path, current, StringComparison.OrdinalIgnoreCase))
                {
                    _list.SelectedIndex = i;
                    return;
                }
            }
        }

        private void ShowDetail()
        {
            var row = _list.SelectedItem as Row;
            _detail.Text = row?.Prompt?.Description ?? string.Empty;
        }

        private void Accept()
        {
            SelectedPath = (_list.SelectedItem as Row)?.Path ?? string.Empty;
            DialogResult = DialogResult.OK;
        }
    }
}
