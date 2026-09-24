using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoQ.Addins.Common.DataStructures;
using MemoQ.MTInterfaces;
using Supervertaler.Core;
using Supervertaler.MemoQ.Settings;

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
            Segment[] tmTargets = null,
            Func<int, int?> statusOf = null)
        {
            var results = new TranslationResult[segments.Length];
            var batchSize = Math.Max(1, Math.Min(100, context.General.BatchSize));

            // Empty segments never reach the model; they are filled in directly so
            // the numbering the model sees has no gaps in it. A row that is only
            // tags or punctuation is filled with its own source (see
            // NothingToTranslate) - it used to be filled with nothing, which
            // memoQ wrote into the target, deleting the tag.
            var pending = new List<int>();
            var copied = 0;
            for (var i = 0; i < segments.Length; i++)
            {
                if (NothingToTranslate.Applies(segments[i]))
                {
                    results[i] = NothingToTranslate.Copy(segments[i]);
                    CaptureStore.Record(context, TagBridge.ToTaggedText(segments[i]), statusOf?.Invoke(i));
                    copied++;
                }
                else if (segments[i] == null || segments[i].IsEmptyText)
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
                CaptureStore.Record(context, tagged, statusOf?.Invoke(i));

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

            if (pending.Count == 0)
            {
                LogRows(results, servedFromStaging, copied, context.General);
                return results;
            }

            // Bridge mode: the rest have been captured for Claude to see, and
            // that is the whole job of this pass. Nothing goes to the model.
            if (context.General.BridgeMode)
            {
                // An ERROR for each, not an empty translation.
                //
                // This returned Segment.Empty until 2026-09-20, and memoQ does
                // exactly what it is told: it writes the empty string into the
                // target. So a row with no staged translation did not keep what
                // was in it - a fuzzy match, an earlier pass, a human's work - it
                // was silently cleared. Found on a live job where 32 rows came
                // back blank, every one of which had content before the run.
                //
                // A result carrying an Exception is how this SDK says "nothing for
                // this segment": memoQ shows the message under the grid and leaves
                // the target alone. The same mechanism the per-segment failure
                // path below already uses.
                //
                // Deliberately one message per row rather than silence. A
                // pre-translate that quietly does nothing to 32 rows is how this
                // went unnoticed until the rows were read one by one.
                var reason = AsMemoQError(new InvalidOperationException(
                    "No staged translation for this segment. Stage it over the Supervertaler MCP bridge, "
                    + "or switch off \"Pre-translate only captures and delivers staged translations\" to translate it with the model."));

                foreach (var i in pending)
                    results[i] = new TranslationResult { Exception = reason };

                PluginLog.Write($"batch: bridge mode - captured {pending.Count} segment(s), none staged, "
                    + "reported as no-result so memoQ leaves those targets untouched");
                LogRows(results, servedFromStaging, copied, context.General, captured: pending.Count);
                return results;
            }

            if (batchSize == 1 || pending.Count == 1)
            {
                foreach (var i in pending)
                    results[i] = await translateOne(segments[i], i, cancellationToken).ConfigureAwait(false);
                LogRows(results, servedFromStaging, copied, context.General);
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

                    for (var k = 0; k < chunk.Count; k++)
                    {
                        results[chunk[k]] = translated[k]
                            ?? await RetryAloneAsync(translateOne, segments[chunk[k]], chunk[k], cancellationToken)
                                .ConfigureAwait(false);
                    }
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

            LogRows(results, servedFromStaging, copied, context.General);
            return results;
        }

        /// <summary>
        /// One segment whose batch reply broke the output contract, asked for on
        /// its own. A failure there is reported on the row, never thrown: the
        /// batch it came from already succeeded.
        /// </summary>
        private static async Task<TranslationResult> RetryAloneAsync(
            Func<Segment, int, CancellationToken, Task<TranslationResult>> translateOne,
            Segment segment, int index, CancellationToken cancellationToken)
        {
            try
            {
                return await translateOne(segment, index, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new TranslationResult { Exception = AsMemoQError(ex) };
            }
        }

        /// <summary>
        /// Where the rows of one Pre-translate call came from. Staged translations
        /// and the model's look identical once they are in the grid, so this line
        /// is the only place a translator can see how much of a run the model
        /// wrote - and how many rows it left for them.
        /// </summary>
        private static void LogRows(TranslationResult[] results, int staged, int copied,
            SupervertalerGeneralSettings general, int captured = 0)
        {
            // Rows Claude Desktop mode deliberately left alone are counted as
            // captured, not as left for the translator: nothing went wrong with
            // them, and counting them as left made a normal capture pass read
            // like twenty failures.
            var left = results.Count(r => r?.Exception != null) - captured;
            var model = results.Count(r => r != null && r.Exception == null
                                           && r.Translation != null && !r.Translation.IsEmpty) - staged - copied;

            PluginLog.Write($"rows: {results.Length} - {staged} staged, {Math.Max(0, model)} by the model "
                + $"({general.Provider} / {general.Model}), "
                + (copied > 0 ? $"{copied} copied from the source (tags only), " : "")
                + (captured > 0 ? $"{captured} captured for Claude Desktop, " : "")
                + $"{Math.Max(0, left)} left for the translator");
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

            // The document's list markers, when the preview tool has told us
            // which file this is (#7). One plan per chunk: reading the .docx is
            // cached per path, the matching is a walk over the paragraphs the
            // preview tool holds, and the log line fires only when the answer
            // changes from the last chunk's.
            var structure = StructureMarkers.PlanFor(context);
            StructureMarkers.LogOnce(structure);

            // Numbering is 1-based and local to the request, which is what
            // BuildBatchUserPrompt and ParseBatchResponse agree on.
            var inputs = chunk
                .Select((s, i) => new BatchSegmentInput
                {
                    Number = i + 1,

                    // Marker first, then the text, when markers are being sent -
                    // and only on the first segment of a paragraph, which is
                    // what MarkerFor answers. Prefix with a null marker is the
                    // text alone.
                    SourceText = structure.Mode == StructureContextMode.Markers
                        ? StructureContext.Prefix(structure.MarkerFor(s.PlainText), TagBridge.ToTaggedText(s))
                        : TagBridge.ToTaggedText(s),

                    // The best fuzzy TM match for this row, when memoQ forwarded
                    // one. Carried per row rather than once per chunk because each
                    // row has its own match, or none.
                    FuzzySourceText = TaggedOrNull(tmSources, i),
                    FuzzyTargetText = TaggedOrNull(tmTargets, i)
                })
                .ToList();

            var segmentsPrompt = TranslationPrompt.BuildBatchUserPrompt(inputs);

            // Context is gathered once for the whole chunk. Terminology is the
            // union of every segment's matches; recalled pairs are keyed on the
            // chunk's text so the examples suit what is actually being translated.
            var joined = string.Join(" ", chunk.Select(s => s.PlainText));

            // Only termbases ticked AI reach the model: that tick is the
            // translator's decision about what leaves the machine, separate from
            // Read, which is what the grid shows them. Languages set immediately
            // before the lookup, never once per engine - memoQ builds one engine
            // per target language.
            TermIndex.UseLanguages(context.SourceLangCode, context.TargetLangCode);
            var ownTerms = context.GlossaryForModel(
                TermIndex.FindForModel(TermbaseSelection.CurrentProject, joined));

            context.WarnIfPromptFacesTheWrongWay();

            var recalled = general.UseDocumentContext
                ? DocumentMemory.GetRelevant(context.MemoryKey, chunk[0], SessionRunner.MaxRecalledPairs)
                : null;

            var instructions = PromptResolver.Resolve(
                general.PromptPath, general.SystemPrompt,
                PromptBuilder.DescribeLanguage(context.SourceLangCode),
                PromptBuilder.DescribeLanguage(context.TargetLangCode));

            var built = PromptBuilder.BuildForBatch(
                general, context.SourceLangCode, context.TargetLangCode,
                context.LastMetadata, recalled, ownTerms, instructions,
                context.KbContextBlock(), structure.Mode);

            // The context this batch produced goes in front of its own segments,
            // leaving the system prompt identical from one batch to the next -
            // which is the whole point, because the cache marker below covers the
            // system block as a unit.
            var system = built.System;
            var userPrompt = string.IsNullOrWhiteSpace(built.User)
                ? segmentsPrompt
                : built.User + Environment.NewLine + Environment.NewLine + segmentsPrompt;

            var apiKey = context.ApiKey;
            var cacheKey = TranslationCache.Key(
                general.Provider, general.Model, general.Endpoint, system, userPrompt);

            string raw;
            global::Supervertaler.Core.ApiUsage usage = null;

            if (TranslationCache.TryGet(cacheKey, out var cached))
            {
                raw = cached;
                PluginLog.Write($"batch of {chunk.Count}: served from cache");
            }
            else
            {
                // The first request of a run goes out alone (#5). Everything
                // after it reads the cache that request wrote, rather than three
                // more requests racing it and each paying write rate. One extra
                // round trip at the start of a job, once.
                var warming = await context.EnterWarmupAsync(cancellationToken).ConfigureAwait(false);

                // Resolved fresh for this request, so this is the model that will
                // actually be called - not the one the engine was built with.
                PluginLog.ModelInUse(general.Provider, general.Model);

                using (var client = new LlmClient(
                           SessionRunner.MapProviderForCore(general.Provider),
                           general.Model,
                           apiKey,
                           string.IsNullOrWhiteSpace(general.Endpoint) ? null : general.Endpoint.Trim()))
                {
                    try
                    {
                    // Pre-translate sends one batch after another with the same
                    // instructions and the same bank, so every batch after the
                    // first is a cache read at a tenth of the input rate. Asking
                    // for it was the other half of the fix: the marker was never
                    // requested here, so even a stable system prompt paid full
                    // price every time.
                    raw = await client.SendPromptAsync(userPrompt, system,
                            cancellationToken: cancellationToken,
                            enablePromptCaching: true)
                        .ConfigureAwait(false);

                    // Read before the client is disposed. This is the only way
                    // the translator can tell whether the caching above is
                    // actually working: cached should be near zero on the first
                    // batch of a run and carry the system prompt on every batch
                    // after it. A saving nobody can see is a saving nobody can
                    // check - and this one is the difference between about seven
                    // dollars and about seventy cents on a long job.
                    usage = client.LastUsage;
                    }
                    finally
                    {
                        // In a finally, and outside the success path: a warming
                        // request that throws must still open the gate, or every
                        // other batch waits on one that will never arrive.
                        if (warming) context.LeaveWarmup();
                    }
                }

                TranslationCache.Set(cacheKey, raw);
            }

            // With the inputs, not just the count: a translation with numbered
            // lines of its own - procedure steps, a contents list - is then kept
            // whole rather than cut apart at every "2." (core f13b1d8), and a list
            // inside one segment is told apart from the next segment's number by
            // the sources that were sent.
            var parsed = TranslationPrompt.ParseBatchResponse(raw, inputs);

            // A marker the model echoed at the start of a target is removed before
            // it can reach the document, and said so: that line is the evidence,
            // per model, of whether the rule is obeyed. Only when markers were
            // sent - with the mode off the reply is whatever it always was.
            if (structure.Mode == StructureContextMode.Markers)
            {
                for (var k = 0; k < parsed.Count; k++)
                {
                    parsed[k].Translation = StructureContext.Strip(parsed[k].Translation, out var echoed);
                    if (echoed)
                        PluginLog.Write($"structure: the model echoed a list marker at the start of segment {k + 1} "
                            + "of this batch; removed before it reached the document");
                }
            }

            var tokens = usage == null ? "" :
                $" | tokens: in {usage.RegularInputTokens:N0}"
                + (usage.CacheReadTokens > 0 ? $" (cached {usage.CacheReadTokens:N0})" : "")
                + (usage.CacheWriteTokens > 0 ? $" (cache write {usage.CacheWriteTokens:N0})" : "")
                + $" out {usage.OutputTokens:N0}";

            PluginLog.Write($"batch: {chunk.Count} segment(s) sent, {parsed.Count} returned | "
                + $"terms: {ownTerms?.Count ?? 0} | recall: {recalled?.Count ?? 0}" + tokens);

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
                var source = TagBridge.ToTaggedText(chunk[i]);

                // A reply carrying commentary is not served from the batch (see
                // ReplyCheck). It is left null, and the caller sends that segment
                // again on its own through the single-segment path, which asks
                // with the contract restated and refuses a second offence. The
                // rest of the batch is unaffected.
                var problem = ReplyCheck.Problem(source, match?.Translation);
                if (problem != null)
                {
                    PluginLog.Write($"reply: retried - {problem}, segment {i + 1} of this batch; "
                        + "asking for it on its own");
                    continue;
                }

                var tags = ReplyCheck.TagDifference(source, match?.Translation);
                if (tags != null)
                    PluginLog.Write($"reply: tags differ from the source ({tags}), segment {i + 1} "
                        + "of this batch - served; worth a look");

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
