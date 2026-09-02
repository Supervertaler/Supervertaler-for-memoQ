using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MemoQ.Addins.Common.DataStructures;
using MemoQ.MTInterfaces;
using Supervertaler.MemoQ.Core;

namespace Supervertaler.MemoQ.Settings
{
    /// <summary>
    /// The plugin's entire UI surface inside memoQ.
    ///
    /// memoQ gives an add-in no view part, no ribbon button and no menu item — an
    /// options dialog launched from Resources > Settings > MT is all there is. So
    /// everything a user must be able to change without leaving memoQ has to fit
    /// here, and everything richer (prompt library, AI assistant, terminology
    /// editor) belongs in the companion app.
    ///
    /// Built in code rather than with the designer: a .resx-backed form adds a
    /// satellite resource lookup inside memoQ's assembly-probing rules for no
    /// benefit on a dialog this size.
    /// </summary>
    internal sealed class OptionsForm : Form
    {
        private readonly ComboBox _provider = new ComboBox();
        private readonly TextBox _model = new TextBox();
        private readonly TextBox _endpoint = new TextBox();
        private readonly TextBox _apiKey = new TextBox();
        private readonly ComboBox _promptPick = new ComboBox();
        private readonly TextBox _systemPrompt = new TextBox();
        private readonly NumericUpDown _maxParallel = new NumericUpDown();
        private readonly NumericUpDown _batchSize = new NumericUpDown();
        private readonly CheckBox _useTerminology = new CheckBox();
        private readonly CheckBox _useDocumentContext = new CheckBox();
        private readonly CheckBox _bridgeMode = new CheckBox();
        private readonly Button _test = new Button();
        private readonly Label _status = new Label();
        private readonly Label _storedInfo = new Label();

        /// <summary>The edited settings. Only meaningful when the dialog returned OK.</summary>
        public SupervertalerSettings Result { get; private set; }

        public OptionsForm(SupervertalerSettings current)
        {
            Result = current ?? new SupervertalerSettings();
            BuildLayout();
            LoadFrom(Result);
        }

