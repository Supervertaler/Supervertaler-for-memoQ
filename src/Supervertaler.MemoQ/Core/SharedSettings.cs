using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The settings that do not belong to memoQ's per-plugin storage, kept in a
    /// file the plugin and the prompt editor both read and write.
    ///
    /// It began as the handful the two directors had to agree on. The two SDKs
    /// persist settings quite differently: an MT plugin round-trips a
    /// <c>PluginSettings</c> blob through memoQ, scoped to an *MT settings
    /// resource*; a TB plugin gets no such thing, only <c>ShowOptionsForm</c> and
    /// the expectation that it persists its own state. A setting they share, like
    /// the glossary path, has nowhere to live in either scheme.
    ///
    /// It has since become the home for settings that are not properties of a
    /// project at all, and for settings the user should be able to reach without
    /// opening memoQ. Reaching the MT plugin's dialog costs six clicks through
    /// Project home; the prompt editor is a program you can pin to the taskbar.
    ///
    /// Every accessor comes in an <c>...Or(fallback)</c> form that answers with
    /// the value in the settings resource until this file has ever carried the
    /// key. That is the whole migration story: nothing is copied, no flag records
    /// whether a move has happened, and an install that never opens either dialog
    /// keeps behaving exactly as it did.
    ///
    /// A file at
    /// <c>C:\Users\&lt;you&gt;\AppData\Local\Supervertaler.memoQ\shared.txt</c>,
    /// deliberately not JSON: a file the user can open in Notepad and fix is a
    /// feature for a plugin whose failure mode is otherwise invisible. Values are
    /// single-line by construction. The one multi-line setting, the inline
    /// instructions, gets its own file rather than an escaping scheme.
    /// </summary>
    internal static class SharedSettings
    {
        private static readonly object _lock = new object();

        private const string GlossaryKey = "glossary";
        private const string MemoryBankKey = "membank";
        private const string BridgeModeKey = "bridgemode";
        private const string ProviderKey = "provider";
        private const string ModelKey = "model";
        private const string EndpointKey = "endpoint";
        private const string ParallelKey = "parallel";
        private const string BatchSizeKey = "batchsize";
        private const string TerminologyContextKey = "useterminology";
        private const string DocumentContextKey = "usedocumentcontext";
        private const string PromptPathKey = "promptpath";
        private const string ApiKeyKey = "apikey";
        private const string SourceLangKey = "langsource";
        private const string TargetLangKey = "langtarget";
        private const string MemoQApiKeyKey = "apikey.memoq";

        /// <summary>
        /// Where failures are reported. The plugin points this at its log; the
        /// prompt editor compiles this same file and has no plugin log, so it
        /// leaves it silent. The alternative was a second implementation of the
        /// file format in the editor, and two readers of one file that can drift
        /// apart is exactly what this class exists to prevent.
        /// </summary>
        internal static Action<string, Exception> ErrorSink = (message, ex) => { };

        /// <summary>
        /// Set by the test harnesses and by the build's smoke test. A harness
        /// builds an engine from a bare defaults object, and two things follow
        /// that must not: seeding writes those defaults into the user's settings
        /// and, because seeding only fills gaps, memoQ then never seeds the real
        /// ones; and key resolution finds the user's real key, so a test that
        /// expects "no key configured" instead makes a billable call. Both have
        /// happened. One variable turns both off.
        /// </summary>
        internal static bool InHarness =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SUPERVERTALER_HARNESS"));

        internal static void ReportError(string message, Exception ex) => Report(message, ex);

        private static void Report(string message, Exception ex)
        {
            var sink = ErrorSink;
            if (sink == null) return;
            try { sink(message, ex); } catch { }
        }

        internal static string Directory
        {
            get
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Supervertaler.memoQ");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
        }

        internal static string Path => System.IO.Path.Combine(Directory, "shared.txt");

        /// <summary>
        /// The inline instructions, in their own file because they are the one
        /// setting that spans lines and a line-based store has no honest way to
        /// hold them.
        /// </summary>
        internal static string InstructionsPath => System.IO.Path.Combine(Directory, "instructions.txt");

        // ── typed settings ───────────────────────────────────────────────

        /// <summary>Path to the tab-separated glossary, or empty.</summary>
        public static string GlossaryPath
        {
            get => Read(GlossaryKey);
            set => Write(GlossaryKey, value);
        }

        /// <summary>
        /// The memory bank this translator has chosen, as a folder name under
        /// the shared memory-banks root. Empty by default and stays empty until
        /// something sets it.
        ///
        /// Empty means "none", never "whichever was used last". A bank carries
        /// one client's terminology and standing instructions, so inheriting the
        /// previous job's bank supplies confident, wrong answers that read
        /// exactly like right ones. Nothing here guesses a bank from the project
        /// name for the same reason.
        /// </summary>
        public static string MemoryBank
        {
            get => Read(MemoryBankKey);
            set => Write(MemoryBankKey, value);
        }

        /// <summary>
        /// Whether Pre-translate hands the segments to Claude Desktop instead of
        /// calling the model. Shared rather than kept in memoQ's MT settings
        /// resource because it says how the translator is working right now, not
        /// anything about a particular project.
        /// </summary>
        public static bool BridgeMode
        {
            get => Read(BridgeModeKey) == "1";
            set => Write(BridgeModeKey, value ? "1" : "0");
        }

        public static bool BridgeModeOr(bool fromResource) => BoolOr(BridgeModeKey, fromResource);

        public static string Provider { get => Read(ProviderKey); set => Write(ProviderKey, value); }
        public static string ProviderOr(string fromResource) => StringOr(ProviderKey, fromResource);

        public static string Model { get => Read(ModelKey); set => Write(ModelKey, value); }
        public static string ModelOr(string fromResource) => StringOr(ModelKey, fromResource);

        public static string Endpoint { get => Read(EndpointKey); set => Write(EndpointKey, value); }
        public static string EndpointOr(string fromResource) => StringOr(EndpointKey, fromResource);

        public static int Parallel { get => IntOr(ParallelKey, 4); set => Write(ParallelKey, value.ToString(CultureInfo.InvariantCulture)); }
        public static int ParallelOr(int fromResource) => IntOr(ParallelKey, fromResource);

        public static int BatchSize { get => IntOr(BatchSizeKey, 20); set => Write(BatchSizeKey, value.ToString(CultureInfo.InvariantCulture)); }
        public static int BatchSizeOr(int fromResource) => IntOr(BatchSizeKey, fromResource);

        public static bool UseTerminologyContext { get => BoolOr(TerminologyContextKey, true); set => Write(TerminologyContextKey, value ? "1" : "0"); }
        public static bool UseTerminologyContextOr(bool fromResource) => BoolOr(TerminologyContextKey, fromResource);

        public static bool UseDocumentContext { get => BoolOr(DocumentContextKey, true); set => Write(DocumentContextKey, value ? "1" : "0"); }
        public static bool UseDocumentContextOr(bool fromResource) => BoolOr(DocumentContextKey, fromResource);

        public static string PromptPath { get => Read(PromptPathKey); set => Write(PromptPathKey, value); }

        /// <summary>
        /// The language pair of the project memoQ last did real work in. Recorded
        /// so the prompt editor can stamp an exported glossary with the right
        /// direction while memoQ is closed. Two keys rather than one, because a
        /// code may carry a region and "dut-NL-eng-GB" cannot be split back apart.
        /// </summary>
        public static string SourceLang { get => Read(SourceLangKey); set => Write(SourceLangKey, value); }

        public static string TargetLang { get => Read(TargetLangKey); set => Write(TargetLangKey, value); }

        /// <summary>
        /// An API key typed into either dialog. Empty means "no override",
        /// and the key is then taken from Supervertaler for Trados, or failing
        /// that from what memoQ stored. Deliberately not seeded from the
        /// settings resource: seeding it would pin memoQ's copy here and stop
        /// the Trados key ever being picked up.
        /// </summary>
        public static string ApiKey { get => Read(ApiKeyKey); set => Write(ApiKeyKey, value); }

        /// <summary>
        /// A copy of whatever key memoQ holds in its settings resource, seeded so
        /// that the prompt editor, which cannot read that resource, resolves the
        /// same key the plugin does. It sits in its own slot rather than in
        /// <see cref="ApiKey"/> because it must not shadow the Trados key.
        /// </summary>
        public static string MemoQApiKey { get => Read(MemoQApiKeyKey); set => Write(MemoQApiKeyKey, value); }
        public static string PromptPathOr(string fromResource) => StringOr(PromptPathKey, fromResource);

        /// <summary>
        /// The inline instructions, or the value from the settings resource while
        /// the file has never been written. An empty file is a real value: the
        /// user is allowed to clear the box.
        /// </summary>
        public static string InstructionsOr(string fromResource)
        {
            lock (_lock)
            {
                try
                {
                    // Cached on the same terms as the key file. This is read once
                    // per segment on the translation path, and going to disk for
                    // it every time would put a file open behind every row of a
                    // ten-thousand-segment document.
                    var now = DateTime.UtcNow;
                    if (_instructions == null || now - _instructionsCheck >= CheckInterval)
                    {
                        _instructionsCheck = now;
                        var path = InstructionsPath;

                        if (!File.Exists(path))
                        {
                            _instructions = null;
                            return fromResource;
                        }

                        var stamp = File.GetLastWriteTimeUtc(path);
                        if (_instructions == null || stamp != _instructionsStamp)
                        {
                            _instructions = File.ReadAllText(path, Encoding.UTF8);
                            _instructionsStamp = stamp;
                        }
                    }

                    return _instructions ?? fromResource;
                }
                catch (Exception ex)
                {
                    Report("SharedSettings: instructions read failed", ex);
                    return fromResource;
                }
            }
        }

        private static string _instructions;
        private static DateTime _instructionsStamp;
        private static DateTime _instructionsCheck = DateTime.MinValue;

        public static void WriteInstructions(string value)
        {
            try
            {
                File.WriteAllText(InstructionsPath, value ?? string.Empty, new UTF8Encoding(false));
                lock (_lock)
                {
                    _instructions = null;
                    _instructionsCheck = DateTime.MinValue;
                }
            }
            catch (Exception ex)
            {
                Report("SharedSettings: instructions write failed", ex);
            }
        }

        /// <summary>
        /// Copies anything this file does not yet carry out of memoQ's settings
        /// resource. Called when memoQ hands the plugin its settings, so the
        /// prompt editor, which cannot see that resource, shows what is actually
        /// in force rather than its own defaults.
        ///
        /// Only ever fills gaps. A key already here is the user's, and a second
        /// call must not walk over it.
        /// </summary>
        public static void SeedIfUnset(
            string provider, string model, string endpoint, string promptPath,
            int parallel, int batchSize, bool useTerminology, bool useDocumentContext,
            bool bridgeMode, string instructions, string apiKey)
        {
            if (InHarness) return;

            SeedString(ProviderKey, provider);
            SeedString(ModelKey, model);
            SeedString(EndpointKey, endpoint);
            SeedString(PromptPathKey, promptPath);
            SeedString(ParallelKey, parallel.ToString(CultureInfo.InvariantCulture));
            SeedString(BatchSizeKey, batchSize.ToString(CultureInfo.InvariantCulture));
            SeedString(TerminologyContextKey, useTerminology ? "1" : "0");
            SeedString(DocumentContextKey, useDocumentContext ? "1" : "0");
            SeedString(BridgeModeKey, bridgeMode ? "1" : "0");

            // Only when memoQ actually has one. Writing an empty value would make
            // the slot look deliberately cleared and stop a later seed.
            if (!string.IsNullOrWhiteSpace(apiKey)) SeedString(MemoQApiKeyKey, apiKey);

            try
            {
                if (!File.Exists(InstructionsPath) && !string.IsNullOrEmpty(instructions))
                    WriteInstructions(instructions);
            }
            catch (Exception ex)
            {
                Report("SharedSettings: instructions seed failed", ex);
            }
        }

        private static void SeedString(string key, string value)
        {
            if (TryRead(key, out _)) return;
            Write(key, value ?? string.Empty);
        }

        // ── store ────────────────────────────────────────────────────────

        private static Dictionary<string, string> _map;
        private static DateTime _stamp;
        private static DateTime _lastCheck = DateTime.MinValue;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

        private static string StringOr(string key, string fromResource)
        {
            return TryRead(key, out var value) ? value : fromResource;
        }

        private static bool BoolOr(string key, bool fromResource)
        {
            return TryRead(key, out var value) ? value == "1" : fromResource;
        }

        private static int IntOr(string key, int fromResource)
        {
            return TryRead(key, out var value)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fromResource;
        }

        private static string Read(string key)
        {
            return TryRead(key, out var value) ? value : string.Empty;
        }

        /// <summary>
        /// False when the key has never been written, which is what separates
        /// "not set, defer to the settings resource" from a value the user has
        /// deliberately cleared.
        /// </summary>
        private static bool TryRead(string key, out string value)
        {
            lock (_lock)
            {
                value = string.Empty;
                try
                {
                    // Read on the translation path, so the file is parsed once and
                    // re-parsed only when it changes, and its timestamp is checked
                    // at most every few seconds. Everything downstream asks for
                    // several keys per segment.
                    var now = DateTime.UtcNow;
                    if (_map == null || now - _lastCheck >= CheckInterval)
                    {
                        _lastCheck = now;
                        var path = Path;

                        if (!File.Exists(path))
                        {
                            _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        }
                        else
                        {
                            var stamp = File.GetLastWriteTimeUtc(path);
                            if (_map == null || stamp != _stamp)
                            {
                                _map = Parse(File.ReadAllText(path, Encoding.UTF8));
                                _stamp = stamp;
                            }
                        }
                    }

                    return _map.TryGetValue(key, out value);
                }
                catch (Exception ex)
                {
                    Report("SharedSettings: read failed", ex);
                    value = string.Empty;
                    return false;
                }
            }
        }

        private static Dictionary<string, string> Parse(string contents)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in (contents ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return map;
        }

        private static void Write(string key, string value)
        {
            lock (_lock)
            {
                try
                {
                    var path = Path;
                    var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;

                    var sb = new StringBuilder();
                    var replaced = false;

                    foreach (var line in existing.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                    {
                        if (line.Length == 0) continue;

                        if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.Append(key).Append('=').Append(value ?? string.Empty).Append("\r\n");
                            replaced = true;
                        }
                        else sb.Append(line).Append("\r\n");
                    }

                    if (!replaced) sb.Append(key).Append('=').Append(value ?? string.Empty).Append("\r\n");

                    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

                    // Force the next read to re-parse rather than wait out the
                    // freshness interval: the writer expects to see its own value.
                    _map = null;
                    _lastCheck = DateTime.MinValue;
                }
                catch (Exception ex)
                {
                    Report("SharedSettings: write failed", ex);
                }
            }
        }
    }
}
