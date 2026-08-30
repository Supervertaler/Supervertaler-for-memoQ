using System;
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
            // tmSource/tmTarget are the fuzzy-correction inputs. The director
            // reports SupportFuzzyForwarding = false, so memoQ passes null here.
            return TranslateOne(segm);
        }

        public TranslationResult[] TranslateCorrectSegment(Segment[] segs, Segment[] tmSources, Segment[] tmTargets)
        {
            return TranslateMany(segs);
        }

        // ---- ISessionWithMetadata ---------------------------------------------

        public TranslationResult TranslateCorrectSegment(
            Segment segm, Segment tmSource, Segment tmTarget, MTRequestMetadata metadata)
        {
            _context.NoteMetadata(metadata);
            LogMetadataOnce(metadata);
            return TranslateOne(segm);
        }

        public TranslationResult[] TranslateCorrectSegment(
            Segment[] segs, Segment[] tmSources, Segment[] tmTargets, MTRequestMetadata metadata)
        {
            _context.NoteMetadata(metadata);
            LogMetadataOnce(metadata);
            return TranslateMany(segs);
        }

        // ---- shared -----------------------------------------------------------

        /// <summary>
        /// The array overload, which memoQ uses for Pre-translate. Segments go to
        /// the model in batches so that prompts written for batch translation —
        /// the ones that refer to segment numbers — are given the shape they
        /// expect. Set Parallel requests' companion BatchSize to 1 to send them
        /// one at a time.
        /// </summary>
        private TranslationResult[] TranslateMany(Segment[] segs)
        {
            if (segs == null) return new TranslationResult[0];

            try
            {
                return Task.Run(() => BatchTranslator.TranslateAsync(
                        segs, _context,
                        (segment, ct) => Task.FromResult(TranslateOne(segment)),
                        CancellationToken.None))
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                PluginLog.Write("Batch translation failed entirely; falling back to one at a time", ex);

                var results = new TranslationResult[segs.Length];
                for (var i = 0; i < segs.Length; i++) results[i] = TranslateOne(segs[i]);
                return results;
            }
        }

        private TranslationResult TranslateOne(Segment source)
        {
            if (source == null || source.IsEmptyText)
                return new TranslationResult { Translation = Segment.Empty, Confidence = 0 };

            var bundle = new TranslationBundle { Source = source };

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
                return new TranslationResult { Exception = ex };
            }
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
                + $"segmentLevel={metadata.SegmentLevelMetadata?.Count ?? 0} item(s), "
                + $"held={DocumentMemory.CountFor(_context.MemoryKey)} confirmed pair(s)");
        }

        public void Dispose() { }
    }
}
