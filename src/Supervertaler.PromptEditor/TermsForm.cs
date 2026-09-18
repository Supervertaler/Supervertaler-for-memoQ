using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.Core;
using Supervertaler.MemoQ.Core;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// The terms of one termbase, editable in place.
    ///
    /// <para><b>Four columns, deliberately.</b> Source, target, forbidden, notes
    /// - what Import and Export already carry, and what a translator changes in
    /// practice. The schema has definitions, domains, parts of speech and
    /// synonyms too, and Supervertaler for Trados has an editor for all of them;
    /// when the termbase layer moves into core that editor becomes shared, and a
    /// second full one here now would be the duplication this product has spent
    /// a week avoiding.</para>
    ///
    /// <para><b>Edits are collected, not written as you type.</b> OK writes every
    /// change in one pass - adds, updates, deletes - and Cancel discards them all.
    /// Each change is its own transaction against the shared database, so a
    /// refusal on one row (a pair the termbase already has) does not lose the
    /// rest; the failures are reported afterwards, by row.</para>
    ///
    /// <para><b>The columns are the termbase's own direction</b>, named in the
    /// headings, and nothing is turned round: this is the place a translator
    /// sees the termbase as it is stored, which for an en-to-nl termbase means
    /// English on the left, however their current job runs.</para>
    /// </summary>
    internal sealed class TermsForm : Form
    {
        private readonly TermbaseDb.Termbase _termbase;
        private readonly DataGridView _grid = new DataGridView();
        private readonly TextBox _filter = new TextBox();
        private readonly Label _count = new Label();
        private readonly Button _remove = new Button();

        // Rows the user deleted, remembered by identity so OK can delete them.
        private readonly List<TermbaseDb.Term> _deleted = new List<TermbaseDb.Term>();

        private const string ColSource = "source";
        private const string ColTarget = "target";
        private const string ColForbidden = "forbidden";
        private const string ColNotes = "notes";

        /// <summary>What OK could not save, one line per row, in words.</summary>
        internal List<string> Failures { get; } = new List<string>();

        internal TermsForm(TermbaseDb.Termbase termbase)
        {
            _termbase = termbase;

            Font = Ui.Default;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = termbase.Name + "  (" + Heading(termbase.SourceLang) + " → " + Heading(termbase.TargetLang) + ")";
            StartPosition = FormStartPosition.CenterParent;
            AppIcon.Apply(this);
            ClientSize = new Size(960, 640);
            MinimumSize = new Size(640, 400);
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Ui.Chrome;

            var line = TextRenderer.MeasureText("Termbase", Font).Height;

            // ---- top: filter and count ------------------------------------------
            var top = new Panel { Dock = DockStyle.Top, Height = line + 26, BackColor = Ui.Chrome };
            var caption = new Label { Text = "Filter", AutoSize = true, Location = new Point(12, 14) };
            _filter.Location = new Point(12 + caption.PreferredWidth + 8, 10);
            _filter.Width = 320;
            _filter.TextChanged += (s, e) => ApplyFilter();
            _count.AutoSize = false;
            _count.TextAlign = ContentAlignment.MiddleRight;
            _count.ForeColor = SystemColors.GrayText;
            _count.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _count.Height = _filter.Height;
            _count.Width = 360;
            _count.Location = new Point(top.ClientSize.Width - _count.Width - 12, 10);
            top.Controls.Add(caption);
            top.Controls.Add(_filter);
            top.Controls.Add(_count);
            top.Resize += (s, e) => _count.Left = top.ClientSize.Width - _count.Width - 12;

            // ---- the grid -------------------------------------------------------
            _grid.Dock = DockStyle.Fill;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.BorderStyle = BorderStyle.None;
            _grid.AllowUserToAddRows = true;       // the empty last row IS the add-a-term control
            _grid.AllowUserToDeleteRows = true;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = true;        // the pencil / asterisk glyphs say what a row is
            _grid.RowHeadersWidth = 28;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
            _grid.ColumnHeadersHeight = line + 10;
            _grid.RowTemplate.Height = line + 8;

            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = ColSource, HeaderText = Heading(termbase.SourceLang), Width = 300 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = ColTarget, HeaderText = Heading(termbase.TargetLang), Width = 300 });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = ColForbidden, HeaderText = "Forbidden", Width = 74,
                ToolTipText = "A term that must NOT be used. Shown in memoQ as a warning, and told to the model as such." });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = ColNotes, HeaderText = "Notes", Width = 200, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

            _grid.UserDeletingRow += (s, e) =>
            {
                var term = e.Row.Tag as TermbaseDb.Term;
                if (term != null) _deleted.Add(term);
            };
            _grid.UserDeletedRow += (s, e) => UpdateCount();
            _grid.RowsAdded += (s, e) => UpdateCount();
            _grid.SelectionChanged += (s, e) => _remove.Enabled = _grid.SelectedCells.Count > 0 && !_grid.CurrentRow.IsNewRow;

            // ---- bottom: remove, hint, OK / Cancel -----------------------------------
            var ok = new Button { Text = "OK", Width = 90, Height = 28, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => SaveAll();

            _remove.Text = "Remove term";
            _remove.Height = 28;
            _remove.AutoSize = true;
            _remove.Padding = new Padding(6, 0, 6, 0);
            _remove.Enabled = false;
            _remove.Click += (s, e) => RemoveSelected();

            var hint = new Label
            {
                Text = "Type in the empty last row to add a term. Changes are written when you press OK.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText
            };

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Ui.Chrome };
            bar.Controls.Add(_remove);
            bar.Controls.Add(hint);
            bar.Controls.Add(ok);
            bar.Controls.Add(cancel);
            bar.Resize += (s, e) =>
            {
                cancel.Left = bar.ClientSize.Width - cancel.Width - 12;
                ok.Left = cancel.Left - ok.Width - 8;
                ok.Top = cancel.Top = 9;
                _remove.Left = 12; _remove.Top = 9;
                hint.Left = _remove.Right + 14; hint.Top = 9 + (28 - hint.Height) / 2;
            };

            Controls.Add(_grid);
            Controls.Add(top);
            Controls.Add(bar);

            AcceptButton = null;      // Enter in a cell commits the cell, not the dialog
            CancelButton = cancel;

            Load += (s, e) => Fill();
        }

        /// <summary>"Dutch (nl-NL)": the name a person reads, the code a file uses.</summary>
        private static string Heading(string lang)
        {
            var code = (lang ?? string.Empty).Trim();
            if (code.Length == 0) return "?";
            var name = LanguageCodes.EnglishName(code);
            return name.Equals(code, StringComparison.OrdinalIgnoreCase) ? code : name + " (" + code + ")";
        }

        private void Fill()
        {
            _grid.Rows.Clear();
            foreach (var term in TermbaseDb.TermsOf(_termbase.Id))
            {
                var row = _grid.Rows[_grid.Rows.Add(term.Source, term.Target, term.Forbidden, term.Notes)];
                row.Tag = term;
            }
            UpdateCount();
        }

        /// <summary>
        /// Hide rows the filter does not match rather than rebuilding the grid,
        /// so that an edit made and then filtered out of sight is still an edit.
        /// The add-a-term row always stays.
        /// </summary>
        private void ApplyFilter()
        {
            var needle = _filter.Text.Trim();

            _grid.CurrentCell = null;   // a hidden row cannot be the current one
            _grid.SuspendLayout();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                row.Visible = needle.Length == 0
                    || Contains(row.Cells[ColSource].Value, needle)
                    || Contains(row.Cells[ColTarget].Value, needle)
                    || Contains(row.Cells[ColNotes].Value, needle);
            }
            _grid.ResumeLayout();
            UpdateCount();
        }

        private static bool Contains(object cell, string needle) =>
            (cell ?? string.Empty).ToString().IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void UpdateCount()
        {
            var total = 0;
            var shown = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                total++;
                if (row.Visible) shown++;
            }

            _count.Text = shown == total
                ? string.Format("{0:N0} term{1}", total, total == 1 ? "" : "s")
                : string.Format("{0:N0} of {1:N0} terms shown", shown, total);
        }

        private void RemoveSelected()
        {
            var rows = new List<DataGridViewRow>();
            foreach (DataGridViewCell cell in _grid.SelectedCells)
                if (!cell.OwningRow.IsNewRow && !rows.Contains(cell.OwningRow)) rows.Add(cell.OwningRow);

            foreach (var row in rows)
            {
                var term = row.Tag as TermbaseDb.Term;
                if (term != null) _deleted.Add(term);
                _grid.Rows.Remove(row);
            }
            UpdateCount();
        }

        /// <summary>
        /// Write everything that changed, one operation per row, and keep going
        /// past a refusal so that one duplicate does not cost the other edits.
        /// </summary>
        private void SaveAll()
        {
            _grid.EndEdit();
            Failures.Clear();

            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;

                var source = Cell(row, ColSource);
                var target = Cell(row, ColTarget);
                var forbidden = row.Cells[ColForbidden].Value is bool b && b;
                var notes = Cell(row, ColNotes);
                var term = row.Tag as TermbaseDb.Term;

                try
                {
                    if (term == null)
                    {
                        // A row the user typed. Empty ones - a half-started add - are
                        // simply not terms.
                        if (source.Length == 0 && target.Length == 0) continue;
                        TermbaseWriter.AddTerm(_termbase.Id, source, target, forbidden, notes);
                    }
                    else if (source != term.Source || target != term.Target
                          || forbidden != term.Forbidden || notes != (term.Notes ?? string.Empty))
                    {
                        TermbaseWriter.UpdateTerm(term.Id, source, target, forbidden, notes);
                    }
                }
                catch (Exception ex)
                {
                    Failures.Add(Describe(source, target) + ": " + ex.Message);
                }
            }

            foreach (var term in _deleted)
            {
                try
                {
                    TermbaseWriter.DeleteTerm(term.Id);
                }
                catch (Exception ex)
                {
                    Failures.Add(Describe(term.Source, term.Target) + " could not be removed: " + ex.Message);
                }
            }
        }

        private static string Cell(DataGridViewRow row, string column) =>
            (row.Cells[column].Value ?? string.Empty).ToString().Trim();

        private static string Describe(string source, string target) =>
            "“" + (source.Length == 0 ? "?" : source) + "” → “" + (target.Length == 0 ? "?" : target) + "”";
    }
}
