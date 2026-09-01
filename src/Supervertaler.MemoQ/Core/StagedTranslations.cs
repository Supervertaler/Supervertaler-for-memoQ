using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Translations staged from outside — by Claude, through the MCP bridge —
    /// waiting for memoQ to ask for them.
    ///
    /// This is the one write channel into memoQ's grid that the SDK leaves open.
    /// A plugin cannot put text into a segment; it can only answer when memoQ
    /// asks. So the collaboration inverts: Claude stages translations here, the
    /// user runs Pre-translate (or lands on a segment), and memoQ receives
    /// Claude's text through the ordinary MT lookup. Every write goes through
    /// the user's hands, which is not a limitation so much as a review step.
    ///
    /// Checked BEFORE the cache and the LLM on the translate path: a staged
    /// translation exists because someone more informed than a fresh LLM call
    /// already decided what this segment should say. Costs nothing when empty —
    /// one lock and a dictionary miss.
    ///
    /// Keyed on normalised source text + language pair, not on segment numbers:
    /// memoQ never tells a plugin which row it is asking for, so text is the
    /// only join there is.
    /// </summary>
    internal static class StagedTranslations
    {
        internal sealed class Entry
        {
            public string Source;      // tagged source text as staged
            public string Target;      // tagged target text
            public string Label;       // who staged it, e.g. "Claude"
            public string LangPair;
            public DateTime StagedUtc;
            public int TimesServed;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>Far beyond any real document; a bound, not a budget.</summary>
        private const int MaxEntries = 20000;

        private static string KeyOf(string source, string langPair)
        {
            // Whitespace-normalised so a trailing space in the editor does not
            // orphan a staged translation; case preserved because case is text.
            var text = (source ?? "").Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return langPair + "\u001F" + text;
        }

        /// <summary>Stage a batch. Returns how many were accepted.</summary>
        public static int Stage(IEnumerable<KeyValuePair<string, string>> pairs, string langPair, string label)
        {
            if (pairs == null) return 0;

            var accepted = 0;
            lock (_lock)
            {
                foreach (var pair in pairs)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) continue;
                    if (_entries.Count >= MaxEntries && !_entries.ContainsKey(KeyOf(pair.Key, langPair))) continue;

                    _entries[KeyOf(pair.Key, langPair)] = new Entry
                    {
                        Source = pair.Key,
                        Target = pair.Value,
                        Label = string.IsNullOrWhiteSpace(label) ? "staged" : label.Trim(),
                        LangPair = langPair,
                        StagedUtc = DateTime.UtcNow
                    };
                    accepted++;
                }
            }
            return accepted;
        }

        /// <summary>The staged translation for this source, or null. Serving marks it delivered but keeps it — memoQ asks repeatedly.</summary>
        public static Entry TryGet(string source, string langPair)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(KeyOf(source, langPair), out var entry)) return null;
                entry.TimesServed++;
                return entry;
            }
        }

        /// <summary>Like <see cref="TryGet"/> but without counting a delivery — for listings.</summary>
        public static Entry TryGetPeek(string source, string langPair)
        {
            lock (_lock)
            {
                return _entries.TryGetValue(KeyOf(source, langPair), out var entry) ? entry : null;
            }
        }

        public static List<Entry> Snapshot(string langPair)
        {
            lock (_lock)
            {
                return _entries.Values
                    .Where(e => langPair == null || e.LangPair == langPair)
                    .OrderBy(e => e.StagedUtc)
                    .Select(e => new Entry
                    {
                        Source = e.Source,
                        Target = e.Target,
                        Label = e.Label,
                        LangPair = e.LangPair,
                        StagedUtc = e.StagedUtc,
                        TimesServed = e.TimesServed
                    })
                    .ToList();
            }
        }

        public static int Clear()
        {
            lock (_lock)
            {
                var n = _entries.Count;
                _entries.Clear();
                return n;
            }
        }
    }
}
