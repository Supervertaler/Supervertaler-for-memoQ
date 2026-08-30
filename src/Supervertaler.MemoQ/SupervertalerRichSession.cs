using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoQ.Addins.Common.DataStructures;
using MemoQ.MTInterfaces;
using Supervertaler.MemoQ.Core;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ
{
    /// <summary>
    /// The batch path, and the one that makes this plugin worth building.
    ///
    /// Each <see cref="TranslationBundle"/> arrives with its own context lists —
    /// termbase hits, forbidden terms, project metadata, neighbouring segments —
    /// so every request can be given the surroundings that a good patent or
    /// technical translation depends on, without the plugin ever touching the
    /// project.
    ///
    /// One LLM call per segment, run concurrently. Batching several segments into
    /// one call is cheaper and gives the model more to work with, but it also
    /// makes a single malformed response spoil a whole group and forces
    /// index-alignment logic that is easy to get subtly wrong. That is the next
    /// slice, once the round trip is proven; <c>BatchSize</c> is already in the
    /// settings waiting for it.
    /// </summary>
    internal sealed class SupervertalerRichSession : IRichSession, IRichSession2, IDisposable
    {
        private readonly EngineContext _context;

        public SupervertalerRichSession(EngineContext context)
        {
            _context = context;
        }

        public Task<IReadOnlyList<TranslationResult>> TranslateBundlesAsync(
            IReadOnlyList<TranslationBundle> bundles,
            CancellationToken cancellationToken)
        {
            return TranslateBundlesAsync(bundles, null, cancellationToken);
        }

        public async Task<IReadOnlyList<TranslationResult>> TranslateBundlesAsync(
            IReadOnlyList<TranslationBundle> bundles,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken)
        {
            if (bundles == null || bundles.Count == 0)
                return new List<TranslationResult>();

            PluginLog.Write($"TranslateBundlesAsync: {bundles.Count} bundle(s), "
                + $"{_context.SourceLangCode} -> {_context.TargetLangCode}"
                + (parameters != null && parameters.Count > 0
                    ? ", params: " + string.Join(", ", parameters.Select(p => p.Key + "=" + p.Value))
                    : string.Empty));

            LogContextShape(bundles[0]);

            // memoQ already limits how many sessions run at once via
            // IParallelEngine.MaxDegreeOfParallelism, but it can still hand a
            // single session a large list — so gate inside the session too.
            var gate = new SemaphoreSlim(Math.Max(1, _context.General.MaxParallelRequests));

            var tasks = bundles.Select(async bundle =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await SessionRunner
                        .TranslateAsync(bundle, _context, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Attach the failure to the individual result instead of
                    // throwing. memoQ shows it against that segment and carries on
                    // with the rest, which is what a translator wants from a
                    // 2,000-segment pre-translate run.
                    PluginLog.Write("Bundle translation failed", ex);
                    return new TranslationResult { Exception = ex };
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results;
        }

        /// <summary>
        /// One-off diagnostic: records which context kinds memoQ actually populates
        /// and in which of the two lists. The SDK documents the kinds but not the
        /// placement, and this is how we find out for real.
        /// </summary>
        private static void LogContextShape(TranslationBundle bundle)
        {
            if (bundle == null) return;

            var plain = bundle.PlainTextContext == null
                ? "(none)"
                : string.Join(", ", bundle.PlainTextContext.Where(i => i != null)
                    .GroupBy(i => i.Kind).Select(g => g.Key + " x" + g.Count()));

            var segs = bundle.SegmentContext == null
                ? "(none)"
                : string.Join(", ", bundle.SegmentContext.Where(i => i != null)
                    .GroupBy(i => i.Kind).Select(g => g.Key + " x" + g.Count()));

            PluginLog.Write($"  context: plainText[{plain}]  segment[{segs}]");
        }

        public void Dispose() { }
    }

    /// <summary>
    /// The single place a bundle becomes a translated segment. Shared by the
    /// interactive and batch sessions so both paths build prompts, call the model
    /// and re-tag the result identically.
    /// </summary>
    internal static class SessionRunner
    {
        /// <summary>
        /// How many confirmed pairs to show the model. Enough to establish a
        /// register and the recurring terms of a document; small enough that a
        /// 2,000-segment pre-translate does not turn every request into an essay.
        /// </summary>
        private const int MaxRecalledPairs = 5;

        public static async Task<TranslationResult> TranslateAsync(
            TranslationBundle bundle,
            EngineContext context,
            CancellationToken cancellationToken)
        {
            if (bundle?.Source == null || bundle.Source.IsEmptyText)
                return new TranslationResult { Translation = Segment.Empty, Confidence = 0 };

            var general = context.General;
            var apiKey = context.Settings.SecureSettings?.ApiKey;

            // What the translator has already confirmed in this document, most
            // similar first. This is the substitute for the terminology and
            // neighbouring-segment context that IRichSession2 would have carried:
            // fewer signals, but every one of them human-approved.
            var recalled = general.UseDocumentContext
                ? DocumentMemory.GetRelevant(context.MemoryKey, bundle.Source, MaxRecalledPairs)
                : null;

            // Our own glossary, via the TB plugin's index. memoQ will not hand an
            // MT plugin its terminology, so we are the terminology source.
            var ownTerms = general.UseTerminologyContext
                ? TermIndex.Find(SharedSettings.GlossaryPath, bundle.Source.PlainText)
                : null;

            var prompt = PromptBuilder.Build(
                bundle, general, context.SourceLangCode, context.TargetLangCode,
                context.LastMetadata, recalled, ownTerms);

            using (var client = new LlmClient(general, apiKey))
            {
                var raw = await client.TranslateAsync(prompt, cancellationToken).ConfigureAwait(false);

                var translation = TagBridge.FromTaggedText(raw?.Trim(), bundle.Source);

                // Sizes and counts only, never the text: this log gets pasted into
                // issues and the source is confidential client material.
                //
                // "used N of M" is the line that matters when judging output. The
                // metadata line reports how many confirmed pairs are HELD for the
                // document; this reports how many the overlap filter actually put
                // in front of the model, which can be zero even when several are
                // held.
                PluginLog.Write(
                    $"translate: {bundle.Source.PlainText?.Length ?? 0} src chars, "
                    + $"{bundle.Source.NumberOfInlineTags} tag(s) -> "
                    + $"{translation?.PlainText?.Length ?? 0} target chars, "
                    + $"{translation?.NumberOfInlineTags ?? 0} tag(s) | "
                    + $"recall: used {recalled?.Count ?? 0} of "
                    + $"{DocumentMemory.CountFor(context.MemoryKey)} held | "
                    + $"terms: {ownTerms?.Count ?? 0}");

                return new TranslationResult
                {
                    Translation = translation,

                    // Confidence and ConfidenceProviderName are memoQ's AIQE
                    // (AI Quality Estimation) fields, NOT a generic match rate.
                    // Setting them makes memoQ display "AIQE: <name>  Score: (n%)"
                    // against every segment and treats us as a quality-estimation
                    // provider alongside the real ones it offers (COMET et al.,
                    // configured on the AIQE tab of the MT settings dialog).
                    //
                    // An earlier version set 0.75 / "Supervertaler" and every
                    // segment came back labelled "AIQE: Supervertaler (75%)" — a
                    // hardcoded constant presented as a per-segment quality
                    // judgement. We do not estimate quality, so we claim nothing;
                    // memoQ then shows the hit as plain MT. Only ever set these if
                    // Supervertaler actually scores its own output.

                    // Info is free text shown under the hit in Translation results,
                    // and is the right place to say where the translation came from.
                    Info = general.Provider + " / " + general.Model
                };
            }
        }
    }
}
