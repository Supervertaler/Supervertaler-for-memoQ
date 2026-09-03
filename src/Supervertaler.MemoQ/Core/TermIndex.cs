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
    /// Supervertaler.Core, and adding Microsoft.Data.Sqlite to the Addins folder
    /// before then would put us in a fight with memoQ's own SQLite for no reason
    /// yet.</para>
    /// </summary>
    internal static class TermIndex
    {
        internal sealed class Entry
        {
            public string Source { get; set; }
            public string Target { get; set; }
            public bool Forbidden { get; set; }
        }

        internal sealed class Match
        {
            public Entry Entry { get; set; }

            /// <summary>Offset into the segment's plain text.</summary>
            public int Start { get; set; }

            public int Length { get; set; }
        }

        private static readonly object _lock = new object();
        private static List<Entry> _entries = new List<Entry>();

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
            EnsureLoaded(glossaryPath);

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

        private static void EnsureLoaded(string path)
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    if (_entries.Count > 0) { _entries = new List<Entry>(); _loadedPath = null; }
                    return;
                }

                var now = DateTime.UtcNow;
                var samePath = string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase);
                if (samePath && now - _lastCheck < CheckInterval) return;
                _lastCheck = now;

                DateTime stamp;
                try
                {
                    if (!File.Exists(path))
                    {
                        if (_entries.Count > 0)
                            PluginLog.Write($"TermIndex: glossary no longer found at {path}");
                        _entries = new List<Entry>();
                        _loadedPath = path;
                        return;
                    }
                    stamp = File.GetLastWriteTimeUtc(path);
                }
                catch (Exception ex)
                {
                    PluginLog.Write("TermIndex: could not stat glossary", ex);
                    return;
                }

                // Edit the file while memoQ is open and the next segment sees it.
                if (samePath && stamp == _loadedStamp) return;

                _entries = Parse(path);
                ReadHeader(path);
                Rebuild();
                _loadedPath = path;
                _loadedStamp = stamp;

                PluginLog.Write($"TermIndex: loaded {_entries.Count} term(s) "
                    + $"({_entries.Count(e => e.Forbidden)} forbidden, "
                    + $"{_byFirstWord.Count} bucket(s)) from {Path.GetFileName(path)}"
                    + (DeclaredPair == null ? " [no language declared]" : $" [{DeclaredPair}]"));
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
                PluginLog.Write("TermIndex: could not read the glossary header", ex);
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
                PluginLog.Write("TermIndex: could not read glossary", ex);
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