        private void BuildLayout()
        {
            Text = "Supervertaler";
            Icon = IconLoader.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(660, 640);

            // The ? in the title bar. This dialog is most of the plugin's UI, so
            // it is also the only place the documentation can be reached from
            // inside memoQ.
            HelpLinks.Attach(this, HelpLinks.GettingStarted);

            var y = 14;
            const int labelX = 14;
            const int fieldX = 150;
            const int fieldW = 496;
            const int rowH = 30;

            Label Caption(string text, int top)
            {
                var l = new Label { Text = text, Left = labelX, Top = top + 3, Width = 130, AutoSize = false };
                Controls.Add(l);
                return l;
            }

            Caption("Provider", y);
            _provider.Left = fieldX; _provider.Top = y; _provider.Width = fieldW;
            _provider.DropDownStyle = ComboBoxStyle.DropDownList;
            _provider.Items.AddRange(LlmProviders.All);
            Controls.Add(_provider);
            y += rowH;

            Caption("Model", y);
            _model.Left = fieldX; _model.Top = y; _model.Width = fieldW;
            Controls.Add(_model);
            y += rowH;

            Caption("API key", y);
            _apiKey.Left = fieldX; _apiKey.Top = y; _apiKey.Width = fieldW;
            _apiKey.UseSystemPasswordChar = true;
            Controls.Add(_apiKey);
            y += rowH;

            Caption("Endpoint (optional)", y);
            _endpoint.Left = fieldX; _endpoint.Top = y; _endpoint.Width = fieldW;
            Controls.Add(_endpoint);
            y += rowH - 6;

            var endpointHint = new Label
            {
                Text = "Leave blank for the provider default. Set this for a local model or a gateway.",
                Left = fieldX, Top = y, AutoSize = true,
                ForeColor = SystemColors.GrayText
            };
            Controls.Add(endpointHint);
            y += 26;

            Caption("Parallel requests", y);
            _maxParallel.Left = fieldX; _maxParallel.Top = y; _maxParallel.Width = 70;
            _maxParallel.Minimum = 1; _maxParallel.Maximum = 16;
            Controls.Add(_maxParallel);
            y += rowH;

            // Segments per request during Pre-translate. Matters beyond cost:
            // prompts written for batch translation refer to segment numbers, and
            // only get them when several segments travel together.
            Caption("Segments per request", y);
            _batchSize.Left = fieldX; _batchSize.Top = y; _batchSize.Width = 70;
            _batchSize.Minimum = 1; _batchSize.Maximum = 100;
            Controls.Add(_batchSize);

            // memoQ decides how many segments it hands the plugin at a time — 10
            // on a measured Pre-translate run — and this setting can only
            // subdivide that array, never combine across calls. Above memoQ's own
            // chunk size it therefore does nothing, which is worth saying rather
            // than letting someone set 100 and wonder why nothing changed.
            var batchHint = new Label
            {
                Text = "Pre-translate only; memoQ caps a batch at about 10.",
                Left = fieldX + 82, Top = y + 3, AutoSize = true,
                ForeColor = SystemColors.GrayText
            };
            Controls.Add(batchHint);
            y += rowH;

            _useTerminology.Text = "Send memoQ's termbase hits and forbidden terms to the model";
            _useTerminology.Left = fieldX; _useTerminology.Top = y; _useTerminology.AutoSize = true;
            Controls.Add(_useTerminology);
            y += 24;

            _useDocumentContext.Text = "Send surrounding segments and project metadata to the model";
            _useDocumentContext.Left = fieldX; _useDocumentContext.Top = y; _useDocumentContext.AutoSize = true;
            Controls.Add(_useDocumentContext);
            y += 24;

            // Named for the question it answers — who translates? — rather than
            // for the plumbing. "Bridge" stays the internal term (MemoQBridge,
            // the handshake file); the user-facing word is MCP, the thing they
            // actually set up.
            _bridgeMode.Text = "Pre-translate via Claude Desktop (MCP) instead of the API key above";
            _bridgeMode.Left = fieldX; _bridgeMode.Top = y; _bridgeMode.AutoSize = true;
            Controls.Add(_bridgeMode);
            y += 20;

            var bridgeHint = new Label
            {
                Text = "Pre-translate then only hands the segments to the chat and inserts the translations it sends back; "
                     + "nothing is charged to the API key. Suggestions as you move through segments still use the API key.",
                Left = fieldX + 18, Top = y, Width = fieldW - 18, Height = 34, AutoSize = false,
                ForeColor = SystemColors.GrayText
            };
            Controls.Add(bridgeHint);
            y += 42;

            // The shared prompt library, the same folder the Trados plugin reads.
            // Selecting one stores its path, not its text, so a prompt edited
            // anywhere takes effect here on the next segment.
            //
            // The Edit button is the only way into the library from memoQ. An
            // add-in owns no ribbon button, no menu item and no panel, so this
            // dialog is the single UI surface available — without it a memoQ user
            // could pick a prompt but never write or correct one.
            Caption("Prompt", y);
            const int editW = 86;
            _promptPick.Left = fieldX; _promptPick.Top = y; _promptPick.Width = fieldW - editW - 6;
            _promptPick.DropDownStyle = ComboBoxStyle.DropDownList;
            _promptPick.SelectedIndexChanged += OnPromptPicked;
            Controls.Add(_promptPick);

            var editPrompts = new Button
            {
                Text = "Edit…",
                Left = fieldX + fieldW - editW,
                Top = y - 1,
                Width = editW,
                Height = _promptPick.Height + 2
            };
            editPrompts.Click += OnEditPrompts;
            Controls.Add(editPrompts);
            y += rowH;

            Caption("Instructions", y);
            _systemPrompt.Left = fieldX; _systemPrompt.Top = y; _systemPrompt.Width = fieldW;
            _systemPrompt.Height = 170;
            _systemPrompt.Multiline = true;
            _systemPrompt.ScrollBars = ScrollBars.Vertical;
            _systemPrompt.AcceptsReturn = true;
            _systemPrompt.Font = new Font(FontFamily.GenericMonospace, 8.5f);
            Controls.Add(_systemPrompt);
            y += 178;

            var promptHint = new Label
            {
                Text = "{SOURCE_LANG} and {TARGET_LANG} are replaced with the project's languages.",
                Left = fieldX, Top = y, AutoSize = true,
                ForeColor = SystemColors.GrayText
            };
            Controls.Add(promptHint);
            y += 26;

            _test.Text = "Test connection";
            _test.Left = fieldX; _test.Top = y; _test.Width = 120; _test.Height = 27;
            _test.Click += OnTestClicked;
            Controls.Add(_test);

            _status.Left = fieldX + 130; _status.Top = y + 6; _status.Width = fieldW - 130; _status.Height = 34;
            _status.ForeColor = SystemColors.GrayText;
            Controls.Add(_status);

            // Confirmed segments are cached on disk so recall survives a restart.
            // That is confidential client text sitting in LocalAppData, so the user
            // gets a visible statement of how much there is and a one-click way to
            // destroy it — not buried in a menu, and not something they have to
            // find a folder path for.
            var forget = new Button
            {
                Text = "Forget stored context",
                Left = labelX, Top = ClientSize.Height - 40, Width = 150, Height = 27
            };
            forget.Click += OnForgetClicked;
            Controls.Add(forget);

            // OK / Cancel / Help, in that order: the same button row memoQ's own
            // dialogs use, so Help is where a memoQ user looks for it.
            //
            // Laid out right-to-left from the form edge rather than with three
            // hardcoded offsets. Adding Help pushed OK 92px left into the
            // stored-context label, whose transparent box then clipped the "O" —
            // deriving both from the same numbers stops that recurring.
            const int btnW = 85;
            const int btnH = 27;
            const int btnGap = 7;

            var rowTop = ClientSize.Height - 40;
            var helpLeft = ClientSize.Width - labelX - btnW;
            var cancelLeft = helpLeft - btnW - btnGap;
            var okLeft = cancelLeft - btnW - btnGap;

            _storedInfo.Left = labelX + 158; _storedInfo.Top = ClientSize.Height - 34;
            _storedInfo.Width = okLeft - _storedInfo.Left - 12; _storedInfo.Height = 20;
            _storedInfo.ForeColor = SystemColors.GrayText;
            _storedInfo.AutoEllipsis = true;
            Controls.Add(_storedInfo);
            UpdateStoredInfo();

            var ok = new Button
            {
                Text = "OK", DialogResult = DialogResult.OK,
                Left = okLeft, Top = rowTop, Width = btnW, Height = btnH
            };
            ok.Click += OnOkClicked;

            var cancel = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel,
                Left = cancelLeft, Top = rowTop, Width = btnW, Height = btnH
            };

