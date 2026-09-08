using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Supervertaler.Core;
using Supervertaler.MemoQ.Core;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// What stands behind the Images dialog on the memoQ side: where the
    /// documents are, where the images go, and the two AI passes.
    ///
    /// <para>Trados knows its project folder and reads the .docx files out of it.
    /// memoQ has no such folder - the original is wherever the user imported it
    /// from - so the documents come from three places, in this order: the
    /// preview tool's report of what is open (which carries the import path,
    /// real on a local project), the plugin's capture store (which knows the
    /// document but not its file), and files the user has named by hand, either
    /// located for a document memoQ listed or added outright. The last two are
    /// remembered in <c>document-files.txt</c>; see <see cref="DocumentFiles"/>.</para>
    ///
    /// <para>The images folder is not chosen: it is <c>figures\</c> inside the
    /// active memory bank, because the bank is where figures.md ends up and the
    /// two belong together. No bank, no folder - the dialog says so and offers
    /// to make one.</para>
    ///
    /// <para>Scale: a project of sixty documents is refreshed on every dialog
    /// action and every 1.5 s while a description run is on. Each refresh reads
    /// every document's image list, so the extraction is cached per path and
    /// write time; the refresh then costs one <c>FileInfo</c> per document.</para>
    /// </summary>
    internal sealed class ImagesHost
    {
        private const string AddedPrefix = "added:";
        private const string FiguresFolder = "figures";

        private readonly Control _owner;
        private readonly Action<string> _activateBank;
        private readonly Action<string> _status;

        // One description run at a time. The dialog polls Build() while this is
        // set; the run clears it on every exit path.
        private int _running;
        private volatile string _progress;

        private List<Doc> _docs = new List<Doc>();

        private sealed class Doc
        {
            /// <summary>memoQ's document GUID, or <c>added:&lt;bank&gt;:&lt;file&gt;</c>.</summary>
            public string Key;
            public string Name;
            /// <summary>What memoQ recorded - the import path - which may be dead.</summary>
            public string RecordedPath;
            /// <summary>The file on this disk, or null.</summary>
            public string Path;
            public bool FromMemoQ;
        }

        private readonly Dictionary<string, KeyValuePair<DateTime, DocxImageSet>> _sets =
            new Dictionary<string, KeyValuePair<DateTime, DocxImageSet>>(StringComparer.OrdinalIgnoreCase);

        public ImagesHost(Control owner, Action<string> activateBank, Action<string> status)
        {
            _owner = owner;
            _activateBank = activateBank;
            _status = status ?? (s => { });
        }

        public void Show()
        {
            var actions = new ImagesActions
            {
                Refresh = Build,
                Extract = Extract,
                OpenFolder = OpenFolder,
                LocateDocument = LocateDocument,
                AddDocumentFile = AddDocumentFile,
                Analyse = Analyse,
                WriteFigures = WriteFigures,
                ShowReport = ShowReport,
                CreateProjectBank = CreateProjectBank,
            };
            using (var dlg = new ImagesDialog(actions, Build()))
                dlg.ShowDialog(_owner);
        }

        // ---- state -------------------------------------------------------------

        internal ImagesState Build()
        {
            var st = new ImagesState();

            var bank = (SharedSettings.MemoryBank ?? "").Trim();
            st.BankName = bank.Length == 0 ? null : bank;
            st.BankIsShared = bank.Length > 0 && MemoryBanks.IsSharedName(bank);
            var bankDir = BankDir(out _);
            if (bankDir != null)
            {
                st.Folder = Path.Combine(bankDir, FiguresFolder);
                st.FolderImages = CountImages(st.Folder);

                var figures = Path.Combine(bankDir, "figures.md");
                if (File.Exists(figures))
                {
                    try
                    {
                        var text = File.ReadAllText(figures);
                        st.FiguresWritten = File.GetLastWriteTime(figures);
                        st.FiguresRows = TableRows(text);
                        st.FiguresWithoutVision = FiguresFile.IsTextOnly(text);
                    }
                    catch { st.FiguresWritten = null; }
                }
            }

            var project = (SharedSettings.MemoryBankProjectName ?? "").Trim();
            st.ProjectName = project.Length == 0 ? null : project;
            var suggested = st.ProjectName == null ? "" : MemoryBanks.Sanitize(st.ProjectName);
            st.SuggestedBankName = suggested.Length == 0 ? null : suggested;
            st.SuggestedBankExists = suggested.Length > 0 && MemoryBanks.DirFor(suggested) != null;

            _docs = Gather(bank, out var why, out var connected);
            st.ProjectKnown = connected;
            // Without the bridge the project name is whatever memoQ said last
            // time; a bank made for it now could be filed against the wrong job.
            st.ProjectIsStale = !connected && st.ProjectName != null;
            st.WhyNoDocuments = why;
            st.DocumentCount = _docs.Count;

            foreach (var doc in _docs)
            {
                if (doc.Path == null)
                {
                    st.DocumentsWithoutFile.Add(new DocumentRow(doc.Name, string.IsNullOrWhiteSpace(doc.RecordedPath)
                        ? "not found - memoQ gave no file path; locate it below"
                        : "not on this computer - memoQ recorded " + doc.RecordedPath));
                    continue;
                }

                var set = SetFor(doc.Path);
                if (set.Images.Count == 0) { st.DocumentsWithoutImages.Add(new DocumentRow(doc.Name, "no images")); continue; }

                var labelled = set.Images.Count(i => !string.IsNullOrEmpty(i.Label));
                st.TotalImages += set.Images.Count;
                st.Labelled += labelled;
                st.Documents.Add(new DocumentRow(doc.Name,
                    ImagesDialog.Plural(set.Images.Count, "image") + ", "
                    + labelled + " with a figure label" + MethodNote(set)));
            }

            st.AnalysisRunning = Volatile.Read(ref _running) != 0;
            st.Progress = _progress;
            st.ProviderName = SharedSettings.ProviderOr(LlmProviders.Anthropic) + " / "
                            + ((SharedSettings.ModelOr("") ?? "").Trim().Length == 0 ? "no model chosen" : SharedSettings.ModelOr("").Trim());
            return st;
        }

        private static string MethodNote(DocxImageSet set)
        {
            switch (set.Method)
            {
                case LabelingMethod.Ordinal: return ", paired by position and checked";
                case LabelingMethod.Proximity: return ", labels taken from nearby text";
                case LabelingMethod.Refused: return ", labels withheld";
                default: return "";
            }
        }

        /// <summary>
        /// Rows across every table in a Markdown file: pipe lines, minus each
        /// table's header and separator. Exact for any number of tables.
        /// </summary>
        internal static int TableRows(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return 0;
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            var rows = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i].TrimStart();
                if (!l.StartsWith("|", StringComparison.Ordinal)) continue;
                if (IsSeparator(l)) continue;
                if (i + 1 < lines.Length && IsSeparator(lines[i + 1].TrimStart())) continue; // header
                rows++;
            }
            return rows;
        }

        private static bool IsSeparator(string line) =>
            line.StartsWith("|", StringComparison.Ordinal) && line.Trim('|', ' ', '\t').Length > 0
            && line.Trim('|', ' ', '\t').All(c => c == '-' || c == ':' || c == '|' || c == ' ');

        /// <summary>
        /// Images in the figures folder, its per-document subfolders included –
        /// core's count, which goes one level deep because that is exactly as
        /// deep as <see cref="TargetFolder"/> ever writes.
        ///
        /// <para>The one thing added here is <c>-1</c> for a folder that does not
        /// exist. Core answers 0, which is right for a count; the dialog needs
        /// the third state, because "not created yet" and "created and empty"
        /// read differently on the folder line and only the second is worth an
        /// Open folder link.</para>
        /// </summary>
        internal static int CountImages(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return -1;
                return ReferenceImages.CountImages(folder);
            }
            catch { return -1; }
        }

        /// <summary>Documents whose file is on this disk and which hold at least one image.</summary>
        private List<Doc> WithImages() =>
            _docs.Where(d => d.Path != null && SetFor(d.Path).Images.Count > 0).ToList();

        /// <summary>
        /// Where one document's images are written. Every document numbers its
        /// figures from 1, so two documents in one folder means the second one's
        /// "Figure 01.png" replaces the first's – silently, and after the model
        /// has already been shown the wrong picture. Several documents therefore
        /// get a folder each; one document keeps the flat folder, which is what
        /// nearly every job is and what the user sees when they click Open folder.
        /// </summary>
        internal static string TargetFolder(string figures, string documentName, int documentCount) =>
            documentCount <= 1 ? figures : Path.Combine(figures, SafeFolderName(documentName));

        /// <summary>A document name reduced to a folder name; never empty, never a path.</summary>
        internal static string SafeFolderName(string documentName)
        {
            // Sanitise BEFORE dropping the extension: GetFileNameWithoutExtension
            // throws on a name holding a character a path cannot, and would then
            // have left the extension on.
            var cleaned = MemoryBanks.Sanitize(documentName);
            try { if (cleaned.Length > 0) cleaned = Path.GetFileNameWithoutExtension(cleaned); } catch { }
            cleaned = cleaned.Trim('.', '_', ' ');
            return cleaned.Length == 0 ? "document" : cleaned;
        }

        private DocxImageSet SetFor(string path)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_sets.TryGetValue(path, out var cached) && cached.Key == stamp) return cached.Value;
                var set = DocxImageExtractor.Extract(path) ?? new DocxImageSet();
                _sets[path] = new KeyValuePair<DateTime, DocxImageSet>(stamp, set);
                return set;
            }
            catch { return new DocxImageSet(); }
        }

        /// <summary>The bank's folder when a project bank is active; null (with the reason) otherwise.</summary>
        private static string BankDir(out string reason)
        {
            reason = null;
            var bank = (SharedSettings.MemoryBank ?? "").Trim();
            if (bank.Length == 0) { reason = "No memory bank is active, so there is nowhere to put the images. Choose or create one first (the Result line offers to)."; return null; }
            if (MemoryBanks.IsSharedName(bank)) { reason = "The active memory bank is the shared one, which every project reads. Choose or create a bank for this project first."; return null; }
            var dir = MemoryBanks.DirFor(bank);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { reason = "The memory bank folder was not found: " + dir; return null; }
            return dir;
        }

        // ---- documents ---------------------------------------------------------

        private List<Doc> Gather(string bank, out string why, out bool connected)
        {
            var docs = new List<Doc>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            why = null; connected = false;

            using (var bridge = MemoQBridgeClient.TryConnect(out var reason))
            {
                if (bridge == null)
                {
                    why = reason + " Or add a document file below – its images are read from the file directly.";
                }
                else
                {
                    var project = Await(bridge.GetProjectAsync(), 4000);
                    if (project == null)
                    {
                        why = "memoQ is running but the Supervertaler engine did not answer. Click into a segment in memoQ and reopen this window, or add a document file below.";
                    }
                    else
                    {
                        connected = true;

                        // The preview tool's view first: it names the document and
                        // says where memoQ imported it from, and it reports whether
                        // or not MT plugins are enabled for the project.
                        foreach (var l in project.LiveDocuments ?? new MemoQBridgeClient.LiveDocumentInfo[0])
                        {
                            if (!Guid.TryParse(l.DocumentGuid ?? "", out var guid) || guid == Guid.Empty) continue;
                            var key = guid.ToString("D");
                            if (!seen.Add(key)) continue;
                            docs.Add(new Doc
                            {
                                Key = key,
                                Name = FirstNonEmpty(l.DocumentName, SafeFileName(l.ImportPath), key),
                                RecordedPath = l.ImportPath,
                                FromMemoQ = true,
                            });
                        }

                        // Then what translation requests have shown the plugin.
                        foreach (var d in project.Documents ?? new MemoQBridgeClient.DocumentInfo[0])
                        {
                            if (d.IsVisitedBucket) continue;
                            if (!Guid.TryParse(d.DocumentGuid ?? "", out var guid) || guid == Guid.Empty) continue;
                            var key = guid.ToString("D");
                            if (!seen.Add(key)) continue;
                            docs.Add(new Doc
                            {
                                Key = key,
                                Name = FirstNonEmpty(d.DocumentName, SafeFileName(d.ImportPath), key),
                                RecordedPath = d.ImportPath,
                                FromMemoQ = true,
                            });
                        }
                    }
                }
            }

            foreach (var doc in docs) doc.Path = DocumentFiles.Resolve(doc.Key, doc.RecordedPath);

            // Files the user added by hand, scoped to the bank: they exist for
            // the job the bank is for, not for every project that comes after.
            if (bank.Length > 0)
            {
                foreach (var kv in DocumentFiles.WithPrefix(AddedPrefix + bank + ":"))
                {
                    var exists = !string.IsNullOrWhiteSpace(kv.Value) && File.Exists(kv.Value);
                    docs.Add(new Doc
                    {
                        Key = kv.Key,
                        Name = SafeFileName(kv.Value) + " (added)",
                        RecordedPath = kv.Value,
                        Path = exists ? kv.Value : null,
                        FromMemoQ = false,
                    });
                }
            }

            if (docs.Count == 0 && why == null)
                why = "memoQ has not reported any documents. Open the project, click into a segment and reopen this window – or add a document file below.";

            return docs;
        }

        private static T Await<T>(Task<T> task, int milliseconds) where T : class
        {
            try { return task.Wait(milliseconds) ? task.Result : null; }
            catch { return null; }
        }

        private static string FirstNonEmpty(params string[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        private static string SafeFileName(string path)
        {
            try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path); }
            catch { return null; }
        }

        // ---- actions -----------------------------------------------------------

        private void Extract()
        {
            var bankDir = BankDir(out var reason);
            if (bankDir == null) { Say(reason); return; }
            var folder = Path.Combine(bankDir, FiguresFolder);
            Directory.CreateDirectory(folder);

            var sources = WithImages();
            int images = 0, documents = 0;
            foreach (var doc in sources)
            {
                var set = DocxImageExtractor.Extract(doc.Path, false, TargetFolder(folder, doc.Name, sources.Count));
                if (set.SavedFiles.Count == 0) continue;
                documents++;
                images += set.SavedFiles.Count;
            }

            _status(images == 0
                ? "No images to extract."
                : "Extracted " + ImagesDialog.Plural(images, "image") + " from " + ImagesDialog.Plural(documents, "document")
                  + " into " + folder + (sources.Count > 1 ? ", a folder per document." : "."));
        }

        private void OpenFolder()
        {
            var bankDir = BankDir(out _);
            if (bankDir == null) return;
            var folder = Path.Combine(bankDir, FiguresFolder);
            if (!Directory.Exists(folder)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }

        private void LocateDocument()
        {
            var missing = _docs.Where(d => d.FromMemoQ && d.Path == null).ToList();
            if (missing.Count == 0) return;

            var target = missing.Count == 1 ? missing[0] : Pick("Locate which document?", missing);
            if (target == null) return;

            using (var dlg = new OpenFileDialog
            {
                Title = "Locate " + target.Name,
                Filter = "Word documents (*.docx)|*.docx|All files (*.*)|*.*",
                FileName = SafeFileName(target.RecordedPath) ?? target.Name,
                CheckFileExists = true,
            })
            {
                if (dlg.ShowDialog(_owner) != DialogResult.OK) return;
                if (!DocumentFiles.Remember(target.Key, dlg.FileName))
                    Say("The file could not be remembered (see the log). Its images can still be read this time.");
                _status("Remembered " + dlg.FileName + " for " + target.Name + ".");
            }
        }

        private void AddDocumentFile()
        {
            var bankDir = BankDir(out var reason);
            if (bankDir == null) { Say(reason); return; }
            var bank = SharedSettings.MemoryBank.Trim();

            using (var dlg = new OpenFileDialog
            {
                Title = "Add a document file",
                Filter = "Word documents (*.docx)|*.docx|All files (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true,
            })
            {
                if (dlg.ShowDialog(_owner) != DialogResult.OK) return;
                foreach (var f in dlg.FileNames)
                {
                    // '=' is the file's own separator, so a name carrying one
                    // would split the line. The key is an identity, not a path.
                    var key = AddedPrefix + bank + ":" + Path.GetFileName(f).Replace('=', '_');
                    DocumentFiles.Remember(key, f);
                }
                _status("Added " + ImagesDialog.Plural(dlg.FileNames.Length, "document file") + " to memory bank “" + bank + "”.");
            }
        }

        private void Analyse()
        {
            if (Volatile.Read(ref _running) != 0) { Say("A description run is already going. Wait for it to finish."); return; }

            var bankDir = BankDir(out var reason);
            if (bankDir == null) { Say(reason); return; }
            var bank = SharedSettings.MemoryBank.Trim();
            var folder = Path.Combine(bankDir, FiguresFolder);

            var withImages = WithImages();
            var total = withImages.Sum(d => SetFor(d.Path).Images.Count);
            if (total == 0) { Say("No images in the documents listed."); return; }

            var provider = SharedSettings.ProviderOr(LlmProviders.Anthropic);
            var model = (SharedSettings.ModelOr("") ?? "").Trim();
            if (model.Length == 0) { Say("No model is chosen. Pick one under Settings first."); return; }
            var key = ApiKeys.Resolve(provider, null);
            if (!key.HasKey) { Say("No API key for " + provider + ". Enter one under Settings first."); return; }
            var endpoint = (SharedSettings.EndpointOr("") ?? "").Trim();

            var outPath = Path.Combine(bankDir, "figures.md");
            if (File.Exists(outPath))
            {
                var answer = MessageBox.Show(_owner,
                    "This will send " + ImagesDialog.Plural(total, "image") + " to " + provider
                    + " and REPLACE the existing figures.md in memory bank “" + bank + "”.\n\n"
                    + "Any corrections you have made to that file will be lost.\n\nContinue?",
                    "Describe images with AI", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }

            // What the text says, for the diff against what the model reads.
            // The inventory is narrow by design (bracketed numerals, lettered
            // points, label series), so the raw text goes along as well: "zone
            // X" six times in prose is still X being in the text.
            var sources = new List<string>();
            foreach (var d in withImages) sources.AddRange(ParagraphTexts(d.Path));
            var inventory = NumeralInventory.Extract(sources);
            var textSigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in inventory.Numerals) textSigns.Add(n.ToString());
            foreach (var k in inventory.LetterPoints.Keys) textSigns.Add(k);
            foreach (var k in inventory.LabelSeries.Keys) textSigns.Add(k);
            var rawSourceText = string.Join(" ", sources);

            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            _progress = "Starting…";
            Directory.CreateDirectory(folder);
            _status("Describing " + ImagesDialog.Plural(total, "image") + " – one AI request each.");

            Task.Run(async () =>
            {
                try
                {
                    // One entry per document, each carrying only the images that
                    // were actually shown to the model, with its vision beside it.
                    // An image that could not be written is left out of both lists
                    // rather than shifting every row after it.
                    var documents = new List<FiguresFile.FigureDocument>();
                    var done = 0;

                    using (var client = new LlmClient(LlmProviders.CoreKey(provider), model, key.Key,
                                                      endpoint.Length == 0 ? null : endpoint))
                    {
                        foreach (var doc in withImages)
                        {
                            var target = TargetFolder(folder, doc.Name, withImages.Count);
                            var set = DocxImageExtractor.Extract(doc.Path, false, target);
                            if (set.Images.Count == 0) continue;

                            var entry = new FiguresFile.FigureDocument { Name = doc.Name, Set = set };
                            foreach (var img in set.Images)
                            {
                                // The file this image went to, from the image itself:
                                // indexing SavedFiles pairs later images with the
                                // wrong file as soon as one fails to save.
                                if (string.IsNullOrEmpty(img.SavedFileName)) continue;

                                done++;
                                _progress = "Describing image " + done + " of " + total + " (" + doc.Name + ")…";
                                var v = await FigureAnalyzer.AnalyseAsync(client, Path.Combine(target, img.SavedFileName),
                                    img.Label ?? ("image " + img.Ordinal), img.Descriptions).ConfigureAwait(false);
                                entry.Images.Add(img);
                                entry.Visions.Add(v);
                            }

                            if (entry.Visions.Count > 0) documents.Add(entry);
                        }
                    }

                    var visions = documents.SelectMany(d => d.Visions).ToList();
                    if (visions.Count == 0) { Done("No images could be described."); return; }

                    var signs = FiguresFile.SignsNotInText(visions, textSigns, rawSourceText);
                    var markdown = FiguresFile.RenderWithVision(documents, signs,
                        "FigureLens → Describe images with AI");
                    FiguresFile.Save(outPath, markdown);

                    var failed = visions.Count(v => !string.IsNullOrEmpty(v.Error));
                    Done("Described " + ImagesDialog.Plural(visions.Count, "image")
                         + (failed > 0 ? " (" + failed + " failed)" : "")
                         + " and wrote figures.md to memory bank “" + bank + "”. It is read with every request from now on – read it first; a wrong caption would be invisible and everywhere."
                         + (signs.Count > 0
                             ? "\n\n" + ImagesDialog.Plural(signs.Count, "reference sign") + " appear in the drawings but nowhere in the text: "
                               + string.Join(", ", signs) + ". Worth raising with the client."
                             : "\n\nEvery sign read in the images also appears in the text."));
                }
                catch (Exception ex)
                {
                    Done("Describing the images failed: " + ex.Message);
                }
                finally
                {
                    _progress = null;
                    Interlocked.Exchange(ref _running, 0);
                }
            });
        }

        private static List<string> ParagraphTexts(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return DocxStructure.ReadParagraphs(fs)
                        .Select(p => p.Text)
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .ToList();
            }
            catch { return new List<string>(); }
        }

        private void WriteFigures()
        {
            var bankDir = BankDir(out var reason);
            if (bankDir == null) { Say(reason); return; }
            var bank = SharedSettings.MemoryBank.Trim();

            var outPath = Path.Combine(bankDir, "figures.md");
            if (File.Exists(outPath))
            {
                var analysed = false;
                try { analysed = !FiguresFile.IsTextOnly(File.ReadAllText(outPath)); } catch { }
                var answer = MessageBox.Show(_owner,
                    "figures.md already exists in memory bank “" + bank + "”"
                    + (analysed ? " and holds descriptions the AI wrote." : ".")
                    + "\n\nThis will REPLACE it with what the text says about each figure, without looking at the images."
                    + (analysed ? "\nThe AI’s descriptions and any corrections you made will be lost." : "")
                    + "\n\nContinue?",
                    "Describe from the text only", MessageBoxButtons.YesNo,
                    analysed ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }

            var documents = _docs.Where(d => d.Path != null)
                .Select(d => new KeyValuePair<string, DocxImageSet>(d.Name, SetFor(d.Path)))
                .ToList();
            var markdown = FiguresFile.RenderFromText(documents, "FigureLens → Describe from the text only",
                out var wrote, out var refused);
            if (markdown == null) { Say("No images in the documents listed – nothing written."); return; }

            FiguresFile.Save(outPath, markdown);
            _status("figures.md written to memory bank “" + bank + "” (" + ImagesDialog.Plural(wrote, "figure")
                    + (refused > 0 ? ", " + ImagesDialog.Plural(refused, "document") + " unlabelled" : "") + ").");
        }

        private void ShowReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Document images");
            sb.AppendLine();

            var readable = _docs.Where(d => d.Path != null).ToList();
            if (readable.Count == 0) sb.AppendLine("No documents whose file is on this computer.");

            foreach (var doc in readable)
            {
                var set = SetFor(doc.Path);
                var images = set.Images;
                sb.AppendLine("### " + doc.Name);
                sb.AppendLine();
                sb.AppendLine("`" + doc.Path + "`");
                sb.AppendLine();
                if (images.Count == 0) { sb.AppendLine("No images."); sb.AppendLine(); continue; }

                sb.AppendLine("**" + ImagesDialog.Plural(images.Count, "image") + "** – "
                    + images.Count(i => !string.IsNullOrEmpty(i.Label)) + " with a figure label, "
                    + images.Count(i => i.Descriptions != null && i.Descriptions.Count > 0) + " with a description in the text, "
                    + images.Count(i => !string.IsNullOrWhiteSpace(i.Anchor)) + " with surrounding text.");
                sb.AppendLine();
                switch (set.Method)
                {
                    case LabelingMethod.Ordinal:
                        sb.AppendLine("Labels paired by position and checked: image *N* carries figure *N*, verified for all " + images.Count + "."); break;
                    case LabelingMethod.Refused:
                        sb.AppendLine("> ⚠ **Labels withheld.** " + set.Warning); break;
                    case LabelingMethod.Proximity:
                        sb.AppendLine("*Labels taken from nearby text – right for captioned inline images, a guess on a document of plates.*"); break;
                }
                sb.AppendLine();
                sb.AppendLine("| # | Label | Type | Size | Caption | Text nearby |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var i in images)
                {
                    sb.AppendLine("| " + i.Ordinal + " | " + FiguresFile.Cell(i.Label) + " | " + FiguresFile.Cell(i.Extension)
                        + " | " + (i.SizeBytes / 1024) + " KB | " + FiguresFile.Cell(i.Caption) + " | "
                        + FiguresFile.Cell(Shorten(i.Anchor, 160)) + " |");
                }
                sb.AppendLine();
            }

            var bankDir = BankDir(out _);
            var path = Path.Combine(bankDir ?? Path.GetTempPath(), "images-report.md");
            File.WriteAllText(path, sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        private void CreateProjectBank()
        {
            var project = (SharedSettings.MemoryBankProjectName ?? "").Trim();
            var name = MemoryBanks.Sanitize(project);
            if (name.Length == 0) { Say("memoQ has not named a project yet."); return; }

            // DirFor resolves a name to a folder and answers null when there is
            // none - it exists to say "no such bank", not to propose a path. So
            // it is asked only whether one is already there, and the folder to
            // create is built here. Handing its null to CreateDirectory was a
            // "Value cannot be null. Parameter name: path" on the one case this
            // link exists for: a project with no bank yet.
            var existing = MemoryBanks.DirFor(name);
            var dir = existing ?? Path.Combine(MemoryBanks.Root, name);

            try
            {
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(Path.Combine(dir, "reference"));

                // Only what is missing: reusing a bank must not overwrite the
                // brief someone has already written in it.
                foreach (var f in new[] { "brief.md", "terminology.md", "style.md" })
                {
                    var file = Path.Combine(dir, f);
                    if (!File.Exists(file))
                        File.WriteAllText(file, MemoryBanks.SkeletonBody(f, name), new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                Say("Could not create the memory bank folder:" + Environment.NewLine + dir
                    + Environment.NewLine + Environment.NewLine + ex.Message);
                return;
            }

            // The folder's own name, which for a bank that already existed is
            // however it was actually spelt.
            _activateBank?.Invoke(Path.GetFileName(dir));
        }

        // ---- helpers -----------------------------------------------------------

        private void Say(string text) =>
            MessageBox.Show(_owner, text, "FigureLens", MessageBoxButtons.OK, MessageBoxIcon.Information);

        /// <summary>Report the end of a background run on the UI thread, if there still is one.</summary>
        private void Done(string text)
        {
            try
            {
                if (_owner == null || _owner.IsDisposed || !_owner.IsHandleCreated) return;
                _owner.BeginInvoke(new Action(() =>
                {
                    _status(text.Split('\n')[0]);
                    MessageBox.Show(_owner, text, "FigureLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
            }
            catch { }
        }

        private Doc Pick(string title, List<Doc> docs)
        {
            using (var f = new Form
            {
                Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(520, 260),
            })
            {
                AppIcon.Apply(f);
                var list = new ListBox { Left = 12, Top = 12, Width = 496, Height = 200, IntegralHeight = false };
                foreach (var d in docs) list.Items.Add(d.Name);
                list.SelectedIndex = 0;
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 340, Top = 222, Width = 80 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 428, Top = 222, Width = 80 };
                f.Controls.AddRange(new Control[] { list, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                list.DoubleClick += (s, e) => { if (list.SelectedIndex >= 0) f.DialogResult = DialogResult.OK; };
                return f.ShowDialog(_owner) == DialogResult.OK && list.SelectedIndex >= 0 ? docs[list.SelectedIndex] : null;
            }
        }
    }
}
