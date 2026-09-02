using System;
using System.Drawing;
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
        private readonly TextBox _model = new TextBox();
        private readonly TextBox _endpoint = new TextBox();
        private readonly NumericUpDown _parallel = new NumericUpDown();
        private readonly NumericUpDown _batchSize = new NumericUpDown();
        private readonly CheckBox _useTerminology = new CheckBox();
        private readonly CheckBox _useDocumentContext = new CheckBox();
        private readonly CheckBox _bridgeMode = new CheckBox();

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

            Label Hint(string text, int top, int left, int width)
            {
                var hint = new Label
                {
                    Text = text, Left = left, Top = top, Width = width,
                    ForeColor = SystemColors.GrayText, AutoSize = false, Height = 30
                };
                Controls.Add(hint);
                return hint;
            }

            Caption("Provider", y);
            _provider.Left = fieldX; _provider.Top = y; _provider.Width = 200;
            _provider.DropDownStyle = ComboBoxStyle.DropDownList;
            _provider.Items.AddRange(LlmProviders.All);
            Controls.Add(_provider);
            y += rowH;

            Caption("Model", y);
            _model.Left = fieldX; _model.Top = y; _model.Width = fieldW;
            Controls.Add(_model);
            y += rowH;

            Caption("Endpoint (optional)", y);
            _endpoint.Left = fieldX; _endpoint.Top = y; _endpoint.Width = fieldW;
            Controls.Add(_endpoint);
            y += 26;
            Hint("Leave blank for the provider default. Set this for a local model or a gateway.", y, fieldX, fieldW);
            y += 26;

            Caption("Parallel requests", y);
            _parallel.Left = fieldX; _parallel.Top = y; _parallel.Width = 70;
            _parallel.Minimum = 1; _parallel.Maximum = 16;
            Controls.Add(_parallel);
            y += rowH;

            Caption("Segments per batch", y);
            _batchSize.Left = fieldX; _batchSize.Top = y; _batchSize.Width = 70;
            _batchSize.Minimum = 1; _batchSize.Maximum = 100;
            Controls.Add(_batchSize);
            Hint("Pre-translate only; memoQ caps a batch at about 10.", y + 3, fieldX + 84, fieldW - 84);
            y += rowH + 4;

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
                + "sends back. Suggestions as you move through segments still use the API key.", y, fieldX, fieldW);
            y += 34;

            Hint("The API key stays in memoQ, where it is stored encrypted: Project home → Settings → "
                + "MT settings → Supervertaler.", y, labelX, fieldW + fieldX - labelX);

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
        /// Reads what is in force. memoQ seeds this file from its settings
        /// resource the first time it builds an engine, so these are the values
        /// the plugin will actually use rather than this program's own defaults.
        /// </summary>
        private void LoadCurrent()
        {
            var provider = SharedSettings.ProviderOr(LlmProviders.Anthropic);
            _provider.SelectedItem = Array.IndexOf(LlmProviders.All, provider) >= 0 ? provider : LlmProviders.Anthropic;

            _model.Text = SharedSettings.ModelOr("claude-opus-5");
            _endpoint.Text = SharedSettings.EndpointOr(string.Empty);
            _parallel.Value = Math.Max(1, Math.Min(16, SharedSettings.ParallelOr(4)));
            _batchSize.Value = Math.Max(1, Math.Min(100, SharedSettings.BatchSizeOr(20)));
            _useTerminology.Checked = SharedSettings.UseTerminologyContextOr(true);
            _useDocumentContext.Checked = SharedSettings.UseDocumentContextOr(true);
            _bridgeMode.Checked = SharedSettings.BridgeMode;
        }

        private void Save()
        {
            SharedSettings.Provider = (_provider.SelectedItem as string) ?? LlmProviders.Anthropic;
            SharedSettings.Model = _model.Text.Trim();
            SharedSettings.Endpoint = _endpoint.Text.Trim();
            SharedSettings.Parallel = (int)_parallel.Value;
            SharedSettings.BatchSize = (int)_batchSize.Value;
            SharedSettings.UseTerminologyContext = _useTerminology.Checked;
            SharedSettings.UseDocumentContext = _useDocumentContext.Checked;
            SharedSettings.BridgeMode = _bridgeMode.Checked;
        }
    }
}
