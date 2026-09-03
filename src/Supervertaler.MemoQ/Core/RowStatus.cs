using System.Collections.Generic;
using MemoQ.MTInterfaces;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// memoQ's translation states, as they arrive on
    /// <see cref="SegmentMetadata.SegmentStatus"/>.
    ///
    /// The MT SDK's own documentation says only that the field exists. The values
    /// are memoQ's <c>TranslationStates</c> constants, recovered from the installed
    /// assemblies; they are sparse on purpose, so a state memoQ adds later lands
    /// between two of these rather than colliding with one. Anything unrecognised
    /// is reported by number rather than guessed at.
    /// </summary>
    internal static class RowStatus
    {
        public const int NotStarted = 0;
        public const int PreTranslated = 1000;
        public const int PartiallyEdited = 2000;
        public const int ManuallyConfirmed = 3000;
        public const int Reviewer1Confirmed = 3200;
        public const int AssembledFromFragments = 4000;
        public const int Proofread = 5000;
        public const int MachineTranslated = 6000;
        public const int Rejected = 7000;

        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            { NotStarted, "not started" },
            { PreTranslated, "pre-translated" },
            { PartiallyEdited, "partially edited" },
            { ManuallyConfirmed, "confirmed" },
            { Reviewer1Confirmed, "confirmed by reviewer 1" },
            { AssembledFromFragments, "assembled from fragments" },
            { Proofread, "proofread" },
            { MachineTranslated, "machine translated" },
            { Rejected, "rejected" }
        };

        public static string Describe(int status)
        {
            return Names.TryGetValue(status, out var name) ? name : "state " + status;
        }

        /// <summary>
        /// The translator looked at a rendering and turned it down. Worth telling
        /// the model about: asked again without that knowledge it tends to return
        /// what was just rejected.
        /// </summary>
        public static bool IsRejected(int status) => status == Rejected;

        /// <summary>
        /// Someone has signed off on this row. Not a reason to refuse to translate
        /// it — memoQ only sends a confirmed row when the user's Pre-translate
        /// scope asked for it, and second-guessing that would be wrong — but worth
        /// recording.
        /// </summary>
        public static bool IsConfirmed(int status) =>
            status == ManuallyConfirmed || status == Reviewer1Confirmed || status == Proofread;
    }
}
