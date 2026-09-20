using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Which staged source strings correspond to nothing in the project.
    ///
    /// <para>Staging matches on the source text, character for character, which
    /// asks an agent to re-emit every string exactly. That fails silently. On a
    /// live job four pairs were staged against a source in which one Chinese
    /// character had been transcribed wrongly - U+9AC2 for U+9ACB, indistinguishable
    /// at a glance - and staging answered "717 translation(s) staged" with no hint
    /// that four of them could never match anything. Those rows would have shipped
    /// untranslated behind a clean confirmation.</para>
    ///
    /// <para>The same trap waits on any non-ASCII source, a no-break space, a
    /// curly quotation mark, an ellipsis character or a trailing space.</para>
    ///
    /// <para>This is not the real fix - staging by part id would remove the class
    /// entirely - but it costs nothing and turns a silent miss into a sentence.</para>
    /// </summary>
    internal static class StagedSourceCheck
    {
        /// <summary>How many unmatched sources to name before summarising the rest.</summary>
        private const int Examples = 5;

        /// <summary>
        /// The staged sources that match nothing this plugin has ever seen.
        ///
        /// <para>Deliberately generous about what counts as a match, because a
        /// false alarm here is worse than a miss: it would teach the reader to
        /// ignore the warning. A source counts as known when memoQ has actually
        /// requested it, when it is the whole text of a live paragraph, or when it
        /// is contained in one - that last case being a sentence of a paragraph
        /// memoQ will split, which is legitimate and common.</para>
        ///
        /// <para>Returns an empty list when the plugin has seen nothing at all, so
        /// staging before a project is open never warns about everything.</para>
        /// </summary>
        public static List<string> Unmatched(IEnumerable<string> stagedSources)
        {
            var unmatched = new List<string>();
            if (stagedSources == null) return unmatched;

            var requested = new HashSet<string>(StringComparer.Ordinal);
            var paragraphs = new List<string>();

            try
            {
                foreach (var document in CaptureStore.Snapshot())
                    foreach (var source in document.Sources)
                        if (!string.IsNullOrEmpty(source)) requested.Add(source);

                foreach (var part in PreviewStore.Documents()
                             .SelectMany(d => PreviewStore.Rows(d.DocumentGuid)))
                    if (!string.IsNullOrEmpty(part.Source)) paragraphs.Add(part.Source);
            }
            catch (Exception ex)
            {
                // A check that throws must not stop a staging call from succeeding.
                PluginLog.Write("Staged source check failed; staging is unaffected", ex);
                return unmatched;
            }

            // Nothing to compare against: say nothing rather than everything.
            if (requested.Count == 0 && paragraphs.Count == 0) return unmatched;

            foreach (var source in stagedSources)
            {
                if (string.IsNullOrEmpty(source)) continue;
                if (requested.Contains(source)) continue;
                if (paragraphs.Any(p => p.IndexOf(source, StringComparison.Ordinal) >= 0)) continue;

                unmatched.Add(source);
            }

            return unmatched;
        }

        /// <summary>
        /// A sentence naming what did not match, or null when everything did.
        /// Sources are quoted and shortened, and only the first few are named.
        /// </summary>
        public static string Describe(List<string> unmatched)
        {
            if (unmatched == null || unmatched.Count == 0) return null;

            var named = string.Join(", ", unmatched.Take(Examples).Select(s => "“" + Short(s) + "”"));
            var rest = unmatched.Count > Examples ? " and " + (unmatched.Count - Examples) + " more" : "";

            return unmatched.Count + " of them match no segment this plugin has seen, so they will never reach the grid: "
                 + named + rest + ". Check the source text character for character against get_segments - "
                 + "a single wrong character is enough, and non-ASCII text, curly quotation marks and "
                 + "trailing spaces are where it usually happens.";
        }

        private static string Short(string text)
        {
            var one = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return one.Length <= 48 ? one : one.Substring(0, 45) + "…";
        }
    }
}
