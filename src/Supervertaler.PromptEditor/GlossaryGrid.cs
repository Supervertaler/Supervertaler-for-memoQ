using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// The glossary as a table, because that is what it is.
    ///
    /// <para>A glossary was the one thing in the tree that had nowhere to open. It
    /// could be activated but not read, which meant checking a term still involved
    /// finding the file on disk. A grid rather than the prose editor: three columns
    /// of short strings in a text box invites a stray tab or a missing one, and
    /// either silently changes what the plugin matches.</para>
    /// </summary>
    internal sealed class GlossaryGrid : Panel
    {
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _header = new Label();
        private GlossaryDocument _document;

        /// <summary>Raised on any edit, so the window can show its unsaved marker.</summary>
        public event EventHandler Edited;

        public GlossaryGrid()
        {
            Dock = DockStyle.Fill;

            _header.Dock = DockStyle.Top;
            _header.Height = 34;
            _header.Padding = new Padding(10, 9, 10, 0);
            _header.ForeColor = SystemColors.GrayText;

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = true;
            _grid.AllowUserToDeleteRows = true;
            _grid.AllowUserToResizeRows = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.RowHeadersWidth = 28;

            // Sized to the text rather than left at the default: at this DPI the
            // default header row is a pixel or two short of a descender, so
            // "Target" lost the tail of its g.
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.BorderStyle = BorderStyle.None;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;

            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "source", HeaderText = "Source", FillWeight = 42 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "target", HeaderText = "Target", FillWeight = 42 });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "forbidden",
                HeaderText = "Forbidden",
                FillWeight = 16,
                ToolTipText = "A rendering that must never be used. Forbidden terms reach the model "
                            + "even when a drafted prompt is holding the rest of the glossary back."
            });

            _grid.CellValueChanged += (s, e) => Touch();
            _grid.RowsRemoved += (s, e) => Touch();

            // A checkbox commits on cell leave otherwise, so ticking Forbidden and
            // pressing Save straight away would save the old value.
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            Controls.Add(_grid);
            Controls.Add(_header);
        }

        public bool Dirty { get; private set; }

        private void Touch()
        {
            if (_loading) return;
            Dirty = true;
            Edited?.Invoke(this, EventArgs.Empty);
        }

        private bool _loading;

        public void Load(GlossaryDocument document)
        {
            _document = document;
            _loading = true;

            try
            {
                _grid.Rows.Clear();
                foreach (var e in document.Entries)
                    _grid.Rows.Add(e.Source, e.Target, e.Forbidden);

                var direction = Direction(document);
                _header.Text = document.Count + (document.Count == 1 ? " term" : " terms")
                    + (direction == null ? "" : "   ·   " + direction)
                    + "   ·   " + document.Path;
            }
            finally
            {
                _loading = false;
                Dirty = false;
            }
        }

        /// <summary>
        /// The language pair from the <c>#!</c> header, or null. Worth showing: a
        /// glossary facing the wrong way matches nothing and looks like an empty
        /// one, which is a slow thing to work out from the terms alone.
        /// </summary>
        private static string Direction(GlossaryDocument document)
        {
            document.Header.TryGetValue("source", out var from);
            document.Header.TryGetValue("target", out var to);

            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return null;
            return from + " → " + to;
        }

        /// <summary>
        /// Writes the grid back through the document, so comments and the header
        /// survive. Returns false and says why when it cannot.
        /// </summary>
        public bool Save(IWin32Window owner)
        {
            if (_document == null) return true;

            var rows = new List<GlossaryDocument.Item>();

            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;

                var source = (row.Cells["source"].Value as string ?? "").Trim();
                var target = (row.Cells["target"].Value as string ?? "").Trim();

                // A row with no source matches nothing; dropping it silently is
                // right, because it is what an accidental Enter in the grid leaves.
                if (source.Length == 0 && target.Length == 0) continue;

                rows.Add(new GlossaryDocument.Item
                {
                    Source = source,
                    Target = target,
                    Forbidden = row.Cells["forbidden"].Value is bool b && b
                });
            }

            var blank = rows.FirstOrDefault(r => r.Source.Length == 0 || r.Target.Length == 0);
            if (blank != null)
            {
                MessageBox.Show(owner,
                    "Every term needs both a source and a target.\r\n\r\nThe first one missing "
                    + "half is \"" + (blank.Source.Length == 0 ? blank.Target : blank.Source) + "\".",
                    "Save glossary", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                _document.SetEntries(rows);
                _document.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Could not save the glossary.\r\n\r\n" + ex.Message,
                    "Save glossary", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            Dirty = false;
            return true;
        }
    }
}
