using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoQ.Addins.Common.DataStructures;
using MemoQ.MTInterfaces;
using Supervertaler.MemoQ.Core;

namespace Supervertaler.MemoQ
{
    /// <summary>
    /// The path memoQ actually uses — for interactive lookup and, via the array
    /// overloads, for Pre-translate.
    ///
    /// Implements <see cref="ISessionWithMetadata"/> as well as
    /// <see cref="ISession"/>, because the metadata overload is the only way an MT
    /// plugin learns which project and document it is working in, and which
    /// segment it is on. memoQ's own bundled ModernMT plugin and the current Lara
    /// plugin both do exactly this — neither implements <c>IRichSession</c>.
    ///
    /// <see cref="MTRequestMetadata"/> gives us <c>Client</c>, <c>Domain</c> and
    /// <c>Subject</c> to put in the prompt, and a <c>DocumentID</c> to key
    /// <see cref="DocumentMemory"/> on, which is what lets earlier confirmed
    /// segments inform later ones.
    /// </summary>
    internal sealed class SupervertalerSession : ISession, ISessionWithMetadata, IDisposable
    {
        private readonly EngineContext _context;

        public SupervertalerSession(EngineContext context)
        {
            _context = context;
        }

        // ---- ISession (no metadata) -------------------------------------------

        public TranslationResult TranslateCorrectSegment(Segment segm, Segment tmSource, Segment tmTarget)
        {
            return TranslateOne(segm, tmSource, tmTarget, null);
        }

        public TranslationResult[] TranslateCorrectSegment(Segment[] segs, Segment[] tmSources, Segment[] tmTargets)
        {
            return TranslateMany(segs, tmSources, tmTargets, null);
        }

        // ---- ISessionWithMetadata ---------------------------------------------

        public TranslationResult TranslateCorrectSegment(
            Segment segm, Segment tmSource, Segment tmTarget, MTRequestMetadata metadata)
        {
            _context.NoteMetadata(metadata);
            LogMetadataOnce(metadata);
            return TranslateOne(segm, tmSource, tmTarget, RowAt(metadata, 0));
        }

        public TranslationResult[] TranslateCorrectSegment(
            Segment[] segs, Segment[] tmSources, Segment[] tmTargets, MTRequestMetadata metadata)
        {
            _context.NoteMetadata(metadata);
            LogMetadataOnce(metadata);
            return TranslateMany(segs, tmSources, tmTargets, metadata);
        }

        // ---- shared -----------------------------------------------------------

        /// <summary>
        /// The array overload, which memoQ uses for Pre-translate. Segments go to
        /// the model in batches so that prompts written for batch translation —
        /// the ones that refer to segment numbers — are given the shape they
        /// expect. Set Parallel requests' companion BatchSize to 1 to send them
        /// one at a time.
        /// </summary>
        private TranslationResult[] TranslateMany(
            Segment[] segs, Segment[] tmSources, Segment[] tmTargets, MTRequestMetadata metadata)
        {
            if (segs == null) return new TranslationResult[0];

            try
            {
                // The index is what carries the fuzzy match through: memoQ's TM
                // arrays are parallel to the segment array, and the batch
                // translator hands back the row it is working on.
                return Task.Run(() => BatchTranslator.TranslateAsync(
                        segs, _context,
                        (segment, i, ct) => Task.FromResult(
                            TranslateOne(segment, At(tmSources, i), At(tmTargets, i), RowAt(metadata, i))),
                        CancellationToken.None, tmSources, tmTargets))
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                PluginLog.Write("Batch translation failed entirely; falling back to one at a time", ex);

                var results = new TranslationResult[segs.Length];
                for (var i = 0; i < segs.Length; i++)
                    results[i] = TranslateOne(segs[i], At(tmSources, i), At(tmTargets, i), RowAt(metadata, i));
                return results;
            }
        }

        /// <summary>
        /// memoQ may pass a shorter array, or none at all, when only some rows
        /// have a TM hit above the threshold.
        /// </summary>
        private static Segment At(Segment[] segments, int index)
        {
            return segments != null && index >= 0 && index < segments.Length ? segments[index] : null;
        }

        /// <summary>
        /// The metadata for one row of the request. memoQ documents SegmentIndex
        /// as the row's position, so that is matched first; the positional
        /// fallback covers a request that leaves it unset.
        /// </summary>
        private static SegmentMetadata RowAt(MTRequestMetadata metadata, int index)
        {
            var rows = metadata?.SegmentLevelMetadata;
            if (rows == null || rows.Count == 0) return null;

            foreach (var row in rows)
                if (row != null && row.SegmentIndex == index) return row;

            return index >= 0 && index < rows.Count ? rows[index] : null;
        }

