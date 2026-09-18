using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The terms of the termbases selected for a memoQ project, and the matcher
    /// that finds them in a segment.
    ///
    /// Shared by both halves of the plugin, which is the whole point:
    ///
    ///   - the <b>TB plugin</b> turns matches into <c>TerminologyResult</c>s, so
    ///     memoQ highlights the term in the source and renders our HTML in its own
    ///     terminology pane;
    ///   - the <b>MT plugin</b> turns matches into "required terminology" and
    ///     "forbidden terms" lines in the prompt.
    ///
    /// That second use is the reason this exists. memoQ never passes
    /// <c>ContextKinds.Terminology</c> to a third-party MT plugin - confirmed by
    /// memoQ on 2026-09-17 - so if we want terms in the prompt we have to be the
    /// terminology source ourselves. Both directors live in one process, so this
    /// is a plain in-process call.
    ///
    /// <para><b>Two questions, two answers.</b> <see cref="Find"/> answers "what
    /// terms are in this text?" from every termbase ticked Read for the project:
    /// that is what the grid highlights and the pane shows. <see cref="FindForModel"/>
    /// answers "what terms may the model be told?" from only those also ticked
    /// AI. The two ticks are separate decisions in the Termbases window, and a
    /// translator who keeps a large general termbase for reference and a small
    /// project one for the model needs them to be.</para>
    ///
    /// <para><b>Termbases only.</b> Until 2026-09-18 this also read a tab-separated
    /// glossary file and merged it in. That was the system before the termbases
    /// existed; keeping both meant two editors, two sources and two places a
    /// term could live, and it was retired. The files import into termbases in
    /// one click.</para>
    /// </summary>
    internal static class TermIndex
    {
        internal sealed class Entry
        {
            public string Source { get; set; }
            public string Target { get; set; }
            public bool Forbidden { get; set; }

            public long TermbaseId { get; set; }

            /// <summary>1 for the project termbase, 0 for a background one. Decides the shade of a hit.</summary>
            public int Rank { get; set; }

            /// <summary>
            /// The termbase's CS tick: this term matches only with its case as
            /// written. Off, "wire" matches "Wire" and "WIRE"; on, an abbreviation
            /// like "AC" stops matching the "ac" inside ordinary words' initials.
            /// </summary>
            public bool CaseSensitive { get; set; }

            /// <summary>The termbase's name, for the terminology pane.</summary>
            public string Origin { get; set; }
        }

        internal sealed class Match
        {
            public Entry Entry { get; set; }

            /// <summary>Character offset into the plain text where the source term starts.</summary>
            public int Start { get; set; }

            /// <summary>Length in characters of the matched source term.</summary>
            public int Length { get; set; }
        }

        /// <summary>
        /// Where a failure is reported. The plugin points this at its log; the
        /// prompt editor compiles this same file and has no log, so it leaves it
        /// silent. Same arrangement as SharedSettings.
        /// </summary>
        internal static Action<string, Exception> ErrorSink = (message, ex) => { };

        private static readonly object _lock = new object();
        private static List<Entry> _entries = new List<Entry>();

        // What the selection was when the entries were loaded: the project, its
        // termbase ids and their ranks, a stamp of the terms themselves, and the
        // selection file's write time. A change in any of them reloads.
        private static string _selectionKey;

        // The job's own languages, which decide whether a termbase stored the
        // other way round is turned before use.
        //
        // State rather than a parameter, deliberately and narrowly: the bridge and
        // the QA checks genuinely do not know the pair, while the callers that do
        // (memoQ's terminology session and the batch translator) are told it by
        // memoQ and set it immediately before they look anything up. So it
        // behaves as a parameter would, without call sites inventing a language
        // pair they have no way of knowing.
        private static string _jobSource;
        private static string _jobTarget;

        /// <summary>
        /// Entries bucketed by the first word of their source term, so a segment
        /// only ever compares against terms that could possibly start in it.
        ///
        /// Not premature optimisation: a real termbase runs to 12,000 entries and
        /// memoQ calls Lookup on every cursor move. Scanning every entry, and
        /// re-sorting the whole list, per segment made the grid visibly slow.
        /// Bucketing cuts the candidates to a handful.
        /// </summary>
        private static Dictionary<string, List<Entry>> _byFirstWord
            = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Entries starting with punctuation or a symbol, so they have no usable first word.</summary>
        private static List<Entry> _unbucketed = new List<Entry>();

        private static DateTime _lastCheck = DateTime.MinValue;

        /// <summary>How often to re-check the selection. Lookup runs per segment; checking every time is wasteful.</summary>
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

        public static int Count { get { lock (_lock) return _entries.Count; } }

        /// <summary>
        /// Terms found in this text, from every termbase ticked Read for the
        /// project. Longest match wins and matches never overlap, so "electric
        /// module" beats a bare "module" sitting inside it.
        /// </summary>
        public static IReadOnlyList<Match> Find(Guid project, string plainText)
        {
            EnsureLoaded(project);
            return FindLoaded(plainText, null);
        }

        /// <summary>
        /// Terms found in this text that the model may be told: those from
        /// termbases ticked both Read and AI for the project. The rest are for
        /// the translator's eyes - the grid, the pane - and never leave the
        /// machine.
        /// </summary>
        public static IReadOnlyList<Match> FindForModel(Guid project, string plainText)
        {
            EnsureLoaded(project);

            HashSet<long> allowed;
            try
            {
                allowed = new HashSet<long>(TermbaseSelection.AiFor(project));
            }
            catch (Exception ex)
            {
                ErrorSink("TermIndex: could not read which termbases reach the model", ex);
                return Array.Empty<Match>();
            }

            return allowed.Count == 0 ? Array.Empty<Match>() : FindLoaded(plainText, allowed);
        }

        private static IReadOnlyList<Match> FindLoaded(string plainText, HashSet<long> onlyTermbases)
        {
            if (string.IsNullOrWhiteSpace(plainText)) return Array.Empty<Match>();

            List<Entry> candidates;
            lock (_lock)
            {
                if (_entries.Count == 0) return Array.Empty<Match>();
                candidates = Candidates(plainText);
            }

            if (onlyTermbases != null)
                candidates = candidates.Where(e => onlyTermbases.Contains(e.TermbaseId)).ToList();

            if (candidates.Count == 0) return Array.Empty<Match>();

            var matches = new List<Match>();
            var taken = new bool[plainText.Length];

            // Longest source first: a longer term is the more specific statement
            // about this text, and claiming its span stops a shorter one inside it
            // from also matching.
            //
            // Entries that share a source are grouped and reported together. They
            // are not rivals for the span, they are complementary statements about
            // one term: a termbase routinely holds "device -> inrichting" next to
            // "device -> apparaat, forbidden", meaning use the first and never the
            // second. Letting the first claim the span silently dropped the second,
            // so the ban never reached the model - which is exactly the instruction
            // the translator most wanted enforced.
            foreach (var group in candidates
                .GroupBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Key.Length))
            {
                var source = group.Key;
                var from = 0;

                while (from <= plainText.Length - source.Length)
                {
                    var at = plainText.IndexOf(source, from, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;

                    var end = at + source.Length;

                    // The search above ignores case, so that one pass serves every
                    // entry. An entry from a termbase ticked CS then has to match
                    // the text exactly as written - and if none in the group does,
                    // the span is not claimed, so a shorter case-insensitive term
                    // inside it can still have its turn.
                    var exact = string.CompareOrdinal(plainText, at, source, 0, source.Length) == 0;
                    var hits = group.Where(entry => !entry.CaseSensitive || exact).ToList();

                    if (hits.Count > 0 && IsWholeWord(plainText, at, end) && !AnyTaken(taken, at, end))
                    {
                        for (var i = at; i < end; i++) taken[i] = true;
                        foreach (var entry in hits)
                            matches.Add(new Match { Entry = entry, Start = at, Length = source.Length });
                    }

                    from = at + 1;
                }
            }

            return matches.OrderBy(m => m.Start).ToList();
        }

        /// <summary>
        /// The entries worth testing against this text: those whose source term
        /// begins with a word that actually occurs in it, plus the handful that
        /// begin with punctuation and cannot be bucketed. Call under the lock.
        /// </summary>
        private static List<Entry> Candidates(string plainText)
        {
            var seen = new HashSet<Entry>();
            var result = new List<Entry>(_unbucketed);
            foreach (var e in _unbucketed) seen.Add(e);

            foreach (var word in Words(plainText))
            {
                if (!_byFirstWord.TryGetValue(word, out var bucket)) continue;
                foreach (var e in bucket) if (seen.Add(e)) result.Add(e);
            }

            result.Sort((a, b) => b.Source.Length.CompareTo(a.Source.Length));
            return result;
        }

        private static IEnumerable<string> Words(string text)
        {
            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                var isWord = i < text.Length && IsWordChar(text[i]);
                if (isWord && start < 0) start = i;
                else if (!isWord && start >= 0)
                {
                    yield return text.Substring(start, i - start);
                    start = -1;
                }
            }
        }

        private static string FirstWord(string term)
        {
            foreach (var w in Words(term)) return w;
            return null;
        }

        // ---- loading ----------------------------------------------------------

        /// <summary>
        /// Tell the index which languages this job runs in, before looking
        /// anything up. A change re-reads the selected termbases at once, since
        /// which way round they are read depends on this.
        /// </summary>
        public static void UseLanguages(string source, string target)
        {
            lock (_lock)
            {
                if (string.Equals(_jobSource, source, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(_jobTarget, target, StringComparison.OrdinalIgnoreCase)) return;

                _jobSource = source;
                _jobTarget = target;

                // Not merely stale - possibly backwards. Drop the key so the next
                // lookup reloads, and clear the throttle so "next" means now.
                _selectionKey = null;
                _lastCheck = DateTime.MinValue;
            }
        }

        private static void EnsureLoaded(Guid project)
        {
            lock (_lock)
            {
                // memoQ asks per segment, so this runs constantly; a check every
                // three seconds is affordable and a reload on every keystroke is not.
                var now = DateTime.UtcNow;
                if (now - _lastCheck < CheckInterval) return;
                _lastCheck = now;

                ReloadIfChanged(project);
            }
        }

        /// <summary>
        /// Load the terms of the termbases selected for this project, when
        /// anything about that has changed since last time.
        ///
        /// <para>Keyed on the selection itself - the ids and their ranks - plus a
        /// stamp of the terms and the selection file's write time, rather than on
        /// any one file's timestamp, so a change made in the editor lands within
        /// one check interval however it was made, and a save that changed
        /// nothing costs nothing.</para>
        ///
        /// <para><b>Scale, stated rather than assumed:</b> the whole selection is
        /// read into memory at once. Measured on the real database - 84 termbases,
        /// 36,000 terms - a full read of every term in the file is around 100 ms,
        /// and a typical selection is far smaller. It is loaded once per change,
        /// not per segment. Selecting every termbase at once is therefore about a
        /// tenth of a second and some tens of megabytes; ten times that data would
        /// want a different design.</para>
        /// </summary>
        private static void ReloadIfChanged(Guid project)
        {
            string key;
            IList<long> ids;
            IDictionary<long, TermbaseSelection.Flags> flags;

            try
            {
                ids = TermbaseSelection.ReadFor(project);
                flags = TermbaseSelection.All();
                key = project.ToString("N") + "|" + string.Join(",",
                    ids.Select(id => id + ":" + (flags.ContainsKey(id) ? flags[id].Rank : 0)))
                    // ...and the terms themselves: an edit in the editor, or a
                    // term added from Studio, moves this and reloads the
                    // selection on the next check.
                    + "|" + TermbaseDb.ChangeStamp(ids)
                    // The database's timestamps are whole seconds, so a
                    // correction made in the same second as the last change
                    // would not move the stamp above. The editor rewrites the
                    // selection file on every OK; its write time closes that gap
                    // for edits made here.
                    + "|" + TermbaseSelection.FileStamp;
            }
            catch (Exception ex)
            {
                ErrorSink("TermIndex: could not read the termbase selection", ex);
                return;
            }

            if (string.Equals(key, _selectionKey, StringComparison.Ordinal)) return;
            _selectionKey = key;

            if (ids.Count == 0)
            {
                if (_entries.Count > 0) { _entries = new List<Entry>(); Rebuild(); }
                return;
            }

            try
            {
                var loaded = TermbaseDb.TermsIn(ids, _jobSource, _jobTarget);
                foreach (var e in loaded)
                {
                    TermbaseSelection.Flags f;
                    var known = flags.TryGetValue(e.TermbaseId, out f);
                    e.Rank = known ? f.Rank : 0;
                    e.CaseSensitive = known && f.CaseSensitive;
                }

                // The project termbase first, so that where two termbases carry
                // the same source term of the same length, its entry is the one
                // that matches.
                _entries = loaded.OrderBy(e => e.Rank == 0 ? int.MaxValue : e.Rank).ToList();
                Rebuild();

                ErrorSink($"TermIndex: loaded {_entries.Count} term(s) "
                    + $"({_entries.Count(e => e.Forbidden)} forbidden) "
                    + $"from {ids.Count} termbase(s)"
                    + (string.IsNullOrEmpty(_jobSource) ? " [no job languages, nothing reversed]"
                                                        : $" [for {_jobSource}->{_jobTarget}]"), null);
            }
            catch (Exception ex)
            {
                // Terminology goes quiet; it does not take the grid down with it.
                ErrorSink("TermIndex: could not load terms from the termbase database", ex);
                _entries = new List<Entry>();
                Rebuild();
            }
        }

        /// <summary>Buckets by first word and pre-sorts each bucket longest-first.</summary>
        private static void Rebuild()
        {
            _byFirstWord = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
            _unbucketed = new List<Entry>();

            foreach (var e in _entries)
            {
                var first = FirstWord(e.Source);
                if (first == null) { _unbucketed.Add(e); continue; }

                if (!_byFirstWord.TryGetValue(first, out var bucket))
                    _byFirstWord[first] = bucket = new List<Entry>();
                bucket.Add(e);
            }

            foreach (var bucket in _byFirstWord.Values)
                bucket.Sort((a, b) => b.Source.Length.CompareTo(a.Source.Length));

            _unbucketed.Sort((a, b) => b.Source.Length.CompareTo(a.Source.Length));
        }

        // ---- helpers ----------------------------------------------------------

        /// <summary>
        /// A match must not sit inside a longer word. Crude but right for the
        /// languages this is built for; it will under-match agglutinative or
        /// unspaced scripts, which is a known limitation rather than a bug.
        /// </summary>
        private static bool IsWholeWord(string text, int start, int end)
        {
            if (start > 0 && IsWordChar(text[start - 1])) return false;
            if (end < text.Length && IsWordChar(text[end])) return false;
            return true;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static bool AnyTaken(bool[] taken, int start, int end)
        {
            for (var i = start; i < end; i++) if (taken[i]) return true;
            return false;
        }
    }
}
