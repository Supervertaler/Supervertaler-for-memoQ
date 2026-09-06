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
        private readonly Button _fetchModels = new Button();
        private readonly CheckBox _showAllModels = new CheckBox();
        private Label _modelStatus;
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
            AppIcon.Apply(this);
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
            // A different provider is a different list, and the model that was
            // chosen for the old one is meaningless under the new one - leaving
            // claude-opus-5 selected under Google saves a pair that can only fail
            // at the provider, with an error that does not say why. Clearing it
            // lets the new provider's first recommendation take the field.
            //
            // Only on a switch the user made. During loading the stored model has
            // to survive, because it may be a gateway id in neither list.
            _provider.SelectedIndexChanged += (s, e) =>
            {
                if (_loading) return;
                RememberTypedKey();
                _lastProvider = Provider;
                _modelId = "";
                ShowKeyFor(Provider);
                ShowModels();
            };
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

            // The short list is the default and the whole inventory is one tick
            // away, because a translator in a hurry needs a list they can read,
            // and a model released after this build has to be reachable anyway.
            _fetchModels.Text = "Fetch list";
            _fetchModels.Left = fieldX; _fetchModels.Top = y; _fetchModels.Width = 84; _fetchModels.Height = 25;
            _fetchModels.Click += FetchModels;
            Controls.Add(_fetchModels);

            _showAllModels.Text = "Show all models";
            _showAllModels.Left = fieldX + 94; _showAllModels.Top = y + 4; _showAllModels.AutoSize = true;
            _showAllModels.CheckedChanged += (s, e) => { if (!_loading) ShowModels(); };
            Controls.Add(_showAllModels);

            var modelTips = new ToolTip();
            modelTips.SetToolTip(_fetchModels,
                "Ask the provider for its current model list, using the key below. "
                + "What it returns is remembered and shown with Show all models ticked.");
            modelTips.SetToolTip(_showAllModels,
                "Off: the short list \u2013 the few models worth recommending, with a verdict "
                + "each. On: everything the provider's own list returned as well.");

            y += 29;

            // One line, fixed, ellipsised. The other hints on this form wrap and
            // then report how tall they became, which is right for text that is
            // written once - but this one is rewritten at runtime with whatever a
            // provider says went wrong, and a two-line failure message would
            // reflow a dialog that has already been laid out.
            _modelStatus = new Label
            {
                Left = fieldX,
                Top = y,
                Width = fieldW,
                Height = 17,
                AutoSize = false,
                AutoEllipsis = true,
                ForeColor = SystemColors.GrayText
            };
            Controls.Add(_modelStatus);
            y += 25;

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

            // No memory bank here. It sits on the main window's context bar with
            // the prompt and the glossary, because those three are what changes
            // between jobs - what the model knows before it is shown a segment -
            // whereas this dialog is provider, model and endpoint, which are set
            // once. It was here first only because it is STORED like the settings
            // below, which turned out to be the wrong test.
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

        private string Provider => (_provider.SelectedItem as string) ?? LlmProviders.Anthropic;

        /// <summary>
        /// Fills the dropdown from what is already known - the short list, plus the
        /// last fetch when the tick box asks for it. Never goes to the network, so
        /// opening the dialog costs nothing and sends no key anywhere.
        /// </summary>
        private void ShowModels()
        {
            var provider = Provider;
            Show(ModelCatalog.Entries(provider, _showAllModels.Checked));

            _fetchModels.Enabled = ModelCatalog.CanFetch(provider);
            _modelStatus.ForeColor = SystemColors.GrayText;

            var extra = ModelCatalog.ExtraCount(provider);
            var on = ModelCatalog.FetchedOn(provider);

            if (on == null)
                _modelStatus.Text = "The models worth recommending. Fetch list asks the provider for the rest.";
            else if (extra == 0)
                _modelStatus.Text = "Provider list fetched " + on + "; nothing in it beyond the short list.";
            else
                _modelStatus.Text = "Provider list fetched " + on + "; " + extra
                    + (extra == 1 ? " model" : " models") + " beyond the short list.";
        }

        /// <summary>
        /// Asks the provider for its own list. Explicit rather than automatic: the
        /// short list is what the dialog shows, so fetching is the act of saying
        /// "show me the rest" - and it ticks the box, because that is what the
        /// click meant.
        /// </summary>
        private async void FetchModels(object sender, EventArgs e)
        {
            var provider = Provider;

            _fetchModels.Enabled = false;
            _modelStatus.ForeColor = SystemColors.GrayText;
            _modelStatus.Text = "Asking " + provider + " for its model list\u2026";

            try
            {
                var fetched = await ModelCatalog
                    .FetchAsync(provider, ApiKeyInUse(), _endpoint.Text.Trim(), CancellationToken.None)
                    .ConfigureAwait(true);

                if (IsDisposed) return;

                if (fetched == null)
                {
                    _modelStatus.Text = "No API key is set, so there is nobody to ask.";
                    return;
                }

                var extra = ModelCatalog.ExtraCount(provider);

                // A click on this button means "show me". Ticking the box
                // repopulates by itself, so only populate here when it was on.
                if (extra > 0 && !_showAllModels.Checked) _showAllModels.Checked = true;
                else ShowModels();

                _modelStatus.ForeColor = SystemColors.GrayText;
                _modelStatus.Text = provider + ": " + fetched.Count + " models"
                    + (extra > 0
                        ? ", " + extra + " beyond the short list."
                        : ", none beyond the short list.");
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;

                // The dropdown still holds the short list, so this is a status
                // line rather than a dialog: nothing anyone was doing is lost.
                _modelStatus.ForeColor = Color.FromArgb(180, 60, 60);
                _modelStatus.Text = "Could not list models: " + ex.Message;
            }
            finally
            {
                if (!IsDisposed) _fetchModels.Enabled = ModelCatalog.CanFetch(provider);
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

            // Re-select what is configured, or leave it in the box when neither
            // list carries it - which is normal for a gateway. With nothing
            // configured at all, the first recommendation is the answer: this
            // list is ordered, and its first entry is the one to reach for.
            var match = entries.FirstOrDefault(e =>
                string.Equals(e.Id, typed, StringComparison.OrdinalIgnoreCase));

            if (match == null && string.IsNullOrWhiteSpace(typed)) match = entries.FirstOrDefault();

            if (match != null)
            {
                _model.SelectedItem = match;
                _modelId = match.Id;
            }
            else
            {
                _model.Text = typed;
                _modelId = typed;
            }
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
            return typed.Length > 0 ? typed : ApiKeys.Resolve(Provider, null).Key;
        }

        /// <summary>The provider the key box is currently showing a key for.</summary>
        private string _lastProvider = "";

        /// <summary>
        /// Keys typed here, by provider, for as long as this dialog is open. Only
        /// the current provider’s key is saved - this exists so that looking at
        /// another provider and coming back does not discard what was half-typed.
        /// </summary>
        private readonly Dictionary<string, string> _typedKeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The API key box follows the provider. It used to be filled once, at
        /// load, for whichever provider the dialog opened on - so switching to
        /// OpenAI and pressing Fetch list sent an Anthropic key to OpenAI and got
        /// back a 401 blaming the key rather than the mix-up.
        /// </summary>
        private void ShowKeyFor(string provider)
        {
            var inherited = ApiKeys.Resolve(provider, null);

            string typed;
            if (_typedKeys.TryGetValue(provider, out typed))
            {
                _apiKey.Text = typed;
                _apiKeySource.Text = "Key typed here, in place of the one "
                    + (inherited.HasKey ? inherited.Source : "that is not set");
                return;
            }

            _apiKey.Text = inherited.Key;
            _apiKeySource.Text = inherited.HasKey
                ? "Key in use: " + inherited.Source
                : "No API key is set for " + provider + ".";
        }

        /// <summary>
        /// Keeps what was typed for the provider being left. Compared against what
        /// that provider would have inherited, so a box merely showing an
        /// inherited key is not recorded as an override.
        /// </summary>
        private void RememberTypedKey()
        {
            if (string.IsNullOrEmpty(_lastProvider)) return;

            var shown = _apiKey.Text.Trim();
            var inherited = ApiKeys.Resolve(_lastProvider, null).Key;

            if (string.Equals(shown, inherited, StringComparison.Ordinal)) _typedKeys.Remove(_lastProvider);
            else _typedKeys[_lastProvider] = shown;
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
            _lastProvider = provider;
            ShowKeyFor(provider);

            _showAllModels.Checked = SharedSettings.ShowAllModels;

            // Last, because the status line reports on the provider just chosen.
            ShowModels();
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
            SharedSettings.ShowAllModels = _showAllModels.Checked;

            // Recorded only as an override. Saving the key it was already showing
            // would pin a copy and stop the Trados file being the one place to
            // rotate it.
            var typed = _apiKey.Text.Trim();
            var without = ApiKeys.Fallback(SharedSettings.Provider, null).Key;
            SharedSettings.ApiKey = string.Equals(typed, without, StringComparison.Ordinal) ? string.Empty : typed;
        }
    }
}
