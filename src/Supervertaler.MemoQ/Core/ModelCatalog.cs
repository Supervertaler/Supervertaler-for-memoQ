using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The models a provider will actually accept, fetched from the provider
    /// rather than kept in a list here.
    ///
    /// A hand-curated list is a treadmill: it goes stale between releases, and
    /// keeping it current was one of the chores that made the previous
    /// Supervertaler generation tiring to maintain. Anthropic, OpenAI and their
    /// kind all expose a models endpoint, so the dropdown can populate itself and
    /// a model released tomorrow appears without a release from us.
    ///
    /// The field it fills stays editable. A gateway, a local model or anything the
    /// endpoint does not list must still be typeable.
    /// </summary>
    internal static class ModelCatalog
    {
        internal sealed class Entry
        {
            public string Id;
            public string DisplayName;

            /// <summary>What the dropdown shows: the human name, falling back to the id.</summary>
            public override string ToString() =>
                string.IsNullOrWhiteSpace(DisplayName) || string.Equals(DisplayName, Id, StringComparison.OrdinalIgnoreCase)
                    ? Id
                    : DisplayName + "   (" + Id + ")";
        }

        /// <summary>
        /// A short list per provider for when there is no key, no network, or the
        /// endpoint refuses. Deliberately tiny: it is a fallback, not a catalogue,
        /// and the moment it pretends to be a catalogue it starts going stale.
        /// </summary>
        private static readonly Dictionary<string, string[]> Fallback =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { LlmProviders.Anthropic, new[] { "claude-opus-5", "claude-sonnet-5" } },
                { LlmProviders.OpenAI, new[] { "gpt-5.4" } },
                { LlmProviders.Google, new[] { "gemini-3.5-pro" } }
            };

        private static string CacheFile(string provider)
        {
            var dir = Path.Combine(SharedSettings.Directory, "models");
            Directory.CreateDirectory(dir);

            var safe = string.Concat((provider ?? "unknown")
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

            return Path.Combine(dir, safe.ToLowerInvariant() + ".txt");
        }

        private static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);

        /// <summary>
        /// What to show immediately: yesterday's answer if we have one, otherwise
        /// the fallback. Never blocks and never goes to the network, so a dialog
        /// can call it while it is being built.
        /// </summary>
        public static List<Entry> Cached(string provider)
        {
            try
            {
                var path = CacheFile(provider);
                if (File.Exists(path))
                {
                    var entries = Parse(File.ReadAllLines(path, Encoding.UTF8));
                    if (entries.Count > 0) return entries;
                }
            }
            catch (Exception ex)
            {
                SharedSettings.ReportError("ModelCatalog: could not read the cache", ex);
            }

            return Fallback.TryGetValue(provider ?? string.Empty, out var ids)
                ? ids.Select(id => new Entry { Id = id }).ToList()
                : new List<Entry>();
        }

        private static bool IsFresh(string provider)
        {
            try
            {
                var path = CacheFile(provider);
                return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheFor;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Asks the provider, and writes the answer to the cache. Returns null when
        /// there is nothing new to show — no key, a fresh cache, an unsupported
        /// provider, or a failure. Callers treat null as "keep what you have".
        /// </summary>
        public static async Task<List<Entry>> RefreshAsync(
            string provider, string apiKey, string endpoint, bool force, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return null;
            if (!force && IsFresh(provider)) return null;

            try
            {
                List<Entry> entries;

                if (string.Equals(provider, LlmProviders.Anthropic, StringComparison.OrdinalIgnoreCase))
                    entries = await FetchAnthropicAsync(apiKey, endpoint, ct).ConfigureAwait(false);
                else if (string.Equals(provider, LlmProviders.OpenAI, StringComparison.OrdinalIgnoreCase))
                    entries = await FetchOpenAiAsync(apiKey, endpoint, ct).ConfigureAwait(false);
                else
                    return null;   // Google's list endpoint differs; typing still works.

                if (entries == null || entries.Count == 0) return null;

                File.WriteAllLines(CacheFile(provider),
                    entries.Select(e => e.Id + "\t" + (e.DisplayName ?? string.Empty)),
                    new UTF8Encoding(false));

                return entries;
            }
            catch (Exception ex)
            {
                // Never surfaced as an error: the field is typeable and the cache or
                // the fallback is already on screen. A provider being unreachable
                // must not stop anyone editing their settings.
                SharedSettings.ReportError("ModelCatalog: could not list models for " + provider, ex);
                return null;
            }
        }

        private static List<Entry> Parse(IEnumerable<string> lines)
        {
            var entries = new List<Entry>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts[0].Trim().Length == 0) continue;

                entries.Add(new Entry
                {
                    Id = parts[0].Trim(),
                    DisplayName = parts.Length > 1 ? parts[1].Trim() : null
                });
            }
            return entries;
        }

        // ── providers ────────────────────────────────────────────────────

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        /// <summary>
        /// GET /v1/models. Paginates with <c>after_id</c> and carries a
        /// <c>display_name</c> per model, which is what makes a readable dropdown
        /// possible without naming anything ourselves.
        /// </summary>
        private static async Task<List<Entry>> FetchAnthropicAsync(string apiKey, string endpoint, CancellationToken ct)
        {
            var root = string.IsNullOrWhiteSpace(endpoint)
                ? "https://api.anthropic.com"
                : endpoint.TrimEnd('/');

            var entries = new List<Entry>();
            string after = null;

            // Bounded: a runaway cursor must not loop forever inside a dialog.
            for (var page = 0; page < 10; page++)
            {
                var url = root + "/v1/models?limit=100" + (after == null ? "" : "&after_id=" + Uri.EscapeDataString(after));

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Add("x-api-key", apiKey);
                    request.Headers.Add("anthropic-version", "2023-06-01");

                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) break;

                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var doc = ToXml(body);
                        if (doc == null) break;

                        var items = doc.Elements("data").Elements("item").ToList();
                        foreach (var item in items)
                        {
                            var id = (string)item.Element("id");
                            if (string.IsNullOrWhiteSpace(id)) continue;

                            entries.Add(new Entry { Id = id, DisplayName = (string)item.Element("display_name") });
                        }

                        var more = (string)doc.Element("has_more");
                        after = (string)doc.Element("last_id");

                        if (!string.Equals(more, "true", StringComparison.OrdinalIgnoreCase)
                            || string.IsNullOrWhiteSpace(after)) break;
                    }
                }
            }

            return entries;
        }

        /// <summary>
        /// GET /v1/models, which returns everything the account can reach —
        /// embeddings, audio, image models included. Filtered to what could
        /// plausibly translate text, because an unfiltered list is the "millions of
        /// names" problem the dropdown exists to avoid.
        /// </summary>
        private static async Task<List<Entry>> FetchOpenAiAsync(string apiKey, string endpoint, CancellationToken ct)
        {
            var root = string.IsNullOrWhiteSpace(endpoint)
                ? "https://api.openai.com"
                : endpoint.TrimEnd('/');

            using (var request = new HttpRequestMessage(HttpMethod.Get, root + "/v1/models"))
            {
                request.Headers.Add("Authorization", "Bearer " + apiKey);

                using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return null;

                    var doc = ToXml(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (doc == null) return null;

                    var skip = new[] { "embedding", "whisper", "tts", "dall-e", "moderation", "audio", "image", "realtime", "transcribe" };

                    return doc.Elements("data").Elements("item")
                        .Select(i => (string)i.Element("id"))
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Where(id => !skip.Any(s => id.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                        .OrderByDescending(id => id, StringComparer.OrdinalIgnoreCase)
                        .Select(id => new Entry { Id = id })
                        .ToList();
                }
            }
        }

        /// <summary>
        /// JSON through the framework's own reader rather than a contract class:
        /// the shapes belong to other people and a contract that walks members in
        /// declaration order returns nothing when a real response does not match.
        /// That already cost an afternoon once, reading the Trados key store.
        /// </summary>
        private static XElement ToXml(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            using (var reader = JsonReaderWriterFactory.CreateJsonReader(
                new UTF8Encoding(false).GetBytes(json), XmlDictionaryReaderQuotas.Max))
            {
                return XDocument.Load(reader).Root;
            }
        }
    }
}