            var help = HelpLinks.CreateButton(HelpLinks.GettingStarted);
            help.Left = helpLeft;
            help.Top = rowTop;

            Controls.Add(ok);
            Controls.Add(cancel);
            Controls.Add(help);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void LoadFrom(SupervertalerSettings settings)
        {
            var g = settings.GeneralSettings ?? new SupervertalerGeneralSettings();
            var s = settings.SecureSettings ?? new SupervertalerSecureSettings();

            _provider.SelectedItem = Array.IndexOf(LlmProviders.All, g.Provider) >= 0
                ? g.Provider
                : LlmProviders.Anthropic;
            _model.Text = g.Model;
            _endpoint.Text = g.Endpoint;
            _apiKey.Text = s.ApiKey;
            // Normalise to CRLF for display. A multiline TextBox does not treat a
            // bare LF as a line break, and the stored prompt reliably has them:
            // XML normalises CRLF to LF on read, so however the settings were
            // saved they come back LF-only and the box shows one run-on paragraph.
            var prompt = string.IsNullOrWhiteSpace(g.SystemPrompt)
                ? SupervertalerGeneralSettings.DefaultSystemPrompt
                : g.SystemPrompt;
            _inlineInstructions = NormaliseForDisplay(prompt);
            _systemPrompt.Text = _inlineInstructions;
            PopulatePrompts(g.PromptPath);
            _maxParallel.Value = Math.Max(1, Math.Min(16, g.MaxParallelRequests));
            _batchSize.Value = Math.Max(1, Math.Min(100, g.BatchSize));
            _useTerminology.Checked = g.UseTerminologyContext;
            _useDocumentContext.Checked = g.UseDocumentContext;
            _bridgeMode.Checked = g.BridgeMode;
        }

