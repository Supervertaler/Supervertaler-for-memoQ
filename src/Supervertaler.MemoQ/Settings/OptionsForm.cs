using System;
using System.Drawing;
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
        private readonly TextBox _systemPrompt = new TextBox();
        private readonly NumericUpDown _maxParallel = new NumericUpDown();
        private readonly CheckBox _useTerminology = new CheckBox();
        private readonly CheckBox _useDocumentContext = new CheckBox();
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
            ClientSize = new Size(660, 540);

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

            _useTerminology.Text = "Send memoQ's termbase hits and forbidden terms to the model";
            _useTerminology.Left = fieldX; _useTerminology.Top = y; _useTerminology.AutoSize = true;
            Controls.Add(_useTerminology);
            y += 24;

            _useDocumentContext.Text = "Send surrounding segments and project metadata to the model";
            _useDocumentContext.Left = fieldX; _useDocumentContext.Top = y; _useDocumentContext.AutoSize = true;
            Controls.Add(_useDocumentContext);
            y += 30;

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

            _storedInfo.Left = labelX + 158; _storedInfo.Top = ClientSize.Height - 34;
            _storedInfo.Width = 250; _storedInfo.Height = 20;
            _storedInfo.ForeColor = SystemColors.GrayText;
            Controls.Add(_storedInfo);
            UpdateStoredInfo();

            // OK / Cancel / Help, in that order: the same button row memoQ's own
            // dialogs use, so the Help button is where a memoQ user looks for it.
            var ok = new Button
            {
                Text = "OK", DialogResult = DialogResult.OK,
                Left = ClientSize.Width - 282, Top = ClientSize.Height - 40, Width = 85, Height = 27
            };
            ok.Click += OnOkClicked;

            var cancel = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel,
                Left = ClientSize.Width - 190, Top = ClientSize.Height - 40, Width = 85, Height = 27
            };

            var help = HelpLinks.CreateButton(HelpLinks.GettingStarted);
            help.Left = ClientSize.Width - 98;
            help.Top = ClientSize.Height - 40;

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
            _systemPrompt.Text = prompt.Replace("\r\n", "\n").Replace("\n", "\r\n");
            _maxParallel.Value = Math.Max(1, Math.Min(16, g.MaxParallelRequests));
            _useTerminology.Checked = g.UseTerminologyContext;
            _useDocumentContext.Checked = g.UseDocumentContext;
        }

        private SupervertalerSettings Collect()
        {
            return SupervertalerSettings.Create(
                new SupervertalerGeneralSettings
                {
                    Provider = (_provider.SelectedItem as string) ?? LlmProviders.Anthropic,
                    Model = _model.Text.Trim(),
                    Endpoint = _endpoint.Text.Trim(),
                    SystemPrompt = _systemPrompt.Text,
                    MaxParallelRequests = (int)_maxParallel.Value,
                    UseTerminologyContext = _useTerminology.Checked,
                    UseDocumentContext = _useDocumentContext.Checked,
                    BatchSize = Result?.GeneralSettings?.BatchSize ?? 20
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
