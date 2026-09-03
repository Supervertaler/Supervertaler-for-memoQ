using System;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// How a glossary's declared language pair relates to the project's.
    ///
    /// The glossary is a tab-separated file whose first column is matched against
    /// the source segment. Point an English-to-Dutch glossary at a Dutch-to-English
    /// project and every lookup misses: the terminology pane stays empty, the
    /// prompt gets no terms, and the terminology QA check reports a clean document
    /// because it found nothing to check. That happened, and nothing anywhere said
    /// why, because the format carried no language information at all.
    ///
    /// Files now declare it in a <c>#! source=… target=…</c> header. The
    /// classification mirrors the one the Trados plugin already applies to
    /// termbases, for the same reason it gives: treating every mismatch as
    /// "inverted" silently mishandles a glossary that matches neither side.
    ///
    /// Nothing here refuses to load or silently swaps columns. A term pair is not
    /// symmetric, least of all a forbidden one, so an inverted glossary is reported
    /// and left alone rather than flipped behind the user's back.
    /// </summary>
    internal static class GlossaryDirection
    {
        internal enum Relation
        {
            /// <summary>The glossary declares no languages, so there is nothing to compare.</summary>
            Undeclared,

            /// <summary>Its source column is the project's source language. Lookups will work.</summary>
            Aligned,

            /// <summary>Its columns are the right pair the wrong way round. Lookups will find nothing.</summary>
            Inverted,

            /// <summary>It is for some other language pair entirely.</summary>
            Unrelated
        }

        public static Relation Compare(
            string projectSource, string projectTarget,
            string glossarySource, string glossaryTarget)
        {
            if (string.IsNullOrWhiteSpace(glossarySource) || string.IsNullOrWhiteSpace(glossaryTarget)
                || string.IsNullOrWhiteSpace(projectSource))
                return Relation.Undeclared;

            if (Matches(projectSource, glossarySource)) return Relation.Aligned;

            // Inverted only when both sides line up the other way. A glossary whose
            // target happens to share a language with the project's source, but
            // whose other side is unrelated, is not a reversed copy of this pair.
            if (Matches(projectSource, glossaryTarget)
                && (string.IsNullOrWhiteSpace(projectTarget) || Matches(projectTarget, glossarySource)))
                return Relation.Inverted;

            return Relation.Unrelated;
        }

        /// <summary>
        /// Prefix matching in both directions, so <c>dut</c> lines up with
        /// <c>dut-NL</c> and <c>eng-GB</c> with <c>eng</c>. memoQ hands the plugin
        /// three-letter codes, sometimes with a region and sometimes without, from
        /// the same project.
        /// </summary>
        private static bool Matches(string a, string b)
        {
            a = Normalise(a);
            b = Normalise(b);
            if (a.Length == 0 || b.Length == 0) return false;

            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
                || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalise(string code)
        {
            return (code ?? string.Empty).Trim().Replace('_', '-');
        }

        /// <summary>A sentence for a log line or a dialog, or null when all is well.</summary>
        public static string Explain(Relation relation, string glossarySource, string glossaryTarget,
            string projectSource, string projectTarget)
        {
            var glossaryPair = glossarySource + " to " + glossaryTarget;
            var projectPair = projectSource + " to " + projectTarget;

            switch (relation)
            {
                case Relation.Inverted:
                    return $"the glossary is {glossaryPair} but this project is {projectPair}, "
                        + "so its first column is being matched against the wrong language and nothing will be found. "
                        + "Export a glossary for this direction, or reverse the file's columns.";

                case Relation.Unrelated:
                    return $"the glossary is {glossaryPair} and this project is {projectPair}. "
                        + "They have no language in common, so nothing will be found.";

                default:
                    return null;
            }
        }
    }
}