        private SupervertalerSettings Collect()
        {
            return SupervertalerSettings.Create(
                new SupervertalerGeneralSettings
                {
                    Provider = (_provider.SelectedItem as string) ?? LlmProviders.Anthropic,
                    Model = _model.Text.Trim(),
                    Endpoint = _endpoint.Text.Trim(),
                    PromptPath = SelectedPromptPath(),
                    SystemPrompt = _systemPrompt.ReadOnly ? _inlineInstructions : _systemPrompt.Text,
                    MaxParallelRequests = (int)_maxParallel.Value,
                    UseTerminologyContext = _useTerminology.Checked,
                    UseDocumentContext = _useDocumentContext.Checked,
                    BridgeMode = _bridgeMode.Checked,
                    BatchSize = (int)_batchSize.Value
                },
                new SupervertalerSecureSettings
                {
                    ApiKey = _apiKey.Text.Trim()
                });
        }

        private void UpdateStoredInfo()
        {
            var files = DocumentMemoryStore.FileCount();
            if (files == 0)
            {
                _storedInfo.Text = "Nothing stored yet.";
                return;
            }

            var kb = Math.Max(1, DocumentMemoryStore.TotalBytes() / 1024);
            _storedInfo.Text = $"{files} document(s), {kb} KB on this computer.";
        }

