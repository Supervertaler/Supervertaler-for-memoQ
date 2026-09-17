using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.MemoQ.Core;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// The shared termbases, and what memoQ does with each.
    ///
    /// <para>Deliberately the same shape as the Termbases tab in Supervertaler
    /// for Trados - every termbase in the shared database, one row each, with
    /// tick columns - because it is the same library seen from a second product
    /// and a translator should not have to learn a second layout for it.</para>
    ///
    /// <para>The columns are Trados's, less the one that cannot apply: Read, CS
    /// and AI mean the same thing, Project means the same thing, and Write is
    /// absent because memoQ never writes to that database.</para>
    ///
    /// <para><b>Project was briefly a Rank, and that was wrong.</b> The
    /// reasoning was that memoQ shades a term hit by the rank of the termbase it
    /// came from, so a project termbase is just rank 1 - but those shades are
    /// memoQ ranking its OWN termbases, a different thing. The distinction a
    /// translator works with here is binary: one small, deliberate project
    /// termbase against any number of background ones. A scale of ten was an
    /// answer to a question nobody had asked, and it made the user invent
    /// numbers that meant nothing.</para>
    ///
    /// <para>Read is per memoQ project and the other three belong to the
    /// termbase, which is why the project is named at the top: the ticks in one
    /// column apply to that job and the rest apply everywhere.</para>
    /// </summary>
    internal sealed class TermbasesDialog : Form
    {
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _project = new Label();
        private readonly Label _summary = new Label();
        private readonly Guid _projectGuid;

        private readonly Button _new = new Button();
        private readonly Button _import = new Button();
        private readonly Button _addTo = new Button();
        private readonly Button _export = new Button();
        private readonly Button _delete = new Button();

        private const string ColRead = "read";
        private const string ColProject = "project";
        private const string ColCase = "cs";
        private const string ColAi = "ai";

        internal TermbasesDialog()
        {
            Font = Ui.Default;

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Termbases";
            StartPosition = FormStartPosition.CenterParent;
            AppIcon.Apply(this);
            ClientSize = new Size(940, 620);
            MinimumSize = new Size(700, 420);
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Ui.Chrome;

            Guid.TryParse((SharedSettings.MemoryBankProject ?? "").Trim(), out _projectGuid);

            _project.Dock = DockStyle.Top;
            _project.AutoSize = false;
            _project.Height = 44;
            _project.Padding = new Padding(12, 10, 12, 0);
            _project.Text = ProjectLine();
            _project.ForeColor = _projectGuid == Guid.Empty ? Color.Firebrick : SystemColors.ControlText;

            _summary.Dock = DockStyle.Bottom;
            _summary.AutoSize = false;
            _summary.Height = 30;
            _summary.Padding = new Padding(12, 6, 12, 0);
            _summary.ForeColor = SystemColors.GrayText;

            BuildGrid();

            var ok = new Button { Text = "OK", Width = 90, Height = 28, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => Save();

            // The termbases themselves: created, filled, written out, removed.
            // Every one of these first saves the ticks as they stand, because
            // the table is rebuilt afterwards and a rebuild reads the file.
            _new.Text = "New…";        _new.Click += (s, e) => Guarded(CreateNew);
            _import.Text = "Import…";  _import.Click += (s, e) => Guarded(ImportAsNew);
            _addTo.Text = "Add to…";   _addTo.Click += (s, e) => Guarded(AddToSelected);
            _export.Text = "Export…";  _export.Click += (s, e) => Guarded(ExportSelected);
            _delete.Text = "Delete";        _delete.Click += (s, e) => Guarded(DeleteSelected);
            foreach (var b in new[] { _new, _import, _addTo, _export, _delete }) { b.Height = 28; b.AutoSize = true; b.Padding = new Padding(6, 0, 6, 0); }

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Ui.Chrome };
            bar.Controls.Add(ok);
            bar.Controls.Add(cancel);
            foreach (var b in new[] { _new, _import, _addTo, _export, _delete }) bar.Controls.Add(b);
            bar.Resize += (s, e) =>
            {
                cancel.Left = bar.ClientSize.Width - cancel.Width - 12;
                ok.Left = cancel.Left - ok.Width - 8;
                ok.Top = cancel.Top = 9;

                var x = 12;
                foreach (var b in new[] { _new, _import, _addTo, _export, _delete })
                {
                    b.Left = x; b.Top = 9;
                    x += b.Width + 6;
                }
            };

            _grid.SelectionChanged += (s, e) => EnableForSelection();

            Controls.Add(_grid);
            Controls.Add(_summary);
            Controls.Add(bar);
            Controls.Add(_project);

            AcceptButton = ok;
            CancelButton = cancel;

            Load += (s, e) => Fill();
        }

        private string ProjectLine()
        {
            var name = (SharedSettings.MemoryBankProjectName ?? "").Trim();

            if (_projectGuid == Guid.Empty)
                return "No memoQ project yet, so the Read column has nothing to apply to. "
                     + "Open a project in memoQ, then press Sync in the main window.";

            return "Read applies to: " + (name.Length > 0 ? name : _projectGuid.ToString("D"))
                 + "     ·     Project, CS and AI belong to the termbase and apply everywhere.";
        }

        private void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.BorderStyle = BorderStyle.None;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

            // Measured, not guessed. The header row and the cells were left at
            // WinForms' defaults, which are sized for the 8.25pt font nothing in
            // this program uses any more - so every header sat a pixel or two
            // short and clipped its own descenders.
            var line = TextRenderer.MeasureText("Termbase", Font).Height;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
            _grid.ColumnHeadersHeight = line + 10;
            _grid.RowTemplate.Height = line + 8;

            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = ColRead,
                HeaderText = "Read",
                Width = 52,
                ToolTipText = "Consult this termbase in this project: matches highlight in the grid "
                            + "and appear in memoQ's Translation results."
            });

            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = ColProject,
                HeaderText = "Project",
                Width = 58,
                ToolTipText = "The one termbase for this job's own terminology. Its hits are shaded "
                            + "darker than the rest, so a project term is recognisable at a glance. "
                            + "Only one termbase can hold this at a time."
            });

            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = ColCase,
                HeaderText = "CS",
                Width = 44,
                ToolTipText = "Match this termbase's terms case-sensitively."
            });

            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = ColAi,
                HeaderText = "AI",
                Width = 44,
                ToolTipText = "Send this termbase's matched terms to the model, in batch translate, "
                            + "single segments and AutoPrompt."
            });

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "name",
                HeaderText = "Termbase",
                Width = 470,
                ReadOnly = true,

                // The one column that should absorb the slack, so the table
                // reaches the right-hand edge at any window size instead of
                // leaving a band of empty grid beside Languages.
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "terms",
                HeaderText = "Terms",
                Width = 70,
                ReadOnly = true,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
            });

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "langs",
                HeaderText = "Languages",
                Width = 110,
                ReadOnly = true
            });

            // Exactly one project termbase: ticking a second clears the first,
            // the way a radio button would. Doing it here rather than refusing
            // the second tick, because a translator changing which termbase is
            // the project one should not have to untick the old one first.
            _grid.CellValueChanged += (s, e) =>
            {
                if (_updating) return;
                if (e.RowIndex < 0 || e.ColumnIndex != _grid.Columns[ColProject].Index) return;
                if (!Ticked(_grid.Rows[e.RowIndex], ColProject)) return;

                _updating = true;
                try
                {
                    foreach (DataGridViewRow row in _grid.Rows)
                        if (row.Index != e.RowIndex && Ticked(row, ColProject))
                            row.Cells[ColProject].Value = false;
                }
                finally
                {
                    _updating = false;
                }
            };

            // A tick registers on the click rather than when the cell loses focus,
            // which is what a checkbox in a grid otherwise does - and is how a
            // dialog comes to be saved without the last tick the user made.
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            _grid.CellValueChanged += (s, e) => UpdateSummary();
        }

        // Guards the single-project rule against its own side effects: clearing
        // the other rows raises CellValueChanged again, once per row.
        private bool _updating;

        private void Fill()
        {
            _grid.Rows.Clear();
            _grid.Visible = true;
            _summary.Height = 30;

            var termbases = TermbaseDb.All();

            if (termbases.Count == 0)
            {
                _grid.Visible = false;
                _summary.Text = TermbaseDb.Exists
                    ? "No termbases yet. New… makes an empty one; Import… makes one from a glossary or a spreadsheet export."
                    : "No termbase database yet - New… or Import… will create it at " + TermbaseDb.Path
                      + ". Supervertaler for Trados and Workbench use the same file, if you have them.";
                _summary.Height = 60;
                EnableForSelection();
                return;
            }

            var flags = TermbaseSelection.All();
            var read = new HashSet<long>(TermbaseSelection.ReadFor(_projectGuid));

            // The project termbase first, then by name: the table opens on the
            // one row that is different from all the others.
            foreach (var tb in termbases
                .OrderByDescending(t => IsProject(flags, t))
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                TermbaseSelection.Flags f;
                flags.TryGetValue(tb.Id, out f);

                var row = _grid.Rows[_grid.Rows.Add(
                    read.Contains(tb.Id),
                    IsProject(flags, tb),
                    f != null && f.CaseSensitive,
                    f != null && f.Ai,
                    tb.Name,
                    tb.Terms.ToString("N0"),
                    (tb.SourceLang ?? "?") + " → " + (tb.TargetLang ?? "?"))];

                row.Tag = tb;
            }

            UpdateSummary();
            EnableForSelection();
        }

        // ---- the buttons ------------------------------------------------------

        private TermbaseDb.Termbase Selected =>
            _grid.Visible && _grid.CurrentRow != null ? _grid.CurrentRow.Tag as TermbaseDb.Termbase : null;

        private void EnableForSelection()
        {
            var any = Selected != null;
            _addTo.Enabled = any;
            _export.Enabled = any;
            _delete.Enabled = any;
        }

        /// <summary>
        /// Run one of the operations with the one thing every failure needs:
        /// to be shown, in words, rather than to close the dialog.
        /// </summary>
        private void Guarded(Action operation)
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Termbases", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CreateNew()
        {
            using (var form = new NewTermbaseForm("New termbase", "", "", ""))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;

                Save();
                var id = TermbaseWriter.Create(form.TermbaseName, form.SourceLang, form.TargetLang, "");
                TickRead(id);
                Fill();
            }
        }

        /// <summary>
        /// A termbase from a file: a prompt-library glossary, a memoQ or Excel
        /// export, a Trados export. The file's own header names it and its
        /// languages where it has one, and the form lets that be corrected
        /// before anything is written.
        /// </summary>
        private void ImportAsNew()
        {
            var path = AskForFile();
            if (path == null) return;

            var contents = TermbaseFiles.Read(path);
            if (contents.Rows.Count == 0)
                throw new InvalidOperationException("No term pairs were found in that file.");

            using (var form = new NewTermbaseForm("Import as a new termbase", contents.Name, contents.SourceLang, contents.TargetLang))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;

                Save();
                var id = TermbaseWriter.Create(form.TermbaseName, form.SourceLang, form.TargetLang, "");
                var result = TermbaseWriter.Import(id, contents.Rows, form.SourceLang, form.TargetLang);
                TickRead(id);
                Fill();
                Report(result, contents, form.TermbaseName);
            }
        }

        private void AddToSelected()
        {
            var tb = Selected;
            if (tb == null) return;

            var path = AskForFile();
            if (path == null) return;

            var contents = TermbaseFiles.Read(path);
            if (contents.Rows.Count == 0)
                throw new InvalidOperationException("No term pairs were found in that file.");

            // A file that does not say which way it runs is taken to run the
            // termbase's way. Say so, because the alternative is silent.
            if (string.IsNullOrEmpty(contents.SourceLang) || string.IsNullOrEmpty(contents.TargetLang))
            {
                var answer = MessageBox.Show(this,
                    "The file does not say which language is which. Take its first column as "
                    + tb.SourceLang + " and its second as " + tb.TargetLang + ", like the termbase?",
                    "Termbases", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (answer != DialogResult.OK) return;
            }

            Save();
            var result = TermbaseWriter.Import(tb.Id, contents.Rows, contents.SourceLang, contents.TargetLang);
            Fill();
            Report(result, contents, tb.Name);
        }

        private void ExportSelected()
        {
            var tb = Selected;
            if (tb == null) return;

            using (var dialog = new SaveFileDialog
            {
                Title = "Export termbase",
                FileName = SafeFileName(tb.Name),
                Filter = "Supervertaler glossary (*.txt)|*.txt|Tab-separated with a header row, for Excel or Trados (*.tsv)|*.tsv",
                InitialDirectory = GlossariesFolder()
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                var shape = dialog.FilterIndex == 2 ? TermbaseFiles.Shape.HeaderRowTsv : TermbaseFiles.Shape.Glossary;
                var rows = TermbaseDb.RowsOf(tb.Id);
                TermbaseFiles.Write(dialog.FileName, shape, tb.Name, tb.SourceLang, tb.TargetLang, rows);

                MessageBox.Show(this,
                    string.Format("{0:N0} term{1} written to {2}.", rows.Count, rows.Count == 1 ? "" : "s", dialog.FileName),
                    "Termbases", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void DeleteSelected()
        {
            var tb = Selected;
            if (tb == null) return;

            var answer = MessageBox.Show(this,
                string.Format("Delete “{0}” and its {1:N0} term{2}? This cannot be undone, and it is removed for "
                            + "Supervertaler for Trados and Workbench as well - they share the database.",
                              tb.Name, tb.Terms, tb.Terms == 1 ? "" : "s"),
                "Termbases", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;

            Save();
            TermbaseWriter.Delete(tb.Id);
            Fill();
        }

        /// <summary>A termbase just made is one this project wants: tick Read for it.</summary>
        private void TickRead(long id)
        {
            if (_projectGuid == Guid.Empty) return;
            var ids = new List<long>(TermbaseSelection.ReadFor(_projectGuid));
            if (!ids.Contains(id)) ids.Add(id);
            TermbaseSelection.Save(_projectGuid, ids, null);
        }

        private string AskForFile()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Import terms",
                Filter = "Glossaries and exports (*.txt;*.tsv;*.csv)|*.txt;*.tsv;*.csv|All files (*.*)|*.*",
                InitialDirectory = GlossariesFolder()
            })
            {
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private static void Report(TermbaseWriter.ImportResult result, TermbaseFiles.Contents contents, string name)
        {
            var text = string.Format("{0:N0} term{1} added to “{2}”.", result.Added, result.Added == 1 ? "" : "s", name);
            if (result.Duplicates > 0) text += string.Format("\r\n{0:N0} already there, skipped.", result.Duplicates);
            if (contents.Unreadable > 0) text += string.Format("\r\n{0:N0} line{1} could not be read as a pair.", contents.Unreadable, contents.Unreadable == 1 ? "" : "s");
            if (result.Reversed) text += "\r\nThe file ran the other way round from the termbase, so each pair was stored turned round.";

            MessageBox.Show(text, "Termbases", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Where Export glossary already writes, so exports and imports meet in one place.</summary>
        private static string GlossariesFolder()
        {
            var dir = Path.Combine(global::Supervertaler.Core.SupervertalerPaths.Root, "memoq", "glossaries");
            return Directory.Exists(dir) ? dir : global::Supervertaler.Core.SupervertalerPaths.Root;
        }

        private static string SafeFileName(string name)
        {
            var text = (name ?? "termbase").Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
            return text.Length == 0 ? "termbase" : text;
        }

        /// <summary>
        /// Whether this is the project termbase - memoQ's own answer where it has
        /// one, and Trados's where it does not.
        ///
        /// <para>Inheriting the default saves nominating the same termbase twice
        /// in two products. It applies only until memoQ has been told otherwise:
        /// once anything is saved here, every termbase has a memoQ answer and the
        /// database is no longer consulted for it.</para>
        /// </summary>
        private static bool IsProject(IDictionary<long, TermbaseSelection.Flags> flags,
                                      TermbaseDb.Termbase termbase)
        {
            TermbaseSelection.Flags f;
            return flags.TryGetValue(termbase.Id, out f) ? f.IsProject : termbase.IsProjectTermbase;
        }

        /// <summary>
        /// How much terminology this selection actually amounts to.
        ///
        /// <para>Worth showing, and not decoration: four of these termbases hold
        /// more than a thousand terms each and one holds twelve thousand, so it
        /// is entirely possible to tick a quiet-looking set of boxes and load
        /// twenty thousand generic dictionary entries into every lookup.</para>
        /// </summary>
        private void UpdateSummary()
        {
            var ticked = 0;
            var terms = 0;
            var ai = 0;

            foreach (DataGridViewRow row in _grid.Rows)
            {
                var tb = row.Tag as TermbaseDb.Termbase;
                if (tb == null || !Ticked(row, ColRead)) continue;

                ticked++;
                terms += tb.Terms;
                if (Ticked(row, ColAi)) ai++;
            }

            var project = 0;
            foreach (DataGridViewRow row in _grid.Rows)
                if (Ticked(row, ColRead) && Ticked(row, ColProject)) project++;

            _summary.Text = ticked == 0
                ? "No termbases selected for this project, so terminology is off."
                : string.Format("{0} termbase{1} selected, {2:N0} terms in all{3}{4}.",
                    ticked, ticked == 1 ? "" : "s", terms,
                    project == 0 ? "; none of them the project termbase" : "; one of them the project termbase",
                    ai == 0 ? "; none reaching the model" :
                    ai == 1 ? "; one reaching the model" :
                    "; " + ai + " reaching the model");
        }

        private static bool Ticked(DataGridViewRow row, string column)
        {
            var value = row.Cells[column].Value;
            return value is bool b && b;
        }

        private void Save()
        {
            var readIds = new List<long>();
            var flags = new List<TermbaseSelection.Flags>();

            foreach (DataGridViewRow row in _grid.Rows)
            {
                var tb = row.Tag as TermbaseDb.Termbase;
                if (tb == null) continue;

                if (Ticked(row, ColRead)) readIds.Add(tb.Id);

                flags.Add(new TermbaseSelection.Flags
                {
                    Id = tb.Id,
                    IsProject = Ticked(row, ColProject),
                    CaseSensitive = Ticked(row, ColCase),
                    Ai = Ticked(row, ColAi),
                    Name = tb.Name
                });
            }

            TermbaseSelection.Save(_projectGuid, readIds, flags);
        }
    }
}
