using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Supervertaler.MemoQ.Core;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// How Supervertaler translates, editable without opening memoQ.
    ///
    /// These are the same settings memoQ's own dialog shows, reading and writing
    /// the same shared file, because reaching that dialog costs six clicks
    /// through Project home and a right-click on a provider in a list of thirty.
    /// This window is a program you can pin to the taskbar.
    ///
    /// The API key is deliberately absent. memoQ keeps it in its own encrypted
    /// settings, and moving it here means either putting a key in a plain text
    /// file or encrypting it ourselves, which is a decision to take on its own
    /// rather than as a side effect of tidying a dialog.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly ComboBox _provider = new ComboBox();
        private readonly ComboBox _model = new ComboBox();
        private readonly TextBox _endpoint = new TextBox();
        private readonly NumericUpDown _parallel = new NumericUpDown();
        private readonly NumericUpDown _batchSize = new NumericUpDown();
        private readonly CheckBox _useTerminology = new CheckBox();
        private readonly CheckBox _useDocumentContext = new CheckBox();
        private readonly CheckBox _bridgeMode = new CheckBox();
        private readonly TextBox _apiKey = new TextBox();
        private Label _apiKeySource;

        public SettingsForm()
        {
            // Same reasoning as the main window: the manifest declares the process
            // DPI-aware, so nothing scales the layout unless the form says what
            // its baseline was.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "Translation settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(620, 396);

            const int labelX = 16;
            const int fieldX = 168;
            const int fieldW = 436;
            const int rowH = 32;
            var y = 18;

            Label Caption(string text, int top)
            {
                var label = new Label { Text = text, Left = labelX, Top = top + 3, Width = fieldX - labelX - 8, AutoSize = false };
                Controls.Add(label);
                return label;
            }

            // Hints wrap and then report how tall they became. Fixing their
            // height instead is what clipped "or a gateway." off the end of the
            // endpoint hint and left the parallel-requests box half covered by
            // the label above it: a Label with AutoSize off silently crops what
            // does not fit, and the row below had already been positioned.
            Label Hint(string text, int left, int width)
            {
                var hint = new Label
                {
                    Text = text,
                    Left = left,
                    Top = y,
                    AutoSize = true,
                    MaximumSize = new Size(width, 0),
                    ForeColor = SystemColors.GrayText
                };
                Controls.Add(hint);
                y += hint.PreferredHeight;
                return hint;
            }

            Caption("Provider", y);
            _provider.Left = fieldX; _provider.Top = y; _provider.Width = 200;
            _provider.DropDownStyle = ComboBoxStyle.DropDownList;
            _provider.Items.AddRange(LlmProviders.All);

            // A different provider is a different catalogue. Guarded because
            // assigning SelectedItem during load raises this too, and at that
            // point the key has not been read yet.
            _provider.SelectedIndexChanged += (s, e) => { if (!_loading) LoadModels(refresh: true); };
            Controls.Add(_provider);
            y += rowH;

            Caption("Model", y);
            _model.Left = fieldX; _model.Top = y; _model.Width = fieldW;

            // Editable on purpose. The list comes from the provider, so it cannot
            // cover a gateway, a local model, or anything the endpoint declines to
            // advertise, and typing must keep working for all three.
            _model.DropDownStyle = ComboBoxStyle.DropDown;
            _model.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _model.AutoCompleteSource = AutoCompleteSource.ListItems;
            _model.SelectedIndexChanged += (s, e) =>
            {
                if (_model.SelectedItem is ModelCatalog.Entry entry) _modelId = entry.Id;
            };
            Controls.Add(_model);
            y += rowH;

            Caption("Endpoint (optional)", y);
            _endpoint.Left = fieldX; _endpoint.Top = y; _endpoint.Width = fieldW;
            Controls.Add(_endpoint);
            y += 26;
            Hint("Leave blank for the provider default. Set this for a local model or a gateway.", fieldX, fieldW);
            y += 10;

            Caption("Parallel requests", y);
            _parallel.Left = fieldX; _parallel.Top = y; _parallel.Width = 70;
            _parallel.Minimum = 1; _parallel.Maximum = 16;
            Controls.Add(_parallel);
            y += rowH;

            Caption("Segments per batch", y);
            _batchSize.Left = fieldX; _batchSize.Top = y; _batchSize.Width = 70;
            _batchSize.Minimum = 1; _batchSize.Maximum = 100;
            Controls.Add(_batchSize);
            var batchHint = new Label
            {
                Text = "Pre-translate only; memoQ caps a batch at about 10.",
                Left = fieldX + 84, Top = y + 3, AutoSize = true, ForeColor = SystemColors.GrayText
            };
            Controls.Add(batchHint);
            y += rowH + 6;

            _useTerminology.Text = "Send memoQ's termbase hits and forbidden terms to the model";
            _useTerminology.Left = fieldX; _useTerminology.Top = y; _useTerminology.Width = fieldW;
            Controls.Add(_useTerminology);
            y += 26;

            _useDocumentContext.Text = "Send surrounding segments and project metadata to the model";
            _useDocumentContext.Left = fieldX; _useDocumentContext.Top = y; _useDocumentContext.Width = fieldW;
            Controls.Add(_useDocumentContext);
            y += 26;

            _bridgeMode.Text = "Pre-translate via Claude Desktop (MCP) instead of the API key";
            _bridgeMode.Left = fieldX; _bridgeMode.Top = y; _bridgeMode.Width = fieldW;
            Controls.Add(_bridgeMode);
            y += 24;
            Hint("Pre-translate then only hands the segments to the chat and inserts the translations it "
                + "sends back. Suggestions as you move through segments still use the API key.", fieldX, fieldW);
            y += 14;

            Caption("API key", y);
            _apiKey.Left = fieldX; _apiKey.Top = y; _apiKey.Width = fieldW;
            _apiKey.UseSystemPasswordChar = true;
            Controls.Add(_apiKey);
            y += 26;
            _apiKeySource = Hint(string.Empty, fieldX, fieldW);
            y += 6;
            Hint("Leave it as it is to keep using the key shown. Supervertaler for Trados keeps its keys "
                + "in the same data folder, so a key rotated there is picked up here.", fieldX, fieldW);
            y += 16;

            // The window is sized to the layout rather than the layout trusted to
            // fit a guessed window: hint heights depend on the display's scaling.
            ClientSize = new Size(ClientSize.Width, y + 44);

            var ok = new Button
            {
                Text = "OK", DialogResult = DialogResult.OK,
                Left = ClientSize.Width - 184, Top = ClientSize.Height - 38, Width = 84, Height = 26
            };
            ok.Click += (s, e) => Save();
            Controls.Add(ok);

            var cancel = new Button
            {
                Text = "Cancel", DialogResult = DialogResult.Cancel,
                Left = ClientSize.Width - 94, Top = ClientSize.Height - 38, Width = 84, Height = 26
            };
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            LoadCurrent();
        }

        /// <summary>
        /// The model id, kept apart from what the combo displays. The list shows a
        /// readable name with the id after it, and it is the id that goes to the
        /// provider.
        /// </summary>
        private string _modelId = "";

        /// <summary>Set while <see cref="LoadCurrent"/> populates the controls.</summary>
        private bool _loading;

        /// <summary>
        /// Fills the dropdown from the cache immediately, then asks the provider in
        /// the background and updates if the answer differs. Nothing here blocks:
        /// a provider that is slow or unreachable must not stop the dialog opening,
        /// and the typed value is always preserved.
        /// </summary>
        private async void LoadModels(bool refresh)
        {
            var provider = (_provider.SelectedItem as string) ?? LlmProviders.Anthropic;

            Show(ModelCatalog.Cached(provider));

            try
            {
                var fresh = await ModelCatalog.RefreshAsync(
                    provider, ApiKeyInUse(), _endpoint.Text.Trim(), refresh, CancellationToken.None)
                    .ConfigureAwait(true);

                if (fresh != null && !IsDisposed) Show(fresh);
            }
            catch
            {
                // Reported inside the catalogue; the cache is already on screen.
            }
        }

        private void Show(List<ModelCatalog.Entry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            var typed = _modelId;

            _model.BeginUpdate();
            _model.Items.Clear();
            foreach (var e in entries) _model.Items.Add(e);
            _model.EndUpdate();

            // Re-select what was configured, or leave it in the box when the
            // provider does not list it — which is normal for a gateway.
            var match = entries.FirstOrDefault(e =>
                string.Equals(e.Id, typed, StringComparison.OrdinalIgnoreCase));

            if (match != null) _model.SelectedItem = match;
            else _model.Text = typed;

            _modelId = typed;
        }

        /// <summary>
        /// The id to send to the provider. The list shows "Display name   (id)",
        /// so a picked row is matched back to its id; anything else is taken
        /// literally, which is how a gateway or a local model gets entered.
        /// </summary>
        private string ChosenModelId()
        {
            var typed = (_model.Text ?? string.Empty).Trim();

            foreach (var item in _model.Items)
            {
                if (item is ModelCatalog.Entry entry
                    && string.Equals(entry.ToString(), typed, StringComparison.Ordinal))
                    return entry.Id;
            }

            return typed;
        }

        private string ApiKeyInUse()
        {
            var typed = _apiKey.Text.Trim();
            return typed.Length > 0 ? typed : ApiKeys.Resolve((_provider.SelectedItem as string), null).Key;
        }

        /// <summary>
        /// Reads what is in force. memoQ seeds this file from its settings
        /// resource the first time it builds an engine, so these are the values
        /// the plugin will actually use rather than this program's own defaults.
        /// </summary>
        private void LoadCurrent()
        {
            _loading = true;
            try { LoadCurrentCore(); }
            finally { _loading = false; }
        }

        private void LoadCurrentCore()
        {
            var provider = SharedSettings.ProviderOr(LlmProviders.Anthropic);
            _provider.SelectedItem = Array.IndexOf(LlmProviders.All, provider) >= 0 ? provider : LlmProviders.Anthropic;

            _modelId = SharedSettings.ModelOr("claude-opus-5");
            _model.Text = _modelId;
            _endpoint.Text = SharedSettings.EndpointOr(string.Empty);
            _parallel.Value = Math.Max(1, Math.Min(16, SharedSettings.ParallelOr(4)));
            _batchSize.Value = Math.Max(1, Math.Min(100, SharedSettings.BatchSizeOr(20)));
            _useTerminology.Checked = SharedSettings.UseTerminologyContextOr(true);
            _useDocumentContext.Checked = SharedSettings.UseDocumentContextOr(true);
            _bridgeMode.Checked = SharedSettings.BridgeMode;

            // Null for the resource: this program cannot read memoQ's settings, and
            // does not need to, because memoQ copies that key into the shared file.
            var key = ApiKeys.Resolve(provider, null);
            _apiKey.Text = key.Key;
            _apiKeySource.Text = key.HasKey ? "Key in use: " + key.Source : "No API key is set.";

            // Last, because listing models needs the key and the endpoint.
            LoadModels(refresh: false);
        }

        private void Save()
        {
            SharedSettings.Provider = (_provider.SelectedItem as string) ?? LlmProviders.Anthropic;
            SharedSettings.Model = ChosenModelId();
            SharedSettings.Endpoint = _endpoint.Text.Trim();
            SharedSettings.Parallel = (int)_parallel.Value;
            SharedSettings.BatchSize = (int)_batchSize.Value;
            SharedSettings.UseTerminologyContext = _useTerminology.Checked;
            SharedSettings.UseDocumentContext = _useDocumentContext.Checked;
            SharedSettings.BridgeMode = _bridgeMode.Checked;

            // Recorded only as an override. Saving the key it was already showing
            // would pin a copy and stop the Trados file being the one place to
            // rotate it.
            var typed = _apiKey.Text.Trim();
            var without = ApiKeys.Fallback(SharedSettings.Provider, null).Key;
            SharedSettings.ApiKey = string.Equals(typed, without, StringComparison.Ordinal) ? string.Empty : typed;
        }
    }
}
