using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// QA checks over the live document view — the same checks the Trados
    /// plugin offers, with the same semantics, run over what memoQ's Preview
    /// SDK delivers instead of what Trados's editor API delivers.
    ///
    /// Two differences the caller has to know. Units are paragraphs, not
    /// segments (see PreviewStore), so an issue names a paragraph and the
    /// bridge reports the index the caller can pass to go_to_segment. And
    /// inline tags arrive as text markers (<c>&lt;b&gt;…&lt;/b&gt;</c>,
    /// <c>&lt;t1&gt;…&lt;/t1&gt;</c>) rather than tag objects with ids, so the
    /// tag check compares marker NAMES as a multiset — which is what the id
    /// comparison in Trados amounts to once ids are the only identity a tag
    /// has.
    /// </summary>
    internal static class QaChecks
    {
        internal sealed class Issue
        {
            public int Index;
            public string PartId;
            public string Detail;
            public string Source;
            public string Target;
        }

        internal sealed class Result
        {
            public string Check;
            public int Checked;
            public int Found;
            public bool Truncated;
            public string Note;
            public List<Issue> Issues = new List<Issue>();
        }

        internal sealed class InconsistencyGroup
        {
            public string Source;
            public List<Occurrence> Occurrences = new List<Occurrence>();
        }

        internal sealed class Occurrence
        {
            public int Index;
            public string PartId;
            public string Target;
        }

        private static readonly Regex TagMarker = new Regex(@"</?([A-Za-z][A-Za-z0-9]*)(?:\s[^>]*)?/?>", RegexOptions.Compiled);

        // Same pattern as the Trados plugin: digit runs joined by ., , or a
        // space/no-break space, compared with separators stripped, so
        // 1.234,56 matches 1,234.56 — and 1 234 matches 1234.
        private static readonly Regex Number = new Regex(@"\p{Nd}+(?:[.,  ]\p{Nd}+)*", RegexOptions.Compiled);

        public static Result Run(string check, List<PreviewStore.Part> rows, int limit, string glossaryPath)
        {
            var r = new Result { Check = check };
            var translated = rows.Select((p, i) => new { Part = p, Index = i + 1 })
                                 .Where(x => !string.IsNullOrWhiteSpace(StripTags(x.Part.Target)))
                                 .ToList();
            r.Checked = translated.Count;

            void Add(PreviewStore.Part p, int index, string detail)
            {
                r.Found++;
                if (r.Issues.Count >= limit) { r.Truncated = true; return; }
                r.Issues.Add(new Issue
                {
                    Index = index, PartId = p.PartId, Detail = detail,
                    Source = Clip(p.Source), Target = Clip(p.Target)
                });
            }

            switch (check)
            {
                case "numbers":
                    foreach (var x in translated)
                    {
                        var src = Numbers(x.Part.Source);
                        var tgt = Numbers(x.Part.Target);
                        if (!src.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(tgt.OrderBy(s => s, StringComparer.Ordinal)))
                            Add(x.Part, x.Index, $"source numbers [{string.Join(", ", src)}] vs target [{string.Join(", ", tgt)}]");
                    }
                    r.Note = r.Found == 0
                        ? "All numbers match between source and target in every translated paragraph."
                        : "Numbers are compared with thousand/decimal separators stripped; a genuine localisation " +
                          "(1,000 → 1.000) passes, a changed or dropped figure does not.";
                    break;

                case "tags":
                    foreach (var x in translated)
                    {
                        var src = TagNames(x.Part.Source);
                        var tgt = TagNames(x.Part.Target);
                        if (src.Count != tgt.Count)
                        {
                            Add(x.Part, x.Index, $"source has {src.Count} tag marker(s), target has {tgt.Count}");
                            continue;
                        }
                        var mismatch = DescribeMultisetMismatch(src, tgt);
                        if (mismatch != null) Add(x.Part, x.Index, mismatch);
                    }
                    r.Note = r.Found == 0
                        ? "Tag markers match in count and name in every translated paragraph."
                        : "A count difference is not always an error (formatting may legitimately differ); a name " +
                          "mismatch with equal counts usually is. Fix by re-staging the paragraph with the source's " +
                          "markers and running Pre-translate, or by editing in memoQ.";
                    break;

                case "nbsp":
                    foreach (var x in translated)
                    {
                        var src = (x.Part.Source ?? "").Count(c => c == ' ');
                        var tgt = (x.Part.Target ?? "").Count(c => c == ' ');
                        if (src > 0 && tgt < src)
                            Add(x.Part, x.Index, $"source has {src} non-breaking space(s), target has {tgt}");
                    }
                    r.Note = r.Found == 0
                        ? "Every translated paragraph keeps at least as many non-breaking spaces as its source."
                        : "A missing non-breaking space is invisible on screen; check against the source before " +
                          "fixing, since the target legitimately needs fewer in some cases.";
                    break;

                case "terminology":
                    RunTerminology(translated.Select(x => (x.Part, x.Index)).ToList(), glossaryPath, Add, r);
                    break;

                default:
                    r.Note = "unknown check '" + check + "' — use numbers, tags, nbsp or terminology";
                    break;
            }

            return r;
        }

        /// <summary>
        /// Repeated source paragraphs whose translations differ. Compared with
        /// tags stripped and whitespace collapsed, so formatting alone never
        /// makes two paragraphs look different.
        /// </summary>
        public static List<InconsistencyGroup> Inconsistencies(List<PreviewStore.Part> rows)
        {
            return rows
                .Select((p, i) => new { Part = p, Index = i + 1, Key = Normalise(StripTags(p.Source)) })
                .Where(x => x.Key.Length > 0 && !string.IsNullOrWhiteSpace(StripTags(x.Part.Target)))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Where(g => g.Select(x => Normalise(StripTags(x.Part.Target))).Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(g => new InconsistencyGroup
                {
                    Source = Clip(g.First().Part.Source),
                    Occurrences = g.Select(x => new Occurrence { Index = x.Index, PartId = x.Part.PartId, Target = Clip(x.Part.Target) }).ToList()
                })
                .ToList();
        }

        // ── terminology ──────────────────────────────────────────────────

        private static void RunTerminology(
            List<(PreviewStore.Part Part, int Index)> rows, string glossaryPath,
            Action<PreviewStore.Part, int, string> add, Result r)
        {
            if (string.IsNullOrWhiteSpace(glossaryPath))
            {
                r.Note = "No glossary is configured (Options > Terminology plugins > Supervertaler terms), so there is nothing to check against.";
                return;
            }

            foreach (var x in rows)
            {
                var srcPlain = StripTags(x.Part.Source);
                var tgtPlain = StripTags(x.Part.Target);
                var matches = TermIndex.Find(glossaryPath, srcPlain);
                if (matches == null || matches.Count == 0) continue;

                // Longest match wins where entries overlap, as in Trados: when
                // both "valve" and "safety valve" hit the same words, only the
                // specific one is judged.
                var kept = new List<TermIndex.Match>();
                foreach (var m in matches.OrderByDescending(m => m.Length))
                {
                    if (kept.Any(k => m.Start < k.Start + k.Length && k.Start < m.Start + m.Length)) continue;
                    kept.Add(m);
                }

                var problems = new List<string>();
                foreach (var m in kept)
                {
                    var target = m.Entry.Target ?? "";
                    if (target.Length == 0) continue;

                    if (m.Entry.Forbidden)
                    {
                        if (ContainsStem(tgtPlain, target))
                            problems.Add($"forbidden \"{target}\" used for \"{m.Entry.Source}\"");
                    }
                    else if (!ContainsStem(tgtPlain, target))
                    {
                        problems.Add($"\"{m.Entry.Source}\" → expected \"{target}\" not found in target");
                    }
                }

                if (problems.Count > 0) add(x.Part, x.Index, string.Join("; ", problems));
            }

            r.Note = r.Found == 0
                ? "Every glossary term found in a source paragraph has its expected rendering in the target, and no forbidden term appears."
                : "Matching allows inflection (the first five letters of each word must appear), so a listed miss is " +
                  "usually a genuine substitution — but check compounds and reordered phrases by eye.";
        }

        /// <summary>Loose containment: every word of the term, by its first five letters, occurs in the text.</summary>
        private static bool ContainsStem(string text, string term)
        {
            var t = text.ToLowerInvariant();
            foreach (var word in term.ToLowerInvariant().Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var stem = word.Length > 5 ? word.Substring(0, 5) : word;
                if (!t.Contains(stem)) return false;
            }
            return true;
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static List<string> Numbers(string text)
        {
            return Number.Matches(text ?? "").Cast<Match>()
                .Select(m => Regex.Replace(m.Value, @"[.,  ]", ""))
                .ToList();
        }

        private static List<string> TagNames(string text)
        {
            return TagMarker.Matches(text ?? "").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        }

        private static string DescribeMultisetMismatch(List<string> src, List<string> tgt)
        {
            var s = src.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var t = tgt.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var extra = t.Where(kv => kv.Value > (s.TryGetValue(kv.Key, out var c) ? c : 0)).Select(kv => kv.Key).ToList();
            var missing = s.Where(kv => kv.Value > (t.TryGetValue(kv.Key, out var c) ? c : 0)).Select(kv => kv.Key).ToList();
            if (extra.Count == 0 && missing.Count == 0) return null;
            var parts = new List<string>();
            if (extra.Count > 0) parts.Add("tag(s) in the target the source does not have: " + string.Join(", ", extra));
            if (missing.Count > 0) parts.Add("tag(s) in the source missing from the target: " + string.Join(", ", missing));
            return string.Join("; ", parts);
        }

        public static string StripTags(string text) => text == null ? "" : TagMarker.Replace(text, "");

        private static string Normalise(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

        private static string Clip(string s) => s == null ? "" : s.Length > 200 ? s.Substring(0, 200) + "…" : s;
    }
}
