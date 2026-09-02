using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Core;
using Supervertaler.Core.Models;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// The editor window.
    ///
    /// Every read and write goes through <see cref="PromptLibrary"/> from Core —
    /// the same parser and writer both plugins use. Nothing here knows what a
    /// frontmatter block looks like, which is the entire design: a second
    /// implementation of the format is how keys got silently deleted before.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly PromptLibrary _library = new PromptLibrary();

        private TreeView _tree;
        private TextBox _name;
        private TextBox _description;
        private ComboBox _app;
        private NumericUpDown _sortOrder;
        private Label _category;
        private Label _preserved;
        private RichTextBox _editor;
        private ListBox _warnings;
        private ToolStripButton _save;
        private ToolStripDropDownButton _insert;
        private ToolStripStatusLabel _status;
        private ToolStripStatusLabel _dirtyLabel;
        private SplitContainer _split;

        private readonly Timer _highlightTimer = new Timer { Interval = 350 };
        private Font _editorFont;

        private PromptTemplate _current;
        private bool _loading;
        private bool _dirty;

        private readonly string _openAtRelativePath;

        public MainForm(string openAtRelativePath)
        {
            _openAtRelativePath = openAtRelativePath;
            BuildUi();
            LoadTree();
            SelectPrompt(_openAtRelativePath);
        }

        // -- construction --------------------------------------------------

        private void BuildUi()
        {
            // Scale from an explicit 96 DPI baseline. The manifest declares the
            // process DPI-aware, which stops Windows stretching the window into
            // blur — but it also means nothing scales the layout for us any
            // more. A designer-generated form would carry AutoScaleDimensions
            // from its .Designer.cs; this one is built in code, so it says so
            // here. Without both halves the window is crisp and half-size on a
            // 150% display.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "Supervertaler — Prompt Library";
            Width = 1180;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(820, 520);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // A missing icon is not worth failing to open the editor over.
            }

            _editorFont = new Font("Consolas", 10.5f);

            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };

            var newPrompt = new ToolStripButton("New prompt") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            newPrompt.Click += (s, e) => NewPrompt();

            var newFolder = new ToolStripButton("New folder") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            newFolder.Click += (s, e) => NewFolder();

            var delete = new ToolStripButton("Delete") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            delete.Click += (s, e) => DeleteSelected();

            _save = new ToolStripButton("Save") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
            _save.Click += (s, e) => Save();

            _insert = new ToolStripDropDownButton("Insert placeholder")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text
            };
            BuildInsertMenu();

            var reload = new ToolStripButton("Reload from disk") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            reload.Click += (s, e) => Reload();

            var openFolder = new ToolStripButton("Open folder") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            openFolder.Click += (s, e) => OpenLibraryFolder();

            var draft = new ToolStripButton("Draft for memoQ project…")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "AutoPrompt: have the AI write a prompt tailored to the document open in memoQ"
            };
            draft.Click += (s, e) => DraftForProject();

            toolbar.Items.AddRange(new ToolStripItem[]
            {
                newPrompt, newFolder, delete, new ToolStripSeparator(),
                _save, new ToolStripSeparator(),
                _insert, new ToolStripSeparator(),
                draft, new ToolStripSeparator(),
                reload, openFolder
            });

            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                PathSeparator = "/",
                ShowLines = true
            };
            _tree.BeforeSelect += TreeBeforeSelect;
            _tree.AfterSelect += TreeAfterSelect;

            var treeMenu = new ContextMenuStrip();
            treeMenu.Items.Add("Move to folder…", null, (s, e) => MoveSelected());
            treeMenu.Items.Add("Delete", null, (s, e) => DeleteSelected());
            _tree.ContextMenuStrip = treeMenu;

            // -- right-hand pane

            var fields = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 4,
                RowCount = 3,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 8, 8, 4)
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _name = new TextBox { Dock = DockStyle.Fill };
            _description = new TextBox { Dock = DockStyle.Fill };

            _app = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            _app.Items.AddRange(new object[] { "both", "trados", "memoq" });

            _sortOrder = new NumericUpDown { Minimum = 0, Maximum = 9999, Value = 100, Width = 70 };

            _category = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 0) };
            _preserved = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 0) };

            fields.Controls.Add(new Label { Text = "Name", AutoSize = true, Padding = new Padding(0, 4, 8, 0) }, 0, 0);
            fields.Controls.Add(_name, 1, 0);
            fields.Controls.Add(new Label { Text = "Available in", AutoSize = true, Padding = new Padding(12, 4, 6, 0) }, 2, 0);
            fields.Controls.Add(_app, 3, 0);

            fields.Controls.Add(new Label { Text = "Description", AutoSize = true, Padding = new Padding(0, 4, 8, 0) }, 0, 1);
            fields.Controls.Add(_description, 1, 1);
            fields.Controls.Add(new Label { Text = "Sort order", AutoSize = true, Padding = new Padding(12, 4, 6, 0) }, 2, 1);
            fields.Controls.Add(_sortOrder, 3, 1);

            var meta = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            meta.Controls.Add(_category);
            meta.Controls.Add(_preserved);
            fields.Controls.Add(meta, 1, 2);

            // FastRichTextBox: RichEdit 4.1 instead of 2.0 — see that class for
            // why a long highlighted prompt stuttered on mouse-wheel scroll.
            _editor = new FastRichTextBox
            {
                Dock = DockStyle.Fill,
                Font = _editorFont,
                AcceptsTab = true,
                WordWrap = true,
                DetectUrls = false,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            _editor.TextChanged += EditorTextChanged;

            _warnings = new ListBox
            {
                Dock = DockStyle.Bottom,
                Height = 74,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle,
                ForeColor = Color.FromArgb(0x8A, 0x50, 0x00)
            };

            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(_editor);
            right.Controls.Add(_warnings);
            right.Controls.Add(fields);
            _editor.BringToFront();

            // SplitterDistance is NOT set here. In the initializer the container
            // still has its default size, so the value is clamped and then
            // re-scaled by DPI autoscaling — which is why the tree opened at a
            // third of its intended width. It is applied in OnShown, when the
            // layout is real, from the saved geometry or the default.
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1
            };
            _split.Panel1.Controls.Add(_tree);
            _split.Panel2.Controls.Add(right);

            var strip = new StatusStrip();
            _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _dirtyLabel = new ToolStripStatusLabel { Text = "" };
            strip.Items.Add(_status);
            strip.Items.Add(_dirtyLabel);

            Controls.Add(_split);
            Controls.Add(toolbar);
            Controls.Add(strip);

            _name.TextChanged += (s, e) => MarkDirty();
            _description.TextChanged += (s, e) => MarkDirty();
            _app.SelectedIndexChanged += (s, e) => MarkDirty();
            _sortOrder.ValueChanged += (s, e) => MarkDirty();

            _highlightTimer.Tick += (s, e) => { _highlightTimer.Stop(); Highlight(); };

            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.S) { Save(); e.Handled = true; }
            };

            FormClosing += (s, e) =>
            {
                if (!ConfirmDiscard()) { e.Cancel = true; return; }
                SaveWindowGeometry();
            };

            Shown += (s, e) => ApplyWindowGeometry();

            SetEditingEnabled(false);
            _status.Text = SupervertalerPaths.PromptLibraryDir;
        }

        // -- window geometry -----------------------------------------------

        /// <summary>
        /// %LocalAppData%\Supervertaler.PromptEditor\window.txt — five integers:
        /// left, top, width, height, splitter, plus 1/0 for maximised.
        /// A plain line of numbers rather than JSON: there is nothing here worth
        /// a parser, and a corrupt file must only ever cost the default layout.
        /// </summary>
        private static string GeometryFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Supervertaler.PromptEditor", "window.txt");

        /// <summary>
        /// Applies the saved size, position and splitter — or the defaults on a
        /// first run. Runs from Shown, after layout and DPI scaling are real;
        /// setting SplitterDistance any earlier is what made the tree open at a
        /// third of its width.
        /// </summary>
        private void ApplyWindowGeometry()
        {
            var applied = false;

            try
            {
                if (File.Exists(GeometryFile))
                {
                    var parts = File.ReadAllText(GeometryFile).Split(',');
                    if (parts.Length >= 6)
                    {
                        var v = new int[6];
                        for (var i = 0; i < 6; i++) v[i] = int.Parse(parts[i].Trim());

                        var bounds = new Rectangle(v[0], v[1], v[2], v[3]);

                        // A monitor that has since been unplugged must not leave
                        // the window stranded somewhere unreachable.
                        if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(bounds)))
                        {
                            StartPosition = FormStartPosition.Manual;
                            Bounds = bounds;
                        }

                        if (v[5] == 1) WindowState = FormWindowState.Maximized;

                        _split.SplitterDistance = Math.Max(
                            _split.Panel1MinSize,
                            Math.Min(v[4], _split.Width - _split.Panel2MinSize - _split.SplitterWidth));

                        applied = true;
                    }
                }
            }
            catch
            {
                // Fall through to the default below.
            }

            if (!applied)
            {
                // Wide enough for the longest prompt names in a real library
                // ("Explain selection (within context of surrounding segments)")
                // without giving up the editor's share of a 1180px window.
                _split.SplitterDistance = Math.Min(360, _split.Width / 3);
            }
        }

        private void SaveWindowGeometry()
        {
            try
            {
                var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                var maximized = WindowState == FormWindowState.Maximized ? 1 : 0;

                Directory.CreateDirectory(Path.GetDirectoryName(GeometryFile));
                File.WriteAllText(GeometryFile, string.Join(",", new[]
                {
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    _split.SplitterDistance, maximized
                }));
            }
            catch
            {
                // Losing the geometry costs one resize next launch; failing the
                // close would cost unsaved work.
            }
        }

        private void BuildInsertMenu()
        {
            _insert.DropDownItems.Clear();

            foreach (var p in Placeholders.All)
            {
                var label = p.Legacy
                    ? p.Token + "   — " + p.Meaning + " (legacy)"
                    : p.Token + "   — " + p.Meaning;

                var item = new ToolStripMenuItem(label);
                var token = p.Token;
                item.Click += (s, e) => InsertAtCaret(token);
                if (p.Legacy) item.ForeColor = SystemColors.GrayText;
                if (!p.FilledByMemoQ) item.ToolTipText = "memoQ leaves this empty — see the warnings pane.";
                _insert.DropDownItems.Add(item);
            }
        }

        // -- tree ----------------------------------------------------------

        private void LoadTree()
        {
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                var root = _library.GetFolderStructure();
                if (root == null) return;

                foreach (var child in root.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    _tree.Nodes.Add(BuildNode(child));

                foreach (var p in root.Prompts.OrderBy(p => p.SortOrder).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                    _tree.Nodes.Add(BuildLeaf(p));

                _tree.ExpandAll();
                if (_tree.Nodes.Count > 0) _tree.Nodes[0].EnsureVisible();
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private TreeNode BuildNode(PromptFolderNode folder)
        {
            var node = new TreeNode(folder.Name) { Tag = folder };

            foreach (var child in folder.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                node.Nodes.Add(BuildNode(child));

            foreach (var p in folder.Prompts.OrderBy(p => p.SortOrder).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                node.Nodes.Add(BuildLeaf(p));

            return node;
        }

        private static TreeNode BuildLeaf(PromptTemplate p)
        {
            var label = p.Name;
            if (p.IsReadOnly) label += "  (read-only)";
            if (p.IsTransform) label += "  [transform]";

            return new TreeNode(label)
            {
                Tag = p,
                ForeColor = p.IsReadOnly ? SystemColors.GrayText : SystemColors.WindowText
            };
        }

        private void TreeBeforeSelect(object sender, TreeViewCancelEventArgs e)
        {
            if (!ConfirmDiscard()) e.Cancel = true;
        }

        private void TreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            var prompt = e.Node?.Tag as PromptTemplate;
            if (prompt == null)
            {
                _current = null;
                SetEditingEnabled(false);
                Clear();
                _status.Text = (e.Node?.Tag as PromptFolderNode)?.RelativePath ?? SupervertalerPaths.PromptLibraryDir;
                return;
            }

            LoadPrompt(prompt);
        }

        private void SelectPrompt(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return;

            foreach (var node in AllNodes(_tree.Nodes))
            {
                var p = node.Tag as PromptTemplate;
                if (p != null && string.Equals(p.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    _tree.SelectedNode = node;
                    node.EnsureVisible();
                    return;
                }
            }
        }

        private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                yield return n;
                foreach (var c in AllNodes(n.Nodes)) yield return c;
            }
        }

        // -- load / save ---------------------------------------------------

        private void LoadPrompt(PromptTemplate p)
        {
            _loading = true;
            try
            {
                _current = p;

                _name.Text = p.Name ?? "";
                _description.Text = p.Description ?? "";
                _app.SelectedItem = NormaliseApp(p.App);
                _sortOrder.Value = Math.Max(_sortOrder.Minimum, Math.Min(_sortOrder.Maximum, p.SortOrder));
                _category.Text = "Folder: " + (string.IsNullOrEmpty(p.Category) ? "(root)" : p.Category);

                // Showing what the parser did not recognise is reassurance, not
                // decoration: these keys used to disappear on save, and an author
                // who put them there should be able to see they are still there.
                var kept = p.UnrecognizedFrontmatter;
                _preserved.Text = (kept != null && kept.Count > 0)
                    ? "     Also kept: " + string.Join(", ", kept.Select(KeyOf).Where(k => k != null))
                    : "";

                _editor.Text = p.Content ?? "";
                _status.Text = p.FilePath ?? "";

                SetEditingEnabled(!p.IsReadOnly);
            }
            finally
            {
                _loading = false;
            }

            _dirty = false;
            UpdateDirtyUi();
            Highlight();
        }

        private static string KeyOf(string frontmatterLine)
        {
            if (string.IsNullOrWhiteSpace(frontmatterLine)) return null;
            var i = frontmatterLine.IndexOf(':');
            return i > 0 ? frontmatterLine.Substring(0, i).Trim() : null;
        }

        private static string NormaliseApp(string app)
        {
            if (string.IsNullOrWhiteSpace(app)) return "both";
            app = app.Trim().ToLowerInvariant();
            return (app == "trados" || app == "memoq") ? app : "both";
        }

        private void Clear()
        {
            _loading = true;
            try
            {
                _name.Text = "";
                _description.Text = "";
                _app.SelectedItem = "both";
                _sortOrder.Value = 100;
                _category.Text = "";
                _preserved.Text = "";
                _editor.Text = "";
                _warnings.Items.Clear();
            }
            finally
            {
                _loading = false;
            }
        }

        private void Save()
        {
            if (_current == null || !_dirty) return;

            if (_current.IsReadOnly)
            {
                MessageBox.Show(this, "This prompt is read-only.", "Supervertaler",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var name = _name.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "A prompt needs a name.", "Supervertaler",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _name.Focus();
                return;
            }

            _current.Name = name;
            _current.Description = _description.Text.Trim();
            _current.App = (string)_app.SelectedItem ?? "both";
            _current.SortOrder = (int)_sortOrder.Value;
            _current.Content = _editor.Text;

            try
            {
                _library.SavePrompt(_current);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save.\r\n\r\n" + ex.Message, "Supervertaler",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _dirty = false;
            UpdateDirtyUi();

            // SavePrompt renames the file when the name changed and refreshes the
            // library, so the tree is rebuilt and the prompt reselected by its
            // new relative path rather than by object identity.
            var relative = _current.RelativePath;
            LoadTree();
            SelectPrompt(relative);
        }

        private void Reload()
        {
            if (!ConfirmDiscard()) return;

            var relative = _current?.RelativePath;
            _library.Refresh();
            LoadTree();
            SelectPrompt(relative);
        }

        // -- commands ------------------------------------------------------

        private void NewPrompt()
        {
            if (!ConfirmDiscard()) return;

            var folder = SelectedFolderRelativePath();

            var name = Prompt("New prompt", "Name:", "New prompt");
            if (string.IsNullOrWhiteSpace(name)) return;

            var template = new PromptTemplate
            {
                Name = name.Trim(),
                Category = folder,
                App = "both",
                Content = "You are a professional {{SOURCE_LANGUAGE}} to {{TARGET_LANGUAGE}} translator.\r\n\r\n"
            };

            try
            {
                _library.SavePrompt(template);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not create the prompt.\r\n\r\n" + ex.Message,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            LoadTree();
            SelectPrompt(template.RelativePath);
        }

        /// <summary>
        /// AutoPrompt. The drafting happens inside memoQ's process over the
        /// bridge — that is where the captured document, the confirmed pairs,
        /// the glossary and the API key all are. The result is saved into the
        /// library under Translate and opened here for review, exactly as a
        /// hand-written prompt would be.
        /// </summary>
        private void DraftForProject()
        {
            if (!ConfirmDiscard()) return;

            var bridge = MemoQBridgeClient.TryConnect(out var reason);
            if (bridge == null)
            {
                MessageBox.Show(this, reason, "AutoPrompt", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            MemoQBridgeClient.AutoPromptResult result;
            using (bridge)
            using (var dlg = new AutoPromptDialog(bridge))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
                result = dlg.Result;
            }

            // Same naming rule as the Trados plugin: suggested name, "v2", "v3"…
            // when it already exists, so a re-draft never overwrites the last one.
            var existing = new HashSet<string>(
                _library.GetAllPrompts().Select(p => p.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            var name = string.IsNullOrWhiteSpace(result.SuggestedName) ? "AutoPrompt" : result.SuggestedName.Trim();
            if (existing.Contains(name))
            {
                var v = 2;
                while (existing.Contains(name + " v" + v)) v++;
                name = name + " v" + v;
            }

            var template = new PromptTemplate
            {
                Name = name,
                Category = "Translate",
                App = "memoq",
                Description = result.Description ?? "Generated by AutoPrompt from the memoQ project",
                Content = result.Content ?? ""
            };

            try
            {
                _library.SavePrompt(template);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The prompt was drafted but could not be saved.\r\n\r\n" + ex.Message,
                    "AutoPrompt", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            LoadTree();
            SelectPrompt(template.RelativePath);

            _status.Text = "Drafted from " + result.TermCount + " glossary term(s) and "
                + result.ConfirmedPairCount + " confirmed segment(s)"
                + (string.IsNullOrWhiteSpace(result.Domain) ? "" : " · domain: " + result.Domain)
                + " — review, then select it in memoQ under Prompt.";
        }

        private void NewFolder()
        {
            var parent = SelectedFolderRelativePath();

            var name = Prompt("New folder", "Folder name:", "");
            if (string.IsNullOrWhiteSpace(name)) return;

            var relative = string.IsNullOrEmpty(parent) ? name.Trim() : parent + "/" + name.Trim();

            try
            {
                _library.CreateFolder(relative);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not create the folder.\r\n\r\n" + ex.Message,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            LoadTree();
        }

        private void DeleteSelected()
        {
            var node = _tree.SelectedNode;
            if (node == null) return;

            var prompt = node.Tag as PromptTemplate;
            if (prompt != null)
            {
                if (prompt.IsReadOnly)
                {
                    MessageBox.Show(this, "This prompt is read-only.", "Supervertaler",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (MessageBox.Show(this, "Delete \"" + prompt.Name + "\"?\r\n\r\nThis deletes the file.",
                        "Supervertaler", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                _library.DeletePrompt(prompt);
                _dirty = false;
                _current = null;
                Clear();
                SetEditingEnabled(false);
                LoadTree();
                return;
            }

            var folder = node.Tag as PromptFolderNode;
            if (folder == null) return;

            var count = CountPrompts(folder);
            var message = count == 0
                ? "Delete the folder \"" + folder.Name + "\"?"
                : "Delete the folder \"" + folder.Name + "\" and the " + count + " prompt(s) in it?";

            if (MessageBox.Show(this, message, "Supervertaler",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            _library.DeleteFolder(folder.RelativePath);
            _dirty = false;
            _current = null;
            Clear();
            SetEditingEnabled(false);
            LoadTree();
        }

        private static int CountPrompts(PromptFolderNode folder)
        {
            return folder.Prompts.Count + folder.Children.Sum(CountPrompts);
        }

        private void MoveSelected()
        {
            var prompt = _tree.SelectedNode?.Tag as PromptTemplate;
            if (prompt == null) return;

            if (prompt.IsReadOnly)
            {
                MessageBox.Show(this, "This prompt is read-only.", "Supervertaler",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!ConfirmDiscard()) return;

            var folders = new List<string> { "" };
            CollectFolders(_library.GetFolderStructure(), folders);

            using (var dlg = new FolderPickerDialog(folders, prompt.Category))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    _library.MovePrompt(prompt, dlg.Selected);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not move the prompt.\r\n\r\n" + ex.Message,
                        "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                LoadTree();
                SelectPrompt(prompt.RelativePath);
            }
        }

        private static void CollectFolders(PromptFolderNode node, List<string> into)
        {
            if (node == null) return;

            foreach (var child in node.Children.OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                into.Add(child.RelativePath);
                CollectFolders(child, into);
            }
        }

        private string SelectedFolderRelativePath()
        {
            var node = _tree.SelectedNode;
            while (node != null)
            {
                var folder = node.Tag as PromptFolderNode;
                if (folder != null) return folder.RelativePath ?? "";
                node = node.Parent;
            }
            return "";
        }

        private void OpenLibraryFolder()
        {
            try
            {
                var dir = SupervertalerPaths.PromptLibraryDir;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Supervertaler",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InsertAtCaret(string text)
        {
            if (!_editor.Enabled) return;

            var at = _editor.SelectionStart;
            _editor.SelectedText = text;
            _editor.SelectionStart = at + text.Length;
            _editor.SelectionLength = 0;
            _editor.Focus();
        }

        // -- editing state -------------------------------------------------

        private void EditorTextChanged(object sender, EventArgs e)
        {
            if (_loading) return;

            MarkDirty();
            _highlightTimer.Stop();
            _highlightTimer.Start();
        }

        private void Highlight()
        {
            if (_editor.IsDisposed || !_editor.IsHandleCreated) return;

            var found = MarkdownHighlighter.Apply(_editor, _editorFont);
            ReportWarnings(found);
        }

        /// <summary>
        /// Turns the placeholders found into the two things worth telling an
        /// author: one they misspelled, and one the host they are targeting will
        /// not fill in.
        /// </summary>
        private void ReportWarnings(List<string> found)
        {
            _warnings.BeginUpdate();
            try
            {
                _warnings.Items.Clear();

                var distinct = found.Distinct(StringComparer.Ordinal).ToList();

                foreach (var token in distinct.Where(t => !Placeholders.IsKnown(t)))
                {
                    _warnings.Items.Add(token + " is not a placeholder Supervertaler substitutes — "
                        + "it will reach the model literally.");
                }

                var app = NormaliseApp((string)_app.SelectedItem);
                if (app == "both" || app == "memoq")
                {
                    var unfilled = distinct
                        .Select(Placeholders.Find)
                        .Where(p => p != null && !p.FilledByMemoQ)
                        .Select(p => p.Token)
                        .ToList();

                    if (unfilled.Count > 0)
                    {
                        _warnings.Items.Add("In memoQ, " + string.Join(", ", unfilled)
                            + " is replaced with nothing. memoQ sends the segments in a numbered "
                            + "batch instead, so a prompt for it should refer to those, not to these.");
                    }
                }

                _warnings.Visible = _warnings.Items.Count > 0;
            }
            finally
            {
                _warnings.EndUpdate();
            }
        }

        private void MarkDirty()
        {
            if (_loading || _current == null) return;
            if (_dirty) return;

            _dirty = true;
            UpdateDirtyUi();
        }

        private void UpdateDirtyUi()
        {
            _save.Enabled = _dirty && _current != null && !_current.IsReadOnly;
            _dirtyLabel.Text = _dirty ? "Unsaved changes" : "";
        }

        private void SetEditingEnabled(bool enabled)
        {
            _name.ReadOnly = !enabled;
            _description.ReadOnly = !enabled;
            _app.Enabled = enabled;
            _sortOrder.Enabled = enabled;
            _editor.ReadOnly = !enabled;
            _editor.BackColor = enabled ? SystemColors.Window : SystemColors.Control;
            _insert.Enabled = enabled;
            UpdateDirtyUi();
        }

        private bool ConfirmDiscard()
        {
            if (!_dirty || _current == null) return true;

            var answer = MessageBox.Show(this,
                "Save changes to \"" + (_current.Name ?? "this prompt") + "\"?",
                "Supervertaler", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes) Save();
            else { _dirty = false; UpdateDirtyUi(); }

            return true;
        }

        // -- small dialogs -------------------------------------------------

        private string Prompt(string title, string label, string initial)
        {
            using (var dlg = new TextInputDialog(title, label, initial))
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.Value : null;
        }
    }
}
