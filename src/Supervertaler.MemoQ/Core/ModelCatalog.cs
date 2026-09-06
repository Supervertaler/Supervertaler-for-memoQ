using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Supervertaler.Core;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// What the model dropdown offers: a short list by default, the provider's
    /// whole inventory on request.
    ///
    /// The short list is <see cref="LlmModels"/> in core - three to five models
    /// per provider, each with a one-line verdict, re-judged at release time and
    /// shared with Supervertaler for Trados so the judgement is made once. It is
    /// the default because the people using this are in a hurry: a dropdown of
    /// forty ids, half of them dated snapshots, is a list nobody can choose from.
    ///
    /// The inventory is the provider's own /models list, fetched on demand and
    /// cached to disk so it survives a restart. It is shown only when the user
    /// ticks "Show all models", and it exists for the case the short list cannot
    /// serve: a model released after this build.
    ///
    /// Nothing here is the last word. The dropdown stays typeable, because a
    /// gateway or a local model appears in neither list.
    /// </summary>
    internal static class ModelCatalog
    {
        internal sealed class Entry
        {
            public string Id;
            public string DisplayName;

            /// <summary>
            /// The one-line verdict. Empty for a fetched model: the short list is
            /// where a verdict comes from, and forty blank ones say nothing.
            /// </summary>
            public string Description;

            /// <summary>
            /// What the dropdown shows: the name and its verdict, or just the name
            /// when there is none, or the id when there is not even that.
            /// </summary>
            public override string ToString()
            {
                var name = string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
                return string.IsNullOrWhiteSpace(Description) ? name : name + "  –  " + Description;
            }
        }

        /// <summary>True when this provider publishes a list we know how to read.</summary>
        public static bool CanFetch(string provider)
        {
            var key = LlmProviders.CoreKey(provider);
            return key != null && LlmModelCatalog.CanFetch(key);
        }

        /// <summary>The short list: what to show when "Show all models" is off.</summary>
        public static List<Entry> Curated(string provider)
        {
            var key = LlmProviders.CoreKey(provider);
            if (key == null) return new List<Entry>();

            return (LlmModels.GetModelsForProvider(key) ?? new LlmModelInfo[0])
                .Select(m => new Entry { Id = m.Id, DisplayName = m.DisplayName, Description = m.Description })
                .ToList();
        }

        /// <summary>
        /// What the dropdown shows. Off: the short list. On: the short list
        /// followed by everything the last fetch added - the order matters, since
        /// the recommended few must still be the first thing read.
        ///
        /// Never blocks and never goes to the network, so a dialog can call it
        /// while it is being built.
        /// </summary>
        public static List<Entry> Entries(string provider, bool showAll)
        {
            var entries = Curated(provider);
            if (!showAll) return entries;

            var known = new HashSet<string>(entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            var extras = Fetched(provider)
                .Where(e => !string.IsNullOrWhiteSpace(e.Id) && known.Add(e.Id))
                .ToList();

            if (extras.Count == 0) return entries;

            // Several ids can share one display name - Google returns three models
            // called "Nano Banana Pro". Two identical rows in a dropdown is a coin
            // toss, so those get their id appended and the rest stay readable.
            var ambiguous = new HashSet<string>(
                entries.Concat(extras)
                    .GroupBy(e => e.DisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);

            foreach (var e in extras)
            {
                if (!ambiguous.Contains(e.DisplayName ?? string.Empty)) continue;
                if (string.Equals(e.DisplayName, e.Id, StringComparison.OrdinalIgnoreCase)) continue;
                e.DisplayName = e.DisplayName + " (" + e.Id + ")";
            }

            entries.AddRange(extras);
            return entries;
        }

        /// <summary>How many fetched models this provider has beyond the short list.</summary>
        public static int ExtraCount(string provider)
        {
            var known = new HashSet<string>(Curated(provider).Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            return Fetched(provider).Count(e => !string.IsNullOrWhiteSpace(e.Id) && !known.Contains(e.Id));
        }

        /// <summary>
        /// Asks the provider for its list and caches the answer. Returns null when
        /// there is nothing to ask with or nobody to ask - no key, or a provider
        /// with no list endpoint - so a caller can treat null as "keep what you
        /// have". Throws on a failed request, with a message fit for a status line.
        /// </summary>
        public static async Task<List<Entry>> FetchAsync(
            string provider, string apiKey, string endpoint, CancellationToken ct)
        {
            var key = LlmProviders.CoreKey(provider);
            if (key == null || !LlmModelCatalog.CanFetch(key)) return null;
            if (string.IsNullOrWhiteSpace(apiKey)) return null;

            var fetched = await LlmModelCatalog
                .FetchAsync(key, apiKey, endpoint, ct)
                .ConfigureAwait(false);

            var entries = (fetched ?? new List<LlmModelCatalog.FetchedModel>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new Entry { Id = m.Id, DisplayName = m.DisplayName })
                .ToList();

            Store(provider, entries);
            return entries;
        }

        // -- the fetched-list cache ---------------------------------------

        private static string CacheFile(string provider)
        {
            var dir = Path.Combine(SharedSettings.Directory, "models");
            Directory.CreateDirectory(dir);

            var safe = string.Concat((provider ?? "unknown")
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

            return Path.Combine(dir, safe.ToLowerInvariant() + ".txt");
        }

        /// <summary>
        /// The last fetch for this provider, or an empty list. A corrupt or
        /// half-written file degrades to a shorter list rather than an exception
        /// inside a form's constructor.
        /// </summary>
        public static List<Entry> Fetched(string provider)
        {
            try
            {
                var path = CacheFile(provider);
                if (File.Exists(path)) return Parse(File.ReadAllLines(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                SharedSettings.ReportError("ModelCatalog: could not read the cache", ex);
            }

            return new List<Entry>();
        }

        /// <summary>The date of the last fetch for this provider, or null.</summary>
        public static string FetchedOn(string provider)
        {
            try
            {
                var path = CacheFile(provider);
                if (File.Exists(path)) return File.GetLastWriteTime(path).ToString("yyyy-MM-dd");
            }
            catch
            {
                // A date on a status line is not worth an error.
            }

            return null;
        }

        private static void Store(string provider, IEnumerable<Entry> entries)
        {
            try
            {
                File.WriteAllLines(CacheFile(provider),
                    entries.Select(e => e.Id + "\t" + (e.DisplayName ?? string.Empty)),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // The list is already on screen; failing to remember it for next
                // time is not worth interrupting anyone over.
                SharedSettings.ReportError("ModelCatalog: could not write the cache", ex);
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
    }
}
