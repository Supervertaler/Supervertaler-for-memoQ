using System;
using MemoQ.MTInterfaces;
using Supervertaler.MemoQ.Core;

namespace Supervertaler.MemoQ
{
    /// <summary>
    /// Receives every segment the translator confirms in memoQ, as they confirm it.
    ///
    /// This is the closest thing an MT plugin gets to watching the translator work,
    /// and both memoQ's bundled ModernMT plugin and the current Lara plugin
    /// implement it. It is what makes an engine "self-learning" from memoQ's point
    /// of view — the *Self-learning MT* dropdown in *Edit machine translation
    /// settings &gt; Settings* lists engines that advertise this.
    ///
    /// Right now confirmed pairs go into <see cref="DocumentMemory"/>, so later
    /// segments in the same document can be shown how the translator actually
    /// rendered similar material. That partly recovers what the unreachable
    /// <c>IRichSession2</c> would have given us for free — with the advantage that
    /// these examples are human-approved rather than merely adjacent.
    ///
    /// The obvious next use is to push them into a Supervertaler TM or memory bank,
    /// once Supervertaler.Core is shared between the two plugins.
    /// </summary>
    internal sealed class SupervertalerStoreSession : ISessionForStoringTranslations, IDisposable
    {
        private readonly EngineContext _context;

        public SupervertalerStoreSession(EngineContext context)
        {
            _context = context;
        }

        public void StoreTranslation(TranslationUnit tu)
        {
            Store(tu);
        }

        /// <summary>
        /// Batch store. The contract for the return value is not documented; the
        /// safe reading is one entry per input, so callers can correlate. Zero is
        /// returned for units we ignored and 1 for units we kept.
        /// </summary>
        public int[] StoreTranslation(TranslationUnit[] tus)
        {
            if (tus == null) return new int[0];

            var results = new int[tus.Length];
            for (var i = 0; i < tus.Length; i++)
                results[i] = Store(tus[i]) ? 1 : 0;

            return results;
        }

        private bool Store(TranslationUnit tu)
        {
            if (tu?.Source == null || tu.Target == null)
            {
                PluginLog.Write("StoreTranslation: skipped (no source or target)");
                return false;
            }

            try
            {
                // TranslationUnit carries no document identity, so it is attributed
                // to whichever document the translate path last reported. See the
                // note on EngineContext for why that is a heuristic.
                var key = _context.MemoryKey;

                var before = DocumentMemory.CountFor(key);
                DocumentMemory.Record(key, tu.Source, tu.Target);
                var after = DocumentMemory.CountFor(key);

                // memoQ creates a fresh store session for every confirmed segment,
                // so a per-session counter would always read 1. The document total
                // is the number worth reporting — and "held unchanged" distinguishes
                // a re-confirmation (source already known, target replaced) or an
                // empty target from a genuine new pair.
                PluginLog.Write($"StoreTranslation: {tu.Source.PlainText?.Length ?? 0} src chars -> "
                    + $"{tu.Target.PlainText?.Length ?? 0} target chars, "
                    + (after > before ? $"{after} held for this document"
                                      : $"{after} held (unchanged — re-confirmed or empty)")
                    + (_context.CurrentDocument == Guid.Empty ? " [no document id — shared bucket]" : string.Empty));

                return true;
            }
            catch (Exception ex)
            {
                // Never let this throw: memoQ calls it on the confirm path, and a
                // failure here would surface as the translator being unable to
                // confirm a segment.
                PluginLog.Write("StoreTranslation failed", ex);
                return false;
            }
        }

        public void Dispose() { }
    }
}
