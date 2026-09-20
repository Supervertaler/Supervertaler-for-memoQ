using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// One answer to the question a translator actually asks before confirming a
    /// document: did my translations land, and where did they not.
    ///
    /// <para>Before this, answering it meant reading every document's rows,
    /// reading the staged list, and joining the two by hand - once per check, and
    /// a real job needed the check three times. Worse, the staged list was too
    /// large to read at all, so the join could not be done from the tools alone.</para>
    ///
    /// <para>This compares the live grid against what is staged and reports the
    /// four outcomes that matter. It is deliberately a verdict rather than a dump:
    /// the counts are always returned, the rows are capped, and the rows it
    /// returns are the problems rather than the successes.</para>
    ///
    /// <para><b>Paragraphs, not grid rows.</b> The live view delivers paragraphs,
    /// and memoQ asks for translations per sentence. A paragraph that memoQ splits
    /// therefore reads as "not staged" here even when its sentences were staged
    /// individually. That is not a defect of this check, it is the same paragraph
    /// versus row gap that loses translations in the first place, and the check
    /// says so in its note rather than pretending otherwise.</para>
    /// </summary>
    internal static class StagingVerdict
    {
        /// <summary>What happened to one row.</summary>
        public sealed class Row
        {
            public string PartId;
            public string DocumentName;
            public string Source;

            /// <summary>matches, differs, empty, or not_staged.</summary>
            public string Verdict;
        }

        public sealed class Document
        {
            public string Name;
            public Guid Guid;
            public int Rows, Matches, Differs, Empty, NotStaged;
        }

        public sealed class Result
        {
            public List<Document> Documents = new List<Document>();
            public List<Row> Problems = new List<Row>();
            public int TotalRows, TotalMatches, TotalDiffers, TotalEmpty, TotalNotStaged;
            public bool Truncated;
        }

        /// <summary>
        /// Compares every live document, or one of them, against what is staged.
        /// <paramref name="maxProblems"/> caps the rows returned, never the counts.
        /// </summary>
        public static Result Build(Guid? onlyDocument, int maxProblems)
        {
            var result = new Result();

            foreach (var document in PreviewStore.Documents())
            {
                if (onlyDocument.HasValue && document.DocumentGuid != onlyDocument.Value) continue;

                var pair = (document.SourceLangCode ?? "?") + "-" + (document.TargetLangCode ?? "?");
                var summary = new Document { Name = document.DocumentName, Guid = document.DocumentGuid };

                foreach (var row in PreviewStore.Rows(document.DocumentGuid))
                {
                    summary.Rows++;

                    var staged = StagedTranslations.TryGetPeek(row.Source, pair)?.Target;
                    var target = row.Target ?? "";
                    string verdict;

                    if (staged == null) verdict = target.Length == 0 ? "not_staged_and_empty" : "not_staged";
                    else if (target.Length == 0) verdict = "empty";
                    else verdict = string.Equals(target, staged, StringComparison.Ordinal) ? "matches" : "differs";

                    switch (verdict)
                    {
                        case "matches": summary.Matches++; break;
                        case "differs": summary.Differs++; break;
                        case "empty": summary.Empty++; break;
                        default: summary.NotStaged++; break;
                    }

                    // Only the problems are listed. A row that matches needs no
                    // further attention and listing it is what made the old
                    // answer too large to read.
                    if (verdict == "matches") continue;

                    if (result.Problems.Count < maxProblems)
                        result.Problems.Add(new Row
                        {
                            PartId = row.PartId,
                            DocumentName = document.DocumentName,
                            Source = Short(row.Source),
                            Verdict = verdict
                        });
                    else result.Truncated = true;
                }

                result.Documents.Add(summary);
                result.TotalRows += summary.Rows;
                result.TotalMatches += summary.Matches;
                result.TotalDiffers += summary.Differs;
                result.TotalEmpty += summary.Empty;
                result.TotalNotStaged += summary.NotStaged;
            }

            return result;
        }

        /// <summary>A sentence a person can act on, rather than four numbers.</summary>
        public static string Note(Result r)
        {
            if (r.TotalRows == 0)
                return "No live document. Open the project in memoQ with the Supervertaler preview tool connected.";

            if (r.TotalEmpty > 0)
                return r.TotalEmpty + " row(s) are EMPTY although a translation is staged for them - that is the "
                     + "shape of a Pre-translate that cleared them. Check those first.";

            if (r.TotalNotStaged > 0)
                return r.TotalNotStaged + " row(s) have nothing staged against their paragraph text. Some of those "
                     + "are real gaps; others are paragraphs memoQ splits into several grid rows, where the sentences "
                     + "were staged individually and will land correctly. Run Pre-translate once and read the "
                     + "captured-requests document to tell the two apart.";

            if (r.TotalDiffers > 0)
                return r.TotalDiffers + " row(s) differ from what was staged, which usually means somebody edited "
                     + "them in memoQ after the fact. That is normal on a reviewed document.";

            return "Every live row carries the staged translation.";
        }

        private static string Short(string text)
        {
            var one = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return one.Length <= 80 ? one : one.Substring(0, 77) + "…";
        }
    }
}
