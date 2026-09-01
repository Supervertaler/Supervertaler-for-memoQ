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
        /// Maps this plugin's provider names onto Supervertaler.Core's provider
        /// constants. The two lists are deliberately separate: the settings value
        /// is persisted in memoQ's MT settings resource and cannot be renamed
        /// without breaking existing projects, whereas the Core constants are
        /// shared with the Trados plugin.
        /// </summary>
        internal static string MapProviderForCore(string provider)
        {
            switch (provider)
            {
                case LlmProviders.OpenAI: return global::Supervertaler.Core.LlmModels.ProviderOpenAi;
                case LlmProviders.Google: return global::Supervertaler.Core.LlmModels.ProviderGemini;
                default: return global::Supervertaler.Core.LlmModels.ProviderClaude;
            }
        }

        /// <summary>
        /// How many confirmed pairs to show the model. Enough to establish a
        /// register and the recurring terms of a document; small enough that a
        /// 2,000-segment pre-translate does not turn every request into an essay.
        /// </summary>
        internal const int MaxRecalledPairs = 5;

        public static async Task<TranslationResult> TranslateAsync(
            TranslationBundle bundle,
            EngineContext context,
            CancellationToken cancellationToken)
        {
            if (bundle?.Source == null || bundle.Source.IsEmptyText)
                return new TranslationResult { Translation = Segment.Empty, Confidence = 0 };

            var general = context.General;
            var apiKey = context.Settings.SecureSettings?.ApiKey;

            var taggedSource = TagBridge.ToTaggedText(bundle.Source);

            // Write down what memoQ showed us — the only full-document view a
            // memoQ plugin can ever have. The MCP bridge and AutoPrompt read it.
            CaptureStore.Record(context, taggedSource);

            // A translation staged over the MCP bridge wins over cache and LLM
            // both: someone who could see the whole document already decided
            // what this segment should say.
            var staged = StagedTranslations.TryGet(
                taggedSource, (context.SourceLangCode ?? "?") + "-" + (context.TargetLangCode ?? "?"));
            if (staged != null)
            {
                PluginLog.Write($"translate: served staged translation ({staged.Label})");
                return new TranslationResult
                {
                    Translation = TagBridge.FromTaggedText(staged.Target, bundle.Source),
                    Info = staged.Label + " (staged via Supervertaler MCP)"
                };
            }

            // Bridge mode: captured, nothing staged, and the model is off limits.
            // An empty result is the honest answer — memoQ shows no hit for the
            // row, and the segment is now visible to Claude over the bridge.
            if (general.BridgeMode)
            {
                PluginLog.Write("translate: bridge mode — captured, not translated");
                return new TranslationResult { Translation = Segment.Empty, Confidence = 0 };
            }

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

            // A selected library prompt wins over the typed instructions; the
            // typed ones are the fallback when nothing is selected or the prompt
            // has gone missing.
            var instructions = PromptResolver.Resolve(
                general.PromptPath, general.SystemPrompt,
                PromptBuilder.DescribeLanguage(context.SourceLangCode),
                PromptBuilder.DescribeLanguage(context.TargetLangCode));

            var prompt = PromptBuilder.Build(
                bundle, general, context.SourceLangCode, context.TargetLangCode,
                context.LastMetadata, recalled, ownTerms, instructions);

            // memoQ asks for the same segment more than once — twice within two
            // seconds merely for landing on it — so an identical prompt is served
            // from memory rather than paid for again. Keyed on the whole prompt,
            // so a segment whose terminology or recalled context has changed still
            // gets a fresh answer.
            var cacheKey = TranslationCache.Key(
                general.Provider, general.Model, general.Endpoint, prompt.System, prompt.User);

            if (TranslationCache.TryGet(cacheKey, out var cached))
            {
                PluginLog.Write($"translate: {bundle.Source.PlainText?.Length ?? 0} src chars — "
                    + $"served from cache (hits {TranslationCache.Hits}, misses {TranslationCache.Misses})");

                return new TranslationResult
                {
                    Translation = TagBridge.FromTaggedText(cached, bundle.Source),
                    Info = general.Provider + " / " + general.Model
                };
            }

            // Supervertaler.Core's client, shared with the Trados plugin: the same
            // provider handling, model catalogue, pricing and usage accounting,
            // rather than the ~250-line stub this replaces.
            using (var client = new global::Supervertaler.Core.LlmClient(
                       MapProviderForCore(general.Provider),
                       general.Model,
                       apiKey,
                       string.IsNullOrWhiteSpace(general.Endpoint) ? null : general.Endpoint.Trim()))
            {
                var raw = await client.SendPromptAsync(
                    prompt.User,
                    prompt.System,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                TranslationCache.Set(cacheKey, raw?.Trim());

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
