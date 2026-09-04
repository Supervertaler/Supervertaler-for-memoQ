using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Localhost HTTP bridge for the Supervertaler MCP server — the same
    /// protocol the Trados plugin speaks, so the same
    /// <c>Supervertaler.McpServer</c> exe drives both: handshake file with port
    /// and bearer token, <c>GET /v1/tools</c> registry, and one endpoint per
    /// tool. The MCP exe is a blind forwarder; everything memoQ-specific lives
    /// in the registry JSON and in these handlers.
    ///
    /// The tool set is the honest subset. memoQ has no project API, no editor
    /// API and no cursor, so there is no go_to_segment and no update_segments —
    /// Claude reads what the plugin has seen (<see cref="CaptureStore"/>,
    /// <see cref="DocumentMemory"/>, the glossary, the prompt library) and
    /// writes through exactly one channel: <see cref="StagedTranslations"/>,
    /// which reaches the grid only when the user runs Pre-translate or lands on
    /// a segment. Claude proposes; the translator's own action disposes.
    ///
    /// Discovery: the handshake goes to &lt;shared root&gt;\memoq\runtime\bridge.json.
    /// The MCP exe is pointed at it with SUPERVERTALER_BRIDGE_FILE, so a
    /// Claude Desktop config can run one server entry for Trados and one for
    /// memoQ side by side without either knowing about the other.
    /// </summary>
    internal sealed class MemoQBridge : IDisposable
    {
        private static MemoQBridge _instance;
        private static readonly object _instanceLock = new object();

        private HttpListener _listener;
        private Thread _thread;
        private string _token;
        private int _port;
        private volatile bool _stopping;

        /// <summary>The engine context most recently created. Latest wins: it is the project the user is working in.</summary>
        private static volatile EngineContext _context;

        public static string HandshakePath => Path.Combine(
            global::Supervertaler.Core.SupervertalerPaths.Root, "memoq", "runtime", "bridge.json");

        /// <summary>
        /// Starts the bridge once per process and points it at the newest engine.
        /// Called from every engine construction: cheap after the first call, and
        /// re-aiming at the latest engine is exactly right — memoQ rebuilds the
        /// engine when settings or project change.
        /// </summary>
        /// <summary>
        /// Points the bridge at the context that is actually doing work. Called
        /// when a session is created: memoQ only asks for a session when it has
        /// real traffic, so a discarded engine can never claim the bridge.
        /// </summary>
        public static void Aim(EngineContext context)
        {
            if (context == null) return;
            _context = context;

            lock (_instanceLock)
            {
                // Self-heal the handshake while we are here. It can go missing or
                // stale under us: a test harness that started its own bridge
                // writes over it with a PID that then exits, leaving memoQ
                // listening on a port nothing can find.
                _instance?.WriteHandshakeIfOurs();
            }
        }

        public static void EnsureStarted(EngineContext context)
        {
            // Only as a fallback. Aiming happens in Aim, from the session, because
            // memoQ builds throwaway engines: saving the MT settings dialog calls
            // CreateEngine("eng","ger") purely to read MaxDegreeOfParallelism off
            // it. Aiming from the constructor pointed the bridge at that phantom
            // pair every time the dialog was saved, which is what produced staged
            // translations under eng-ger for a Dutch project.
            if (_context == null) _context = context;

            lock (_instanceLock)
            {
                if (_instance != null)
                {
                    // Self-heal. The handshake can go missing or stale under us —
                    // a test harness that started its own bridge wrote over it
                    // with a PID that then exited, and memoQ was left listening
                    // on a port nothing could find. Each engine creation is a
                    // cheap moment to put it right.
                    _instance.WriteHandshakeIfOurs();
                    return;
                }

                try
                {
                    var bridge = new MemoQBridge();
                    bridge.Start();
                    if (bridge._listener != null) _instance = bridge;
                }
                catch (Exception ex)
                {
                    // The bridge is a bonus feature; translation must never
                    // depend on it starting.
                    PluginLog.Write("MCP bridge failed to start", ex);
                }
            }
        }

        private void Start()
        {
            _token = Guid.NewGuid().ToString("N");

            // HttpListener has no "port 0, OS picks": try random high ports.
            var rng = new Random();
            for (var attempt = 0; attempt < 16 && _listener == null; attempt++)
            {
                var candidate = rng.Next(49152, 65535);
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add("http://127.0.0.1:" + candidate + "/");
                    listener.Start();
                    _listener = listener;
                    _port = candidate;
                }
                catch (Exception)
                {
                    // Port taken; try another.
                }
            }

            if (_listener == null)
            {
                PluginLog.Write("MCP bridge: no free port after 16 attempts – bridge disabled this session");
                return;
            }

            _thread = new Thread(ListenLoop) { IsBackground = true, Name = "SupervertalerMemoQBridge" };
            _thread.Start();

            WriteHandshakeIfOurs();
            PluginLog.Write($"MCP bridge listening on 127.0.0.1:{_port}, handshake at {HandshakePath}");
        }

        /// <summary>
        /// Rewrites the handshake unless a different, still-running process owns
        /// it. One handshake path, many possible writers — a second memoQ, a
        /// test harness loading the DLL — and the first version let the last
        /// writer win, which meant a harness exiting left the live memoQ
        /// unreachable. The live owner keeps the file; everyone else logs and
        /// stands down.
        /// </summary>
        private void WriteHandshakeIfOurs()
        {
            try
            {
                var owner = HandshakeOwnerPid();
                var me = Process.GetCurrentProcess().Id;

                if (owner.HasValue && owner.Value != me && IsAlive(owner.Value))
                {
                    PluginLog.Write($"MCP bridge: handshake owned by live process {owner.Value}; not overwriting");
                    return;
                }

                WriteHandshake();
            }
            catch (Exception ex)
            {
                PluginLog.Write("MCP bridge: handshake check failed", ex);
            }
        }

        private static int? HandshakeOwnerPid()
        {
            if (!File.Exists(HandshakePath)) return null;
            var text = File.ReadAllText(HandshakePath);
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"pid\"\\s*:\\s*(\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var pid) ? pid : (int?)null;
        }

        private static bool IsAlive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch (Exception) { return false; }
        }

        private void WriteHandshake()
        {
            var dir = Path.GetDirectoryName(HandshakePath);
            Directory.CreateDirectory(dir);

            // Same field names as the Trados handshake, because the same
            // BridgeClient reads it: version/port/token/pid/startedAt, plus
            // processName for the PID-reuse liveness check.
            var payload = "{"
                + "\"version\":1,"
                + $"\"port\":{_port},"
                + $"\"token\":\"{_token}\","
                + $"\"pid\":{Process.GetCurrentProcess().Id},"
                + $"\"startedAt\":\"{DateTime.UtcNow:o}\","
                + $"\"processName\":\"{Process.GetCurrentProcess().ProcessName}\","
                + "\"studioVersion\":\"memoQ\""
                + "}";

            File.WriteAllText(HandshakePath, payload, new UTF8Encoding(false));
        }

        private void ListenLoop()
        {
            while (!_stopping)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    // Stop() closed the listener, or it died — either way the
                    // loop is over.
                    return;
                }

                ThreadPool.QueueUserWorkItem(state =>
                {
                    var ctx = (HttpListenerContext)state;
                    try
                    {
                        Handle(ctx);
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Write("MCP bridge request failed", ex);
                        TryWrite(ctx, 500, Json(new ErrorBody { Error = ex.Message }));
                    }
                }, context);
            }
        }

        // ── dispatch ─────────────────────────────────────────────────────

        private void Handle(HttpListenerContext ctx)
        {
            var request = ctx.Request;

            var auth = request.Headers["Authorization"] ?? "";
            if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)
                || !string.Equals(auth.Substring(7).Trim(), _token, StringComparison.Ordinal))
            {
                TryWrite(ctx, 401, Json(new ErrorBody { Error = "invalid token" }));
                return;
            }

            var path = request.Url.AbsolutePath.TrimEnd('/');
            var method = request.HttpMethod.ToUpperInvariant();

            switch (method + " " + path)
            {
                case "GET /v1/tools": HandleTools(ctx); return;
                case "GET /v1/help": HandleHelp(ctx); return;
                case "GET /v1/project": HandleProject(ctx); return;
                case "GET /v1/segments": HandleSegments(ctx); return;
                case "GET /v1/confirmed": HandleConfirmed(ctx); return;
                case "GET /v1/terms": HandleTermLookup(ctx); return;
                case "POST /v1/terms": HandleTermAdd(ctx); return;
                case "POST /v1/stage": HandleStage(ctx); return;
                case "GET /v1/staged": HandleStagedList(ctx); return;
                case "POST /v1/staged/clear": HandleStagedClear(ctx); return;
                case "GET /v1/prompts": HandlePromptList(ctx); return;
                case "GET /v1/prompt": HandlePromptGet(ctx); return;
                case "POST /v1/prompt": HandlePromptSave(ctx); return;
                case "POST /v1/autoprompt/classify": HandleClassify(ctx); return;
                case "POST /v1/autoprompt/preview": HandleAutoPromptPreview(ctx); return;
                case "POST /v1/autoprompt": HandleAutoPrompt(ctx); return;

                // From the preview tool (memoQ → tool → here).
                case "POST /v1/preview/content": HandlePreviewContent(ctx); return;
                case "POST /v1/preview/ids": HandlePreviewIds(ctx); return;
                case "POST /v1/preview/highlight": HandlePreviewHighlight(ctx); return;
                case "POST /v1/preview/status": HandlePreviewStatus(ctx); return;
                case "GET /v1/preview/commands": HandlePreviewCommands(ctx); return;

                // From the MCP client, answered from the preview view.
                case "GET /v1/active": HandleActiveSegment(ctx); return;
                case "POST /v1/goto": HandleGoTo(ctx); return;
                case "GET /v1/qa-check": HandleQaCheck(ctx); return;
                case "GET /v1/supermemory-banks": HandleSuperMemoryBanks(ctx); return;
                case "GET /v1/supermemory-context": HandleSuperMemoryContext(ctx); return;
                case "GET /v1/supermemory-search": HandleSuperMemorySearch(ctx); return;
                case "GET /v1/inconsistencies": HandleInconsistencies(ctx); return;
                case "POST /v1/glossary/activate": HandleGlossaryActivate(ctx); return;
                default:
                    TryWrite(ctx, 404, Json(new ErrorBody { Error = "unknown endpoint " + method + " " + path }));
                    return;
            }
        }

        // ── endpoints ────────────────────────────────────────────────────

        private void HandleTools(HttpListenerContext ctx)
        {
            using (var stream = typeof(MemoQBridge).Assembly
                       .GetManifestResourceStream("Supervertaler.MemoQ.Resources.mcp-tools.json"))
            {
                if (stream == null)
                {
                    TryWrite(ctx, 500, Json(new ErrorBody { Error = "tool registry resource missing" }));
                    return;
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    TryWrite(ctx, 200, reader.ReadToEnd());
            }
        }

        private void HandleHelp(HttpListenerContext ctx)
        {
            TryWrite(ctx, 200, Json(new HelpBody { Markdown = HelpCard }));
        }

        private void HandleProject(HttpListenerContext ctx)
        {
            var docs = CaptureStore.Snapshot();

            // Languages of the document actually being worked on (the most
            // recently active bucket), falling back to the latest engine only
            // when nothing has been seen yet. See HandleStage for why the
            // latest engine is not a safe answer on its own.
            var active = docs.FirstOrDefault();
            var srcCode = active?.SourceLangCode ?? _context?.SourceLangCode;
            var trgCode = active?.TargetLangCode ?? _context?.TargetLangCode;

            var body = new ProjectBody
            {
                SourceLanguage = srcCode == null ? null : PromptBuilder.DescribeLanguage(srcCode),
                TargetLanguage = trgCode == null ? null : PromptBuilder.DescribeLanguage(trgCode),
                LangPair = srcCode == null ? null : srcCode + "-" + (trgCode ?? "?"),
                Documents = docs.Select(d => new ProjectDocumentBody
                {
                    Key = d.Key,
                    ProjectName = DocumentNames.Resolve(d.DocumentId)?.Project,
                    DocumentName = DocumentNames.Resolve(d.DocumentId)?.Document,
                    Origin = d.ViaTerminology
                        ? "rows the cursor has visited (via terminology lookups; document identity unknown, so this is one bucket per language pair)"
                        : "translation requests (Pre-translate or MT lookup with Supervertaler as provider)",
                    Client = d.Client,
                    Domain = d.Domain,
                    Subject = d.Subject,
                    CapturedSegments = d.Sources.Count,
                    ConfirmedPairs = DocumentMemory.CountFor(d.Key),
                    LastSeenUtc = d.LastSeenUtc.ToString("o")
                }).ToArray(),
                StagedTranslations = StagedTranslations.Snapshot(null).Count,
                PreviewToolConnected = PreviewStore.ToolAlive,
                // The glossary all three consumers (terminology pane, prompts, QA) read.
                ActiveGlossary = string.IsNullOrWhiteSpace(SharedSettings.GlossaryPath) ? null : SharedSettings.GlossaryPath,
                LiveDocuments = PreviewStore.ToolAlive
                    ? PreviewStore.Documents().Select(d => new LiveDocumentBody
                    {
                        DocumentGuid = d.DocumentGuid.ToString("D"),
                        DocumentName = d.DocumentName,
                        LangPair = (d.SourceLangCode ?? "?") + "-" + (d.TargetLangCode ?? "?"),
                        Rows = PreviewStore.Count(d.DocumentGuid)
                    }).ToArray()
                    : null,
                Note = docs.Count == 0
                    ? "No segments captured yet. The plugin only sees what memoQ sends it: "
                      + "ask the user to run Pre-translate once (any model) or visit some segments, "
                      + "then this project view fills in."
                    : null
            };

            TryWrite(ctx, 200, Json(body));
        }

        private void HandleSegments(HttpListenerContext ctx)
        {
            var key = ctx.Request.QueryString["document"];
            var offset = ParseInt(ctx.Request.QueryString["offset"], 0);
            var limit = Math.Min(500, ParseInt(ctx.Request.QueryString["limit"], 200));

            // The preview view wins when it exists: it has target text, real
            // row order and the document's name, none of which the MT capture
            // has. The capture store remains the answer when the preview tool
            // is not running.
            if (PreviewStore.ToolAlive)
            {
                var previewDoc = PreviewStore.Documents().FirstOrDefault(d =>
                    string.IsNullOrEmpty(key)
                    || d.DocumentGuid.ToString("D") == key
                    || (key.StartsWith(d.DocumentGuid.ToString("N"), StringComparison.OrdinalIgnoreCase))
                    || string.Equals(d.DocumentName, key, StringComparison.OrdinalIgnoreCase));

                if (previewDoc != null)
                {
                    var rows = PreviewStore.Rows(previewDoc.DocumentGuid);
                    var livePair = (previewDoc.SourceLangCode ?? "?") + "-" + (previewDoc.TargetLangCode ?? "?");
                    var active = PreviewStore.GetActive()?.PartId;

                    TryWrite(ctx, 200, Json(new SegmentsBody
                    {
                        DocumentKey = previewDoc.DocumentGuid.ToString("D"),
                        DocumentName = previewDoc.DocumentName,
                        Total = rows.Count,
                        Source = "live view from memoQ (preview tool). Units are PARAGRAPHS as memoQ's Preview SDK delivers them: "
                               + "a paragraph of three sentences that memoQ shows as three grid rows is ONE item here, with the whole "
                               + "paragraph's source and target. Order and targets are real; use sourceStart/sourceLength with "
                               + "go_to_segment to land on one sentence of a paragraph.",
                        Segments = rows.Skip(offset).Take(limit).Select((r, i) => new SegmentBody
                        {
                            Index = offset + i + 1,
                            PartId = r.PartId,
                            Source = r.Source,
                            Target = string.IsNullOrEmpty(r.Target) ? null : r.Target,
                            Staged = StagedTranslations.TryGetPeek(r.Source, livePair)?.Target,
                            IsActive = r.PartId == active ? true : (bool?)null
                        }).ToArray()
                    }));
                    return;
                }
            }

            var doc = CaptureStore.Get(key);
            if (doc == null)
            {
                TryWrite(ctx, 200, Json(new SegmentsBody
                {
                    Segments = new SegmentBody[0],
                    Total = 0,
                    Note = "Nothing captured yet – run Pre-translate once, or visit segments in the editor."
                }));
                return;
            }

            var pair = doc.SourceLangCode + "-" + doc.TargetLangCode;
            var slice = doc.Sources.Skip(offset).Take(limit).Select((source, i) =>
            {
                var staged = StagedTranslations.TryGetPeek(source, pair);
                return new SegmentBody
                {
                    Index = offset + i + 1,
                    Source = source,
                    Staged = staged?.Target,
                };
            }).ToArray();

            TryWrite(ctx, 200, Json(new SegmentsBody
            {
                DocumentKey = doc.Key,
                Total = doc.Sources.Count,
                Segments = slice
            }));
        }

        private void HandleConfirmed(HttpListenerContext ctx)
        {
            var key = ctx.Request.QueryString["document"];
            var limit = Math.Min(500, ParseInt(ctx.Request.QueryString["limit"], 100));

            var doc = CaptureStore.Get(key);
            var pairs = doc == null
                ? new List<DocumentMemory.Pair>()
                : DocumentMemory.GetAll(doc.Key, limit);

            TryWrite(ctx, 200, Json(new ConfirmedBody
            {
                DocumentKey = doc?.Key,
                Pairs = pairs.Select(p => new PairBody { Source = p.Source, Target = p.Target }).ToArray()
            }));
        }

        private void HandleTermLookup(HttpListenerContext ctx)
        {
            var text = ctx.Request.QueryString["text"] ?? "";
            var matches = TermIndex.Find(SharedSettings.GlossaryPath, text);

            TryWrite(ctx, 200, Json(new TermsBody
            {
                Terms = (matches ?? (IReadOnlyList<TermIndex.Match>)new TermIndex.Match[0])
                    .Select(m => new TermBody
                    {
                        Source = m.Entry.Source,
                        Target = m.Entry.Target,
                        Forbidden = m.Entry.Forbidden
                    }).ToArray()
            }));
        }

        private void HandleTermAdd(HttpListenerContext ctx)
        {
            var req = Read<AddTermRequest>(ctx);
            if (req == null || string.IsNullOrWhiteSpace(req.Source) || string.IsNullOrWhiteSpace(req.Target))
            {
                TryWrite(ctx, 400, Json(new ErrorBody { Error = "source and target are required" }));
                return;
            }

            var path = SharedSettings.GlossaryPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                TryWrite(ctx, 400, Json(new ErrorBody
                {
                    Error = "No glossary is configured. The user sets one in memoQ under "
                          + "Resources > Settings > MT > Supervertaler (glossary in the terminology plugin settings)."
                }));
                return;
            }

            // The TB plugin's own format: source TAB target [TAB forbidden].
            var line = req.Source.Trim() + "\t" + req.Target.Trim() + (req.Forbidden ? "\tforbidden" : "");
            File.AppendAllText(path, Environment.NewLine + line, new UTF8Encoding(false));

            TryWrite(ctx, 200, Json(new OkBody
            {
                Ok = true,
                Message = "Term added. memoQ's terminology pane and the translation prompts pick it up "
                        + "within a few seconds (the index reloads when the file changes)."
            }));
        }

        private void HandleStage(HttpListenerContext ctx)
        {
            var req = Read<StageRequest>(ctx);
            if (req?.Pairs == null || req.Pairs.Length == 0)
            {
                TryWrite(ctx, 400, Json(new ErrorBody { Error = "pairs is required and must be non-empty" }));
                return;
            }

            // The pair comes from the document being worked on, not from the
            // most recently created engine. memoQ builds one engine per target
            // language in the project, in an order of its own choosing, so the
            // "latest" engine can be the German one while every lookup is Dutch
            // — and translations staged under the wrong pair never match.
            // The most recently active capture bucket is the document the user
            // actually has in front of them.
            var active = CaptureStore.Get(null);
            var pair = active != null
                ? (active.SourceLangCode ?? "?") + "-" + (active.TargetLangCode ?? "?")
                : LangPair(_context);

            if (pair == null)
            {
                TryWrite(ctx, 409, Json(new ErrorBody
                {
                    Error = "No segments have been seen yet – the language pair is unknown. "
                          + "Ask the user to open their project and touch one segment (or run Pre-translate) first."
                }));
                return;
            }

            var accepted = StagedTranslations.Stage(
                req.Pairs.Select(p => new KeyValuePair<string, string>(p.Source, p.Target)),
                pair,
                req.Label ?? "Claude");

            TryWrite(ctx, 200, Json(new OkBody
            {
                Ok = true,
                Message = accepted + " translation(s) staged. They reach the grid when the user runs "
                        + "Pre-translate or lands on the matching segments – matched by source text. "
                        + "Nothing is written into memoQ until then."
            }));
        }

        private void HandleStagedList(HttpListenerContext ctx)
        {
            var entries = StagedTranslations.Snapshot(null);
            TryWrite(ctx, 200, Json(new StagedListBody
            {
                Staged = entries.Select(e => new StagedEntryBody
                {
                    Source = e.Source,
                    Target = e.Target,
                    Label = e.Label,
                    TimesServed = e.TimesServed,
                    StagedUtc = e.StagedUtc.ToString("o")
                }).ToArray()
            }));
        }

        private void HandleStagedClear(HttpListenerContext ctx)
        {
            var n = StagedTranslations.Clear();
            TryWrite(ctx, 200, Json(new OkBody { Ok = true, Message = n + " staged translation(s) cleared." }));
        }

        private void HandlePromptList(HttpListenerContext ctx)
        {
            var library = new global::Supervertaler.Core.PromptLibrary();
            var prompts = library.GetAllPrompts()
                .Where(p => p != null && !p.IsQuickLauncher)
                .Select(p => new PromptInfoBody
                {
                    Name = p.Name,
                    RelativePath = p.RelativePath,
                    Category = p.Category,
                    Description = p.Description,
                    App = p.App
                }).ToArray();

            TryWrite(ctx, 200, Json(new PromptListBody { Prompts = prompts }));
        }

        private void HandlePromptGet(HttpListenerContext ctx)
        {
            var rel = ctx.Request.QueryString["path"] ?? "";
            var library = new global::Supervertaler.Core.PromptLibrary();
            var prompt = library.GetPromptByRelativePath(rel);

            if (prompt == null)
            {
                TryWrite(ctx, 404, Json(new ErrorBody { Error = "no prompt at " + rel }));
                return;
            }

            TryWrite(ctx, 200, Json(new PromptBody
            {
                Name = prompt.Name,
                RelativePath = prompt.RelativePath,
                Category = prompt.Category,
                Description = prompt.Description,
                Content = prompt.Content
            }));
        }

        private void HandlePromptSave(HttpListenerContext ctx)
        {
            var req = Read<SavePromptRequest>(ctx);
            if (req == null || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Content))
            {
                TryWrite(ctx, 400, Json(new ErrorBody { Error = "name and content are required" }));
                return;
            }

            var library = new global::Supervertaler.Core.PromptLibrary();

            // Existing prompt of that name in that category updates in place;
            // otherwise this creates a new file. Same semantics as the editor.
            var category = string.IsNullOrWhiteSpace(req.Category) ? "Translate" : req.Category.Trim();
            var existing = library.GetAllPrompts().FirstOrDefault(p =>
                string.Equals(p.Name, req.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Category ?? "", category, StringComparison.OrdinalIgnoreCase));

            var prompt = existing ?? new global::Supervertaler.Core.Models.PromptTemplate
            {
                Name = req.Name.Trim(),
                Category = category
            };

            if (prompt.IsReadOnly)
            {
                TryWrite(ctx, 400, Json(new ErrorBody { Error = "that prompt is read-only" }));
                return;
            }

            prompt.Content = req.Content;
            if (!string.IsNullOrWhiteSpace(req.Description)) prompt.Description = req.Description.Trim();

            // Stamped with the project's languages rather than asked for, because
            // the bridge knows them and the caller would have to be told. Only on
            // a new prompt: re-saving an existing one must not silently relabel a
            // prompt written for another pair.
            if (existing == null && _context != null)
            {
                prompt.SourceLang = _context.SourceLangCode ?? string.Empty;
                prompt.TargetLang = _context.TargetLangCode ?? string.Empty;
            }

            library.SavePrompt(prompt);
            PromptResolver.Invalidate();

            TryWrite(ctx, 200, Json(new OkBody
            {
                Ok = true,
                Message = "Saved to the shared prompt library as \"" + prompt.Name + "\" (" + prompt.RelativePath + "). "
                        + "The user selects it in memoQ under Resources > Settings > MT > Supervertaler > Prompt."
            }));
        }

        /// <summary>
        /// AutoPrompt: draft a project-specific translation prompt from what
        /// the plugin has seen, using the plugin's own provider, model and key.
        ///
        /// Lives in the bridge rather than in the prompt editor because this is
        /// where the inputs are — captured segments, confirmed pairs, the
        /// glossary — and where the API key is (memoQ stores it encrypted; an
        /// external process cannot read it). The editor is a thin caller.
        ///
        /// Deliberately NOT in the MCP tool registry: when Claude is the client,
        /// Claude is the model, and it drafts prompts with save_prompt directly.
        /// This endpoint is for the button.
        /// </summary>
        private void HandleAutoPrompt(HttpListenerContext ctx)
        {
            var req = Read<AutoPromptRequest>(ctx) ?? new AutoPromptRequest();
            var context = _context;
            var general = context?.General;
            var apiKey = context?.ApiKey;

            if (general == null || string.IsNullOrWhiteSpace(apiKey))
            {
                TryWrite(ctx, 409, Json(new ErrorBody
                {
                    Error = "No API key is configured in memoQ's Supervertaler settings. "
                          + "AutoPrompt uses the plugin's provider and key; set them under "
                          + "Resource console > MT settings > Supervertaler > Configure plugin."
                }));
                return;
            }

            var doc = ResolveAutoPromptSource(req.Document);
            if (doc.Sources.Count == 0)
            {
                TryWrite(ctx, 409, Json(new ErrorBody
                {
                    Error = "No document text is available yet. Open the document in memoQ so the preview "
                          + "tool can read it, or run Pre-translate once (with the Pre-translate-only box "
                          + "ticked it costs nothing), then try again."
                }));
                return;
            }

            try
            {
                var plan = PlanAutoPrompt(req, doc, general, apiKey, mayClassify: true);

                var provider = SessionRunner.MapProviderForCore(general.Provider);
                var endpoint = string.IsNullOrWhiteSpace(general.Endpoint) ? null : general.Endpoint.Trim();

                // Phase 2: the generation itself.
                string raw;
                using (var client = new global::Supervertaler.Core.LlmClient(provider, general.Model, apiKey, endpoint))
                {
                    raw = client.SendPromptAsync(
                        global::Supervertaler.Core.PromptGenerator.BuildMetaPrompt(plan.Context),
                        maxTokens: 32768).GetAwaiter().GetResult();
                }

                var content = global::Supervertaler.Core.PromptGenerator.ParseGeneratedPrompt(raw) ?? raw;

                // The memoQ project's own name, which is what its title bar shows
                // and what the translator calls the job. Used as-is: the pair is
                // already in the frontmatter, the chooser shows it, and appending
                // it produced "Example project (patent, en-nl) eng-dut" - the same
                // fact twice, in two vocabularies.
                //
                // The language codes are a fallback for having nothing better to
                // say, not a component of a good name.
                var nameBits = new List<string>();
                if (!string.IsNullOrWhiteSpace(doc.ProjectName))
                {
                    nameBits.Add(doc.ProjectName.Trim());
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(doc.Client)) nameBits.Add(doc.Client.Trim());
                    else if (!string.IsNullOrWhiteSpace(plan.Domain)) nameBits.Add(plan.Domain);

                    nameBits.Add((doc.SourceLangCode ?? "?") + "-" + (doc.TargetLangCode ?? "?"));
                }

                PluginLog.Write($"AutoPrompt: drafted {content.Length} chars for {doc.Key} "
                    + $"(domain {plan.Domain}, {plan.TermCount} terms, {plan.PairCount} confirmed pairs)");

                TryWrite(ctx, 200, Json(new AutoPromptResponse
                {
                    Content = content,
                    SuggestedName = string.Join(" ", nameBits),
                    Domain = plan.Domain,
                    Summary = plan.Summary,
                    Description = "Generated by AutoPrompt from the memoQ project"
                        + (plan.TermCount == 0 ? " – glossary derived from the document (no glossary hits)" : ""),
                    TermCount = plan.TermCount,
                    ConfirmedPairCount = plan.PairCount,
                    SourceLang = context.SourceLangCode,
                    TargetLang = context.TargetLangCode
                }));
            }
            catch (Exception ex)
            {
                PluginLog.Write("AutoPrompt failed", ex);
                TryWrite(ctx, 500, Json(new ErrorBody { Error = "AutoPrompt failed: " + ex.Message }));
            }
        }

        /// <summary>
        /// Everything AutoPrompt assembles before it says a word to the model: the
        /// source text, the glossary hits, the confirmed pairs, the project
        /// metadata and the host constraints.
        ///
        /// It exists so that the preview and the generation cannot disagree. A
        /// preview that rebuilt the context separately would be a second
        /// implementation of the same assembly, and the first time the two drifted
        /// the preview would start lying about what was sent – which is worse
        /// than having no preview at all.
        /// </summary>
        private sealed class AutoPromptPlan
        {
            public global::Supervertaler.Core.PromptGenerationContext Context;
            public string Domain;
            public string Summary;
            public int TermCount;
            public int PairCount;
        }

        /// <param name="mayClassify">
        /// False for the preview, which must cost nothing.
        /// </param>
        private AutoPromptPlan PlanAutoPrompt(
            AutoPromptRequest req, AutoPromptSource doc,
            Settings.SupervertalerGeneralSettings general, string apiKey, bool mayClassify)
        {
            var sources = doc.Sources;
            var sourceLang = PromptBuilder.DescribeLanguage(doc.SourceLangCode);
            var targetLang = PromptBuilder.DescribeLanguage(doc.TargetLangCode);

            var analysis = global::Supervertaler.Core.DocumentAnalyzer.Analyze(doc.Plain);

            // Glossary hits across the whole document, deduplicated.
            var terms = new List<global::Supervertaler.Core.Models.TermEntry>();
            if (req.IncludeTerms)
            {
                var matches = TermIndex.Find(SharedSettings.GlossaryPath, string.Join("\n", doc.Plain))
                              ?? (IReadOnlyList<TermIndex.Match>)new TermIndex.Match[0];
                terms = matches
                    .GroupBy(m => m.Entry.Source + "\t" + m.Entry.Target, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First().Entry)
                    .Select(e => new global::Supervertaler.Core.Models.TermEntry
                    {
                        SourceTerm = e.Source,
                        TargetTerm = e.Target,
                        Forbidden = e.Forbidden,
                        SourceLang = sourceLang,
                        TargetLang = targetLang,
                        TermbaseName = "Supervertaler glossary"
                    })
                    .ToList();
            }

            // The translator's confirmed pairs are the TM here: not fuzzy
            // matches from an archive, but this document's own approved
            // renderings.
            var pairs = req.IncludeConfirmed
                ? DocumentMemory.GetAll(doc.Key, 60).Select(p => new global::Supervertaler.Core.Models.TmMatch
                {
                    SourceText = p.Source,
                    TargetText = p.Target,
                    MatchPercentage = 100,
                    TmName = "Confirmed in memoQ"
                }).ToList()
                : new List<global::Supervertaler.Core.Models.TmMatch>();

            var provider = SessionRunner.MapProviderForCore(general.Provider);
            var endpoint = string.IsNullOrWhiteSpace(general.Endpoint) ? null : general.Endpoint.Trim();

            // Phase 1: domain and a one-line description. Normally the editor
            // has already run /v1/autoprompt/classify and the user confirmed
            // or changed the domain, which arrives in the request; only a
            // caller that skipped that step pays for the classification here.
            string detectedDomain, description;
            if (!string.IsNullOrWhiteSpace(req.Domain))
            {
                detectedDomain = req.Domain.Trim();
                description = req.Description ?? "";
            }
            else if (mayClassify)
            {
                Classify(doc.Plain, analysis.PrimaryDomain, provider, general.Model, apiKey, endpoint,
                    out detectedDomain, out description);
            }
            else
            {
                // The preview costs nothing, so it settles for the keyword guess.
                // In practice the editor has classified already and sends the
                // domain the user confirmed.
                detectedDomain = analysis.PrimaryDomain;
                description = "";
            }

            var summary = string.IsNullOrEmpty(description)
                ? $"{analysis.SegmentCount:N0} segments | {analysis.WordCount:N0} words"
                : $"Context: {description} | {analysis.SegmentCount:N0} segments | {analysis.WordCount:N0} words";

            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(doc.Client)) meta.Add("Client: " + doc.Client);
            if (!string.IsNullOrWhiteSpace(doc.Domain)) meta.Add("memoQ domain: " + doc.Domain);
            if (!string.IsNullOrWhiteSpace(doc.Subject)) meta.Add("memoQ subject: " + doc.Subject);
            var hint = string.Join("\n", new[] { string.Join(" | ", meta), req.Hint ?? "" }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            return new AutoPromptPlan
            {
                Domain = detectedDomain,
                Summary = summary,
                TermCount = terms.Count,
                PairCount = pairs.Count,
                Context = new global::Supervertaler.Core.PromptGenerationContext
                {
                    SourceLang = sourceLang,
                    TargetLang = targetLang,
                    DetectedDomain = detectedDomain,
                    AnalysisSummary = summary,
                    SegmentCount = sources.Count,
                    SourceSegments = sources,
                    TermbaseTerms = terms,
                    TotalTermCount = terms.Count,
                    TmPairs = pairs,
                    UserContextHint = hint,
                    HostConstraints = MemoQHostConstraints,

                    // The selected memory bank, whole. A drafted prompt is the
                    // one place a client's standing decisions can be written in
                    // once and then apply to every request of the job, which is
                    // both cheaper and more reliable than re-sending the bank
                    // with each batch - so this is where the bank is worth the
                    // most, and where it is least worth trimming.
                    KbContext = _context?.KbContextForAutoPrompt()
                }
            };

        }

        /// <summary>
        /// POST /v1/autoprompt/preview – the meta-prompt itself, verbatim,
        /// without sending it anywhere.
        ///
        /// AutoPrompt gathers its context from four places the translator cannot
        /// see at once: the live document, the glossary, their own confirmed
        /// segments, and memoQ's project metadata. Approving a call that takes
        /// minutes and costs money on faith is a poor deal when the briefing box
        /// is right there and one look at the assembled context is what tells you
        /// whether it needs filling in.
        /// </summary>
        private void HandleAutoPromptPreview(HttpListenerContext ctx)
        {
            var req = Read<AutoPromptRequest>(ctx) ?? new AutoPromptRequest();
            var context = _context;
            var general = context?.General;

            if (general == null)
            {
                TryWrite(ctx, 409, Json(new ErrorBody
                {
                    Error = "memoQ has not built a Supervertaler engine yet. Open a document first."
                }));
                return;
            }

            var doc = ResolveAutoPromptSource(req.Document);
            if (doc.Sources.Count == 0)
            {
                TryWrite(ctx, 409, Json(new ErrorBody
                {
                    Error = "No document text is available yet. Open the document in memoQ so the preview "
                          + "tool can read it, or run Pre-translate once, then try again."
                }));
                return;
            }

            try
            {
                var plan = PlanAutoPrompt(req, doc, general, context.ApiKey, mayClassify: false);

                TryWrite(ctx, 200, Json(new AutoPromptPreviewResponse
                {
                    MetaPrompt = global::Supervertaler.Core.PromptGenerator.BuildMetaPrompt(plan.Context),
                    Origin = doc.Origin,
                    DocumentName = doc.DocumentName,
                    SegmentCount = doc.Sources.Count,
                    TermCount = plan.TermCount,
                    ConfirmedPairCount = plan.PairCount,
                    Provider = general.Provider,
                    Model = general.Model
                }));
            }
            catch (Exception ex)
            {
                PluginLog.Write("AutoPrompt preview failed", ex);
                TryWrite(ctx, 500, Json(new ErrorBody { Error = "Could not assemble the context: " + ex.Message }));
            }
        }

        /// <summary>
        /// The text AutoPrompt should work from, and the document it belongs to.
        ///
        /// Two sources exist and they are very unequal. The capture store fills as
        /// segments pass through translation, so on a project the translator has
        /// merely opened it holds whatever rows they happened to visit — one, in
        /// the case that prompted this. The preview tool has had the entire
        /// document since memoQ opened it. Classifying and drafting from one
        /// sentence produced a "general" domain for a patent, which is what a
        /// reader of the resulting prompt would have had to live with.
        ///
        /// So: prefer the live document when it is richer, and keep the capture
        /// store's metadata either way, because client, domain and subject arrive
        /// on translation requests and the preview channel carries none of them.
        /// </summary>
        private sealed class AutoPromptSource
        {
            /// <summary>
            /// The segments as they arrive, tag markers and all. This is what the
            /// drafting call sees, because a document's tag behaviour is one of the
            /// things a project prompt most needs to pin down and it is invisible
            /// once the markers are gone. The prompt AutoPrompt wrote for the
            /// Trados side of the same job devotes a whole section to this document
            /// mixing tagged digits with Unicode subscripts; the memoQ side could
            /// not, because the evidence was stripped before drafting.
            /// </summary>
            public List<string> Sources = new List<string>();

            /// <summary>
            /// The same segments with the markers removed, for the passes that want
            /// prose: keyword analysis, domain classification and glossary
            /// matching. A tag marker is not a word, and letting one act like one
            /// skews all three.
            /// </summary>
            public List<string> Plain = new List<string>();
            public string SourceLangCode;
            public string TargetLangCode;
            public string Client;
            public string Domain;
            public string Subject;
            public string DocumentName;

            /// <summary>
            /// The memoQ project's own name, which is what the title bar shows and
            /// therefore what the translator calls this job. Not in the MT
            /// metadata - only a document GUID is - so it comes from resolving
            /// that GUID against memoQ's folders.
            /// </summary>
            public string ProjectName;

            public string Origin;

            /// <summary>The capture key, which is what DocumentMemory is filed under.</summary>
            public string Key;
        }

        private AutoPromptSource ResolveAutoPromptSource(string documentKey)
        {
            var captured = CaptureStore.Get(documentKey);
            var context = _context;

            var result = new AutoPromptSource
            {
                SourceLangCode = captured?.SourceLangCode ?? context?.SourceLangCode,
                TargetLangCode = captured?.TargetLangCode ?? context?.TargetLangCode,
                Client = captured?.Client,
                Domain = captured?.Domain,
                Subject = captured?.Subject,
                Key = captured?.Key ?? documentKey,
                ProjectName = ProjectNameOf(captured?.DocumentId)
            };

            if (captured != null)
            {
                result.Sources = captured.Sources.ToList();
                result.Plain = result.Sources.Select(TagBridge.StripTagMarkers).ToList();
                result.Origin = "captured segments";
            }

            // The live document, when the preview tool is connected and holds more
            // than has been captured. Its rows are paragraphs, which is if anything
            // better for classification than a scattering of sentences.
            if (PreviewStore.ToolAlive)
            {
                var live = PreviewStore.Documents()
                    .Where(d => d != null)
                    .Select(d => new { d.DocumentGuid, d.DocumentName, Rows = PreviewStore.Rows(d.DocumentGuid) })
                    .OrderByDescending(d => d.Rows.Count)
                    .FirstOrDefault();

                if (live != null && live.Rows.Count > result.Sources.Count)
                {
                    result.Sources = live.Rows
                        .Select(r => r.Source ?? string.Empty)
                        .Where(t => !string.IsNullOrWhiteSpace(TagBridge.StripTagMarkers(t)))
                        .ToList();

                    result.Plain = result.Sources.Select(TagBridge.StripTagMarkers).ToList();

                    result.DocumentName = live.DocumentName ?? result.DocumentName;
                    result.Origin = "the live document";

                    var first = live.Rows.FirstOrDefault();
                    if (first != null)
                    {
                        result.SourceLangCode = first.SourceLangCode ?? result.SourceLangCode;
                        result.TargetLangCode = first.TargetLangCode ?? result.TargetLangCode;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// The project a document belongs to, or null. Labels only: memoQ gives a
        /// plugin a GUID and nothing else, so this reads the folder the GUID lives
        /// in and must never be used as a key.
        /// </summary>
        private static string ProjectNameOf(Guid? documentId)
        {
            if (documentId == null || documentId == Guid.Empty) return null;

            try { return DocumentNames.Resolve(documentId.Value)?.Project; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// memoQ's own answer, when the model has too little text to give one. A
        /// project carries both fields in its metadata, and that beats defaulting
        /// to "general" on a document the classifier never really saw.
        ///
        /// Subject is tried first, deliberately. memoQ's own guidance is that
        /// Subject holds the subject matter and Domain holds the end client, which
        /// is the opposite of what the names suggest and is why translators have
        /// asked which is which for years. Reading Domain first offered a client
        /// name as a subject-matter guess on any project filled in the recommended
        /// way. Domain stays as the second guess, because plenty of projects do
        /// use it for subject matter regardless.
        ///
        /// Note that this ordering governs only this fallback. The prompt itself
        /// shows the model both fields under memoQ's own labels and interprets
        /// neither, which is the right treatment for a convention this contested.
        /// </summary>
        private static string DomainFromProject(string domain, string subject)
        {
            foreach (var candidate in new[] { subject, domain })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                var needle = candidate.Trim().ToLowerInvariant().TrimEnd('s');

                foreach (var known in global::Supervertaler.Core.DocumentContextClassifier.Domains)
                {
                    var k = (known ?? string.Empty).ToLowerInvariant();
                    if (k.Length == 0 || k == "general") continue;
                    if (k.TrimEnd('s') == needle) return known;
                }
            }

            return null;
        }

        /// POST /v1/autoprompt/classify — the cheap first step: what kind of
        /// document is this? The editor shows the answer and lets the user
        /// confirm or correct it before the expensive generation call, as the
        /// Trados plugin does. Returns the detected domain, a one-line
        /// description, and the list of domains the generator has templates for.
        /// </summary>
        private void HandleClassify(HttpListenerContext ctx)
        {
            var req = Read<AutoPromptRequest>(ctx) ?? new AutoPromptRequest();
            var context = _context;
            var general = context?.General;
            var apiKey = context?.ApiKey;

            if (general == null || string.IsNullOrWhiteSpace(apiKey))
            {
                TryWrite(ctx, 409, Json(new ErrorBody { Error = "No API key is configured in memoQ's Supervertaler settings." }));
                return;
            }

            var doc = ResolveAutoPromptSource(req.Document);
            if (doc.Sources.Count == 0)
            {
                TryWrite(ctx, 409, Json(new ErrorBody
                {
                    Error = "No document text is available yet. Open the document in memoQ so the preview "
                          + "tool can read it, or run Pre-translate once, then try again."
                }));
                return;
            }

            // Classification reads prose: tag markers are not evidence of a domain.
            var sources = doc.Plain;
            var analysis = global::Supervertaler.Core.DocumentAnalyzer.Analyze(sources);

            Classify(sources, analysis.PrimaryDomain,
                SessionRunner.MapProviderForCore(general.Provider), general.Model, apiKey,
                string.IsNullOrWhiteSpace(general.Endpoint) ? null : general.Endpoint.Trim(),
                out var domain, out var description);

            // memoQ knows what kind of project this is. Prefer that over a guess
            // the model could not really make.
            if (string.IsNullOrWhiteSpace(domain) || domain.Equals("general", StringComparison.OrdinalIgnoreCase))
            {
                var fromProject = DomainFromProject(doc.Domain, doc.Subject);
                if (fromProject != null)
                {
                    domain = fromProject;
                    description = (string.IsNullOrWhiteSpace(description) ? "" : description + " ")
                        + $"(domain taken from the memoQ project, which is set to {doc.Domain ?? doc.Subject}.)";
                }
            }

            PluginLog.Write($"AutoPrompt classify: {sources.Count} source(s) from {doc.Origin}, "
                + $"domain={domain}");

            TryWrite(ctx, 200, Json(new ClassifyResponse
            {
                Domain = domain,
                Description = description,
                KeywordDomain = analysis.PrimaryDomain,
                Domains = global::Supervertaler.Core.DocumentContextClassifier.Domains,
                SegmentCount = analysis.SegmentCount,
                WordCount = analysis.WordCount,
                Origin = doc.Origin
            }));
        }

        /// <summary>One short model call: domain plus a one-line description. Falls back to the keyword domain on any failure.</summary>
        private static void Classify(
            List<string> sources, string keywordDomain,
            string provider, string model, string apiKey, string endpoint,
            out string domain, out string description)
        {
            domain = keywordDomain;
            description = "";
            try
            {
                var sample = global::Supervertaler.Core.DocumentContextClassifier.BuildSample(sources);
                if (string.IsNullOrEmpty(sample)) return;

                string classified;
                using (var client = new global::Supervertaler.Core.LlmClient(provider, model, apiKey, endpoint))
                {
                    classified = client.SendPromptAsync(
                        global::Supervertaler.Core.DocumentContextClassifier.BuildUserPrompt(sample),
                        global::Supervertaler.Core.DocumentContextClassifier.SystemPrompt,
                        maxTokens: 300, suppressLog: true).GetAwaiter().GetResult();
                }

                global::Supervertaler.Core.DocumentContextClassifier.Parse(classified, out var aiDomain, out var aiDesc);
                if (!string.IsNullOrEmpty(aiDomain)) { domain = aiDomain; description = aiDesc ?? ""; }
            }
            catch (Exception ex)
            {
                PluginLog.Write("AutoPrompt: classification failed; using keyword domain", ex);
            }
        }

        [DataContract]
        internal class ClassifyResponse
        {
            [DataMember(Name = "domain")] public string Domain { get; set; }
            [DataMember(Name = "description")] public string Description { get; set; }
            [DataMember(Name = "keywordDomain")] public string KeywordDomain { get; set; }
            [DataMember(Name = "domains")] public string[] Domains { get; set; }
            [DataMember(Name = "segmentCount")] public int SegmentCount { get; set; }
            [DataMember(Name = "wordCount")] public int WordCount { get; set; }

            /// <summary>
            /// Where the text came from. The dialog used to report the capture
            /// store's count, which read "1 segment captured" on a run that in
            /// fact drafted from 374 paragraphs of the live document.
            /// </summary>
            [DataMember(Name = "origin")] public string Origin { get; set; }
        }

        [DataContract]
        internal class AutoPromptPreviewResponse
        {
            [DataMember(Name = "metaPrompt")] public string MetaPrompt { get; set; }
            [DataMember(Name = "origin")] public string Origin { get; set; }
            [DataMember(Name = "documentName")] public string DocumentName { get; set; }
            [DataMember(Name = "segmentCount")] public int SegmentCount { get; set; }
            [DataMember(Name = "termCount")] public int TermCount { get; set; }
            [DataMember(Name = "confirmedPairCount")] public int ConfirmedPairCount { get; set; }
            [DataMember(Name = "provider")] public string Provider { get; set; }
            [DataMember(Name = "model")] public string Model { get; set; }
        }

        /// <summary>
        /// What the memoQ runtime actually does with a prompt — the ways it
        /// differs from the Trados plugin the meta-prompt was written for.
        /// </summary>
        private const string MemoQHostConstraints =
            "This prompt will run inside memoQ, through the Supervertaler MT plugin. The runtime differs from " +
            "the defaults described above in these ways, and the generated prompt MUST reflect them:\n" +
            "- The prompt is the SYSTEM prompt of every request. During Pre-translate the runtime delivers " +
            "numbered batches of about 10 segments in the format described above; during interactive work it " +
            "delivers ONE segment at a time with no numbering. The prompt must handle both: when a single " +
            "unnumbered segment is delivered, return only its translation.\n" +
            "- EVERY character the model returns is written verbatim into the target cell, exactly as in Trados. " +
            "Keep the translator-comment convention described above exactly as written, [[TC: ...]] included. " +
            "No other non-translation text of any kind.\n" +
            "- Inline formatting arrives as tag markers such as <t1>...</t1> or <b>...</b>. The prompt must " +
            "require every marker to be reproduced exactly, in the equivalent position, never invented, dropped " +
            "or renumbered.\n" +
            "- The document sample below retains its tag markers, so inspect them. Note any pattern worth " +
            "locking: a symbol or digit that appears sometimes inside a tag pair and sometimes as a plain " +
            "Unicode character, tags wrapping nothing but a symbol, or tags nested unusually. Where the same " +
            "thing is represented two ways in one document, the prompt must forbid harmonising it in either " +
            "direction: normalising a tagged digit into a Unicode character destroys a tag pair, and expanding " +
            "a Unicode character into a tagged digit invents one. Do NOT state which notation the runtime uses " +
            "as though it were fixed \u2013 the sample may have been rendered by a different channel than the " +
            "one that will deliver the segments. State the rule in terms of what arrives.\n" +
            "- The runtime appends, per request, the glossary terms found in that request's segments and up to " +
            "five of the translator's own CONFIRMED translations from this document as reference. The prompt " +
            "should say that confirmed translations supplied at request time take precedence over the prompt's " +
            "own glossary where they conflict, because they are the translator's later decisions.\n" +
            "- Glossary terms supplied at request time are marked as either preferred or FORBIDDEN. Forbidden " +
            "terms are absolute. Preferred terms should be followed unless clearly wrong for the sentence.\n" +
            "- Those runtime terms come from a glossary file the translator edits, which is normally exported " +
            "from this prompt's own glossary table, so the two are the same terminology in two places. The " +
            "prompt should say that where they disagree the runtime terms govern, because the file is the copy " +
            "the translator maintains, and that a runtime FORBIDDEN term overrides this prompt's glossary even " +
            "when the glossary locks that exact rendering.\n" +
            "- IMPORTANT – the TERMINOLOGY DATA above did NOT come from a project termbase. It is the set of " +
            "hits from the user's GENERAL glossary (patents, legal, technical, all mixed) that happen to occur " +
            "in this document, and a general glossary carries senses that are wrong for a given text: " +
            "\"application\" as a patent application (aanvrage) in a document about software applications, " +
            "\"program\" as a course of study in a document about computer programs. Therefore: (a) read the " +
            "document text and decide, term by term, whether the glossary sense is the sense this document " +
            "uses; (b) where the document plainly uses a different sense, LOCK the document's sense and say " +
            "explicitly that the glossary rendering does not apply here; (c) present glossary-derived " +
            "mappings as \"preferred (from the general glossary)\", not as \"project termbase\", and never " +
            "write \"never use X\" against a rendering merely because the glossary offered another – reserve " +
            "prohibitions for terms actually marked FORBIDDEN. Getting this wrong locks a nonsense rendering " +
            "into every segment of the job.\n" +
            "- Because the whole prompt is re-sent with every ~10-segment request, aim for 1500-3000 words " +
            "rather than 2000-5000: complete, but no padding.\n" +
            "- Use the placeholders {{SOURCE_LANGUAGE}} and {{TARGET_LANGUAGE}} for the language names wherever " +
            "they occur, instead of writing the names out. Do NOT use any other {{PLACEHOLDER}}; the runtime " +
            "fills only those two.";

        [DataContract]
        internal class AutoPromptRequest
        {
            [DataMember(Name = "document")] public string Document { get; set; }
            [DataMember(Name = "hint")] public string Hint { get; set; }
            [DataMember(Name = "domain")] public string Domain { get; set; }
            [DataMember(Name = "description")] public string Description { get; set; }
            [DataMember(Name = "includeTerms")] public bool IncludeTerms { get; set; } = true;
            [DataMember(Name = "includeConfirmed")] public bool IncludeConfirmed { get; set; } = true;
        }

        [DataContract]
        internal class AutoPromptResponse
        {
            [DataMember(Name = "content")] public string Content { get; set; }
            [DataMember(Name = "suggestedName")] public string SuggestedName { get; set; }
            [DataMember(Name = "domain")] public string Domain { get; set; }
            [DataMember(Name = "summary")] public string Summary { get; set; }
            [DataMember(Name = "description")] public string Description { get; set; }
            [DataMember(Name = "termCount")] public int TermCount { get; set; }
            [DataMember(Name = "confirmedPairCount")] public int ConfirmedPairCount { get; set; }

            /// <summary>
            /// The languages the prompt was written for. A prompt is not
            /// direction-neutral — it names them in its role, locks terminology
            /// one way round and carries register rules for one target — so the
            /// draft is stamped with the pair it was drafted against and the
            /// plugin can say so later if it is used the other way round.
            /// </summary>
            [DataMember(Name = "sourceLang")] public string SourceLang { get; set; }

            [DataMember(Name = "targetLang")] public string TargetLang { get; set; }
        }

        // ── preview tool channel ─────────────────────────────────────────
        //
        // memoQ's Preview SDK talks to a separate process, not to a plugin. So
        // Supervertaler.MemoQ.Preview.exe registers as a preview tool, receives
        // memoQ's pushes, and forwards them here — the same bridge, the same
        // token. What it forwards is exactly what the MT SDK never showed us:
        // target text, the active row, the document's real name.

        private void HandlePreviewContent(HttpListenerContext ctx)
        {
            var req = Read<PreviewContentRequest>(ctx);
            var parts = (req?.Parts ?? new PreviewPartBody[0])
                .Where(p => p != null && !string.IsNullOrEmpty(p.PartId))
                .Select(p => new PreviewStore.Part
                {
                    PartId = p.PartId,
                    DocumentGuid = Guid.TryParse(p.DocumentGuid, out var g) ? g : Guid.Empty,
                    DocumentName = p.DocumentName,
                    ImportPath = p.ImportPath,
                    SourceLangCode = p.SourceLangCode,
                    TargetLangCode = p.TargetLangCode,
                    Source = p.Source ?? "",
                    Target = p.Target ?? "",
                    WordCount = p.WordCount,
                    CharCount = p.CharCount
                })
                .ToList();

            PreviewStore.Upsert(parts);
            PreviewStore.NoteTool(true);
            TryWrite(ctx, 200, Json(new OkBody { Ok = true, Message = parts.Count + " part(s) stored" }));
        }

        private void HandlePreviewIds(HttpListenerContext ctx)
        {
            var req = Read<PreviewIdsRequest>(ctx);
            PreviewStore.SetOrder(req?.PartIds ?? new string[0]);
            PreviewStore.NoteTool(true);
            TryWrite(ctx, 200, Json(new OkBody { Ok = true }));
        }

        private void HandlePreviewHighlight(HttpListenerContext ctx)
        {
            var req = Read<PreviewHighlightRequest>(ctx);
            if (req?.Part != null) PreviewStore.Upsert(new[]
            {
                new PreviewStore.Part
                {
                    PartId = req.Part.PartId,
                    DocumentGuid = Guid.TryParse(req.Part.DocumentGuid, out var g) ? g : Guid.Empty,
                    DocumentName = req.Part.DocumentName,
                    ImportPath = req.Part.ImportPath,
                    SourceLangCode = req.Part.SourceLangCode,
                    TargetLangCode = req.Part.TargetLangCode,
                    Source = req.Part.Source ?? "",
                    Target = req.Part.Target ?? "",
                    WordCount = req.Part.WordCount,
                    CharCount = req.Part.CharCount
                }
            });

            PreviewStore.SetActive(req?.Part == null ? null : new PreviewStore.Active
            {
                PartId = req.Part.PartId,
                SourceStart = req.SourceStart, SourceLength = req.SourceLength,
                TargetStart = req.TargetStart, TargetLength = req.TargetLength,
                AtUtc = DateTime.UtcNow
            });
            PreviewStore.NoteTool(true);
            TryWrite(ctx, 200, Json(new OkBody { Ok = true }));
        }

        private void HandlePreviewStatus(HttpListenerContext ctx)
        {
            var req = Read<PreviewStatusRequest>(ctx);
            PreviewStore.NoteTool(req?.Connected ?? false);
            TryWrite(ctx, 200, Json(new OkBody { Ok = true }));
        }

        /// <summary>Long-poll: the tool asks, and waits up to ~25 s for something to do.</summary>
        private void HandlePreviewCommands(HttpListenerContext ctx)
        {
            var wait = Math.Min(25, ParseInt(ctx.Request.QueryString["wait"], 20));
            PreviewStore.NoteTool(true);
            var commands = PreviewStore.TakeCommands(TimeSpan.FromSeconds(wait));

            TryWrite(ctx, 200, Json(new PreviewCommandsBody
            {
                Commands = commands.Select(c => new PreviewCommandBody
                {
                    Type = c.Type,
                    PartId = c.PartId,
                    SourceStart = c.SourceStart,
                    SourceLength = c.SourceLength,
                    Part = ToBody(PreviewStore.GetPart(c.PartId))
                }).ToArray()
            }));
        }

        private void HandleActiveSegment(HttpListenerContext ctx)
        {
            if (!PreviewStore.ToolAlive)
            {
                TryWrite(ctx, 409, Json(new ErrorBody
                {
                    Error = "The Supervertaler preview tool is not connected, so the active segment is unknown. "
                          + "It starts with memoQ once registered under Options > External preview tools; "
                          + "if it is not running, ask the user to start Supervertaler.MemoQ.Preview.exe."
                }));
                return;
            }

            var active = PreviewStore.GetActive();
            var part = PreviewStore.GetPart(active?.PartId);
            if (part == null)
            {
                TryWrite(ctx, 200, Json(new ActiveSegmentBody { Note = "No segment has been selected yet in this session." }));
                return;
            }

            var rows = PreviewStore.Rows(part.DocumentGuid);
            var index = rows.FindIndex(r => r.PartId == part.PartId);

            // A part is a paragraph; the focused range says which sentence of it
            // — which memoQ grid row — the cursor is on. Cut it out so the
            // caller gets the segment, not the paragraph, when they differ.
            string Slice(string text, int start, int length)
            {
                if (string.IsNullOrEmpty(text) || length <= 0 || start < 0 || start >= text.Length) return null;
                var cut = text.Substring(start, Math.Min(length, text.Length - start));
                return cut.Length == text.Length ? null : cut;
            }

            TryWrite(ctx, 200, Json(new ActiveSegmentBody
            {
                Index = index >= 0 ? index + 1 : 0,
                Part = ToBody(part),
                ActiveSource = Slice(part.Source, active.SourceStart, active.SourceLength),
                ActiveTarget = Slice(part.Target, active.TargetStart, active.TargetLength),
                SourceSelectionStart = active.SourceStart, SourceSelectionLength = active.SourceLength,
                TargetSelectionStart = active.TargetStart, TargetSelectionLength = active.TargetLength,
                SelectedAgoSeconds = (int)(DateTime.UtcNow - active.AtUtc).TotalSeconds
            }));
        }

        /// <summary>
        /// go_to_segment. Resolved to a part id here, executed by the preview
        /// tool through RequestHighlightChange — the call memoQ's own PDF tool
        /// uses to select a row from outside.
        /// </summary>
        private void HandleGoTo(HttpListenerContext ctx)
        {
            var req = Read<GoToRequest>(ctx) ?? new GoToRequest();

            if (!PreviewStore.ToolAlive)
            {
                TryWrite(ctx, 409, Json(new ErrorBody { Error = "The Supervertaler preview tool is not connected; memoQ cannot be navigated." }));
                return;
            }

            PreviewStore.Part target = null;
            if (!string.IsNullOrEmpty(req.PartId))
            {
                target = PreviewStore.GetPart(req.PartId);
            }
            else if (req.Index > 0)
            {
                var doc = PreviewStore.Documents().FirstOrDefault(d =>
                    string.IsNullOrEmpty(req.Document) || d.DocumentGuid.ToString("D") == req.Document || d.DocumentName == req.Document);
                if (doc != null)
                {
                    var rows = PreviewStore.Rows(doc.DocumentGuid);
                    if (req.Index <= rows.Count) target = rows[req.Index - 1];
                }
            }

            if (target == null)
            {
                TryWrite(ctx, 404, Json(new ErrorBody { Error = "No such segment. Use get_segments to see indexes and part ids." }));
                return;
            }

            // A part is a paragraph. A range within it says which sentence —
            // memoQ's grid row — to land on; without one, the whole paragraph.
            var start = Math.Max(0, req.SourceStart);
            var length = req.SourceLength > 0 ? Math.Min(req.SourceLength, Math.Max(0, target.Source.Length - start)) : 0;

            PreviewStore.Enqueue(new PreviewStore.Command
            {
                Type = "goto", PartId = target.PartId, SourceStart = start, SourceLength = length
            });

            var shown = length > 0 && start < target.Source.Length
                ? target.Source.Substring(start, Math.Min(length, target.Source.Length - start))
                : target.Source;
            TryWrite(ctx, 200, Json(new OkBody
            {
                Ok = true,
                Message = "Asked memoQ to select \"" + Truncate(TagBridge.StripTagMarkers(shown), 60) + "\"."
            }));
        }

        private static string Truncate(string s, int n) =>
            string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";

        private static PreviewPartBody ToBody(PreviewStore.Part p)
        {
            if (p == null) return null;
            return new PreviewPartBody
            {
                PartId = p.PartId,
                DocumentGuid = p.DocumentGuid.ToString("D"),
                DocumentName = p.DocumentName,
                ImportPath = p.ImportPath,
                SourceLangCode = p.SourceLangCode,
                TargetLangCode = p.TargetLangCode,
                Source = p.Source,
                Target = p.Target,
                WordCount = p.WordCount,
                CharCount = p.CharCount
            };
        }

        [DataContract]
        internal class PreviewPartBody
        {
            [DataMember(Name = "partId")] public string PartId { get; set; }
            [DataMember(Name = "documentGuid")] public string DocumentGuid { get; set; }
            [DataMember(Name = "documentName", EmitDefaultValue = false)] public string DocumentName { get; set; }
            [DataMember(Name = "importPath", EmitDefaultValue = false)] public string ImportPath { get; set; }
            [DataMember(Name = "sourceLangCode", EmitDefaultValue = false)] public string SourceLangCode { get; set; }
            [DataMember(Name = "targetLangCode", EmitDefaultValue = false)] public string TargetLangCode { get; set; }
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "target")] public string Target { get; set; }
            [DataMember(Name = "wordCount")] public int WordCount { get; set; }
            [DataMember(Name = "charCount")] public int CharCount { get; set; }
        }

        [DataContract] internal class PreviewContentRequest { [DataMember(Name = "parts")] public PreviewPartBody[] Parts { get; set; } }
        [DataContract] internal class PreviewIdsRequest { [DataMember(Name = "partIds")] public string[] PartIds { get; set; } }
        [DataContract] internal class PreviewStatusRequest { [DataMember(Name = "connected")] public bool Connected { get; set; } }

        [DataContract]
        internal class PreviewHighlightRequest
        {
            [DataMember(Name = "part")] public PreviewPartBody Part { get; set; }
            [DataMember(Name = "sourceStart")] public int SourceStart { get; set; }
            [DataMember(Name = "sourceLength")] public int SourceLength { get; set; }
            [DataMember(Name = "targetStart")] public int TargetStart { get; set; }
            [DataMember(Name = "targetLength")] public int TargetLength { get; set; }
        }

        [DataContract]
        internal class PreviewCommandsBody { [DataMember(Name = "commands")] public PreviewCommandBody[] Commands { get; set; } }

        [DataContract]
        internal class PreviewCommandBody
        {
            [DataMember(Name = "type")] public string Type { get; set; }
            [DataMember(Name = "partId")] public string PartId { get; set; }
            [DataMember(Name = "sourceStart")] public int SourceStart { get; set; }
            [DataMember(Name = "sourceLength")] public int SourceLength { get; set; }
            [DataMember(Name = "part", EmitDefaultValue = false)] public PreviewPartBody Part { get; set; }
        }

        [DataContract]
        internal class ActiveSegmentBody
        {
            [DataMember(Name = "index")] public int Index { get; set; }
            [DataMember(Name = "part", EmitDefaultValue = false)] public PreviewPartBody Part { get; set; }
            [DataMember(Name = "activeSource", EmitDefaultValue = false)] public string ActiveSource { get; set; }
            [DataMember(Name = "activeTarget", EmitDefaultValue = false)] public string ActiveTarget { get; set; }
            [DataMember(Name = "sourceSelectionStart")] public int SourceSelectionStart { get; set; }
            [DataMember(Name = "sourceSelectionLength")] public int SourceSelectionLength { get; set; }
            [DataMember(Name = "targetSelectionStart")] public int TargetSelectionStart { get; set; }
            [DataMember(Name = "targetSelectionLength")] public int TargetSelectionLength { get; set; }
            [DataMember(Name = "selectedAgoSeconds")] public int SelectedAgoSeconds { get; set; }
            [DataMember(Name = "note", EmitDefaultValue = false)] public string Note { get; set; }
        }

        [DataContract]
        internal class GoToRequest
        {
            [DataMember(Name = "index")] public int Index { get; set; }
            [DataMember(Name = "partId")] public string PartId { get; set; }
            [DataMember(Name = "document")] public string Document { get; set; }
            [DataMember(Name = "sourceStart")] public int SourceStart { get; set; }
            [DataMember(Name = "sourceLength")] public int SourceLength { get; set; }
        }

        // ── QA over the live view ────────────────────────────────────────

        /// <summary>The live document to run QA on: the one asked for, else the most recently active. Null with a reason when there is none.</summary>
        private static List<PreviewStore.Part> QaRows(HttpListenerContext ctx, out string problem, out PreviewStore.Part doc)
        {
            problem = null; doc = null;
            if (!PreviewStore.ToolAlive)
            {
                problem = "The Supervertaler preview tool is not connected, so the target text is not available. "
                        + "QA checks need it; ask the user to start Supervertaler.MemoQ.Preview.exe (memoQ auto-starts it once registered).";
                return null;
            }

            var key = ctx.Request.QueryString["document"];
            doc = PreviewStore.Documents().FirstOrDefault(d =>
                string.IsNullOrEmpty(key)
                || d.DocumentGuid.ToString("D") == key
                || string.Equals(d.DocumentName, key, StringComparison.OrdinalIgnoreCase));

            if (doc == null) { problem = "No document has been seen yet. Open one in memoQ and click into a segment."; return null; }
            return PreviewStore.Rows(doc.DocumentGuid);
        }

        // ── SuperMemory ──────────────────────────────────────────────
        //
        // The translator's own notes on a client: terminology decisions and the
        // reasoning behind them, style rules, standing instructions. Read-only,
        // and read on demand rather than injected: this is the path used when
        // someone says "look at the memory bank for this project" mid-chat.
        //
        // Query-string parameter names match the Trados bridge exactly, because
        // one MCP server exe serves both products.

        private void HandleSuperMemoryBanks(HttpListenerContext ctx)
        {
            TryWrite(ctx, 200, Json(SuperMemory.Banks(SharedSettings.MemoryBank)));
        }

        private void HandleSuperMemoryContext(HttpListenerContext ctx)
        {
            var q = ctx.Request.QueryString;

            // No bank named falls back to the one selected for this project,
            // which is normally nothing. It never falls back to "whichever was
            // used last": a bank supplies one client's terminology, and the
            // wrong one reads exactly like the right one.
            var bank = Trim(q["bank"]) ?? SharedSettings.MemoryBank;

            int budget;
            if (!int.TryParse(q["tokenBudget"], out budget)) budget = 0;

            var context = _context;

            TryWrite(ctx, 200, Json(SuperMemory.Context(
                bank,
                Trim(q["q"]),
                Trim(q["domain"]),
                budget,
                PromptBuilder.DescribeLanguage(context?.SourceLangCode),
                PromptBuilder.DescribeLanguage(context?.TargetLangCode),
                LatestClientName())));
        }

        private void HandleSuperMemorySearch(HttpListenerContext ctx)
        {
            var q = ctx.Request.QueryString;

            int limit;
            if (!int.TryParse(q["limit"], out limit)) limit = 0;

            TryWrite(ctx, 200, Json(SuperMemory.Search(
                Trim(q["bank"]) ?? SharedSettings.MemoryBank,
                Trim(q["q"]),
                limit)));
        }

        /// <summary>
        /// The memoQ project's Client field, from the most recent document seen.
        ///
        /// Only a label: the reader uses it to title the block, since the bank
        /// itself is the selection. Null is a perfectly good answer.
        /// </summary>
        private static string LatestClientName()
        {
            try
            {
                return CaptureStore.Snapshot()
                    .Select(d => d?.Client)
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Trim(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Trim();
        }

        private void HandleQaCheck(HttpListenerContext ctx)
        {
            var type = (ctx.Request.QueryString["type"] ?? "").ToLowerInvariant();
            if (type != "numbers" && type != "tags" && type != "nbsp" && type != "terminology")
            {
                TryWrite(ctx, 400, Json(new ErrorBody { Error = "missing or unknown 'type' – use numbers, tags, nbsp or terminology" }));
                return;
            }

            var rows = QaRows(ctx, out var problem, out var doc);
            if (rows == null) { TryWrite(ctx, 409, Json(new ErrorBody { Error = problem })); return; }

            var limit = Math.Min(200, ParseInt(ctx.Request.QueryString["limit"], 50));
            var result = QaChecks.Run(type, rows, limit, SharedSettings.GlossaryPath);

            TryWrite(ctx, 200, Json(new QaBody
            {
                Check = result.Check,
                DocumentName = doc.DocumentName,
                Checked = result.Checked,
                Found = result.Found,
                Truncated = result.Truncated,
                Note = result.Note,
                Unit = "paragraph – pass 'index' to go_to_segment to jump there",
                Issues = result.Issues.Select(i => new QaIssueBody
                {
                    Index = i.Index, PartId = i.PartId, Detail = i.Detail, Source = i.Source, Target = i.Target
                }).ToArray()
            }));
        }

        private void HandleInconsistencies(HttpListenerContext ctx)
        {
            var rows = QaRows(ctx, out var problem, out var doc);
            if (rows == null) { TryWrite(ctx, 409, Json(new ErrorBody { Error = problem })); return; }

            var limit = Math.Min(500, ParseInt(ctx.Request.QueryString["limit"], 50));
            var offset = ParseInt(ctx.Request.QueryString["offset"], 0);
            var groups = QaChecks.Inconsistencies(rows);

            TryWrite(ctx, 200, Json(new InconsistenciesBody
            {
                DocumentName = doc.DocumentName,
                Total = groups.Count,
                Unit = "paragraph – the same source paragraph translated differently",
                Note = groups.Count == 0 ? "No repeated source paragraph has more than one translation." : null,
                Groups = groups.Skip(offset).Take(limit).Select(g => new InconsistencyGroupBody
                {
                    Source = g.Source,
                    Occurrences = g.Occurrences.Select(o => new OccurrenceBody { Index = o.Index, PartId = o.PartId, Target = o.Target }).ToArray()
                }).ToArray()
            }));
        }

        [DataContract]
        internal class QaBody
        {
            [DataMember(Name = "check")] public string Check { get; set; }
            [DataMember(Name = "documentName", EmitDefaultValue = false)] public string DocumentName { get; set; }
            [DataMember(Name = "unit")] public string Unit { get; set; }
            [DataMember(Name = "checked")] public int Checked { get; set; }
            [DataMember(Name = "found")] public int Found { get; set; }
            [DataMember(Name = "truncated")] public bool Truncated { get; set; }
            [DataMember(Name = "note", EmitDefaultValue = false)] public string Note { get; set; }
            [DataMember(Name = "issues")] public QaIssueBody[] Issues { get; set; }
        }

        [DataContract]
        internal class QaIssueBody
        {
            [DataMember(Name = "index")] public int Index { get; set; }
            [DataMember(Name = "partId")] public string PartId { get; set; }
            [DataMember(Name = "detail")] public string Detail { get; set; }
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "target")] public string Target { get; set; }
        }

        [DataContract]
        internal class InconsistenciesBody
        {
            [DataMember(Name = "documentName", EmitDefaultValue = false)] public string DocumentName { get; set; }
            [DataMember(Name = "unit")] public string Unit { get; set; }
            [DataMember(Name = "total")] public int Total { get; set; }
            [DataMember(Name = "note", EmitDefaultValue = false)] public string Note { get; set; }
            [DataMember(Name = "groups")] public InconsistencyGroupBody[] Groups { get; set; }
        }

        [DataContract]
        internal class InconsistencyGroupBody
        {
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "occurrences")] public OccurrenceBody[] Occurrences { get; set; }
        }

        [DataContract]
        internal class OccurrenceBody
        {
            [DataMember(Name = "index")] public int Index { get; set; }
            [DataMember(Name = "partId")] public string PartId { get; set; }
            [DataMember(Name = "target")] public string Target { get; set; }
        }

        /// <summary>
        /// POST /v1/glossary/activate — make a glossary file the one the
        /// terminology plugin serves and the prompts and QA checks use. The
        /// setting lives in the plugin's shared settings, which only the plugin
        /// should write; the prompt editor asks for it here after exporting a
        /// prompt's glossary.
        /// </summary>
        private void HandleGlossaryActivate(HttpListenerContext ctx)
        {
            var req = Read<GlossaryActivateRequest>(ctx);
            var path = req?.Path?.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                TryWrite(ctx, 400, Json(new ErrorBody { Error = "path is required and must exist: " + path }));
                return;
            }

            var previous = SharedSettings.GlossaryPath;
            SharedSettings.GlossaryPath = path;
            PluginLog.Write($"glossary activated over the bridge: {path} (was: {previous})");

            TryWrite(ctx, 200, Json(new OkBody
            {
                Ok = true,
                Message = "Active glossary is now " + Path.GetFileName(path) + ". The terminology pane, translation prompts "
                        + "and check_terminology use it from the next lookup."
            }));
        }

        [DataContract]
        internal class GlossaryActivateRequest
        {
            [DataMember(Name = "path")] public string Path { get; set; }
        }

        // ── plumbing ─────────────────────────────────────────────────────

        private static string LangPair(EngineContext context)
        {
            if (context == null) return null;
            return (context.SourceLangCode ?? "?") + "-" + (context.TargetLangCode ?? "?");
        }

        private static int ParseInt(string s, int fallback)
        {
            return int.TryParse(s, out var v) && v >= 0 ? v : fallback;
        }

        private static T Read<T>(HttpListenerContext ctx) where T : class
        {
            try
            {
                using (var body = ctx.Request.InputStream)
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    return (T)serializer.ReadObject(body);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Json(object body)
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(body.GetType());
                serializer.WriteObject(stream, body);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static void TryWrite(HttpListenerContext ctx, int status, string json)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception)
            {
                // Client went away mid-response; nothing to do.
            }
        }

        public void Dispose()
        {
            _stopping = true;
            try { _listener?.Stop(); } catch (Exception) { }
            try { File.Delete(HandshakePath); } catch (Exception) { }
        }

        // ── bodies ───────────────────────────────────────────────────────
        // DataContract types, because DataContractJsonSerializer is the JSON
        // this plugin gets without shipping a package (see "Dependencies: ship
        // nothing" in CLAUDE.md).

        [DataContract]
        internal class ErrorBody
        {
            [DataMember(Name = "error")] public string Error { get; set; }
        }

        [DataContract]
        internal class OkBody
        {
            [DataMember(Name = "ok")] public bool Ok { get; set; }
            [DataMember(Name = "message", EmitDefaultValue = false)] public string Message { get; set; }
        }

        [DataContract]
        internal class HelpBody
        {
            [DataMember(Name = "markdown")] public string Markdown { get; set; }
        }

        [DataContract]
        internal class ProjectBody
        {
            [DataMember(Name = "sourceLanguage", EmitDefaultValue = false)] public string SourceLanguage { get; set; }
            [DataMember(Name = "targetLanguage", EmitDefaultValue = false)] public string TargetLanguage { get; set; }
            [DataMember(Name = "langPair", EmitDefaultValue = false)] public string LangPair { get; set; }
            [DataMember(Name = "documents")] public ProjectDocumentBody[] Documents { get; set; }
            [DataMember(Name = "stagedTranslations")] public int StagedTranslations { get; set; }
            [DataMember(Name = "previewToolConnected")] public bool PreviewToolConnected { get; set; }
            [DataMember(Name = "activeGlossary", EmitDefaultValue = false)] public string ActiveGlossary { get; set; }
            [DataMember(Name = "liveDocuments", EmitDefaultValue = false)] public LiveDocumentBody[] LiveDocuments { get; set; }
            [DataMember(Name = "note", EmitDefaultValue = false)] public string Note { get; set; }
        }

        [DataContract]
        internal class LiveDocumentBody
        {
            [DataMember(Name = "documentGuid")] public string DocumentGuid { get; set; }
            [DataMember(Name = "documentName", EmitDefaultValue = false)] public string DocumentName { get; set; }
            [DataMember(Name = "langPair")] public string LangPair { get; set; }
            [DataMember(Name = "rows")] public int Rows { get; set; }
        }

        [DataContract]
        internal class ProjectDocumentBody
        {
            [DataMember(Name = "key")] public string Key { get; set; }
            [DataMember(Name = "origin")] public string Origin { get; set; }
            [DataMember(Name = "projectName", EmitDefaultValue = false)] public string ProjectName { get; set; }
            [DataMember(Name = "documentName", EmitDefaultValue = false)] public string DocumentName { get; set; }
            [DataMember(Name = "client", EmitDefaultValue = false)] public string Client { get; set; }
            [DataMember(Name = "domain", EmitDefaultValue = false)] public string Domain { get; set; }
            [DataMember(Name = "subject", EmitDefaultValue = false)] public string Subject { get; set; }
            [DataMember(Name = "capturedSegments")] public int CapturedSegments { get; set; }
            [DataMember(Name = "confirmedPairs")] public int ConfirmedPairs { get; set; }
            [DataMember(Name = "lastSeenUtc")] public string LastSeenUtc { get; set; }
        }

        [DataContract]
        internal class SegmentsBody
        {
            [DataMember(Name = "documentKey", EmitDefaultValue = false)] public string DocumentKey { get; set; }
            [DataMember(Name = "documentName", EmitDefaultValue = false)] public string DocumentName { get; set; }
            [DataMember(Name = "total")] public int Total { get; set; }
            [DataMember(Name = "source", EmitDefaultValue = false)] public string Source { get; set; }
            [DataMember(Name = "segments")] public SegmentBody[] Segments { get; set; }
            [DataMember(Name = "note", EmitDefaultValue = false)] public string Note { get; set; }
        }

        [DataContract]
        internal class SegmentBody
        {
            [DataMember(Name = "index")] public int Index { get; set; }
            [DataMember(Name = "partId", EmitDefaultValue = false)] public string PartId { get; set; }
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "target", EmitDefaultValue = false)] public string Target { get; set; }
            [DataMember(Name = "staged", EmitDefaultValue = false)] public string Staged { get; set; }
            [DataMember(Name = "isActive", EmitDefaultValue = false)] public bool? IsActive { get; set; }
        }

        [DataContract]
        internal class ConfirmedBody
        {
            [DataMember(Name = "documentKey", EmitDefaultValue = false)] public string DocumentKey { get; set; }
            [DataMember(Name = "pairs")] public PairBody[] Pairs { get; set; }
        }

        [DataContract]
        internal class PairBody
        {
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "target")] public string Target { get; set; }
        }

        [DataContract]
        internal class TermsBody
        {
            [DataMember(Name = "terms")] public TermBody[] Terms { get; set; }
        }

        [DataContract]
        internal class TermBody
        {
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "target")] public string Target { get; set; }
            [DataMember(Name = "forbidden")] public bool Forbidden { get; set; }
        }

        [DataContract]
        internal class AddTermRequest
        {
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "target")] public string Target { get; set; }
            [DataMember(Name = "forbidden")] public bool Forbidden { get; set; }
        }

        [DataContract]
        internal class StageRequest
        {
            [DataMember(Name = "pairs")] public StagePairBody[] Pairs { get; set; }
            [DataMember(Name = "label")] public string Label { get; set; }
        }

        [DataContract]
        internal class StagePairBody
        {
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "target")] public string Target { get; set; }
        }

        [DataContract]
        internal class StagedListBody
        {
            [DataMember(Name = "staged")] public StagedEntryBody[] Staged { get; set; }
        }

        [DataContract]
        internal class StagedEntryBody
        {
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "target")] public string Target { get; set; }
            [DataMember(Name = "label")] public string Label { get; set; }
            [DataMember(Name = "timesServed")] public int TimesServed { get; set; }
            [DataMember(Name = "stagedUtc")] public string StagedUtc { get; set; }
        }

        [DataContract]
        internal class PromptListBody
        {
            [DataMember(Name = "prompts")] public PromptInfoBody[] Prompts { get; set; }
        }

        [DataContract]
        internal class PromptInfoBody
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "relativePath")] public string RelativePath { get; set; }
            [DataMember(Name = "category", EmitDefaultValue = false)] public string Category { get; set; }
            [DataMember(Name = "description", EmitDefaultValue = false)] public string Description { get; set; }
            [DataMember(Name = "app", EmitDefaultValue = false)] public string App { get; set; }
        }

        [DataContract]
        internal class PromptBody
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "relativePath")] public string RelativePath { get; set; }
            [DataMember(Name = "category", EmitDefaultValue = false)] public string Category { get; set; }
            [DataMember(Name = "description", EmitDefaultValue = false)] public string Description { get; set; }
            [DataMember(Name = "content")] public string Content { get; set; }
        }

        [DataContract]
        internal class SavePromptRequest
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "category")] public string Category { get; set; }
            [DataMember(Name = "description")] public string Description { get; set; }
            [DataMember(Name = "content")] public string Content { get; set; }
        }

        private const string HelpCard = @"# Supervertaler for memoQ – what you can ask

**Reading the project** (the plugin sees what memoQ sends it – after one Pre-translate pass it has the whole document):
- *What is this project about?* – languages, client, domain, captured documents
- *Show me the segments* – the captured source text, with anything already staged
- *What has the translator confirmed so far?* – human-approved pairs, the gold standard for style and terminology

**Terminology:**
- *Look up a term* – search the Supervertaler glossary
- *Add a term* – appended to the glossary; memoQ's terminology pane and every later translation request pick it up

**Translating (the memoQ way):**
- *Translate these segments* – translations are **staged**, not written. They reach the grid when the user runs Pre-translate or lands on the segment: memoQ asks the plugin, and the plugin serves your staged text. Nothing changes in memoQ until the user acts.
- *What's staged?* / *Clear the staging area*

**Prompts:**
- *List / read / save prompts* in the shared Supervertaler library. Draft a project-specific prompt, save it, and the user selects it under Resources > Settings > MT > Supervertaler.

**What this bridge cannot do:** move the cursor, edit segments directly, confirm anything, or read memoQ's own TMs and termbases – memoQ gives plugins no API for any of that. The translator stays the hands.";
    }
}
