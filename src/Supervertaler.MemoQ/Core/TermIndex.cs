using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// A glossary loaded from a delimited file, and the matcher that finds its
    /// entries in a segment.
    ///
    /// Shared by both halves of the plugin, which is the whole point:
    ///
    ///   - the <b>TB plugin</b> turns matches into <c>TerminologyResult</c>s, so
    ///     memoQ highlights the term in the source and renders our HTML in its own
    ///     terminology pane;
    ///   - the <b>MT plugin</b> turns the same matches into "required terminology"
    ///     and "forbidden terms" lines in the prompt.
    ///
    /// That second use is the reason this exists. memoQ never passes
    /// <c>ContextKinds.Terminology</c> to a third-party MT plugin, so if we want
    /// terms in the prompt we have to be the terminology source ourselves. Both
    /// directors live in the same assembly and the same process, so this is a
    /// plain in-process call — no SDK involved.
    ///
    /// <para>File format: tab-separated, one entry per line.</para>
    /// <code>
    /// elektrische module    electric module
    /// elektrische module    electrical module    forbidden
    /// koppelmechanisme      coupling mechanism   # note after a hash is ignored
    /// </code>
    /// <para>Blank lines and lines starting with <c>#</c> are skipped. A third
    /// column containing "forbidden" (or "!") marks a target that must not be
    /// used. Deliberately a text file rather than a Supervertaler SQLite termbase:
    /// that reader lives in the Trados plugin and comes across with
    /// Supervertaler.Core. The fight with memoQ's own SQLite that this once
    /// worried about does not arise: memoQ ships Microsoft.Data.Sqlite itself
    /// and TermbaseDb uses memoQ's copy in place, adding nothing to Addins
    /// yet.</para>
    /// </summary>
    internal static class TermIndex
    {
        internal sealed class Entry
        {
            public string Source { get; set; }
            public string Target { get; set; }
            public bool Forbidden { get; set; }

            /// <summary>0 for a term from the glossary file.</summary>
            public long TermbaseId { get; set; }

            /// <summary>
            /// The rank of the termbase this came from: 1 is highest, 0 unranked
            /// or from the glossary file. memoQ shades a term hit by the rank of
            /// the termbase behind it, so this decides the colour.
            /// </summary>
            public int Rank { get; set; }

            /// <summary>Termbase or glossary name, for the terminology pane.</summary>
            public string Origin { get; set; }
        }

        internal sealed class Match
        {
            public Entry Entry { get; set; }

            /// <summary>Offset into the segment's plain text.</summary>
            public int Start { get; set; }

            public int Length { get; set; }
        }

        /// <summary>
        /// Where this reports. The plugin points it at its log; the prompt editor
        /// compiles this same file to read termbases and has no log, so it leaves
        /// it silent. The arrangement SharedSettings uses, for the same reason.
        /// </summary>
        internal static Action<string, Exception> ErrorSink = (message, ex) => { };

        private static readonly object _lock = new object();
        private static List<Entry> _entries = new List<Entry>();

        // Kept apart so that a change to one source does not cost a reload of the
        // other: the glossary file is re-parsed when it is touched, the termbases
        // when the selection changes, and neither triggers the other.
        private static List<Entry> _glossaryEntries = new List<Entry>();
        private static List<Entry> _termbaseEntries = new List<Entry>();
        private static string _selectionKey;

        // The job's own languages, which decide whether a termbase stored the
        // other way round is turned before use.
        //
        // State rather than a parameter, deliberately and narrowly: five of the
        // seven callers of Find - the bridge, the QA checks, the index warm-up -
        // genuinely do not know the pair, while the two that do (memoQ's
        // terminology session and the batch translator) are told it by memoQ and
        // set it immediately before they look anything up. So it behaves as a
        // parameter would, without four call sites inventing a language pair
        // they have no way of knowing.
        private static string _jobSource;
        private static string _jobTarget;

        /// <summary>
        /// Entries bucketed by the first word of their source term, so a segment
        /// only ever compares against terms that could possibly start in it.
        ///
        /// Not premature optimisation: a real termbase export runs to 9,000+
        /// entries and memoQ calls Lookup on every cursor move. Scanning every
        /// entry, and re-sorting the whole list, per segment made the grid
        /// visibly slow. Bucketing cuts the candidates to a handful.
        /// </summary>
        private static Dictionary<string, List<Entry>> _byFirstWord
            = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Entries starting with punctuation or a symbol, so they have no usable first word.</summary>
        private static List<Entry> _unbucketed = new List<Entry>();
        private static string _loadedPath;
        private static DateTime _loadedStamp;
        private static DateTime _lastCheck = DateTime.MinValue;

        /// <summary>How often to stat the file. Lookup runs per segment; stat-ing every time is wasteful.</summary>
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

        public static int Count { get { lock (_lock) return _entries.Count; } }

        public static string LoadedPath { get { lock (_lock) return _loadedPath; } }

        /// <summary>
        /// Finds glossary entries in the segment's plain text. Longest match wins
        /// and matches never overlap, so "electric module" beats a bare "module"
        /// sitting inside it.
        /// </summary>
        public static IReadOnlyList<Match> Find(string glossaryPath, string plainText)
        {
            return Find(glossaryPath, Guid.Empty, plainText);
        }

        /// <summary>
        /// Terms found in this segment, from the glossary file AND from whichever
        /// termbases are selected for this memoQ project.
        ///
        /// <para>The two sources are merged rather than one replacing the other:
        /// a translator may have a job-specific glossary exported from a prompt
        /// and a standing termbase, and both are true at once. Where they collide
        /// the longest match wins, which is what already decided between two
        /// glossary entries.</para>
        /// </summary>
        public static IReadOnlyList<Match> Find(string glossaryPath, Guid project, string plainText)
        {
            EnsureLoaded(glossaryPath, project);

            if (string.IsNullOrWhiteSpace(plainText)) return Array.Empty<Match>();

            List<Entry> candidates;
            lock (_lock)
            {
                if (_entries.Count == 0) return Array.Empty<Match>();
                candidates = Candidates(plainText);
            }
            if (candidates.Count == 0) return Array.Empty<Match>();

            var matches = new List<Match>();
            var taken = new bool[plainText.Length];

            // Longest source first: a longer term is the more specific statement
            // about this text, and claiming its span stops a shorter one inside it
            // from also matching.
            //
            // Entries that share a source are grouped and reported together. They
            // are not rivals for the span, they are complementary statements about
            // one term: a glossary routinely holds "device -> inrichting" next to
            // "device -> apparaat, forbidden", meaning use the first and never the
            // second. Letting the first claim the span silently dropped the second,
            // so the ban never reached the model — which is exactly the instruction
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
                    if (IsWholeWord(plainText, at, end) && !AnyTaken(taken, at, end))
                    {
                        for (var i = at; i < end; i++) taken[i] = true;
                        foreach (var entry in group)
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

        private static void EnsureLoaded(string path, Guid project)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var samePath = string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase);

                // Both sources are checked on the same throttle. memoQ asks per
                // segment, so this runs constantly; a stat every three seconds is
                // affordable and a reload on every keystroke is not.
                if (samePath && now - _lastCheck < CheckInterval) return;
                _lastCheck = now;

                ReloadTermbasesIfChanged(project);

                if (string.IsNullOrWhiteSpace(path))
                {
                    // No glossary file is not "no terminology" any more: the
                    // termbases stand on their own.
                    if (_glossaryEntries.Count > 0)
                    {
                        _glossaryEntries = new List<Entry>();
                        _loadedPath = null;
                        Combine();
                    }
                    return;
                }

                DateTime stamp;
                try
                {
                    if (!File.Exists(path))
                    {
                        if (_glossaryEntries.Count > 0)
                            ErrorSink($"TermIndex: glossary no longer found at {path}", null);
                        _glossaryEntries = new List<Entry>();
                        _loadedPath = path;
                        Combine();
                        return;
                    }
                    stamp = File.GetLastWriteTimeUtc(path);
                }
                catch (Exception ex)
                {
                    ErrorSink("TermIndex: could not stat glossary", ex);
                    return;
                }

                // Edit the file while memoQ is open and the next segment sees it.
                if (samePath && stamp == _loadedStamp) return;

                _glossaryEntries = Parse(path);
                foreach (var e in _glossaryEntries) e.Origin = Path.GetFileName(path);
                ReadHeader(path);
                _loadedPath = path;
                _loadedStamp = stamp;
                Combine();

                ErrorSink($"TermIndex: loaded {_glossaryEntries.Count} term(s) "
                    + $"({_glossaryEntries.Count(e => e.Forbidden)} forbidden) "
                    + $"from {Path.GetFileName(path)}"
                    + (DeclaredPair == null ? " [no language declared]" : $" [{DeclaredPair}]"), null);
            }
        }

        /// <summary>
        /// Load the terms of the termbases selected for this project, when the
        /// selection has changed since last time.
        ///
        /// <para>Keyed on the selection itself - the ids and their ranks - rather
        /// than on a file timestamp, so a change made in the prompt editor lands
        /// within one check interval however it was made, and a save that changed
        /// nothing costs nothing.</para>
        ///
        /// <para><b>Scale, stated rather than assumed:</b> the whole selection is
        /// read into memory at once. Measured on the real database - 84 termbases,
        /// 36,091 terms - a full read of every term in the file is around 100 ms,
        /// and a typical selection is far smaller. It is loaded once per change,
        /// not per segment. Selecting every termbase at once is therefore about a
        /// tenth of a second and some tens of megabytes; ten times that data would
        /// want a different design.</para>
        /// </summary>
        private static void ReloadTermbasesIfChanged(Guid project)
        {
            string key;
            IList<long> ids;
            IDictionary<long, TermbaseSelection.Flags> flags;

            try
            {
                ids = TermbaseSelection.ReadFor(project);
                flags = TermbaseSelection.All();
                key = project.ToString("N") + "|" + string.Join(",",
                    ids.Select(id => id + ":" + (flags.ContainsKey(id) ? flags[id].Rank : 0)));
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
                if (_termbaseEntries.Count > 0) { _termbaseEntries = new List<Entry>(); Combine(); }
                return;
            }

            try
            {
                var loaded = TermbaseDb.TermsIn(ids, _jobSource, _jobTarget);
                foreach (var e in loaded)
                {
                    TermbaseSelection.Flags f;
                    e.Rank = flags.TryGetValue(e.TermbaseId, out f) ? f.Rank : 0;
                }

                _termbaseEntries = new List<Entry>(loaded);
                Combine();

                ErrorSink($"TermIndex: loaded {_termbaseEntries.Count} term(s) "
                    + $"({_termbaseEntries.Count(e => e.Forbidden)} forbidden) "
                    + $"from {ids.Count} termbase(s)"
                    + (string.IsNullOrEmpty(_jobSource) ? " [no job languages, nothing reversed]"
                                                        : $" [for {_jobSource}->{_jobTarget}]"), null);
            }
            catch (Exception ex)
            {
                // Terminology degrades to whatever the glossary file holds; it
                // does not take the grid down with it.
                ErrorSink("TermIndex: could not load terms from the termbase database", ex);
                _termbaseEntries = new List<Entry>();
                Combine();
            }
        }

        /// <summary>
        /// The two sources become one index. Ranked termbase terms first, so that
        /// where two sources carry the same source term of the same length, the
        /// better-ranked one is the entry that matches.
        /// </summary>
        private static void Combine()
        {
            var all = new List<Entry>(_termbaseEntries.Count + _glossaryEntries.Count);
            all.AddRange(_termbaseEntries.OrderBy(e => e.Rank == 0 ? int.MaxValue : e.Rank));
            all.AddRange(_glossaryEntries);
            _entries = all;
            Rebuild();
        }

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

        /// <summary>
        /// The language pair the glossary declares, as "eng to dut", or null when
        /// the file does not say. Read from a <c>#! source=… target=…</c> line.
        /// </summary>
        public static string DeclaredPair =>
            DeclaredSource == null || DeclaredTarget == null ? null : DeclaredSource + " to " + DeclaredTarget;

        public static string DeclaredSource { get; private set; }

        public static string DeclaredTarget { get; private set; }

        /// <summary>
        /// Reads the machine-readable header. Ordinary <c>#</c> lines stay prose
        /// for the reader; only <c>#!</c> carries settings, so a hand-written
        /// comment can never be mistaken for one.
        /// </summary>
        private static void ReadHeader(string path)
        {
            DeclaredSource = null;
            DeclaredTarget = null;

            try
            {
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw?.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    // Stop at the first entry: the header belongs at the top.
                    if (!line.StartsWith("#")) break;
                    if (!line.StartsWith("#!")) continue;

                    foreach (var part in line.Substring(2).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = part.IndexOf('=');
                        if (eq <= 0) continue;

                        var key = part.Substring(0, eq).Trim();
                        var value = part.Substring(eq + 1).Trim();
                        if (value.Length == 0) continue;

                        if (string.Equals(key, "source", StringComparison.OrdinalIgnoreCase)) DeclaredSource = value;
                        else if (string.Equals(key, "target", StringComparison.OrdinalIgnoreCase)) DeclaredTarget = value;
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorSink("TermIndex: could not read the glossary header", ex);
            }
        }

        private static List<Entry> Parse(string path)
        {
            var entries = new List<Entry>();

            try
            {
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw?.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;

                    var source = parts[0].Trim();
                    var target = parts[1].Trim();
                    if (source.Length == 0 || target.Length == 0) continue;

                    var flag = parts.Length > 2 ? parts[2].Trim() : string.Empty;
                    var forbidden = flag.StartsWith("!", StringComparison.Ordinal)
                        || flag.IndexOf("forbidden", StringComparison.OrdinalIgnoreCase) >= 0;

                    entries.Add(new Entry { Source = source, Target = target, Forbidden = forbidden });
                }
            }
            catch (Exception ex)
            {
                ErrorSink("TermIndex: could not read glossary", ex);
            }

            return entries;
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
