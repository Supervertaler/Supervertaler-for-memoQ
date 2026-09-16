using System;
using System.Collections.Generic;
using System.Drawing;
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

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Ui.Chrome };
            bar.Controls.Add(ok);
            bar.Controls.Add(cancel);
            bar.Resize += (s, e) =>
            {
                cancel.Left = bar.ClientSize.Width - cancel.Width - 12;
                ok.Left = cancel.Left - ok.Width - 8;
                ok.Top = cancel.Top = 9;
            };

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
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

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
                ReadOnly = true
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
            var termbases = TermbaseDb.All();

            if (termbases.Count == 0)
            {
                _grid.Visible = false;
                _summary.Text = TermbaseDb.Exists
                    ? "The termbase database has no termbases in it yet."
                    : "No termbase database at " + TermbaseDb.Path
                      + " - it is created by Supervertaler for Trados and Supervertaler Workbench.";
                _summary.Height = 60;
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