        private void OnForgetClicked(object sender, EventArgs e)
        {
            var files = DocumentMemoryStore.FileCount();
            if (files == 0)
            {
                MessageBox.Show(this, "There is no stored context to forget.", "Supervertaler",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                $"Delete the confirmed segments Supervertaler has stored for {files} document(s)?"
                + Environment.NewLine + Environment.NewLine
                + "This is the material it uses to stay consistent with your earlier choices "
                + "within a document. Your translations in memoQ are not affected.",
                "Forget stored context",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes) return;

            var removed = DocumentMemory.ForgetEverything();
            Core.PluginLog.Write($"User cleared stored document context ({removed} file(s))");
            UpdateStoredInfo();
        }

        /// <summary>
        /// Wraps a library prompt for the combo. The path is the identity; the
        /// display text is category plus name, since two folders may hold prompts
        /// with the same name.
        /// </summary>
        private sealed class PromptChoice
        {
            public string Path;
            public string Display;
            public string Content;
            public override string ToString() => Display;
        }

        private void PopulatePrompts(string selectedPath)
        {
            _promptPick.Items.Clear();

            // First entry is always the escape hatch: whatever is typed below.
            _promptPick.Items.Add(new PromptChoice
            {
                Path = PromptResolver.InlineInstructions,
                Display = "(use the instructions below)"
            });

            foreach (var p in PromptResolver.Available())
            {
                var category = string.IsNullOrWhiteSpace(p.Category) ? "" : p.Category + "  —  ";
                _promptPick.Items.Add(new PromptChoice
                {
                    Path = p.RelativePath,
                    Display = category + p.Name,
                    Content = p.Content
                });
            }

            var match = _promptPick.Items.Cast<PromptChoice>()
                .FirstOrDefault(c => string.Equals(c.Path, selectedPath, StringComparison.OrdinalIgnoreCase));

            _promptPick.SelectedItem = match ?? _promptPick.Items[0];
            UpdatePromptDisplay();
        }

        private string SelectedPromptPath()
        {
            return (_promptPick.SelectedItem as PromptChoice)?.Path ?? PromptResolver.InlineInstructions;
        }

        private void OnPromptPicked(object sender, EventArgs e)
        {
            UpdatePromptDisplay();
        }

        /// <summary>
        /// Opens the prompt editor on whichever prompt is currently selected, and
        /// re-reads the library when it closes so a prompt written or renamed in
        /// there appears in the dropdown straight away.
        ///
        /// Run as a separate process rather than a form of our own: the editor is
        /// shared with the Trados plugin, and hosting a second WinForms window
        /// inside memoQ's UI thread means its bugs become memoQ's bugs. It also
        /// keeps the add-in free of the editor's code entirely.
        /// </summary>
        private void OnEditPrompts(object sender, EventArgs e)
        {
            var exe = Path.Combine(
                Path.GetDirectoryName(typeof(OptionsForm).Assembly.Location) ?? "",
                "Supervertaler.PromptEditor.exe");

            if (!File.Exists(exe))
            {
                MessageBox.Show(this,
                    "The prompt editor is not installed next to the add-in.\r\n\r\nExpected:\r\n" + exe,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selected = _promptPick.SelectedItem as PromptChoice;

            try
            {
                using (var process = Process.Start(new ProcessStartInfo(exe)
                {
                    Arguments = selected != null && !string.IsNullOrEmpty(selected.Path)
                        ? "\"" + selected.Path + "\""
                        : "",
                    UseShellExecute = false
                }))
                {
                    if (process == null) return;

                    // Modal by waiting: the library is a folder of files with no
                    // locking, and letting the user edit a prompt in one window
                    // while changing which prompt is selected in another is a way
                    // to save a stale choice.
                    Enabled = false;
                    try
                    {
                        while (!process.WaitForExit(50))
                            Application.DoEvents();
                    }
                    finally
                    {
                        Enabled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Write("Could not start the prompt editor", ex);
                MessageBox.Show(this, "Could not start the prompt editor.\r\n\r\n" + ex.Message,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // The dropdown caches for ten seconds; the user has almost certainly
            // been in the editor longer, but say so explicitly rather than rely
            // on that.
            PromptResolver.Invalidate();
            PopulatePrompts(SelectedPromptPath());
        }

        /// <summary>
        /// Shows the selected prompt's text read-only, or hands the box back when
        /// the inline instructions are chosen.
        ///
        /// Read-only on purpose: the library file is the original, and letting
        /// someone edit a copy here that silently does nothing would be worse than
        /// not offering it. Editing happens where the prompt lives.
        /// </summary>
        private void UpdatePromptDisplay()
        {
            var choice = _promptPick.SelectedItem as PromptChoice;

            if (choice == null || string.IsNullOrEmpty(choice.Path))
            {
                _systemPrompt.ReadOnly = false;
                _systemPrompt.BackColor = SystemColors.Window;
                _systemPrompt.Text = _inlineInstructions;
                return;
            }

            // Remember what was typed, so switching back does not lose it.
            if (!_systemPrompt.ReadOnly) _inlineInstructions = _systemPrompt.Text;

            _systemPrompt.ReadOnly = true;
            _systemPrompt.BackColor = SystemColors.Control;
            _systemPrompt.Text = NormaliseForDisplay(choice.Content);
        }

        private string _inlineInstructions = "";

        /// <summary>
        /// CRLF for the Instructions box. A multiline TextBox does not treat a
        /// bare LF as a line break, and prompt files — Markdown written on any
        /// platform, plus settings that XML has normalised — routinely arrive
        /// LF-only, which renders as one run-on paragraph.
        /// </summary>
        private static string NormaliseForDisplay(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        private void OnOkClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_model.Text))
            {
                MessageBox.Show(this, "Enter a model name.", "Supervertaler",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            Result = Collect();
        }

        private async void OnTestClicked(object sender, EventArgs e)
        {
            _test.Enabled = false;
            _status.ForeColor = SystemColors.GrayText;
            _status.Text = "Contacting the provider…";

            try
            {
                var settings = Collect();

                // A real round trip, not a ping: translating one short segment
                // exercises the key, the model name, the endpoint and the response
                // shape together, which is what actually goes wrong.
                var bundle = new TranslationBundle
                {
                    Source = SegmentBuilder.CreateFromString("The quick brown fox.")
                };

                // A throwaway context: no document, so DocumentMemory contributes
                // nothing and the test exercises exactly the network path.
                var context = new EngineContext(settings, "eng", "nld");

                var result = await SessionRunner.TranslateAsync(
                    bundle, context, CancellationToken.None).ConfigureAwait(true);

                if (result.Exception != null) throw result.Exception;

                var text = result.Translation?.PlainText ?? string.Empty;
                _status.ForeColor = Color.FromArgb(0, 120, 40);
                _status.Text = "OK — " + (text.Length > 40 ? text.Substring(0, 40) + "…" : text);
            }
            catch (Exception ex)
            {
                PluginLog.Write("Test connection failed", ex);
                _status.ForeColor = Color.Firebrick;
                _status.Text = ex.Message.Length > 140 ? ex.Message.Substring(0, 140) + "…" : ex.Message;
            }
            finally
            {
                _test.Enabled = true;
            }
        }
    }
}