        private TranslationResult TranslateOne(
            Segment source, Segment tmSource, Segment tmTarget, SegmentMetadata row)
        {
            if (source == null || source.IsEmptyText)
                return new TranslationResult { Translation = Segment.Empty, Confidence = 0 };

            var bundle = new TranslationBundle { Source = source };

            // The best fuzzy TM match, when the user has routed it to us under
            // "Send best fuzzy TM match to". It is a human-approved rendering of
            // nearly this segment, so it goes into the prompt as the thing to
            // adapt rather than as background context.
            if (tmSource != null && tmTarget != null
                && !tmSource.IsEmptyText && !tmTarget.IsEmptyText)
            {
                Context(bundle).Add(new SegmentContextItem
                {
                    Kind = PromptBuilder.FuzzyMatchKind,
                    SourceSegment = tmSource,
                    TargetSegment = tmTarget
                });
            }

            // memoQ's state for this row. Only a rejected one changes the request,
            // but it is carried whenever it is known so the prompt builder decides
            // rather than the session.
            if (row != null)
            {
                Context(bundle).Add(new SegmentContextItem
                {
                    Kind = PromptBuilder.RowStatusKind,
                    NumericValue = row.SegmentStatus
                });
            }

            try
            {
                // memoQ calls this synchronously from a UI-adjacent thread. Running
                // the await chain on the thread pool via Task.Run detaches it from
                // any SynchronizationContext, so the blocking wait below cannot
                // deadlock against a continuation that wants the caller's thread.
                var translated = Task.Run(() => SessionRunner.TranslateAsync(
                        bundle, _context, CancellationToken.None))
                    .GetAwaiter().GetResult();

                // SessionRunner logs the per-segment line — it is the only place
                // that knows both the sizes and how much recalled context was used.
                return translated;
            }
            catch (Exception ex)
            {
                PluginLog.Write("Interactive translation failed", ex);

                // memoQ shows an MTException's message under the translation grid.
                // Anything else gets a poorer presentation, and the SDK asks for
                // this explicitly.
                return new TranslationResult { Exception = BatchTranslator.AsMemoQError(ex) };
            }
        }

        private static System.Collections.Generic.List<SegmentContextItem> Context(TranslationBundle bundle)
        {
            return bundle.SegmentContext
                ?? (bundle.SegmentContext = new System.Collections.Generic.List<SegmentContextItem>());
        }

        private bool _metadataLogged;

        /// <summary>
        /// Records what memoQ actually populates on <see cref="MTRequestMetadata"/>
        /// — once per session, since it is the same for every segment. Field names
        /// and presence only; Client and Subject can identify a real customer, so
        /// their values stay out of the log.
        /// </summary>
        private void LogMetadataOnce(MTRequestMetadata metadata)
        {
            if (_metadataLogged || metadata == null) return;
            _metadataLogged = true;

            PluginLog.Write("  metadata: "
                + $"project={(metadata.ProjectGuid == Guid.Empty ? "(empty)" : "set")}, "
                + $"document={(metadata.DocumentID == Guid.Empty ? "(empty)" : "set")}, "
                + $"client={(string.IsNullOrEmpty(metadata.Client) ? "(empty)" : "set")}, "
                + $"domain={(string.IsNullOrEmpty(metadata.Domain) ? "(empty)" : "set")}, "
                + $"subject={(string.IsNullOrEmpty(metadata.Subject) ? "(empty)" : "set")}, "
                + $"segmentLevel={metadata.SegmentLevelMetadata?.Count ?? 0} item(s){DescribeRows(metadata)}, "
                + $"held={DocumentMemory.CountFor(_context.MemoryKey)} confirmed pair(s)");
        }

        /// <summary>
        /// What memoQ actually puts in the per-row metadata. The SDK says the
        /// field exists and nothing else, so this records the shape of it once per
        /// session: whether row identities arrive, and which states. Row GUIDs are
        /// not logged — they identify a customer's segments.
        /// </summary>
        private static string DescribeRows(MTRequestMetadata metadata)
        {
            var rows = metadata?.SegmentLevelMetadata;
            if (rows == null || rows.Count == 0) return string.Empty;

            var withId = 0;
            var states = new System.Collections.Generic.Dictionary<int, int>();

            foreach (var row in rows)
            {
                if (row == null) continue;
                if (row.SegmentID != Guid.Empty) withId++;

                var status = row.SegmentStatus;
                states[status] = states.TryGetValue(status, out var n) ? n + 1 : 1;
            }

            var described = string.Join(", ", states
                .OrderBy(kv => kv.Key)
                .Select(kv => RowStatus.Describe(kv.Key) + " x" + kv.Value));

            return $" [{withId} with a row id; {described}]";
        }

        public void Dispose() { }
    }
}
