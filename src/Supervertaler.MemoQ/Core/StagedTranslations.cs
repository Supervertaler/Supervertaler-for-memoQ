using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

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
    ///
    /// <para>Two keys per entry. The EXACT one is the text as staged. The LOOSE
    /// one is that text with inline tags removed and XML escapes undone, because
    /// memoQ asks in its own segment XML - tags as elements, "&amp;" for "&" -
    /// while a source taken from the live document link has neither. On one job
    /// 31 of 501 rows had inline tags, never matched what was staged for them, and
    /// quietly went to the model instead. A request is looked up exactly first,
    /// then loosely.</para>
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

        /// <summary>The same entries by their loose key; the last one staged wins.</summary>
        private static readonly Dictionary<string, Entry> _loose =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        // A tag starts with a letter or "/": memoQ's XML escapes a literal "<" as
        // "&lt;", and the live link's "a < b" must survive as text, not be cut out.
        private static readonly Regex TagMarkup = new Regex("</?[A-Za-z_][^<>]*>", RegexOptions.Compiled);
        private static readonly Regex SpecChar = new Regex(@"<spec_char\s+val=""([^""]*)""\s*/>", RegexOptions.Compiled);

        /// <summary>
        /// <paramref name="text"/> with inline tags removed and XML escapes undone:
        /// what memoQ's segment XML and the live link's text have in common. A
        /// special character memoQ sends as a tag (<c>&lt;spec_char val="&amp;amp;"/&gt;</c>)
        /// is the character itself in the live link, so it becomes its value, not nothing.
        /// </summary>
        internal static string Loose(string text)
        {
            var specials = SpecChar.Replace(text ?? "", m => m.Groups[1].Value);
            return WebUtility.HtmlDecode(TagMarkup.Replace(specials, ""));
        }

        /// <summary>Whether <paramref name="text"/> carries any inline tag markup.</summary>
        internal static bool HasTags(string text)
        {
            return !string.IsNullOrEmpty(text) && TagMarkup.IsMatch(text);
        }

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

                    var entry = new Entry
                    {
                        Source = pair.Key,
                        Target = pair.Value,
                        Label = string.IsNullOrWhiteSpace(label) ? "staged" : label.Trim(),
                        LangPair = langPair,
                        StagedUtc = DateTime.UtcNow
                    };
                    _entries[KeyOf(pair.Key, langPair)] = entry;
                    _loose[KeyOf(Loose(pair.Key), langPair)] = entry;
                    accepted++;
                }
            }
            return accepted;
        }

        /// <summary>The staged translation for this source, or null. Serving marks it delivered but keeps it — memoQ asks repeatedly.</summary>
        public static Entry TryGet(string source, string langPair)
        {
            return TryGet(source, langPair, out _);
        }

        /// <summary>
        /// As <see cref="TryGet(string,string)"/>, saying whether the match was
        /// loose - found only once tags and escapes were set aside.
        /// </summary>
        public static Entry TryGet(string source, string langPair, out bool loose)
        {
            lock (_lock)
            {
                var entry = Find(source, langPair, out loose);
                if (entry != null) entry.TimesServed++;
                return entry;
            }
        }

        /// <summary>Like <see cref="TryGet(string,string)"/> but without counting a delivery — for listings.</summary>
        public static Entry TryGetPeek(string source, string langPair)
        {
            lock (_lock) return Find(source, langPair, out _);
        }

        private static Entry Find(string source, string langPair, out bool loose)
        {
            loose = false;
            if (_entries.TryGetValue(KeyOf(source, langPair), out var exact)) return exact;
            if (_loose.TryGetValue(KeyOf(Loose(source), langPair), out var found))
            {
                loose = true;
                return found;
            }
            return null;
        }

        /// <summary>
        /// memoQ's own tagged form of each source it has asked about for this
        /// language pair, by loose key - so a source taken from the live link can be
        /// staged as memoQ will ask for it, and the assistant can be shown the tags.
        /// Built once per call: thousands of captured rows, not a lookup per row.
        /// Only forms that differ from their loose text are kept.
        /// </summary>
        internal static Dictionary<string, string> CapturedTaggedForms(string langPair)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var doc in CaptureStore.Snapshot())
            {
                // Whole pairs compared: memoQ's codes carry hyphens of their own
                // ("eng-GB"), so a joined pair cannot be split back reliably.
                var docPair = (doc.SourceLangCode ?? "?") + "-" + (doc.TargetLangCode ?? "?");
                if (!string.Equals(docPair, langPair, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var tagged in doc.Sources)
                {
                    if (string.IsNullOrEmpty(tagged)) continue;
                    var loose = Loose(tagged);
                    if (string.Equals(loose.Trim(), tagged.Trim(), StringComparison.Ordinal)) continue;
                    var key = KeyOf(loose, "");
                    if (!map.ContainsKey(key)) map[key] = tagged;
                }
            }
            return map;
        }

        /// <summary>memoQ's tagged form of <paramref name="source"/> from a <see cref="CapturedTaggedForms"/> map, or null.</summary>
        internal static string TaggedFormOf(Dictionary<string, string> captured, string source)
        {
            if (captured == null || captured.Count == 0 || string.IsNullOrEmpty(source)) return null;
            return captured.TryGetValue(KeyOf(Loose(source), ""), out var tagged) ? tagged : null;
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
                _loose.Clear();
                return n;
            }
        }
    }
}
