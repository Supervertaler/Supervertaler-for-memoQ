using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Supervertaler.Core;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Which API key this plugin actually uses, and where it came from.
    ///
    /// <para>The answer is now one file for every Supervertaler product:
    /// <c>&lt;data root&gt;\settings\api-keys.json</c>, read through core's
    /// <see cref="ApiKeyStore"/>. A key pasted in Trados, in memoQ or in Sidekick
    /// is the same key everywhere, and rotating it means editing one line in one
    /// text file. Before it existed there were three dialogs in three products
    /// each holding their own, which cost an hour to a key for another service
    /// pasted into the wrong box.</para>
    ///
    /// Order:
    ///
    /// 1. The shared key file, by provider.
    /// 2. <c>apikey</c> in memoQ's shared settings - a legacy override from
    ///    before the file existed. Migrated into the file on first use, then
    ///    never written again.
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
            // are charged to them. This also keeps the migration below away from
            // the real key file.
            if (SharedSettings.InHarness)
                return new Resolved { Key = string.Empty, Source = "suppressed for a test run" };

            EnsureMigrated();

            var shared = ApiKeyStore.Get(LlmProviders.CoreKey(provider));
            if (!string.IsNullOrWhiteSpace(shared))
                return new Resolved { Key = shared.Trim(), Source = "the shared key file" };

            return Fallback(provider, fromResource);
        }

        /// <summary>
        /// What would be used if the shared file had nothing for this provider.
        /// Both of these are pre-file leftovers kept so that an existing install
        /// never stops working; neither is written to any more.
        /// </summary>
        public static Resolved Fallback(string provider, string fromResource)
        {
            var own = SharedSettings.ApiKey;
            if (!string.IsNullOrWhiteSpace(own))
                return new Resolved { Key = own.Trim(), Source = "set in Supervertaler" };

            // The editor cannot read memoQ's resource, so it passes null and
            // falls back to the copy seeded into the shared file instead.
            var stored = string.IsNullOrWhiteSpace(fromResource) ? SharedSettings.MemoQApiKey : fromResource;
            if (!string.IsNullOrWhiteSpace(stored))
                return new Resolved { Key = stored.Trim(), Source = "stored in memoQ's MT settings" };

            return new Resolved { Key = string.Empty, Source = "not set" };
        }

        /// <summary>
        /// Records a key for a provider in the shared file, where every product
        /// reads it. Empty removes it. Returns false when the file could not be
        /// written, which a dialog says rather than swallowing: the user would
        /// otherwise close it believing a key was saved.
        /// </summary>
        public static bool Remember(string provider, string key)
        {
            if (SharedSettings.InHarness) return true;

            var core = LlmProviders.CoreKey(provider);
            if (core == null) return false;

            return ApiKeyStore.Set(core, (key ?? string.Empty).Trim());
        }

        /// <summary>
        /// A sentence when the key plainly belongs to another service, else null -
        /// shown under the key box as it is typed. An OpenAI key in the Anthropic
        /// box is a mistake worth catching before the 401 does, because the 401
        /// says the key is incorrect rather than that it is the wrong one.
        /// </summary>
        public static string CheckShape(string provider, string key)
        {
            return ApiKeyStore.CheckShape(LlmProviders.CoreKey(provider), key);
        }

        // -- one-time migration into the shared file ----------------------

        private static readonly object MigrateGate = new object();
        private static bool _migrated;

        /// <summary>
        /// Fills the shared file, once per process, from wherever this user's keys
        /// used to live: memoQ's own settings, and Supervertaler for Trados's
        /// <c>settings.json</c>, which was the accidental shared store before there
        /// was a real one. Never overwrites - the file wins the moment it holds a
        /// key for a provider.
        ///
        /// <para>The Trados half is migration only; it is no longer a place keys
        /// are read from. Without it, dropping that read would have stopped memoQ
        /// working for anyone whose keys are still only there.</para>
        ///
        /// <para>Runs inside <see cref="Resolve"/> rather than at startup because
        /// two processes resolve keys - the plugin and the prompt editor - and
        /// neither has one obvious place to call it from.</para>
        /// </summary>
        private static void EnsureMigrated()
        {
            lock (MigrateGate)
            {
                if (_migrated) return;
                _migrated = true;

                try
                {
                    foreach (var provider in LlmProviders.All)
                    {
                        var core = LlmProviders.CoreKey(provider);
                        if (core == null) continue;
                        if (!string.IsNullOrWhiteSpace(ApiKeyStore.Get(core))) continue;

                        var trados = FromTrados(provider);
                        if (!string.IsNullOrWhiteSpace(trados)) ApiKeyStore.Set(core, trados.Trim());
                    }

                    // memoQ's own two are provider-less: whatever they hold belongs
                    // to the provider currently selected, and to no other.
                    var current = SharedSettings.ProviderOr(LlmProviders.Anthropic);
                    var currentCore = LlmProviders.CoreKey(current);

                    if (currentCore != null && string.IsNullOrWhiteSpace(ApiKeyStore.Get(currentCore)))
                    {
                        var own = SharedSettings.ApiKey;
                        var stored = string.IsNullOrWhiteSpace(own) ? SharedSettings.MemoQApiKey : own;
                        if (!string.IsNullOrWhiteSpace(stored)) ApiKeyStore.Set(currentCore, stored.Trim());
                    }
                }
                catch (Exception ex)
                {
                    // Nothing here is required for translation to work: the
                    // fallbacks below the file are still in place.
                    SharedSettings.ReportError("ApiKeys: could not migrate keys into the shared file", ex);
                }
            }
        }

        // -- the Trados key store, for migration only ---------------------

        internal static string TradosSettingsPath => Path.Combine(
            SupervertalerPaths.Root, "trados", "settings", "settings.json");

        /// <summary>
        /// The Trados key for a provider, or empty. Read once when the shared file
        /// is first filled, and not on the translation path any more.
        /// </summary>
        public static string FromTrados(string provider)
        {
            var keys = Load();
            if (keys == null) return string.Empty;

            var slug = LlmProviders.CoreKey(provider);
            if (slug == null) return string.Empty;

            return keys.TryGetValue(slug, out var key) ? key ?? string.Empty : string.Empty;
        }

        /// <summary>
        /// Reads the key block out of a file another product owns.
        ///
        /// Deliberately not a data contract. DataContractJsonSerializer walks the
        /// document in the order its members are declared and quietly yields
        /// nothing when a real file does not match, which is exactly what it did
        /// here: every key came back empty against a file that plainly had them.
        /// JsonReaderWriterFactory turns the JSON into an XML tree instead, so the
        /// lookups are order-independent and indifferent to the several dozen
        /// other settings around them.
        /// </summary>
        private static Dictionary<string, string> Load()
        {
            try
            {
                var path = TradosSettingsPath;
                if (!File.Exists(path)) return null;

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

                    return map;
                }
            }
            catch (Exception ex)
            {
                SharedSettings.ReportError("ApiKeys: could not read the Trados key store", ex);
                return null;
            }
        }
    }
}
