using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Which API key this plugin actually uses, and where it came from.
    ///
    /// Three places, in order:
    ///
    /// 1. <c>apikey</c> in the shared settings file. A key typed into either
    ///    dialog lands here, so what the user last typed always wins. Clearing
    ///    the box removes it and the next source takes over.
    /// 2. Supervertaler for Trados, which already keeps its keys per provider in
    ///    plain JSON in the shared data folder. A translator running both
    ///    products rotates a key once, in one file, and both pick it up.
    /// 3. What memoQ stored in the MT settings resource, so an install that
    ///    predates all of this keeps working untouched.
    ///
    /// The keys are on disk in clear text, which is the same posture Supervertaler
    /// for Trados has always had. It is a deliberate choice: a key that can be
    /// replaced by pasting a line into a text file is a key that actually gets
    /// rotated, and anyone who can read that file can already read everything else
    /// in the user's profile.
    /// </summary>
    internal static class ApiKeys
    {
        internal struct Resolved
        {
            public string Key;
            public string Source;

            public bool HasKey => !string.IsNullOrWhiteSpace(Key);
        }

        /// <summary>
        /// <paramref name="fromResource"/> is what memoQ handed us in the MT
        /// settings resource, which is only consulted last.
        /// </summary>
        public static Resolved Resolve(string provider, string fromResource)
        {
            // A harness must never reach the user's real key: its assertions are
            // written around there being none, and the calls it would then make
            // are charged to them.
            if (SharedSettings.InHarness)
                return new Resolved { Key = string.Empty, Source = "suppressed for a test run" };

            var own = SharedSettings.ApiKey;
            if (!string.IsNullOrWhiteSpace(own))
                return new Resolved { Key = own.Trim(), Source = "set in Supervertaler" };

            return Fallback(provider, fromResource);
        }

        /// <summary>
        /// What would be used if no key were typed in. A dialog compares the box
        /// against this before recording an override, so that showing the Trados
        /// key and pressing OK does not silently copy it and stop that file being
        /// the one place to rotate it.
        /// </summary>
        public static Resolved Fallback(string provider, string fromResource)
        {
            var trados = FromTrados(provider);
            if (!string.IsNullOrWhiteSpace(trados))
                return new Resolved { Key = trados.Trim(), Source = "shared with Supervertaler for Trados" };

            // The editor cannot read memoQ's resource, so it passes null and
            // falls back to the copy seeded into the shared file instead.
            var stored = string.IsNullOrWhiteSpace(fromResource) ? SharedSettings.MemoQApiKey : fromResource;
            if (!string.IsNullOrWhiteSpace(stored))
                return new Resolved { Key = stored.Trim(), Source = "stored in memoQ's MT settings" };

            return new Resolved { Key = string.Empty, Source = "not set" };
        }

        // ── the Trados key store ─────────────────────────────────────────

        internal static string TradosSettingsPath => System.IO.Path.Combine(
            global::Supervertaler.Core.SupervertalerPaths.Root, "trados", "settings", "settings.json");

        /// <summary>
        /// The Trados key for a provider, or empty. Its file is large and this is
        /// asked on the translation path, so it is parsed once and re-parsed only
        /// when the file changes.
        /// </summary>
        public static string FromTrados(string provider)
        {
            var keys = Load();
            if (keys == null) return string.Empty;

            string slug;
            switch (provider)
            {
                case LlmProviders.OpenAI: slug = "openai"; break;
                case LlmProviders.Google: slug = "gemini"; break;
                case LlmProviders.Anthropic:
                default: slug = "claude"; break;
            }

            return keys.TryGetValue(slug, out var key) ? key ?? string.Empty : string.Empty;
        }

        private static readonly object _lock = new object();
        private static Dictionary<string, string> _cache;
        private static DateTime _stamp;
        private static DateTime _lastCheck = DateTime.MinValue;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Reads just the three values we care about out of a file another product
        /// owns.
        ///
        /// Deliberately not a data contract. DataContractJsonSerializer walks the
        /// document in the order its members are declared and quietly yields
        /// nothing when a real file does not match, which is exactly what it did
        /// here: every key came back empty against a file that plainly had them.
        /// JsonReaderWriterFactory turns the JSON into an XML tree instead, so the
        /// three lookups are order-independent and indifferent to the several
        /// dozen other settings around them.
        /// </summary>
        private static Dictionary<string, string> Load()
        {
            lock (_lock)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if (_cache != null && now - _lastCheck < CheckInterval) return _cache;
                    _lastCheck = now;

                    var path = TradosSettingsPath;
                    if (!File.Exists(path)) { _cache = null; return null; }

                    var stamp = File.GetLastWriteTimeUtc(path);
                    if (_cache != null && stamp == _stamp) return _cache;

                    // ReadAllText consumes the byte order mark the file carries;
                    // the JSON reader would treat it as an unexpected character.
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    var bytes = new UTF8Encoding(false).GetBytes(json);

                    using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, XmlDictionaryReaderQuotas.Max))
                    {
                        var root = XDocument.Load(reader).Root;
                        var keys = root?.Element("aiSettings")?.Element("apiKeys");

                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (keys != null)
                            foreach (var entry in keys.Elements())
                                map[entry.Name.LocalName] = entry.Value;

                        _cache = map;
                        _stamp = stamp;
                        return _cache;
                    }
                }
                catch (Exception ex)
                {
                    SharedSettings.ReportError("ApiKeys: could not read the Trados key store", ex);
                    _cache = null;
                    return null;
                }
            }
        }
    }
}
