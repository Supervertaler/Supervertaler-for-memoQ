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
    /// <para>The answer is now one set of keys for every Supervertaler product,
    /// read and written through core's <see cref="ApiKeyStore"/>. A key pasted in
    /// Trados, in memoQ or in Sidekick is the same key everywhere. Before it
    /// existed there were three dialogs in three products each holding their own,
    /// which cost an hour to a key for another service pasted into the wrong
    /// box.</para>
    ///
    /// Order:
    ///
    /// 1. The shared keys, by provider (core's step-1 rule: the plain file when it
    ///    has the key, else the locked file).
    /// 2. <c>apikey</c> in memoQ's shared settings - a legacy override from
    ///    before the file existed. Migrated into the file on first use, then
    ///    never written again.
    /// 3. What memoQ stored in the MT settings resource, so an install that
    ///    predates all of this keeps working untouched.
    ///
    /// <para>A shared key this Windows account cannot unlock ends the search
    /// there. The two below it may hold an older key, which the provider would
    /// answer with "invalid key"; the translator is told the key cannot be read
    /// instead, in core's words, and no request is made
    /// (<see cref="ForRequest"/>).</para>
    ///
    /// <para>Step 1 of core's docs/design/api-key-protection.md: every key is
    /// also kept in a file locked by Windows to this Windows account, in the
    /// user's profile rather than the portable data folder. The plain file is
    /// still written exactly as before, so products not yet updated keep
    /// working; it is a plain-text copy that travels wherever the data folder
    /// does, until step 2 removes it. <c>apikey</c> and <c>apikey.memoq</c> in
    /// <c>shared.txt</c> are plain-text copies too, and go in the same
    /// step.</para>
    /// </summary>
    internal static class ApiKeys
    {
        internal struct Resolved
        {
            public string Key;
            public string Source;

            /// <summary>
            /// Core's sentence when a shared key is stored for this provider but this
            /// Windows account cannot unlock it - saying so, and what to do - to show
            /// instead of sending a request; null otherwise. A provider with no key
            /// at all is refused by <see cref="ForRequest"/>.
            /// </summary>
            public string Problem;

            public bool HasKey => !string.IsNullOrWhiteSpace(Key);
        }

        /// <summary>Where a memoQ user enters keys, for the sentences that send them there.</summary>
        internal const string SettingsPlace = "Supervertaler's translation settings";

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

            // Core sends Ollama no key at all, so there is nothing to look up.
            if (string.Equals(provider, LlmProviders.Ollama, StringComparison.OrdinalIgnoreCase))
                return new Resolved { Key = string.Empty, Source = "not needed for Ollama" };

            // Get answers null both for no key and for one this account cannot
            // unlock; only Status tells them apart. Asked first, so an unreadable
            // key never falls through to an older one below.
            var core = LlmProviders.CoreKey(provider);
            if (ApiKeyStore.Status(core) == ApiKeyStatus.Unreadable)
            {
                // Null only if a key was saved in the moment between the two
                // reads, which the lookup below then finds.
                var problem = ApiKeyStore.MissingKeyMessage(core, SettingsPlace);
                if (problem != null) return new Resolved { Key = string.Empty, Source = problem, Problem = problem };
            }

            var shared = ApiKeyStore.Get(core);
            if (!string.IsNullOrWhiteSpace(shared))
                return new Resolved { Key = shared.Trim(), Source = "the shared key file" };

            // A server of the translator's own gets its own key or none. The
            // fallbacks below hold whichever cloud provider's key was in use
            // before the shared file existed, and sending that to an address the
            // translator typed would hand an Anthropic or OpenAI key to whoever
            // runs it.
            if (LlmProviders.TakesEndpoint(provider))
                return new Resolved { Key = string.Empty, Source = "not set, which most local servers do not need" };

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
        /// The key for a request about to be sent. Throws, with the sentence memoQ
        /// shows under the grid, when there is no key this computer can use and the
        /// request would need one: a stored key Windows will not unlock here
        /// (<see cref="Resolved.Problem"/>), or none at all for a provider that
        /// cannot work without. A provider is never sent a blank key in place of
        /// one; it would answer as if the key were wrong. Asked where the client is
        /// made, after a staged or cached translation has had its turn - those need
        /// no key. Under a harness that refusal is what a cloud provider gets.
        /// </summary>
        public static string ForRequest(string provider, string fromResource)
        {
            var resolved = Resolve(provider, fromResource);
            if (resolved.Problem != null) throw new InvalidOperationException(resolved.Problem);
            if (!resolved.HasKey && LlmProviders.NeedsKey(provider))
                throw new InvalidOperationException("No API key is set for " + provider + ". Add one in " + SettingsPlace + ".");
            return resolved.Key;
        }

        /// <summary>
        /// The note beside the key box when this provider's shared key is stored
        /// but cannot be unlocked by this Windows account (core's wording); null
        /// otherwise. The box itself stays empty, and saving it empty keeps the
        /// stored key until a new one is pasted.
        /// </summary>
        public static string UnreadableNote(string provider)
        {
            if (SharedSettings.InHarness) return null;
            if (string.Equals(provider, LlmProviders.Ollama, StringComparison.OrdinalIgnoreCase)) return null;
            var core = LlmProviders.CoreKey(provider);
            return core == null ? null : ApiKeyStore.UnreadableKeyNote(core);
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

            // Nothing to keep: Ollama is never sent a key.
            if (string.Equals(provider, LlmProviders.Ollama, StringComparison.OrdinalIgnoreCase)) return true;

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

        // -- once per process: migration, then the locked copy --------------

        private static readonly object MigrateGate = new object();
        private static bool _migrated;
        private static string _syncNotice;

        /// <summary>
        /// Fills the shared file, once per process, from wherever this user's keys
        /// used to live: memoQ's own settings, and Supervertaler for Trados's
        /// <c>settings.json</c>, which was the accidental shared store before there
        /// was a real one. Never overwrites - the shared keys win the moment they
        /// hold one for a provider, readable on this computer or not. A key
        /// Windows will not unlock here may well be newer than these copies, and
        /// replacing it with one of them would hand the provider an older key.
        ///
        /// <para>The Trados half is migration only; it is no longer a place keys
        /// are read from. Without it, dropping that read would have stopped memoQ
        /// working for anyone whose keys are still only there.</para>
        ///
        /// <para>Then core's start-up sync (<see cref="ApiKeyStore.SyncLocked"/>)
        /// copies every key in the plain file into the locked file, adding and
        /// replacing, never removing. Done on every computer during step 1, it is
        /// what lets step 2 remove the plain file without anyone retyping a
        /// key.</para>
        ///
        /// <para>Runs inside <see cref="Resolve"/> rather than at startup because
        /// two processes resolve keys - the plugin and the prompt editor - and
        /// neither has one obvious place to call it from.</para>
        /// </summary>
        private static void EnsureMigrated()
        {
            // Never in a harness: this reads the user's own settings and writes the
            // shared key files, and the locked one is in their profile, which no
            // redirected data folder moves.
            if (SharedSettings.InHarness) return;

            lock (MigrateGate)
            {
                if (_migrated) return;
                _migrated = true;

                try
                {
                    // The cloud providers only. Trados keeps a key for its own
                    // OpenAI-compatible endpoints, and copying it here would send
                    // it to whatever server memoQ is pointed at, which need not be
                    // the one it was issued for.
                    foreach (var provider in LlmProviders.All)
                    {
                        if (!LlmProviders.NeedsKey(provider)) continue;
                        var core = LlmProviders.CoreKey(provider);
                        if (ApiKeyStore.Status(core) != ApiKeyStatus.Absent) continue;

                        var trados = FromTrados(provider);
                        if (!string.IsNullOrWhiteSpace(trados)) ApiKeyStore.Set(core, trados.Trim());
                    }

                    // memoQ's own two are provider-less: whatever they hold belongs
                    // to the provider currently selected, and to no other. They
                    // predate the local providers, so a local provider being the
                    // current one says nothing about whose key they are.
                    var current = SharedSettings.ProviderOr(LlmProviders.Anthropic);
                    var currentCore = LlmProviders.NeedsKey(current) ? LlmProviders.CoreKey(current) : null;

                    if (currentCore != null && ApiKeyStore.Status(currentCore) == ApiKeyStatus.Absent)
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

                // Never throws. Core has already logged a failure and what was set
                // aside; nothing was removed, and the next start tries again.
                var sync = ApiKeyStore.SyncLocked();
                if (sync.Locked > 0 || sync.Failed)
                    ApiKeyStore.Log?.Invoke("API keys: " + sync.Locked + " locked to this computer"
                        + (sync.Failed ? "; not all could be, the next start tries again" : ""));
                _syncNotice = sync.Notice;
            }
        }

        /// <summary>
        /// What the start-up sync has to tell the translator - that a damaged
        /// locked key file was set aside (core's wording) - once, for the settings
        /// window's status line; null when there is nothing, or it was already
        /// taken. Already in the log either way.
        /// </summary>
        public static string TakeSyncNotice()
        {
            EnsureMigrated();
            lock (MigrateGate)
            {
                var notice = _syncNotice;
                _syncNotice = null;
                return notice;
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
