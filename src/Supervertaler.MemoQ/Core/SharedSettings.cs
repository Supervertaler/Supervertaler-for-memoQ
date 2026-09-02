using System;
using System.IO;
using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The handful of settings both directors need to agree on, kept outside
    /// memoQ's per-plugin settings storage.
    ///
    /// Necessary because the two SDKs persist settings quite differently. An MT
    /// plugin round-trips a <c>PluginSettings</c> blob through memoQ, scoped to an
    /// *MT settings resource*; a TB plugin gets no such thing — it has
    /// <c>ShowOptionsForm</c> and is expected to persist its own state. So a
    /// setting they must share, like the glossary path, has nowhere to live in
    /// either scheme.
    ///
    /// A single file at
    /// <c>%LocalAppData%\Supervertaler.memoQ\shared.txt</c> keeps it simple and
    /// means the glossary can be set from either dialog and is picked up by both.
    ///
    /// Deliberately not JSON: two keys do not justify a serializer, and a file a
    /// user can open in Notepad and fix is a feature for a plugin whose failure
    /// mode is otherwise invisible.
    /// </summary>
    internal static class SharedSettings
    {
        private static readonly object _lock = new object();

        /// <summary>
        /// Where failures are reported. The plugin points this at its log; the
        /// prompt editor compiles this same file and has no plugin log, so it
        /// leaves it silent. The alternative was a second implementation of the
        /// file format in the editor, and two readers of one file that can drift
        /// apart is exactly what this class exists to prevent.
        /// </summary>
        internal static Action<string, Exception> ErrorSink = (message, ex) => { };

        private static void Report(string message, Exception ex)
        {
            var sink = ErrorSink;
            if (sink == null) return;
            try { sink(message, ex); } catch { }
        }

        private const string GlossaryKey = "glossary";
        private const string BridgeModeKey = "bridgemode";

        internal static string Path
        {
            get
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Supervertaler.memoQ");
                Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "shared.txt");
            }
        }

        /// <summary>
        /// Path to the tab-separated glossary, or empty. Read on the translation
        /// path, so it is cached and only re-read when the file changes.
        /// </summary>
        public static string GlossaryPath
        {
            get => Read(GlossaryKey);
            set => Write(GlossaryKey, value);
        }

        /// <summary>
        /// Whether Pre-translate hands the segments to Claude Desktop instead of
        /// calling the model. Shared rather than kept in memoQ's MT settings
        /// resource because it says how the translator is working right now, not
        /// anything about a particular project, and because it has to be
        /// reachable without opening memoQ's dialogs.
        /// </summary>
        public static bool BridgeMode
        {
            get => Read(BridgeModeKey) == "1";
            set => Write(BridgeModeKey, value ? "1" : "0");
        }

        /// <summary>
        /// The shared value once it has ever been set, otherwise the value passed
        /// in. This is the migration: the switch used to live in the MT settings
        /// resource, and a user who had it on there keeps it on until the first
        /// time either dialog writes the shared file. No copying, no flag to
        /// leak, and nothing to undo once every install has moved across.
        /// </summary>
        public static bool BridgeModeOr(bool fromResource)
        {
            var raw = Read(BridgeModeKey);
            return raw.Length == 0 ? fromResource : raw == "1";
        }

        private static string _cache;
        private static DateTime _cacheStamp;
        private static DateTime _lastCheck = DateTime.MinValue;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

        private static string Read(string key)
        {
            lock (_lock)
            {
                try
                {
                    var path = Path;
                    var now = DateTime.UtcNow;

                    if (_cache != null && now - _lastCheck < CheckInterval) return Extract(_cache, key);
                    _lastCheck = now;

                    if (!File.Exists(path)) { _cache = string.Empty; return string.Empty; }

                    var stamp = File.GetLastWriteTimeUtc(path);
                    if (_cache != null && stamp == _cacheStamp) return Extract(_cache, key);

                    _cache = File.ReadAllText(path, Encoding.UTF8);
                    _cacheStamp = stamp;
                    return Extract(_cache, key);
                }
                catch (Exception ex)
                {
                    Report("SharedSettings: read failed", ex);
                    return string.Empty;
                }
            }
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
                    _cache = null;
                }
                catch (Exception ex)
                {
                    Report("SharedSettings: write failed", ex);
                }
            }
        }

        private static string Extract(string contents, string key)
        {
            foreach (var line in contents.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;
                return line.Substring(key.Length + 1).Trim();
            }
            return string.Empty;
        }
    }
}
