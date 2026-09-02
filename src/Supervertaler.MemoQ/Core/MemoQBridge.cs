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
        public static void EnsureStarted(EngineContext context)
        {
            _context = context;

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
                PluginLog.Write("MCP bridge: no free port after 16 attempts — bridge disabled this session");
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
                case "POST /v1/autoprompt": HandleAutoPrompt(ctx); return;
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

            var doc = CaptureStore.Get(key);
            if (doc == null)
            {
                TryWrite(ctx, 200, Json(new SegmentsBody
                {
                    Segments = new SegmentBody[0],
                    Total = 0,
                    Note = "Nothing captured yet — run Pre-translate once, or visit segments in the editor."
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
                    Error = "No segments have been seen yet — the language pair is unknown. "
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
                        + "Pre-translate or lands on the matching segments — matched by source text. "
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
            var apiKey = context?.Settings?.SecureSettings?.ApiKey;

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

            var doc = CaptureStore.Get(req.Document);
            if (doc == null || doc.Sources.Count == 0)
            {
                TryWrite(ctx, 409, Json(new ErrorBody
                {
                    Error = "Nothing captured yet. Run Pre-translate once (with the Pre-translate-only "
                          + "box ticked it costs nothing), then try again."
                }));
                return;
            }

            try
            {
                var sources = doc.Sources.Select(TagBridge.StripTagMarkers).ToList();
                var sourceLang = PromptBuilder.DescribeLanguage(doc.SourceLangCode);
                var targetLang = PromptBuilder.DescribeLanguage(doc.TargetLangCode);

                var analysis = global::Supervertaler.Core.DocumentAnalyzer.Analyze(sources);

                // Glossary hits across the whole document, deduplicated.
                var terms = new List<global::Supervertaler.Core.Models.TermEntry>();
                if (req.IncludeTerms)
                {
                    var matches = TermIndex.Find(SharedSettings.GlossaryPath, string.Join("\n", sources))
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
                else
                {
                    Classify(sources, analysis.PrimaryDomain, provider, general.Model, apiKey, endpoint,
                        out detectedDomain, out description);
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

                var generationContext = new global::Supervertaler.Core.PromptGenerationContext
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
                    HostConstraints = MemoQHostConstraints
                };

                // Phase 2: the generation itself.
                string raw;
                using (var client = new global::Supervertaler.Core.LlmClient(provider, general.Model, apiKey, endpoint))
                {
                    raw = client.SendPromptAsync(
                        global::Supervertaler.Core.PromptGenerator.BuildMetaPrompt(generationContext),
                        maxTokens: 32768).GetAwaiter().GetResult();
                }

                var content = global::Supervertaler.Core.PromptGenerator.ParseGeneratedPrompt(raw) ?? raw;

                var nameBits = new List<string>();
                if (!string.IsNullOrWhiteSpace(doc.Client)) nameBits.Add(doc.Client);
                else if (!string.IsNullOrWhiteSpace(detectedDomain)) nameBits.Add(detectedDomain);
                nameBits.Add((doc.SourceLangCode ?? "?") + "-" + (doc.TargetLangCode ?? "?"));

                PluginLog.Write($"AutoPrompt: drafted {content.Length} chars for {doc.Key} "
                    + $"(domain {detectedDomain}, {terms.Count} terms, {pairs.Count} confirmed pairs)");

                TryWrite(ctx, 200, Json(new AutoPromptResponse
                {
                    Content = content,
                    SuggestedName = string.Join(" ", nameBits),
                    Domain = detectedDomain,
                    Summary = summary,
                    Description = "Generated by AutoPrompt from the memoQ project"
                        + (terms.Count == 0 ? " – glossary derived from the document (no glossary hits)" : ""),
                    TermCount = terms.Count,
                    ConfirmedPairCount = pairs.Count
                }));
            }
            catch (Exception ex)
            {
                PluginLog.Write("AutoPrompt failed", ex);
                TryWrite(ctx, 500, Json(new ErrorBody { Error = "AutoPrompt failed: " + ex.Message }));
            }
        }

        /// <summary>
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
            var apiKey = context?.Settings?.SecureSettings?.ApiKey;

            if (general == null || string.IsNullOrWhiteSpace(apiKey))
            {
                TryWrite(ctx, 409, Json(new ErrorBody { Error = "No API key is configured in memoQ's Supervertaler settings." }));
                return;
            }

            var doc = CaptureStore.Get(req.Document);
            if (doc == null || doc.Sources.Count == 0)
            {
                TryWrite(ctx, 409, Json(new ErrorBody { Error = "Nothing captured yet. Run Pre-translate once, then try again." }));
                return;
            }

            var sources = doc.Sources.Select(TagBridge.StripTagMarkers).ToList();
            var analysis = global::Supervertaler.Core.DocumentAnalyzer.Analyze(sources);

            Classify(sources, analysis.PrimaryDomain,
                SessionRunner.MapProviderForCore(general.Provider), general.Model, apiKey,
                string.IsNullOrWhiteSpace(general.Endpoint) ? null : general.Endpoint.Trim(),
                out var domain, out var description);

            TryWrite(ctx, 200, Json(new ClassifyResponse
            {
                Domain = domain,
                Description = description,
                KeywordDomain = analysis.PrimaryDomain,
                Domains = global::Supervertaler.Core.DocumentContextClassifier.Domains,
                SegmentCount = analysis.SegmentCount,
                WordCount = analysis.WordCount
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
            "Keep the translator-comment convention described above unchanged: a genuinely necessary note goes " +
            "inline as a ⟦TC: ...⟧ marker, which the translator finds and processes while reviewing the grid. " +
            "Use them sparingly and never for anything that is not a real defect or ambiguity. No other " +
            "non-translation text of any kind.\n" +
            "- Inline formatting arrives as tag markers such as <t1>...</t1> or <b>...</b>. The prompt must " +
            "require every marker to be reproduced exactly, in the equivalent position, never invented, dropped " +
            "or renumbered.\n" +
            "- The runtime appends, per request, the glossary terms found in that request's segments and up to " +
            "five of the translator's own CONFIRMED translations from this document as reference. The prompt " +
            "should say that confirmed translations supplied at request time take precedence over the prompt's " +
            "own glossary where they conflict, because they are the translator's later decisions.\n" +
            "- Glossary terms supplied at request time are marked as either preferred or FORBIDDEN. Forbidden " +
            "terms are absolute. Preferred terms should be followed unless clearly wrong for the sentence.\n" +
            "- IMPORTANT — the TERMINOLOGY DATA above did NOT come from a project termbase. It is the set of " +
            "hits from the user's GENERAL glossary (patents, legal, technical, all mixed) that happen to occur " +
            "in this document, and a general glossary carries senses that are wrong for a given text: " +
            "\"application\" as a patent application (aanvrage) in a document about software applications, " +
            "\"program\" as a course of study in a document about computer programs. Therefore: (a) read the " +
            "document text and decide, term by term, whether the glossary sense is the sense this document " +
            "uses; (b) where the document plainly uses a different sense, LOCK the document's sense and say " +
            "explicitly that the glossary rendering does not apply here; (c) present glossary-derived " +
            "mappings as \"preferred (from the general glossary)\", not as \"project termbase\", and never " +
            "write \"never use X\" against a rendering merely because the glossary offered another — reserve " +
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
            [DataMember(Name = "note", EmitDefaultValue = false)] public string Note { get; set; }
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
            [DataMember(Name = "total")] public int Total { get; set; }
            [DataMember(Name = "segments")] public SegmentBody[] Segments { get; set; }
            [DataMember(Name = "note", EmitDefaultValue = false)] public string Note { get; set; }
        }

        [DataContract]
        internal class SegmentBody
        {
            [DataMember(Name = "index")] public int Index { get; set; }
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "staged", EmitDefaultValue = false)] public string Staged { get; set; }
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

        private const string HelpCard = @"# Supervertaler for memoQ — what you can ask

**Reading the project** (the plugin sees what memoQ sends it — after one Pre-translate pass it has the whole document):
- *What is this project about?* — languages, client, domain, captured documents
- *Show me the segments* — the captured source text, with anything already staged
- *What has the translator confirmed so far?* — human-approved pairs, the gold standard for style and terminology

**Terminology:**
- *Look up a term* — search the Supervertaler glossary
- *Add a term* — appended to the glossary; memoQ's terminology pane and every later translation request pick it up

**Translating (the memoQ way):**
- *Translate these segments* — translations are **staged**, not written. They reach the grid when the user runs Pre-translate or lands on the segment: memoQ asks the plugin, and the plugin serves your staged text. Nothing changes in memoQ until the user acts.
- *What's staged?* / *Clear the staging area*

**Prompts:**
- *List / read / save prompts* in the shared Supervertaler library. Draft a project-specific prompt, save it, and the user selects it under Resources > Settings > MT > Supervertaler.

**What this bridge cannot do:** move the cursor, edit segments directly, confirm anything, or read memoQ's own TMs and termbases — memoQ gives plugins no API for any of that. The translator stays the hands.";
    }
}
