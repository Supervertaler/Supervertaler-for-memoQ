using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoQ.Addins.Common.DataStructures;
using MemoQ.MTInterfaces;
using Supervertaler.Core;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Translates several segments in one request, using the same numbered
    /// request format the Trados plugin uses.
    ///
    /// The format is not an implementation detail — it is a contract with the
    /// prompt library. Prompts written for batch translation tell the model
    /// things like "segment numbers match the [SEGMENT XXXX] numbers in this
    /// batch"; against a one-segment-at-a-time request those instructions point
    /// at nothing, and the model quietly ignores the better half of a carefully
    /// tuned prompt. In a real library, 15 of 17 translate prompts were written
    /// that way.
    ///
    /// So both the request builder and the response parser come from
    /// <see cref="TranslationPrompt"/> in Supervertaler.Core rather than being
    /// written again here. One format, one parser, both plugins.
    ///
    /// <para>Batching only applies to the array overload memoQ uses for
    /// Pre-translate. Interactive lookup is one segment by definition and stays on
    /// the single-segment path.</para>
    /// </summary>
    internal static class BatchTranslator
    {
        /// <summary>
        /// Translates an array of segments, in chunks.
        ///
        /// A failed chunk falls back to translating its segments one at a time
        /// rather than failing them all: a batch can fail for reasons that have
        /// nothing to do with an individual segment — a truncated reply, a
        /// miscounted response — and losing twenty good segments to one bad reply
        /// is not a trade a translator would accept.
        /// </summary>
        /// <summary>
        /// Wraps a failure the way the MT SDK asks for: memoQ shows an
        /// <see cref="MTException"/>'s message under the translation grid, and
        /// presents anything else less helpfully. Both message slots get the same
        /// text because this plugin does not go through memoQ's localisation.
        /// Cancellation is passed through untouched, and an MTException is never
        /// wrapped twice.
        /// </summary>
        internal static Exception AsMemoQError(Exception ex)
        {
            if (ex == null || ex is MTException || ex is OperationCanceledException) return ex;

            var message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            return new MTException(message, message, ex);
        }

        public static async Task<TranslationResult[]> TranslateAsync(
            Segment[] segments,
            EngineContext context,
            Func<Segment, int, CancellationToken, Task<TranslationResult>> translateOne,
            CancellationToken cancellationToken,
            Segment[] tmSources = null,
            Segment[] tmTargets = null)
        {
            var results = new TranslationResult[segments.Length];
            var batchSize = Math.Max(1, Math.Min(100, context.General.BatchSize));

            // Empty segments never reach the model; they are filled in directly so
            // the numbering the model sees has no gaps in it.
            var pending = new List<int>();
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i] == null || segments[i].IsEmptyText)
                    results[i] = new TranslationResult { Translation = Segment.Empty, Confidence = 0 };
                else
                    pending.Add(i);
            }

            // Capture everything, and serve anything already staged over the MCP
            // bridge before it costs a request. Staged segments leave the pending
            // list entirely, so a fully staged document translates with zero LLM
            // calls — Claude already did the work, this run just delivers it.
            var langPair = (context.SourceLangCode ?? "?") + "-" + (context.TargetLangCode ?? "?");
            var servedFromStaging = 0;
            for (var k = pending.Count - 1; k >= 0; k--)
            {
                var i = pending[k];
                var tagged = TagBridge.ToTaggedText(segments[i]);
                CaptureStore.Record(context, tagged);

                var staged = StagedTranslations.TryGet(tagged, langPair);
                if (staged != null)
                {
                    results[i] = new TranslationResult
                    {
                        Translation = TagBridge.FromTaggedText(staged.Target, segments[i]),
                        Info = staged.Label + " (staged via Supervertaler MCP)"
                    };
                    pending.RemoveAt(k);
                    servedFromStaging++;
                }
            }

            if (servedFromStaging > 0)
                PluginLog.Write($"batch: {servedFromStaging} segment(s) served from staging, {pending.Count} left for the model");

            if (pending.Count == 0) return results;

            // Bridge mode: the rest have been captured for Claude to see, and
            // that is the whole job of this pass. Nothing goes to the model.
            if (context.General.BridgeMode)
            {
                foreach (var i in pending)
                    results[i] = new TranslationResult { Translation = Segment.Empty, Confidence = 0 };

                PluginLog.Write($"batch: bridge mode – captured {pending.Count} segment(s), not translated");
                return results;
            }

            if (batchSize == 1 || pending.Count == 1)
            {
                foreach (var i in pending)
                    results[i] = await translateOne(segments[i], i, cancellationToken).ConfigureAwait(false);
                return results;
            }

            for (var offset = 0; offset < pending.Count; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = pending.Skip(offset).Take(batchSize).ToList();

                try
                {
                    var translated = await TranslateChunkAsync(
                        chunk.Select(i => segments[i]).ToList(),
                        chunk.Select(i => At(tmSources, i)).ToList(),
                        chunk.Select(i => At(tmTargets, i)).ToList(),
                        context, cancellationToken)
                        .ConfigureAwait(false);

                    for (var k = 0; k < chunk.Count; k++) results[chunk[k]] = translated[k];
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PluginLog.Write($"Batch of {chunk.Count} failed; retrying them individually", ex);

                    foreach (var i in chunk)
                    {
                        try
                        {
                            results[i] = await translateOne(segments[i], i, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception single)
                        {
                            // MTException is what memoQ expects: it shows the
                            // message under the translation grid. A raw exception
                            // gets a less useful presentation.
                            results[i] = new TranslationResult { Exception = AsMemoQError(single) };
                        }
                    }
                }
            }

            return results;
        }

        private static string TaggedOrNull(List<Segment> segments, int index)
        {
            if (segments == null || index < 0 || index >= segments.Count) return null;
            var segment = segments[index];
            return segment == null || segment.IsEmptyText ? null : TagBridge.ToTaggedText(segment);
        }

        /// <summary>memoQ may send a shorter TM array, or none at all.</summary>
        private static Segment At(Segment[] segments, int index)
        {
            return segments != null && index >= 0 && index < segments.Length ? segments[index] : null;
        }

        private static async Task<TranslationResult[]> TranslateChunkAsync(
            List<Segment> chunk,
            List<Segment> tmSources,
            List<Segment> tmTargets,
            EngineContext context,
            CancellationToken cancellationToken)
        {
            var general = context.General;

            // Numbering is 1-based and local to the request, which is what
            // BuildBatchUserPrompt and ParseBatchResponse agree on.
            var inputs = chunk
                .Select((s, i) => new BatchSegmentInput
                {
                    Number = i + 1,
                    SourceText = TagBridge.ToTaggedText(s),

                    // The best fuzzy TM match for this row, when memoQ forwarded
                    // one. Carried per row rather than once per chunk because each
                    // row has its own match, or none.
                    FuzzySourceText = TaggedOrNull(tmSources, i),
                    FuzzyTargetText = TaggedOrNull(tmTargets, i)
                })
                .ToList();

            var userPrompt = TranslationPrompt.BuildBatchUserPrompt(inputs);

            // Context is gathered once for the whole chunk. Terminology is the
            // union of every segment's matches; recalled pairs are keyed on the
            // chunk's text so the examples suit what is actually being translated.
            var joined = string.Join(" ", chunk.Select(s => s.PlainText));
            var ownTerms = general.UseTerminologyContext
                ? TermIndex.Find(SharedSettings.GlossaryPath, joined)
                : null;

            context.WarnIfGlossaryFacesTheWrongWay();
            context.WarnIfPromptFacesTheWrongWay();

            var recalled = general.UseDocumentContext
                ? DocumentMemory.GetRelevant(context.MemoryKey, chunk[0], SessionRunner.MaxRecalledPairs)
                : null;

            var instructions = PromptResolver.Resolve(
                general.PromptPath, general.SystemPrompt,
                PromptBuilder.DescribeLanguage(context.SourceLangCode),
                PromptBuilder.DescribeLanguage(context.TargetLangCode));

            var system = PromptBuilder.BuildSystemOnly(
                general, context.SourceLangCode, context.TargetLangCode,
                context.LastMetadata, recalled, ownTerms, instructions);

            var apiKey = context.ApiKey;
            var cacheKey = TranslationCache.Key(
                general.Provider, general.Model, general.Endpoint, system, userPrompt);

            string raw;
            if (TranslationCache.TryGet(cacheKey, out var cached))
            {
                raw = cached;
                PluginLog.Write($"batch of {chunk.Count}: served from cache");
            }
            else
            {
                using (var client = new LlmClient(
                           SessionRunner.MapProviderForCore(general.Provider),
                           general.Model,
                           apiKey,
                           string.IsNullOrWhiteSpace(general.Endpoint) ? null : general.Endpoint.Trim()))
                {
                    raw = await client.SendPromptAsync(userPrompt, system, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }

                TranslationCache.Set(cacheKey, raw);
            }

            var parsed = TranslationPrompt.ParseBatchResponse(raw, chunk.Count);

            PluginLog.Write($"batch: {chunk.Count} segment(s) sent, {parsed.Count} returned | "
                + $"terms: {ownTerms?.Count ?? 0} | recall: {recalled?.Count ?? 0}");

            // A short reply is the failure worth catching: silently leaving the
            // tail untranslated would look like the model declining to translate
            // those segments rather than like a broken response.
            if (parsed.Count < chunk.Count)
                throw new InvalidOperationException(
                    $"The model returned {parsed.Count} translations for {chunk.Count} segments.");

            var results = new TranslationResult[chunk.Count];
            for (var i = 0; i < chunk.Count; i++)
            {
                var match = parsed.FirstOrDefault(p => p.Number == i + 1);

                results[i] = new TranslationResult
                {
                    Translation = TagBridge.FromTaggedText(match?.Translation?.Trim(), chunk[i]),
                    Info = general.Provider + " / " + general.Model
                };
            }

            return results;
        }
    }
}
