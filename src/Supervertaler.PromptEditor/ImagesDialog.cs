using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// One document as the list shows it: what it is called, and what was found
    /// in it. Two fields rather than one sentence because they are two columns –
    /// a name and a finding run together read as one long line, and the eye has
    /// to hunt for where the name ends on every row.
    /// </summary>
    internal sealed class DocumentRow
    {
        public string Name;
        public string Note;

        public DocumentRow() { }
        public DocumentRow(string name, string note) { Name = name; Note = note; }
    }

    /// <summary>
    /// Everything the Images dialog shows, gathered once by the host. A plain
    /// bag so the dialog can be laid out and probed without memoQ, a bridge or
    /// a document.
    /// </summary>
    internal sealed class ImagesState
    {
        /// <summary>memoQ has told the plugin about this project's documents.</summary>
        public bool ProjectKnown;
        public string ProjectName;

        /// <summary>Why nothing is listed, when nothing is. Shown instead of a bare "no documents".</summary>
        public string WhyNoDocuments;

        /// <summary>Documents with images, one row each.</summary>
        public List<DocumentRow> Documents = new List<DocumentRow>();
        public int DocumentCount;
        public List<DocumentRow> DocumentsWithoutImages = new List<DocumentRow>();

        /// <summary>Documents memoQ named whose file is not on this disk.</summary>
        public List<DocumentRow> DocumentsWithoutFile = new List<DocumentRow>();

        public int TotalImages;
        public int Labelled;

        /// <summary>The images folder - derived from the bank, so never "not chosen".</summary>
        public string Folder;
        /// <summary>-1 when the folder does not exist yet.</summary>
        public int FolderImages = -1;

        public string BankName;
        public bool BankIsShared;
        public string SuggestedBankName;
        /// <summary>A bank of that name is already there – the offer reuses it rather than making one.</summary>
        public bool SuggestedBankExists;
        public bool ProjectIsStale;

        public string FiguresPath;
        public DateTime? FiguresWritten;
        public int FiguresRows;
        public bool FiguresWithoutVision;

        public bool AnalysisRunning;
        public string Progress;
        public string ProviderName;
    }

    /// <summary>What the buttons do. Any left null is hidden or disabled.</summary>
    internal sealed class ImagesActions
    {
        public Func<ImagesState> Refresh;
        public Action Extract;
        public Action OpenFolder;
        public Action LocateDocument;
        public Action AddDocumentFile;
        public Action Analyse;
        public Action WriteFigures;
        public Action ShowReport;
        public Action CreateProjectBank;
    }

    /// <summary>
    /// The Images panel: get a document's images out into a folder, have them
    /// described, and save the descriptions where every prompt reads them.
    ///
    /// <para>Copied from the Trados dialog of the same name and given memoQ's
    /// actions, so the two products say the same things in the same order. What
    /// differs is what memoQ cannot know: the images folder is derived from the
    /// memory bank rather than chosen, and there are two ways to name a file
    /// memoQ has not - locate a document memoQ listed, or add a file memoQ never
    /// mentioned, which is the server-project case.</para>
    /// </summary>
    internal sealed class ImagesDialog : Form
    {
        private readonly ImagesActions _actions;
        private ImagesState _state;

        private readonly Label _lblDocs;
        private readonly ListView _lstDocs;
        private readonly LinkLabel _lnkLocate, _lnkAddFile, _lnkCreateBank;
        private readonly TableLayoutPanel _root;
        private readonly Button _btnExtract;
        private readonly Label _lblExtractNote;
        private readonly Label _lblFolder;
        private readonly LinkLabel _lnkOpen;
        private readonly Button _btnAnalyse;
        private readonly Label _lblAnalyseNote;
        private readonly Button _btnWrite;
        private readonly Label _lblWriteNote;
        private readonly Label _lblResult;
        private readonly Timer _poll;

        /// <summary>For the layout probe: the longest realistic state, no actions.</summary>
        public ImagesDialog() : this(null, new ImagesState
        {
            ProjectKnown = true,
            ProjectName = "Acme PROJ-001 (application as filed, drawings as filed, sequence listing)",
            Folder = @"D:\Supervertaler\memory-banks\acme-proj-001-application-as-filed\figures",
            FolderImages = 14,
            Documents = new List<DocumentRow>
            {
                new DocumentRow("20260713-PROJ-001 Figures as filed.docx", "14 images, 14 with a figure label, paired by position and checked"),
                new DocumentRow("20260713-PROJ-001 Figures as filed, sheet 2 of a very long document title.docx", "9 images, 9 with a figure label, labels taken from nearby text"),
                new DocumentRow("Annex A.docx", "2 images, 0 with a figure label"),
            },
            DocumentCount = 12,
            DocumentsWithoutImages = new List<DocumentRow> { new DocumentRow("Annex F.docx", "no images"), new DocumentRow("Annex G.docx", "no images") },
            DocumentsWithoutFile = new List<DocumentRow> { new DocumentRow("Annex H.docx", @"not on this computer - memoQ recorded C:\_In\source\eng\Annex H.docx") },
            BankIsShared = true, SuggestedBankName = "acme-proj-001-application-as-filed",
            TotalImages = 25, Labelled = 23,
            BankName = "_shared",
            FiguresPath = @"D:\Supervertaler\memory-banks\acme-proj-001\figures.md",
            FiguresWritten = new DateTime(2026, 8, 26, 23, 58, 0),
            FiguresRows = 14, FiguresWithoutVision = true,
            ProviderName = "Anthropic / claude-opus-5",
        }) { }

        public ImagesDialog(ImagesActions actions, ImagesState initial)
        {
            _actions = actions ?? new ImagesActions();
            _state = initial ?? new ImagesState();

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            AppIcon.Apply(this);
            Text = "FigureLens";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(880, 420);
            MinimumSize = new Size(640, 380);

            var tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300 };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
            _root = root;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            var row = 0;

            var intro = Wrap("The AI sees the text of your documents, not the pictures in them. FigureLens gives it a description of each image, "
                           + "in two steps: get the images out of the documents into a folder, then have them described.");
            root.Controls.Add(intro, 0, row); root.SetColumnSpan(intro, 2); row++;

            // What is there
            root.Controls.Add(L("Your documents:"), 0, row);
            _lblDocs = Wrap("");
            root.Controls.Add(_lblDocs, 1, row); row++;

            // A table, not a list of sentences: a project can hold sixty files,
            // and a name run together with its finding makes the eye hunt for
            // where the name ends on every row. Two columns line the names up
            // under each other and the findings under each other.
            _lstDocs = new ListView
            {
                Dock = DockStyle.Fill, Height = 96,
                View = View.Details, FullRowSelect = true, MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = true,
                ShowItemToolTips = true,
                Margin = new Padding(0, 0, 0, 6)
            };
            _lstDocs.Columns.Add("Document", 240);
            _lstDocs.Columns.Add("Images", 420);
            _lstDocs.SizeChanged += (s, e) => FitColumns();
            root.Controls.Add(_lstDocs, 1, row); row++;

            // Two ways to name a file memoQ has not. "Locate" is for a document
            // memoQ listed whose recorded path is dead - a server project
            // remembers where the file was on the project manager's machine.
            // "Add" is for when memoQ has said nothing at all, which is a server
            // project with MT plugins disabled and no preview: the panel then
            // works from the file alone.
            var fileRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
            _lnkLocate = Lnk("Locate the original document\u2026");
            _lnkLocate.LinkClicked += (s, e) => Run(_actions.LocateDocument);
            tips.SetToolTip(_lnkLocate, "memoQ named a document whose file is not on this computer - a project checked out from a server records where the file was on the project manager's machine. Point at your copy once; it is remembered for this document.");
            _lnkAddFile = Lnk("Add a document file\u2026");
            _lnkAddFile.LinkClicked += (s, e) => Run(_actions.AddDocumentFile);
            tips.SetToolTip(_lnkAddFile, "A Word document memoQ has not told Supervertaler about - the file the project manager sent, say. Its images are read from the file directly.");
            fileRow.Controls.Add(_lnkLocate); fileRow.Controls.Add(_lnkAddFile);
            root.Controls.Add(fileRow, 1, row); row++;

            // Step 1
            root.Controls.Add(Step("Step 1"), 0, row);
            _btnExtract = Btn("Extract images");
            _btnExtract.Click += (s, e) => Run(_actions.Extract);
            tips.SetToolTip(_btnExtract, "Copies the images out of the documents into the memory bank's figures folder, named after their figure numbers (Figure 01.png, Figure 02.png\u2026). Running it again replaces the copies. Free, no AI.");
            _lblExtractNote = Note("");
            root.Controls.Add(Pair(_btnExtract, _lblExtractNote), 1, row); row++;

            var folderRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
            _lblFolder = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 8, 0) };
            _lnkOpen = Lnk("Open folder"); _lnkOpen.LinkClicked += (s, e) => Run(_actions.OpenFolder);
            folderRow.Controls.Add(_lblFolder); folderRow.Controls.Add(_lnkOpen);
            root.Controls.Add(folderRow, 1, row); row++;

            // Step 2
            root.Controls.Add(Step("Step 2"), 0, row);
            _btnAnalyse = Btn("Describe images with AI");
            _btnAnalyse.Click += (s, e) => Run(_actions.Analyse);
            tips.SetToolTip(_btnAnalyse, "Shows each image to the AI, together with what the text says about it, and saves the descriptions where every prompt reads them. One paid request per image. Asks before replacing descriptions that already exist.");
            _lblAnalyseNote = Note("");
            root.Controls.Add(Pair(_btnAnalyse, _lblAnalyseNote), 1, row); row++;

            _btnWrite = Btn("Describe from the text only");
            _btnWrite.Click += (s, e) => Run(_actions.WriteFigures);
            tips.SetToolTip(_btnWrite, "The free alternative: saves what the document itself says about each figure, without looking at the images. Use one or the other. Asks before replacing descriptions that already exist.");
            _lblWriteNote = Note("");
            var alt = Pair(_btnWrite, _lblWriteNote); alt.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(alt, 1, row); row++;

            // Result
            root.Controls.Add(L("Result:"), 0, row);
            _lblResult = Wrap("");
            root.Controls.Add(_lblResult, 1, row); row++;
            _lnkCreateBank = Lnk(""); _lnkCreateBank.Margin = new Padding(0, 0, 0, 6);
            _lnkCreateBank.LinkClicked += (s, e) => Run(_actions.CreateProjectBank);
            tips.SetToolTip(_lnkCreateBank, "Makes a memory bank named after this project (or reuses one with that name) and switches to it, so the descriptions belong to this project.");
            root.Controls.Add(_lnkCreateBank, 1, row); row++;

            // filler
            root.RowStyles.Clear();
            for (var i = 0; i < row; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, row); row++;

            // bottom: report link, help, close
            var bottom = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var btnClose = Btn("Close"); btnClose.Click += (s, e) => Close();
            bottom.Controls.Add(btnClose);
            var lnkReport = Lnk("Document images report"); lnkReport.Margin = new Padding(0, 7, 12, 0);
            lnkReport.LinkClicked += (s, e) => Run(_actions.ShowReport);
            tips.SetToolTip(lnkReport, "Every image in the documents, with its figure label and the text around it. Written into the memory bank and opened. No AI call.");
            bottom.Controls.Add(lnkReport);
            var lnkHelp = Lnk("? Help"); lnkHelp.Margin = new Padding(0, 7, 12, 0);
            lnkHelp.LinkClicked += (s, e) => OpenHelp();
            bottom.Controls.Add(lnkHelp);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(bottom, 0, row); root.SetColumnSpan(bottom, 2); row++;
            root.RowCount = row;

            Controls.Add(root);
            CancelButton = btnClose;
            ActiveControl = btnClose;

            // While a description run is on a pool thread, the result line follows it.
            _poll = new Timer { Interval = 1500 };
            _poll.Tick += (s, e) => { if (_state.AnalysisRunning) RefreshState(); };
            _poll.Start();
            FormClosed += (s, e) => _poll.Dispose();

            Render();
        }

        private static void OpenHelp()
        {
            try { Process.Start(new ProcessStartInfo("https://docs.supervertaler.com/memoq/prompt-editor/#figurelens") { UseShellExecute = true }); }
            catch { /* a browser that will not open is not worth a second dialog */ }
        }

        private void Run(Action action)
        {
            if (action == null) return;
            try { action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "FigureLens", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            RefreshState();
        }

        private void RefreshState()
        {
            if (_actions.Refresh == null) return;
            try { _state = _actions.Refresh() ?? _state; } catch { }
            Render();
        }

        private void Render()
        {
            RenderInner();
            FitHeight();
        }

        /// <summary>Tall enough for everything, never taller than the screen.</summary>
        private void FitHeight()
        {
            try
            {
                var pref = _root.GetPreferredSize(new Size(ClientSize.Width, 0));
                var wanted = pref.Height + 8;
                var max = Screen.FromControl(this).WorkingArea.Height - 80;
                if (wanted > ClientSize.Height) ClientSize = new Size(ClientSize.Width, Math.Min(wanted, max));
            }
            catch { }
        }

        internal void RenderInner()
        {
            var st = _state;
            var haveImages = st.TotalImages > 0;
            var haveBank = !string.IsNullOrEmpty(st.BankName) && !st.BankIsShared;
            var busy = st.AnalysisRunning;
            var n = Plural(st.TotalImages, "image");

            _lblDocs.Text = DocumentsText(st);
            _lstDocs.BeginUpdate();
            _lstDocs.Items.Clear();
            // The ones with images first: they are what the two steps act on.
            foreach (var d in st.Documents) AddRow(d, SystemColors.ControlText);
            foreach (var d in st.DocumentsWithoutImages) AddRow(d, SystemColors.GrayText);
            foreach (var d in st.DocumentsWithoutFile) AddRow(d, Color.Firebrick);
            FitColumns();
            _lstDocs.EndUpdate();
            _lstDocs.Visible = _lstDocs.Items.Count > 0;

            _lnkLocate.Visible = _actions.LocateDocument != null && st.DocumentsWithoutFile.Count > 0;
            _lnkAddFile.Visible = _actions.AddDocumentFile != null;

            // Step 1
            _btnExtract.Enabled = haveImages && haveBank && !busy;
            _lblExtractNote.Text = !haveImages ? "Nothing to extract: no images in the documents above."
                : !haveBank ? "Needs a memory bank for this project, whose figures folder is where the images go \u2013 see Result."
                : "Copies the " + n + " out of the documents above into the folder below. Free, no AI.";
            _lblFolder.Text = string.IsNullOrEmpty(st.Folder) ? "Folder: the memory bank\u2019s figures folder, once a bank is chosen."
                : st.FolderImages < 0 ? "Folder: " + st.Folder + "  (not created yet)"
                : "Folder: " + st.Folder + "  (" + Plural(st.FolderImages, "image file") + ")";
            _lnkOpen.Visible = !string.IsNullOrEmpty(st.Folder) && st.FolderImages >= 0;

            // Step 2
            _btnAnalyse.Enabled = haveImages && haveBank && st.FolderImages > 0 && !busy;
            _lblAnalyseNote.Text = busy ? (string.IsNullOrEmpty(st.Progress) ? "Running\u2026" : st.Progress)
                : !haveImages ? "No images to describe."
                : !haveBank ? "Needs a memory bank for this project to save the descriptions in \u2013 see Result."
                : st.FolderImages <= 0 ? "Do step 1 first."
                : Plural(st.TotalImages, "AI request") + " to " + (st.ProviderName ?? "the provider") + ", one per image.";
            _btnWrite.Enabled = haveImages && haveBank && !busy;
            _lblWriteNote.Text = !haveBank ? "Needs a memory bank for this project \u2013 see Result."
                : "Free, no AI: only what the document says about each figure. The alternative to the button above, not a third step.";

            // Result
            _lnkCreateBank.Visible = !haveBank && !string.IsNullOrEmpty(st.SuggestedBankName) && !st.ProjectIsStale && _actions.CreateProjectBank != null;
            _lnkCreateBank.Text = (st.SuggestedBankExists ? "Use memory bank \u201c" : "Create memory bank \u201c")
                                + st.SuggestedBankName + "\u201d for this project and switch to it";
            if (st.ProjectIsStale)
                _lblResult.Text = "The project shown was last seen in an earlier memoQ session, so a bank made for it now could be filed against the wrong project. Click into a segment in memoQ, then reopen this window.";
            else if (st.BankIsShared)
                _lblResult.Text = "The active memory bank is the shared one, which every project reads. Descriptions of this project\u2019s images belong in a bank of its own:";
            else if (!haveBank)
                _lblResult.Text = "No memory bank is active, so there is nowhere to save the descriptions:";
            else if (st.FiguresWritten == null)
                _lblResult.Text = "No descriptions yet. They are saved as figures.md in memory bank \u201c" + st.BankName + "\u201d, which the AI reads with every request.";
            else
                _lblResult.Text = "Descriptions saved " + st.FiguresWritten.Value.ToString("yyyy-MM-dd HH:mm")
                    + " \u00b7 " + Plural(st.FiguresRows, "figure")
                    + (st.FiguresWithoutVision ? " \u00b7 from the text only, the images not yet looked at" : " \u00b7 with what the AI saw")
                    + " \u00b7 figures.md in memory bank \u201c" + st.BankName + "\u201d, read by the AI with every request.";
        }

        /// <summary>
        /// A summary first, then only the documents that have images. When memoQ
        /// has listed nothing, the reason - which on a server project is a
        /// setting nobody on this side can change.
        /// </summary>
        internal static string DocumentsText(ImagesState st)
        {
            if (st.DocumentCount == 0)
                return string.IsNullOrEmpty(st.WhyNoDocuments)
                    ? "No documents yet. FigureLens reads images from the documents memoQ has told Supervertaler about, or from a file you add below."
                    : st.WhyNoDocuments;
            // A document whose file is not here was never opened, so nothing is
            // known about what is in it. Counting it among documents found to
            // have no images states a fact that was never established - and on a
            // project checked out from a server that is EVERY document.
            var missing = st.DocumentsWithoutFile.Count;
            var readable = st.DocumentCount - missing;

            if (readable <= 0)
                return st.DocumentCount == 1
                    ? "The document's file is not on this computer, so it could not be read. Locate it below, or add a document file."
                    : "None of the " + Plural(st.DocumentCount, "document") + " could be read: their files are not on this computer. "
                      + "Locate them below, or add a document file.";

            if (st.TotalImages == 0)
                return "No images in the " + Plural(readable, "document") + " that could be read"
                     + (missing > 0 ? "; " + Plural(missing, "document") + " not on this computer." : ".");

            return Plural(st.TotalImages, "image") + " in " + st.Documents.Count + " of " + Plural(readable, "document")
                 + (st.DocumentsWithoutImages.Count > 0 ? "; the rest have none." : ".")
                 + (missing > 0 ? " " + Plural(missing, "document") + " not on this computer." : "");
        }

        private void AddRow(DocumentRow row, Color colour)
        {
            var item = new ListViewItem(row?.Name ?? "") { ForeColor = colour };
            item.SubItems.Add(row?.Note ?? "");
            item.ToolTipText = (row?.Name ?? "") + "  \u2013  " + (row?.Note ?? "");
            _lstDocs.Items.Add(item);
        }

        /// <summary>
        /// The name column as wide as the longest name, up to a share of the
        /// width; the findings take the rest. Auto-size alone gives a name
        /// column that pushes the findings off the right edge on a project of
        /// long file names, which is the case this table exists to serve.
        /// </summary>
        private bool _fitting;

        private void FitColumns()
        {
            // Re-entrancy guard, and not a theoretical one: a column width set
            // inside the ListView's own SizeChanged re-enters the layout and the
            // handler fires again, which hung the harness outright rather than
            // failing anything.
            if (_fitting || _lstDocs.Items.Count == 0) return;
            _fitting = true;
            try { FitColumnsCore(); }
            finally { _fitting = false; }
        }

        private void FitColumnsCore()
        {
            _lstDocs.Columns[0].Width = -1;
            var available = _lstDocs.ClientSize.Width;
            var cap = Math.Max(140, (int)(available * 0.42));
            if (_lstDocs.Columns[0].Width > cap) _lstDocs.Columns[0].Width = cap;
            _lstDocs.Columns[1].Width = Math.Max(140, available - _lstDocs.Columns[0].Width - 4);
        }

        internal static string Plural(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

        private static Label L(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, 10, 0) };

        private static Label Step(string text) => new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(0, 8, 10, 0) };

        private static Label Note(string text) => new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(10, 8, 0, 0) };

        private Label Wrap(string text)
        {
            var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(ClientSize.Width - 40, 0), Margin = new Padding(0, 4, 0, 4) };
            SizeChanged += (s, e) => l.MaximumSize = new Size(ClientSize.Width - 40, 0);
            return l;
        }

        private static LinkLabel Lnk(string text) => new LinkLabel { Text = text, AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 2, 12, 0) };

        private static Button Btn(string text) => new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.System, Padding = new Padding(8, 0, 8, 0), Margin = new Padding(0, 3, 0, 3) };

        /// <summary>A button with its note to the right; the note wraps under the dialog width.</summary>
        private Control Pair(Button b, Label note)
        {
            var host = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            note.MaximumSize = new Size(ClientSize.Width - 300, 0);
            SizeChanged += (s, e) => note.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 300), 0);
            host.Controls.Add(b, 0, 0);
            host.Controls.Add(note, 1, 0);
            return host;
        }
    }
}
