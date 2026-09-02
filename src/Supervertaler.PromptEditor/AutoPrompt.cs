using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Supervertaler.Core;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// The editor's view of the running memoQ plugin, over the same localhost
    /// bridge the MCP server uses.
    ///
    /// The editor is a separate process and knows nothing about the project on
    /// its own. Everything AutoPrompt needs — the captured document, confirmed
    /// pairs, glossary hits, and the API key — lives inside memoQ's process, so
    /// the drafting happens there and this class only asks for it.
    /// </summary>
    internal sealed class MemoQBridgeClient : IDisposable
    {
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        private readonly string _base;

        private MemoQBridgeClient(string baseUrl, string token)
        {
            _base = baseUrl;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public static string HandshakePath => Path.Combine(SupervertalerPaths.Root, "memoq", "runtime", "bridge.json");

        /// <summary>
        /// Connects to the live bridge, or returns null with a reason. A handshake
        /// whose process has exited is treated as absent: memoQ closed without
        /// tidying up, and the port in the file is nobody's.
        /// </summary>
        public static MemoQBridgeClient TryConnect(out string reason)
        {
            reason = null;
            try
            {
                if (!File.Exists(HandshakePath))
                {
                    reason = "memoQ is not running with the Supervertaler engine active. Open your project in memoQ "
                           + "and click into a segment (or run Pre-translate), then try again.";
                    return null;
                }

                var text = File.ReadAllText(HandshakePath);
                var port = Regex.Match(text, "\"port\"\\s*:\\s*(\\d+)").Groups[1].Value;
                var token = Regex.Match(text, "\"token\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                var pid = Regex.Match(text, "\"pid\"\\s*:\\s*(\\d+)").Groups[1].Value;

                if (!IsAlive(pid))
                {
                    reason = "memoQ appears to have closed. Start it, open your project and click into a segment.";
                    return null;
                }

                return new MemoQBridgeClient("http://127.0.0.1:" + port, token);
            }
            catch (Exception ex)
            {
                reason = "Could not read the bridge handshake: " + ex.Message;
                return null;
            }
        }

        private static bool IsAlive(string pid)
        {
            try { return int.TryParse(pid, out var id) && !Process.GetProcessById(id).HasExited; }
            catch (Exception) { return false; }
        }

        public async Task<ProjectInfo> GetProjectAsync()
        {
            var json = await _http.GetStringAsync(_base + "/v1/project").ConfigureAwait(false);
            return Deserialize<ProjectInfo>(json);
        }

        public async Task<AutoPromptResult> DraftAsync(AutoPromptRequest request)
        {
            var body = new StringContent(Serialize(request), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(_base + "/v1/autoprompt", body).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var err = Deserialize<ErrorBody>(json);
                throw new InvalidOperationException(err?.Error ?? ("HTTP " + (int)response.StatusCode));
            }

            return Deserialize<AutoPromptResult>(json);
        }

        private static string Serialize<T>(T obj)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(ms, obj);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static T Deserialize<T>(string json) where T : class
        {
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
            }
            catch (Exception) { return null; }
        }

        public void Dispose() => _http.Dispose();

        [DataContract] internal class ErrorBody { [DataMember(Name = "error")] public string Error { get; set; } }

        [DataContract]
        internal class ProjectInfo
        {
            [DataMember(Name = "sourceLanguage")] public string SourceLanguage { get; set; }
            [DataMember(Name = "targetLanguage")] public string TargetLanguage { get; set; }
            [DataMember(Name = "documents")] public DocumentInfo[] Documents { get; set; }
            [DataMember(Name = "note")] public string Note { get; set; }
        }

        [DataContract]
        internal class DocumentInfo
        {
            [DataMember(Name = "key")] public string Key { get; set; }
            [DataMember(Name = "origin")] public string Origin { get; set; }
            [DataMember(Name = "projectName")] public string ProjectName { get; set; }
            [DataMember(Name = "documentName")] public string DocumentName { get; set; }
            [DataMember(Name = "client")] public string Client { get; set; }

            public bool IsVisitedBucket => Key != null && Key.StartsWith("visited_", StringComparison.Ordinal);
            [DataMember(Name = "domain")] public string Domain { get; set; }
            [DataMember(Name = "subject")] public string Subject { get; set; }
            [DataMember(Name = "capturedSegments")] public int CapturedSegments { get; set; }
            [DataMember(Name = "confirmedPairs")] public int ConfirmedPairs { get; set; }
        }

        [DataContract]
        internal class AutoPromptRequest
        {
            [DataMember(Name = "document")] public string Document { get; set; }
            [DataMember(Name = "hint")] public string Hint { get; set; }
            [DataMember(Name = "includeTerms")] public bool IncludeTerms { get; set; }
            [DataMember(Name = "includeConfirmed")] public bool IncludeConfirmed { get; set; }
        }

        [DataContract]
        internal class AutoPromptResult
        {
            [DataMember(Name = "content")] public string Content { get; set; }
            [DataMember(Name = "suggestedName")] public string SuggestedName { get; set; }
            [DataMember(Name = "domain")] public string Domain { get; set; }
            [DataMember(Name = "summary")] public string Summary { get; set; }
            [DataMember(Name = "description")] public string Description { get; set; }
            [DataMember(Name = "termCount")] public int TermCount { get; set; }
            [DataMember(Name = "confirmedPairCount")] public int ConfirmedPairCount { get; set; }
        }
    }

    /// <summary>
    /// "Draft a prompt for this project": pick the captured document, add a
    /// briefing if you have one, generate. The result opens in the editor as a
    /// saved prompt for review — the same place every other prompt is edited.
    /// </summary>
    internal sealed class AutoPromptDialog : Form
    {
        private readonly MemoQBridgeClient _bridge;
        private readonly ComboBox _document = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label _documentInfo = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly TextBox _hint = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
        private readonly CheckBox _terms = new CheckBox { Text = "Include glossary hits from the document", Checked = true, AutoSize = true };
        private readonly CheckBox _confirmed = new CheckBox { Text = "Include segments already confirmed in memoQ", Checked = true, AutoSize = true };
        private readonly Button _generate = new Button { Text = "Generate", Width = 110, Height = 28 };
        private readonly Button _cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };

        private MemoQBridgeClient.DocumentInfo[] _documents = new MemoQBridgeClient.DocumentInfo[0];

        public MemoQBridgeClient.AutoPromptResult Result { get; private set; }

        public AutoPromptDialog(MemoQBridgeClient bridge)
        {
            _bridge = bridge;

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Draft a prompt for the memoQ project";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(560, 372);

            var y = 14;
            Controls.Add(new Label { Text = "Document", Left = 14, Top = y + 4, AutoSize = true });
            _document.Left = 120; _document.Top = y; _document.Width = 426;
            Controls.Add(_document);
            y += 30;
            _documentInfo.Left = 120; _documentInfo.Top = y;
            Controls.Add(_documentInfo);
            y += 30;

            Controls.Add(new Label
            {
                Text = "Anything the AI should know? (client, audience, style, what to avoid)",
                Left = 14, Top = y, AutoSize = true
            });
            y += 22;
            _hint.Left = 14; _hint.Top = y; _hint.Width = 532; _hint.Height = 110;
            Controls.Add(_hint);
            y += 120;

            _terms.Left = 14; _terms.Top = y; Controls.Add(_terms); y += 24;
            _confirmed.Left = 14; _confirmed.Top = y; Controls.Add(_confirmed); y += 34;

            _status.Left = 14; _status.Top = y + 6; Controls.Add(_status);
            _cancel.Left = 546 - 90; _cancel.Top = y; Controls.Add(_cancel);
            _generate.Left = _cancel.Left - 110 - 8; _generate.Top = y; Controls.Add(_generate);

            AcceptButton = _generate;
            CancelButton = _cancel;

            _document.SelectedIndexChanged += (s, e) => ShowDocumentInfo();
            _generate.Click += async (s, e) => await GenerateAsync();
            Shown += async (s, e) => await LoadDocumentsAsync();
        }

        private async Task LoadDocumentsAsync()
        {
            _status.Text = "Reading the project…";
            try
            {
                var project = await _bridge.GetProjectAsync();

                // Real documents first (most recently active first, as the
                // bridge orders them); the "rows you have visited" bucket last.
                // That bucket is one bag per language pair fed by the
                // terminology plugin, useful when a document was pre-translated
                // with another engine, but never the natural default.
                _documents = (project?.Documents ?? new MemoQBridgeClient.DocumentInfo[0])
                    .OrderBy(d => d.IsVisitedBucket ? 1 : 0)
                    .ToArray();

                _document.Items.Clear();
                foreach (var d in _documents)
                {
                    string label;
                    if (d.IsVisitedBucket)
                        label = "Rows you have visited in the editor (any MT engine)";
                    else if (!string.IsNullOrWhiteSpace(d.DocumentName))
                        label = d.DocumentName + (string.IsNullOrWhiteSpace(d.ProjectName) ? "" : "  —  " + d.ProjectName);
                    else if (!string.IsNullOrWhiteSpace(d.Client))
                        label = d.Client + (string.IsNullOrWhiteSpace(d.Subject) ? "" : " / " + d.Subject);
                    else
                        label = (d.Subject ?? d.Domain ?? "Document") + "  (name unknown)";

                    _document.Items.Add(label + "   (" + d.CapturedSegments + " segment"
                        + (d.CapturedSegments == 1 ? "" : "s") + ")");
                }

                if (_documents.Length == 0)
                {
                    _status.Text = project?.Note ?? "Nothing captured yet.";
                    _generate.Enabled = false;
                }
                else
                {
                    _document.SelectedIndex = 0;
                    _status.Text = (project.SourceLanguage ?? "?") + " → " + (project.TargetLanguage ?? "?");
                }
            }
            catch (Exception ex)
            {
                _status.Text = "Could not read the project: " + ex.Message;
                _generate.Enabled = false;
            }
        }

        private void ShowDocumentInfo()
        {
            var i = _document.SelectedIndex;
            if (i < 0 || i >= _documents.Length) { _documentInfo.Text = ""; return; }
            var d = _documents[i];
            _documentInfo.Text = d.CapturedSegments + " segments captured, " + d.ConfirmedPairs + " confirmed"
                + (string.IsNullOrWhiteSpace(d.Domain) ? "" : " · " + d.Domain)
                + (d.Origin != null && d.Origin.StartsWith("rows the cursor") ? " · visited rows only" : "");
        }

        private async Task GenerateAsync()
        {
            var i = _document.SelectedIndex;
            if (i < 0 || i >= _documents.Length) return;

            _generate.Enabled = false; _cancel.Enabled = false;
            _document.Enabled = false; _hint.Enabled = false; _terms.Enabled = false; _confirmed.Enabled = false;
            _status.Text = "Drafting… this takes a minute or two (two model calls, the second a long one).";
            UseWaitCursor = true;

            try
            {
                Result = await _bridge.DraftAsync(new MemoQBridgeClient.AutoPromptRequest
                {
                    Document = _documents[i].Key,
                    Hint = _hint.Text.Trim(),
                    IncludeTerms = _terms.Checked,
                    IncludeConfirmed = _confirmed.Checked
                });

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                UseWaitCursor = false;
                _status.Text = "";
                MessageBox.Show(this, ex.Message, "AutoPrompt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _generate.Enabled = true; _cancel.Enabled = true;
                _document.Enabled = true; _hint.Enabled = true; _terms.Enabled = true; _confirmed.Enabled = true;
            }
        }
    }
}
