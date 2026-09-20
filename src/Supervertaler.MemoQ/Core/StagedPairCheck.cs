using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// What is wrong with a staged pair, checked before it is stored rather than
    /// after it has reached the grid.
    ///
    /// <para>The QA checks already compare a target's tags against its source, but
    /// they run over the document <em>after</em> a Pre-translate has written
    /// everything. By then a dropped placeholder is in the translator's file and
    /// has to be found and fixed there. The same comparison at staging time costs
    /// nothing and answers while the agent that made the mistake is still holding
    /// the pen.</para>
    ///
    /// <para>These warn; they do not refuse. A staged pair the plugin dislikes may
    /// still be right - memoQ's markup varies by file filter, and a target that
    /// legitimately drops a formatting tag is not rare - and refusing would turn a
    /// judgement call into a blocked job. Saying so is enough.</para>
    /// </summary>
    internal static class StagedPairCheck
    {
        /// <summary>
        /// A tag marker, by the same rule the QA check uses: the NAME is the
        /// identity, since memoQ hands a plugin text markers rather than tag
        /// objects. Covers the forms actually seen in production -
        /// <c>&lt;inline_tag id="0"/&gt;</c> and <c>&lt;spec_char val="&amp;"/&gt;</c>
        /// as well as <c>&lt;b&gt;</c> and <c>&lt;t1&gt;</c>.
        /// </summary>
        private static readonly Regex TagMarker =
            new Regex(@"</?([A-Za-z_][A-Za-z0-9_]*)(?:\s[^>]*)?/?>", RegexOptions.Compiled);

        /// <summary>How many problem pairs to name before summarising the rest.</summary>
        private const int Examples = 5;

        /// <summary>
        /// One line per staged pair worth complaining about, or an empty list.
        /// </summary>
        public static List<string> Problems(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var found = new List<string>();
            if (pairs == null) return found;

            foreach (var pair in pairs)
            {
                var source = pair.Key ?? "";
                var target = pair.Value ?? "";
                if (source.Length == 0) continue;

                var sourceTags = Names(source);
                var targetTags = Names(target);

                if (sourceTags.Count != targetTags.Count)
                {
                    found.Add(Short(source) + ": source has " + sourceTags.Count
                        + " tag(s), target has " + targetTags.Count);
                    continue;
                }

                var missing = sourceTags.Except(targetTags).ToList();
                var added = targetTags.Except(sourceTags).ToList();

                if (missing.Count > 0 || added.Count > 0)
                    found.Add(Short(source) + ": tags differ by name ("
                        + (missing.Count > 0 ? "missing " + string.Join(", ", missing) : "")
                        + (missing.Count > 0 && added.Count > 0 ? "; " : "")
                        + (added.Count > 0 ? "unexpected " + string.Join(", ", added) : "") + ")");
            }

            return found;
        }

        /// <summary>
        /// <paramref name="target"/> with its trailing whitespace made to match the
        /// source's.
        ///
        /// <para>memoQ appends the source's trailing whitespace to whatever a
        /// provider returns, so a target that already carries it ends up with it
        /// twice - which memoQ's own QA then flags. Observed on a production job:
        /// one source ended in a space, the staged target ended in a space, and the
        /// grid came back with two.</para>
        ///
        /// <para>Leading whitespace is left alone. It is far rarer, and unlike the
        /// trailing case memoQ does not duplicate it.</para>
        /// </summary>
        public static string MatchTrailingWhitespace(string source, string target)
        {
            if (target == null) return null;
            if (string.IsNullOrEmpty(source)) return target;

            var trimmed = target.TrimEnd();

            // An all-whitespace target is not a translation; leave it untouched
            // rather than turn it into the source's trailing run.
            if (trimmed.Length == 0) return target;

            return trimmed + Trailing(source);
        }

        /// <summary>Whether <paramref name="target"/> would change.</summary>
        public static bool TrailingDiffers(string source, string target)
        {
            return !string.Equals(target, MatchTrailingWhitespace(source, target), StringComparison.Ordinal);
        }

        private static string Trailing(string text)
        {
            var i = text.Length;
            while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
            return text.Substring(i);
        }

        private static List<string> Names(string text)
        {
            return TagMarker.Matches(text ?? "").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        }

        /// <summary>A sentence naming the problems, or null when there are none.</summary>
        public static string Describe(List<string> problems)
        {
            if (problems == null || problems.Count == 0) return null;

            var named = string.Join("; ", problems.Take(Examples));
            var rest = problems.Count > Examples ? " and " + (problems.Count - Examples) + " more" : "";

            return problems.Count + " staged pair(s) have tag differences: " + named + rest
                 + ". These were staged anyway - a difference is not always an error - but a dropped "
                 + "placeholder reaches the translator's file, so check them before running Pre-translate.";
        }

        private static string Short(string text)
        {
            var one = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return "“" + (one.Length <= 40 ? one : one.Substring(0, 37) + "…") + "”";
        }
    }
}
