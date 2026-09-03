using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Supervertaler.Core;
using Supervertaler.MemoQ.Core;
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
        private ToolStripButton _glossary;
        private ToolStripButton _prompt;
        private ToolStripStatusLabel _dirtyLabel;
        private SplitContainer _split;

        private readonly Timer _highlightTimer = new Timer { Interval = 350 };
        private Font _editorFont;

        private PromptTemplate _current;
        private bool _loading;

        /// <summary>
        /// Set while the syntax highlighter is repainting.
        ///
        /// A RichTextBox raises TextChanged for SelectionColor and SelectionFont,
        /// so the highlighter's own repaint arrived as if the user had typed. That
        /// marked an untouched prompt as modified within 350ms of opening it, and
        /// it restarted the debounce timer, which fired the highlighter, which
        /// raised TextChanged again - a loop that re-highlighted the whole
        /// document three times a second for as long as the editor was open, and
        /// made "Save changes?" unanswerable: discarding cleared the flag and the
        /// next tick set it again.
        /// </summary>
        private bool _highlighting;
        private bool _dirty;

        private string _openAtRelativePath;

        public MainForm(string openAtRelativePath)
        {
            _openAtRelativePath = openAtRelativePath;
            BuildUi();
            RetagPromptFiles();
            LoadTree();
            SelectPrompt(_openAtRelativePath);
        }

        /// <summary>
        /// Gives every prompt file the marker its app field says it should have,
        /// and repairs the active-prompt setting for anything that moved.
        ///
        /// Cheap and idempotent, so it runs at every start rather than once behind
        /// a flag: a prompt whose product changed in another session, or a file
        /// renamed by hand in Explorer, is corrected the next time the editor
        /// opens. Nothing is renamed unless the marker is actually wrong.
        ///
        /// One thing this cannot repair is the Trados plugin's own record of its
        /// selected prompt, which lives in its settings and is not ours to write.
        /// A Trados-only prompt that gets its marker for the first time therefore
        /// has to be re-selected there once.
        /// </summary>
        private void RetagPromptFiles()
        {
            try
            {
                var moved = _library.RetagFiles();
                if (moved.Count == 0) return;

                var active = SharedSettings.PromptPath;
                foreach (var m in moved)
                {
                    if (string.Equals(m.OldRelativePath, active, StringComparison.OrdinalIgnoreCase))
                    {
                        SharedSettings.PromptPath = m.NewRelativePath;
                        break;
                    }
                }

                // Anything opened by path from the command line moved too.
                if (_openAtRelativePath != null)
                {
                    foreach (var m in moved)
                    {
                        if (string.Equals(m.OldRelativePath, _openAtRelativePath, StringComparison.OrdinalIgnoreCase))
                        {
                            _openAtRelativePath = m.NewRelativePath;
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A library that cannot be tidied is still a library worth opening.
            }
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

            Text = "Supervertaler for memoQ";
            Width = 1180;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(820, 520);

            AppIcon.Apply(this);

            _editorFont = new Font("Consolas", 10.5f);

            // A menu bar rather than one strip of eleven buttons. The strip had
            // come to hold file operations, editing helpers, memoQ integration and
            // settings side by side with nothing to say which was which, and the
            // settings end of it is still growing. Menus group by purpose and cost
            // no width; the toolbar keeps only what is reached for constantly.
            var menu = new MenuStrip();

            var fileMenu = new ToolStripMenuItem("&File");
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("&New prompt", null, (s, e) => NewPrompt())
            {
                ShortcutKeys = Keys.Control | Keys.N
            });
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("New &folder", null, (s, e) => NewFolder()));
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Delete", null, (s, e) => DeleteSelected()));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());

            // Ctrl+S is already handled in the form's KeyDown. Declaring it as a
            // real shortcut here too would save twice on one keystroke, so this
            // only advertises it.
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Save", null, (s, e) => Save())
            {
                ShortcutKeyDisplayString = "Ctrl+S"
            });
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Reload from disk", null, (s, e) => Reload())
            {
                ShortcutKeys = Keys.F5
            });
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("Open library f&older", null, (s, e) => OpenLibraryFolder()));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (s, e) => Close()));

            var memoqMenu = new ToolStripMenuItem("&memoQ");
            // Named for the feature, not for what it does. AutoPrompt is what the
            // Trados plugin calls it in every string it shows, and what the docs
            // and the website call it, so a user who has read about AutoPrompt
            // finds a button called AutoPrompt. The tooltip carries the
            // explanation the name drops.
            memoqMenu.DropDownItems.Add(new ToolStripMenuItem("&AutoPrompt…", null, (s, e) => DraftForProject())
            {
                ToolTipText = "AutoPrompt: have the AI write a prompt tailored to the document open in memoQ"
            });
            memoqMenu.DropDownItems.Add(new ToolStripSeparator());
            memoqMenu.DropDownItems.Add(new ToolStripMenuItem("&Export this prompt's terms as a glossary…", null, (s, e) => ExportGlossary()));
            memoqMenu.DropDownItems.Add(new ToolStripMenuItem("&Choose the active glossary…", null, (s, e) => ChooseGlossary()));
            memoqMenu.DropDownItems.Add(new ToolStripMenuItem("Choose the active &prompt…", null, (s, e) => ChoosePrompt()));

            // The first setting to move out of memoQ's dialog. It says how you are
            // working at this moment, chat-driven or key-driven, which is not a
            // property of any one project.
            var bridgeMode = new ToolStripMenuItem("&Pre-translate via Claude Desktop (MCP)")
            {
                CheckOnClick = true,
                Checked = SharedSettings.BridgeMode,
                ToolTipText = "On: Pre-translate hands the segments to the chat and inserts what it stages back. "
                    + "Off: Pre-translate calls the model with the API key set in memoQ."
            };
            bridgeMode.CheckedChanged += (s, e) =>
            {
                if (SharedSettings.BridgeMode == bridgeMode.Checked) return;
                SharedSettings.BridgeMode = bridgeMode.Checked;
                _status.Text = bridgeMode.Checked
                    ? "Pre-translate will hand segments to Claude Desktop."
                    : "Pre-translate will call the model directly.";
            };

            var settingsMenu = new ToolStripMenuItem("&Settings");
            settingsMenu.DropDownItems.Add(new ToolStripMenuItem("&Translation settings…", null, (s, e) => ShowSettings()));
            settingsMenu.DropDownItems.Add(new ToolStripSeparator());

            // Also on the menu itself, not only inside the dialog: this is the one
            // that gets flipped between jobs rather than set once.
            settingsMenu.DropDownItems.Add(bridgeMode);

            // memoQ's dialog writes the same file, so re-read on opening rather
            // than trust what this menu was last showing.
            settingsMenu.DropDownOpening += (s, e) => bridgeMode.Checked = SharedSettings.BridgeMode;

            var helpMenu = new ToolStripMenuItem("&Help");
            helpMenu.DropDownItems.Add(new ToolStripMenuItem("&Documentation", null, (s, e) => OpenDocumentation()));

            menu.Items.AddRange(new ToolStripItem[] { fileMenu, memoqMenu, settingsMenu, helpMenu });

            // Only what is used while actually writing a prompt.
            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };

            var newPromptButton = new ToolStripButton("New prompt") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            newPromptButton.Click += (s, e) => NewPrompt();

            _save = new ToolStripButton("Save") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
            _save.Click += (s, e) => Save();

            _insert = new ToolStripDropDownButton("Insert placeholder")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text
            };
            BuildInsertMenu();

            var draft = new ToolStripButton("AutoPrompt…")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "AutoPrompt: have the AI write a prompt tailored to the document open in memoQ"
            };
            draft.Click += (s, e) => DraftForProject();

            toolbar.Items.AddRange(new ToolStripItem[]
            {
                newPromptButton, _save, new ToolStripSeparator(),
                _insert, new ToolStripSeparator(),
                draft
            });

            // What memoQ will actually use, in the one place you cannot miss it.
            // These two decide every translation between them, and both used to be
            // findable only by opening a dialog: the glossary sat in the far
            // bottom-right corner of the status bar with nothing to say it could
            // be clicked, and the prompt was visible only inside memoQ's own
            // options dialog, six clicks deep.
            var context = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ToolStripProfessionalRenderer(),
                Padding = new Padding(4, 2, 4, 2)
            };

            _prompt = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                AutoToolTip = false
            };
            _prompt.Click += (s, e) => ChoosePrompt();

            _glossary = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                AutoToolTip = false
            };
            _glossary.Click += (s, e) => ChooseGlossary();

            context.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripLabel("In use by memoQ:") { ForeColor = SystemColors.GrayText },
                _prompt,
                new ToolStripSeparator(),
                _glossary
            });

            RefreshPrompt();
            RefreshGlossary();

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

            // Added before the toolbar so it sits below it: docked children are
            // laid out from the back of the collection forwards, so the last Top
            // control added claims the top of the window.
            Controls.Add(context);
            Controls.Add(toolbar);

            // Added after the toolbar on purpose: docked children are laid out
            // from the back of the collection forwards, so the last Top control
            // added claims the very top of the window.
            Controls.Add(menu);
            Controls.Add(strip);
            MainMenuStrip = menu;

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
                    ? p.Token + "   – " + p.Meaning + " (legacy)"
                    : p.Token + "   – " + p.Meaning;

                var item = new ToolStripMenuItem(label);
                var token = p.Token;
                item.Click += (s, e) => InsertAtCaret(token);
                if (p.Legacy) item.ForeColor = SystemColors.GrayText;
                if (!p.FilledByMemoQ) item.ToolTipText = "memoQ leaves this empty – see the warnings pane.";
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

        /// <summary>
        /// The product this editor belongs to. The library is shared with
        /// Supervertaler for Trados, and the two need different prompts: memoQ
        /// delivers single unnumbered segments as well as batches, its tags arrive
        /// in a different notation, and the translator-comment delimiters had to
        /// change because memoQ's default font cannot render the Trados ones. A
        /// prompt written for one is wrong in the other, and until now the only
        /// way to see which was which was to open it.
        /// </summary>
        private const string ThisApp = "memoq";

        private static TreeNode BuildLeaf(PromptTemplate p)
        {
            var label = p.Name;
            if (p.IsReadOnly) label += "  (read-only)";
            if (p.IsTransform) label += "  [transform]";

            // Named only when it is not for both, so the common case stays quiet
            // and a suffix always means something.
            // Quotes because at least one prompt in the live library writes the
            // value as "memoq" with them; the flag means the same either way.
            var app = (p.App ?? "both").Trim().Trim(QuoteChars);
            var forThisApp = app.Length == 0
                || app.Equals("both", StringComparison.OrdinalIgnoreCase)
                || app.Equals(ThisApp, StringComparison.OrdinalIgnoreCase);

            if (!app.Equals("both", StringComparison.OrdinalIgnoreCase) && app.Length > 0)
                label += "   · " + Describe(app);

            // Dimmed rather than coloured: this is one binary fact, and dimming
            // already carries "does not apply here" without asking anyone to learn
            // a colour code or to be able to tell two colours apart.
            return new TreeNode(label)
            {
                Tag = p,
                ForeColor = p.IsReadOnly || !forThisApp ? SystemColors.GrayText : SystemColors.WindowText,
                ToolTipText = forThisApp ? null
                    : "This prompt is marked for " + Describe(app) + ". memoQ will not offer it, "
                      + "and will fall back to the instructions in its own settings if it is somehow selected."
            };
        }

        /// <summary>At least one prompt in the live library writes the flag as
        /// "memoq" with quotes; it means the same either way.</summary>
        private static readonly char[] QuoteChars = { (char)34, (char)39 };

        private static string Describe(string app)
        {
            if (app.Equals("memoq", StringComparison.OrdinalIgnoreCase)) return "memoQ only";
            if (app.Equals("trados", StringComparison.OrdinalIgnoreCase)) return "Trados only";
            if (app.Equals("workbench", StringComparison.OrdinalIgnoreCase)) return "Workbench only";
            return app;
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

            _dirty = false;
            UpdateDirtyUi();
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

        /// <summary>
        /// Writes the current prompt. Returns false when it did not, so a caller
        /// that was about to close a window or move away on the strength of a save
        /// can stop instead. Answering "Save changes?" with Yes and having the save
        /// quietly fail is how an unanswerable dialog gets built.
        /// </summary>
        private bool Save()
        {
            if (_current == null || !_dirty) return true;

            if (_current.IsReadOnly)
            {
                MessageBox.Show(this, "This prompt is read-only.", "Supervertaler",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var name = _name.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "A prompt needs a name.", "Supervertaler",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _name.Focus();
                return false;
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
                return false;
            }

            _dirty = false;
            UpdateDirtyUi();

            // SavePrompt renames the file when the name changed and refreshes the
            // library, so the tree is rebuilt and the prompt reselected by its
            // new relative path rather than by object identity.
            var relative = _current.RelativePath;
            LoadTree();
            SelectPrompt(relative);

            return true;
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
        private static string FirstNonEmpty(string preferred, string fallback)
        {
            return string.IsNullOrWhiteSpace(preferred) ? (fallback ?? string.Empty) : preferred;
        }

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

            // Stamped with the pair it was drafted against, so selecting it in a
            // project running the other way is caught rather than producing a
            // confident translation against instructions for the opposite job.
            // The response is authoritative — it comes from the engine that did
            // the drafting — and the recorded project pair is the fallback for an
            // older plugin that does not send it.
            var template = new PromptTemplate
            {
                Name = name,
                Category = "Translate",
                App = "memoq",
                Description = result.Description ?? "Generated by AutoPrompt from the memoQ project",
                Content = result.Content ?? "",
                SourceLang = FirstNonEmpty(result.SourceLang, SharedSettings.SourceLang),
                TargetLang = FirstNonEmpty(result.TargetLang, SharedSettings.TargetLang)
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
                + " – review, then select it in memoQ under Prompt.";
        }

        /// <summary>
        /// The prompt's locked-terms table becomes the project glossary. Written
        /// to &lt;Supervertaler data folder&gt;\memoq\glossaries\&lt;prompt&gt;.txt, in
        /// the format the terminology plugin reads; then, if memoQ is running,
        /// made the active glossary over the bridge. A general glossary flags a
        /// term in every paragraph for senses the document does not use; a
        /// dozen terms chosen for this job are what check_terminology and the
        /// terminology pane should work from.
        /// </summary>
        private void ExportGlossary()
        {
            if (_current == null) return;

            var entries = PromptGlossaryExtractor.Extract(_editor.Text);
            if (entries.Count == 0)
            {
                MessageBox.Show(this,
                    "No glossary table found in this prompt.\r\n\r\nThe export looks for a Markdown table whose header names a source and a target column – the PROJECT-SPECIFIC GLOSSARY that AutoPrompt writes, or any table laid out the same way.",
                    "Export glossary", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var dir = Path.Combine(SupervertalerPaths.Root, "memoq", "glossaries");
            var safe = string.Concat((_current.Name ?? "glossary").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            var path = Path.Combine(dir, safe + ".txt");

            var forbidden = entries.Count(e => e.Forbidden);
            var summary = entries.Count + " term(s)" + (forbidden > 0 ? " including " + forbidden + " forbidden" : "")
                + "\r\n\r\nWrite to:\r\n" + path
                + (File.Exists(path) ? "\r\n\r\n(The file exists and will be replaced.)" : "")
                + "\r\n\r\nMake it the active glossary in memoQ as well?";

            var choice = MessageBox.Show(this, summary, "Export glossary", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) return;

            try
            {
                Directory.CreateDirectory(dir);
                // Stamped with the direction of the project memoQ last worked
                // in. Without it the file's direction lives only in its filename,
                // which nothing reads, and a glossary facing the wrong way finds
                // nothing and says nothing.
                File.WriteAllText(
                    path,
                    PromptGlossaryExtractor.ToGlossaryText(
                        entries, _current.Name, SharedSettings.SourceLang, SharedSettings.TargetLang),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not write the glossary.\r\n\r\n" + ex.Message, "Export glossary", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (choice != DialogResult.Yes)
            {
                _status.Text = "Glossary written: " + path;
                return;
            }

            // Written straight to the shared setting rather than asked of a
            // running memoQ over the bridge. It is the same single value either
            // way, the plugin re-reads the file within seconds, and doing it here
            // means exporting works with memoQ closed instead of ending in
            // "select it by hand".
            SharedSettings.GlossaryPath = path;
            RefreshGlossary();
            _status.Text = "Glossary written and made active: " + path;
        }

        /// <summary>
        /// Names the prompt memoQ will use. It governs more of a translation than
        /// anything else here and, until now, could only be seen inside memoQ's
        /// own options dialog.
        /// </summary>
        private void RefreshPrompt()
        {
            var path = SharedSettings.PromptPath;

            if (string.IsNullOrWhiteSpace(path))
            {
                _prompt.Text = "Prompt: the instructions in memoQ's settings";
                _prompt.ForeColor = SystemColors.GrayText;
                _prompt.ToolTipText = "No library prompt is selected, so memoQ uses the Instructions box "
                    + "in its own Supervertaler settings. Click to choose a prompt.";
                return;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            var prompt = FindPrompt(path);

            _prompt.Text = "Prompt: " + name;

            if (prompt == null)
            {
                _prompt.ForeColor = Color.Firebrick;
                _prompt.ToolTipText = "memoQ has this prompt selected, but it is not in the library or is "
                    + "marked for another product. memoQ will fall back to the Instructions box in its "
                    + "own settings.";
                return;
            }

            // A prompt that declares a pair is checked against the project memoQ
            // last worked in, the same test the glossary gets.
            var declared = string.IsNullOrWhiteSpace(prompt.SourceLang) || string.IsNullOrWhiteSpace(prompt.TargetLang)
                ? null
                : prompt.SourceLang + " to " + prompt.TargetLang;

            var project = string.IsNullOrWhiteSpace(SharedSettings.SourceLang) || string.IsNullOrWhiteSpace(SharedSettings.TargetLang)
                ? null
                : SharedSettings.SourceLang + " to " + SharedSettings.TargetLang;

            var mismatched = declared != null && project != null
                && GlossaryDirection.Compare(SharedSettings.SourceLang, SharedSettings.TargetLang,
                        prompt.SourceLang, prompt.TargetLang) != GlossaryDirection.Relation.Aligned;

            _prompt.ForeColor = mismatched ? Color.Firebrick : SystemColors.ControlText;
            _prompt.ToolTipText = mismatched
                ? $"This prompt was written for {declared}, but the project is {project}. Its "
                  + "instructions and locked terminology are for the other direction."
                : (declared == null ? path : path + "   (" + declared + ")");
        }

        private static Supervertaler.Core.Models.PromptTemplate FindPrompt(string relativePath)
        {
            try
            {
                return new Supervertaler.Core.PromptLibrary().GetAllPrompts()
                    .FirstOrDefault(p => p != null && ForThisApp(p)
                        && string.Equals(p.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        private static bool ForThisApp(Supervertaler.Core.Models.PromptTemplate p)
        {
            var app = (p.App ?? "both").Trim().Trim(QuoteChars);
            return app.Length == 0
                || app.Equals("both", StringComparison.OrdinalIgnoreCase)
                || app.Equals(ThisApp, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Picks the prompt memoQ will use, writing it to the shared settings the
        /// plugin reads. Works with memoQ closed, like the glossary chooser.
        /// Prompts belonging to the other product are not offered: memoQ cannot
        /// run them and would fall back silently.
        /// </summary>
        private void ChoosePrompt()
        {
            List<Supervertaler.Core.Models.PromptTemplate> prompts;
            try
            {
                prompts = new Supervertaler.Core.PromptLibrary().GetAllPrompts()
                    .Where(p => p != null && !p.IsQuickLauncher && ForThisApp(p))
                    .OrderBy(p => p.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not read the prompt library.\r\n\r\n" + ex.Message,
                    "Choose a prompt", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (var dialog = new PromptChooserForm(prompts, SharedSettings.PromptPath))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                SharedSettings.PromptPath = dialog.SelectedPath ?? string.Empty;
                RefreshPrompt();
                _status.Text = string.IsNullOrEmpty(dialog.SelectedPath)
                    ? "memoQ will use the instructions in its own settings."
                    : "Active prompt: " + dialog.SelectedPath;
            }
        }

        /// <summary>Shows which glossary is active, or says plainly that none is.</summary>
        /// <summary>How Supervertaler translates: the same settings memoQ shows.</summary>
        private void ShowSettings()
        {
            using (var dialog = new SettingsForm())
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _status.Text = "Translation settings saved.";
            }
        }

        /// <summary>Opens the editor's page on the documentation site.</summary>
        private void OpenDocumentation()
        {
            try
            {
                Process.Start("https://docs.supervertaler.com/memoq/prompt-editor/");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the documentation.\r\n\r\n" + ex.Message,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void RefreshGlossary()
        {
            var path = SharedSettings.GlossaryPath;

            if (string.IsNullOrWhiteSpace(path))
            {
                _glossary.Text = "Glossary: none";
                _glossary.ForeColor = SystemColors.GrayText;
                _glossary.ToolTipText = "No glossary is active, so the terminology pane, the prompts "
                    + "and the terminology check have nothing to work from. Click to choose one.";
                return;
            }

            var missing = !File.Exists(path);
            _glossary.Text = "Glossary: " + Path.GetFileName(path) + (missing ? " (missing)" : "");
            _glossary.ForeColor = missing ? Color.Firebrick : SystemColors.ControlText;
            _glossary.ToolTipText = (missing ? "This file no longer exists:\r\n" : "Active glossary:\r\n") + path
                + "\r\n\r\nClick to choose a different one.";
        }

        /// <summary>
        /// Picks the glossary the plugin uses for the terminology pane, the prompts
        /// and the terminology check. One setting, so this is the same choice as the
        /// one offered by memoQ's own dialogs, and either can be used.
        /// </summary>
        private void ChooseGlossary()
        {
            var glossaries = Path.Combine(SupervertalerPaths.Root, "memoq", "glossaries");
            var current = SharedSettings.GlossaryPath;
            var currentDir = string.IsNullOrWhiteSpace(current) ? null : Path.GetDirectoryName(current);

            using (var dialog = new OpenFileDialog
            {
                Title = "Choose the active glossary",
                Filter = "Glossary files (*.txt;*.tsv)|*.txt;*.tsv|All files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = Directory.Exists(currentDir ?? "") ? currentDir
                    : (Directory.Exists(glossaries) ? glossaries : SupervertalerPaths.Root),
                FileName = string.IsNullOrWhiteSpace(current) ? "" : Path.GetFileName(current)
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                SharedSettings.GlossaryPath = dialog.FileName;
                RefreshGlossary();
                _status.Text = "Active glossary: " + dialog.FileName;
            }
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
            if (_loading || _highlighting) return;

            MarkDirty();
            _highlightTimer.Stop();
            _highlightTimer.Start();
        }

        private void Highlight()
        {
            if (_editor.IsDisposed || !_editor.IsHandleCreated) return;

            _highlighting = true;
            try
            {
                var found = MarkdownHighlighter.Apply(_editor, _editorFont);
                ReportWarnings(found);
            }
            finally
            {
                _highlighting = false;
            }
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
                    _warnings.Items.Add(token + " is not a placeholder Supervertaler substitutes – "
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
            if (_loading || _highlighting || _current == null) return;

            // A read-only prompt cannot be edited, so anything that reaches here
            // for one is machinery, not the user.
            if (_current.IsReadOnly) return;
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

        /// <summary>
        /// True when it is safe to move on: nothing to save, saved, or discarded.
        /// </summary>
        private bool ConfirmDiscard()
        {
            if (!_dirty || _current == null) return true;

            // Nothing could have been written to a read-only prompt, so there is
            // nothing to ask about. Discard and carry on.
            if (_current.IsReadOnly)
            {
                _dirty = false;
                UpdateDirtyUi();
                return true;
            }

            var answer = MessageBox.Show(this,
                "Save changes to \"" + (_current.Name ?? "this prompt") + "\"?",
                "Supervertaler", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel) return false;

            // A refused save must not read as a completed one - that is what left
            // the question being asked again on the next click, with no answer
            // that ended it. No still discards, so there is always a way out.
            if (answer == DialogResult.Yes) return Save();

            _dirty = false;
            UpdateDirtyUi();
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
