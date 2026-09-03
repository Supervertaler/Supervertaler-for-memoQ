using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using MemoQ.PreviewInterfaces;
using MemoQ.PreviewInterfaces.Entities;
using MemoQ.PreviewInterfaces.Exceptions;
using MemoQ.PreviewInterfaces.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Supervertaler.MemoQ.Preview
{
    /// <summary>
    /// The Supervertaler preview tool: memoQ's Preview SDK on one side, the
    /// plugin's bridge on the other.
    ///
    /// memoQ only talks Preview SDK to a separate process, which it launches
    /// itself once the tool is registered (Options > External preview tools,
    /// "Auto-start with memoQ"). This process registers, receives memoQ's
    /// pushes — every row's source and target text, the active row on each
    /// cursor move, the document's real name — and forwards them to the
    /// plugin's localhost bridge, which serves them to the MCP client. It also
    /// long-polls the bridge for the one command that goes the other way:
    /// select a segment, done through RequestHighlightChange, the same call
    /// memoQ's PDF preview uses to jump to a row from outside.
    ///
    /// Nothing here is UI: a tray icon to see status and quit. Everything else
    /// is two connections that each reconnect on their own when the other end
    /// goes away — memoQ restarts, the plugin's bridge starts late (it starts
    /// with the first Supervertaler engine, which can be minutes after memoQ).
    /// </summary>
    internal static class Program
    {
        // Fixed for life: memoQ keys its "allow this tool" decision on it.
        internal static readonly Guid ToolId = new Guid("7a3c5a52-3f0e-4b7b-9d2a-5e6f2c1a9b41");

        internal static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Supervertaler.memoQ");
        internal static readonly string LogPath = Path.Combine(DataDir, "preview-tool.log");

        private static volatile bool _quit;
        private static NotifyIcon _icon;

        /// <summary>
        /// The Supervertaler mark at the size the notification area wants.
        ///
        /// Asking the .ico for SmallIconSize picks the frame Windows would
        /// otherwise have to rescale, which matters here because the tray is
        /// where this program lives: it has no window of its own.
        /// </summary>
        private static System.Drawing.Icon TrayIcon()
        {
            try
            {
                using (var stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("Supervertaler.MemoQ.Preview.Resources.sv-icon.ico"))
                {
                    if (stream != null)
                        return new System.Drawing.Icon(stream, SystemInformation.SmallIconSize);
                }
            }
            catch (Exception ex)
            {
                Log("tray icon: " + ex.Message);
            }

            // A generic glyph in the tray beats no tray presence at all.
            return System.Drawing.SystemIcons.Application;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // One instance. memoQ auto-starts the tool; the user may too.
            using (var mutex = new Mutex(true, "Supervertaler.MemoQ.Preview", out var first))
            {
                if (!first) return;

                Application.EnableVisualStyles();
                Directory.CreateDirectory(DataDir);
                TrimLog();
                Log("start");

                var bridge = new BridgeLink();
                var memoq = new MemoQLink(bridge);

                using (_icon = new NotifyIcon
                {
                    Icon = TrayIcon(),
                    Visible = true,
                    Text = "Supervertaler – memoQ live document link"
                })
                {
                    var menu = new ContextMenuStrip();
                    var status = new ToolStripMenuItem("Starting…") { Enabled = false };
                    menu.Items.Add(status);
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("Open log", null, (s, e) => Process.Start("notepad.exe", LogPath));
                    menu.Items.Add("Quit", null, (s, e) => { _quit = true; Application.Exit(); });
                    _icon.ContextMenuStrip = menu;

                    var ticker = new System.Windows.Forms.Timer { Interval = 2000 };
                    ticker.Tick += (s, e) =>
                    {
                        status.Text = (memoq.Connected ? "memoQ: connected" : "memoQ: waiting")
                                    + "   ·   " + (bridge.Reachable ? "plugin: connected" : "plugin: waiting");
                        _icon.Text = "Supervertaler – " + status.Text;
                    };
                    ticker.Start();

                    new Thread(memoq.Run) { IsBackground = true, Name = "memoq-link" }.Start();
                    new Thread(() => bridge.CommandLoop(memoq)) { IsBackground = true, Name = "bridge-commands" }.Start();

                    Application.Run();
                }

                _quit = true;
                memoq.Shutdown();
                Log("exit");
            }
        }

        internal static bool Quitting => _quit;

        private static readonly object LogLock = new object();

        internal static void Log(string line)
        {
            lock (LogLock)
            {
                try
                {
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + line + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
        }

        private static void TrimLog()
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                    File.WriteAllText(LogPath, "");
            }
            catch (Exception) { }
        }
    }

    // ── memoQ side ─────────────────────────────────────────────────────────

    internal sealed class MemoQLink : IPreviewToolCallback
    {
        private readonly BridgeLink _bridge;
        private PreviewServiceProxy _proxy;
        private volatile bool _connected;

        public MemoQLink(BridgeLink bridge) { _bridge = bridge; }

        public bool Connected => _connected;

        /// <summary>Connect, and keep reconnecting for as long as we live.</summary>
        public void Run()
        {
            var backoff = 3;
            while (!Program.Quitting)
            {
                if (!_connected)
                {
                    try
                    {
                        ConnectOnce();
                        backoff = 3;
                    }
                    catch (PreviewServiceUnavailableException)
                    {
                        // memoQ not running, or its preview service off. Quietly retry.
                    }
                    catch (Exception ex)
                    {
                        Program.Log("memoQ connect failed: " + ex.GetType().Name + ": " + ex.Message);
                        backoff = Math.Min(30, backoff * 2);
                    }
                }
                // While connected, ask for the id list again now and then. The
                // first list after connect held 11 of 21 rows — memoQ appears to
                // list what it has loaded — and rows arrive as the user scrolls.
                if (_connected && (DateTime.UtcNow - _lastIdRefresh) > TimeSpan.FromSeconds(45))
                {
                    _lastIdRefresh = DateTime.UtcNow;
                    try { _proxy?.RequestPreviewPartIdUpdate(); } catch (Exception ex) { Program.Log("id refresh: " + ex.Message); }
                }

                Thread.Sleep(TimeSpan.FromSeconds(_connected ? 5 : backoff));
            }
        }

        private DateTime _lastIdRefresh = DateTime.MinValue;

        private void ConnectOnce()
        {
            Dispose();
            _proxy = new PreviewServiceProxy(this, "MQ_PREVIEW_PIPE", CommunicationProtocols.NamedPipe);

            var reg = _proxy.Register(new RegistrationRequest(
                Program.ToolId,
                "Supervertaler",
                "Supervertaler for memoQ – live document link for the MCP bridge",
                "\"" + Application.ExecutablePath + "\"",
                ".*",
                false,
                ContentComplexityLevel.PlainWithInterpretedFormatting,
                new[] { PropertyNames.WordCount, PropertyNames.CharCount }));

            // Measured: accepting memoQ's connection dialog IS the connection, and
            // Connect() then throws AlreadyConnected. Connect() is for a tool that
            // was registered earlier and is starting up again (memoQ's auto-start).
            if (!(reg?.RequestAccepted ?? false))
            {
                var con = _proxy.Connect(Program.ToolId);
                if (!(con?.RequestAccepted ?? false))
                    throw new InvalidOperationException("Connect refused: " + con?.ErrorCode + " " + con?.ErrorMessage);
            }

            _connected = true;
            Program.Log("memoQ: connected");
            _bridge.PostStatus(true);

            // Whole document, once: every part id memoQ knows, then all content.
            // memoQ answers both through the callbacks below.
            _proxy.RequestPreviewPartIdUpdate();
        }

        public void SelectSegment(string partId, JObject part, int sourceStart = 0, int sourceLength = 0)
        {
            var p = _proxy;
            if (p == null || !_connected || string.IsNullOrEmpty(partId)) return;
            try
            {
                var source = (string)part?["source"] ?? "";
                var target = (string)part?["target"] ?? "";

                // A part is a paragraph. A range picks the sentence — the grid
                // row — within it; no range means the whole paragraph.
                var srcRange = sourceLength > 0 && sourceStart < source.Length
                    ? new FocusedRange(sourceStart, Math.Min(sourceLength, source.Length - sourceStart))
                    : new FocusedRange(0, source.Length);

                var r = p.RequestHighlightChange(new ChangeHighlightRequestFromPreviewTool(
                    partId,
                    (string)part?["sourceLangCode"],
                    (string)part?["targetLangCode"],
                    source, target,
                    srcRange,
                    new FocusedRange(0, target.Length)));
                Program.Log("goto " + partId + " -> accepted=" + r?.RequestAccepted + " " + r?.ErrorMessage);

                // Measured: memoQ moves the cursor but sends no highlight change
                // for a selection it was asked to make. Report it ourselves, or
                // get_active_segment keeps answering with the previous row.
                if (r?.RequestAccepted ?? false)
                    _bridge.PostHighlightFor(partId, part, srcRange.StartIndex, srcRange.Length, target.Length);
            }
            catch (Exception ex)
            {
                Program.Log("goto failed: " + ex.Message);
            }
        }

        // ── callbacks from memoQ ─────────────────────────────────────────

        public void HandleContentUpdateRequest(ContentUpdateRequestFromMQ r)
        {
            var parts = r?.PreviewParts ?? new PreviewPart[0];
            _bridge.PostContent(parts);
        }

        public void HandleChangeHighlightRequest(ChangeHighlightRequestFromMQ r)
        {
            var active = r?.ActivePreviewParts;
            if (active == null || active.Length == 0) { _bridge.PostHighlight(null); return; }
            _bridge.PostHighlight(active[0]);
        }

        public void HandlePreviewPartIdUpdateRequest(PreviewPartIdUpdateRequestFromMQ r)
        {
            var ids = r?.PreviewPartIds ?? new string[0];
            Program.Log("memoQ listed " + ids.Length + " part id(s)");
            _bridge.PostIds(ids);

            // The id list is the cue to pull everything: this is how the tool
            // gets the whole document on start, not only rows touched since.
            var p = _proxy;
            if (p != null && ids.Length > 0)
            {
                try { p.RequestContentUpdate(new ContentUpdateRequestFromPreviewTool(ids, new string[0])); }
                catch (Exception ex) { Program.Log("content pull failed: " + ex.Message); }
            }
        }

        public void HandleDisconnect()
        {
            Program.Log("memoQ: disconnected");
            _connected = false;
            _bridge.PostStatus(false);
        }

        public void Shutdown()
        {
            try { _proxy?.Disconnect(); } catch (Exception) { }
            Dispose();
        }

        private void Dispose()
        {
            try { _proxy?.Dispose(); } catch (Exception) { }
            _proxy = null;
            _connected = false;
        }
    }

    // ── plugin bridge side ─────────────────────────────────────────────────

    internal sealed class BridgeLink
    {
        private static readonly string HandshakePath = Path.Combine(
            SharedRoot(), "memoq", "runtime", "bridge.json");

        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        private string _base, _token;
        private DateTime _lastHandshakeRead = DateTime.MinValue;
        private volatile bool _reachable;

        public bool Reachable => _reachable;

        /// <summary>
        /// The shared Supervertaler root, resolved the way Core does — the
        /// %APPDATA%\Supervertaler\config.json pointer, else ~\Supervertaler.
        /// Duplicated in twenty lines rather than compiling Core in: this exe
        /// needs nothing else from it.
        /// </summary>
        private static string SharedRoot()
        {
            try
            {
                var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Supervertaler", "config.json");
                if (File.Exists(cfg))
                {
                    var m = Regex.Match(File.ReadAllText(cfg), "\"user_data_path\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) return m.Groups[1].Value.Replace("\\\\", "\\");
                }
            }
            catch (Exception) { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Supervertaler");
        }

        /// <summary>Re-reads the handshake when it changes — the bridge's port and token are per memoQ session.</summary>
        private bool Resolve()
        {
            try
            {
                if (!File.Exists(HandshakePath)) { _base = null; return false; }
                var stamp = File.GetLastWriteTimeUtc(HandshakePath);
                if (stamp != _lastHandshakeRead || _base == null)
                {
                    var text = File.ReadAllText(HandshakePath);
                    var port = Regex.Match(text, "\"port\"\\s*:\\s*(\\d+)").Groups[1].Value;
                    var token = Regex.Match(text, "\"token\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                    var pid = Regex.Match(text, "\"pid\"\\s*:\\s*(\\d+)").Groups[1].Value;
                    if (!Alive(pid)) { _base = null; return false; }

                    _base = "http://127.0.0.1:" + port;
                    _token = token;
                    _lastHandshakeRead = stamp;
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                return _base != null;
            }
            catch (Exception) { _base = null; return false; }
        }

        private static bool Alive(string pid)
        {
            try { return int.TryParse(pid, out var id) && !Process.GetProcessById(id).HasExited; }
            catch (Exception) { return false; }
        }

        private bool Post(string path, object body)
        {
            if (!Resolve()) { _reachable = false; return false; }
            try
            {
                var json = JsonConvert.SerializeObject(body);
                var resp = _http.PostAsync(_base + path, new StringContent(json, Encoding.UTF8, "application/json")).Result;
                _reachable = resp.IsSuccessStatusCode || (int)resp.StatusCode == 404;
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _reachable = false;
                Program.Log("bridge " + path + " failed: " + (ex.InnerException ?? ex).Message);
                return false;
            }
        }

        public void PostStatus(bool connected) => Post("/v1/preview/status", new { connected });

        public void PostIds(string[] ids) => Post("/v1/preview/ids", new { partIds = ids });

        public void PostContent(PreviewPart[] parts)
        {
            // In slices: a 3,000-row document on connect is one pull, and one
            // request body of several megabytes helps nobody.
            const int slice = 200;
            for (var i = 0; i < parts.Length; i += slice)
                Post("/v1/preview/content", new { parts = parts.Skip(i).Take(slice).Select(ToBody).ToArray() });
        }

        public void PostHighlight(PreviewPartWithFocusedRange active)
        {
            if (active == null) { Post("/v1/preview/highlight", new { part = (object)null }); return; }
            Post("/v1/preview/highlight", new
            {
                part = ToBody(active),
                sourceStart = active.SourceFocusedRange?.StartIndex ?? 0,
                sourceLength = active.SourceFocusedRange?.Length ?? 0,
                targetStart = active.TargetFocusedRange?.StartIndex ?? 0,
                targetLength = active.TargetFocusedRange?.Length ?? 0
            });
        }

        /// <summary>Report a selection we caused ourselves (see MemoQLink.SelectSegment).</summary>
        public void PostHighlightFor(string partId, JObject part, int sourceStart, int sourceLength, int targetLength)
        {
            if (part == null) part = new JObject { ["partId"] = partId };
            Post("/v1/preview/highlight", new
            {
                part = part,
                sourceStart = sourceStart, sourceLength = sourceLength,
                targetStart = 0, targetLength = targetLength
            });
        }

        private static object ToBody(PreviewPart p)
        {
            int Prop(string name)
            {
                var v = p.PreviewProperties?.FirstOrDefault(x => x.Name == name)?.Value;
                return v == null ? 0 : Convert.ToInt32(v);
            }
            return new
            {
                partId = p.PreviewPartId,
                documentGuid = p.SourceDocument?.DocumentGuid.ToString("D"),
                documentName = p.SourceDocument?.DocumentName,
                importPath = p.SourceDocument?.ImportPath,
                sourceLangCode = p.SourceLangCode,
                targetLangCode = p.TargetLangCode,
                source = p.SourceContent?.Content ?? "",
                target = p.TargetContent?.Content ?? "",
                wordCount = Prop(PropertyNames.WordCount),
                charCount = Prop(PropertyNames.CharCount)
            };
        }

        /// <summary>Long-polls the bridge for commands and executes them against memoQ.</summary>
        public void CommandLoop(MemoQLink memoq)
        {
            while (!Program.Quitting)
            {
                if (!Resolve()) { _reachable = false; Thread.Sleep(3000); continue; }
                try
                {
                    var resp = _http.GetAsync(_base + "/v1/preview/commands?wait=20").Result;
                    _reachable = resp.IsSuccessStatusCode;
                    if (!resp.IsSuccessStatusCode) { Thread.Sleep(3000); continue; }

                    var body = JObject.Parse(resp.Content.ReadAsStringAsync().Result);
                    foreach (var c in body["commands"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
                    {
                        if ((string)c["type"] == "goto")
                            memoq.SelectSegment((string)c["partId"], c["part"] as JObject,
                                (int?)c["sourceStart"] ?? 0, (int?)c["sourceLength"] ?? 0);
                    }
                }
                catch (Exception ex)
                {
                    _reachable = false;
                    Program.Log("command loop: " + (ex.InnerException ?? ex).Message);
                    Thread.Sleep(3000);
                }
            }
        }
    }
}
