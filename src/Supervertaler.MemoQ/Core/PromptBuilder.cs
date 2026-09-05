using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MemoQ.Addins.Common.DataStructures;
using MemoQ.MTInterfaces;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Turns a memoQ <see cref="TranslationBundle"/> into an LLM prompt.
    ///
    /// This is the part of the plugin that has no Trados counterpart, because in
    /// Trados the equivalent context had to be dug out of the project ourselves.
    /// memoQ hands it over: <see cref="TranslationBundle.PlainTextContext"/> and
    /// <see cref="TranslationBundle.SegmentContext"/> carry the termbase hits, the
    /// forbidden terms, the project metadata and the surrounding segments, tagged
    /// by <see cref="ContextKinds"/>.
    ///
    /// The two lists are read defensively — each kind is looked for in both, since
    /// which list a given kind lands in is memoQ's choice. Anything unrecognised is
    /// ignored rather than guessed at.
    /// </summary>
    internal static class PromptBuilder
    {
        /// <summary>
        /// Ceiling on approved terms per request. Forbidden terms are not capped —
        /// there are few of them and each is a hard constraint.
        /// </summary>
        private const int MaxTermsInPrompt = 25;

        public sealed class BuiltPrompt
        {
            public string System { get; set; }
            public string User { get; set; }
        }

        public static BuiltPrompt Build(
            TranslationBundle bundle,
            SupervertalerGeneralSettings settings,
            string sourceLangCode,
            string targetLangCode,
            MTRequestMetadata metadata = null,
            IReadOnlyList<DocumentMemory.Pair> recalled = null,
            IReadOnlyList<TermIndex.Match> ownTerms = null,
            string instructions = null,
            string kbContext = null)
        {
            // `instructions` is the resolved prompt — a library prompt when one is
            // selected, otherwise the settings' own text. The settings fallback
            // keeps the Test-connection path and any direct caller working.
            var system = (instructions ?? settings.SystemPrompt ?? SupervertalerGeneralSettings.DefaultSystemPrompt)
                .Replace("{SOURCE_LANG}", DescribeLanguage(sourceLangCode))
                .Replace("{TARGET_LANG}", DescribeLanguage(targetLangCode));

            // The memory bank goes in the SYSTEM half, not with the rest of the
            // context, and the distinction is not cosmetic. Everything appended
            // below varies per request - this segment's terminology, the pairs
            // recalled for this chunk - whereas the bank is the same text for
            // every request in the job. Kept here it is a stable prefix the
            // provider's prompt cache can recognise; moved down it would be
            // re-read at full price on every row the translator lands on.
            //
            // It follows the instructions rather than preceding them: a prompt
            // is written for this job and the bank is standing background, so
            // where the two disagree the later text is the one a model weighs
            // more heavily, and that is the right way round.
            if (!string.IsNullOrWhiteSpace(kbContext))
            {
                system = system + Environment.NewLine + Environment.NewLine + kbContext.Trim();
            }

            var sb = new StringBuilder();

            // Outside the document-context switch on purpose. That switch governs
            // what we volunteer about the surrounding document; a forwarded fuzzy
            // match is a translation memory hit the user deliberately routed to
            // this engine in the MT settings, and turning off document context
            // should not silently discard it.
            AppendFuzzyMatch(sb, bundle);
            AppendRowState(sb, bundle);

            if (settings.UseDocumentContext)
            {
                // Two sources of project context, and in practice only the second
                // ever fires: ContextKinds.MetaInfo arrives on the bundle, which
                // memoQ only populates on the rich path, whereas MTRequestMetadata
                // arrives on ISessionWithMetadata, which it does use.
                AppendMetaInfo(sb, bundle);
                AppendRequestMetadata(sb, metadata);

                AppendSurroundingSegments(sb, bundle);
                AppendRecalled(sb, recalled);
            }

            if (settings.UseTerminologyContext)
            {
                // Two sources again, and again only the second ever fires in
                // practice: the bundle's Terminology/ForbiddenTerm lists need the
                // rich path, whereas ownTerms comes from our own TB plugin.
                AppendTerminology(sb, bundle, ownTerms);
                AppendForbiddenTerms(sb, bundle, ownTerms);
            }

            AppendFragmentRule(sb, bundle.Source);

            sb.AppendLine("Source segment:");
            sb.AppendLine(TagBridge.ToTaggedText(bundle.Source));

            return new BuiltPrompt { System = system, User = sb.ToString() };
        }

        /// <summary>
        /// A batch request, split by what changes.
        ///
        /// <para><see cref="BuiltPrompt.System"/> is the instructions and the
        /// memory bank: the same text for every request in a job. <see
        /// cref="BuiltPrompt.User"/> is everything that varies with the batch —
        /// project metadata, the pairs recalled for these segments, the
        /// terminology matched in them — which the caller puts in front of the
        /// segments themselves.</para>
        ///
        /// <para>Both halves used to be returned joined, as the system prompt.
        /// That is what made prompt caching impossible: the marker covers the
        /// whole system block, so a single varying line in it means every batch
        /// pays the full input rate for the instructions and the bank as well.
        /// The model sees the same text in the same order either way — system,
        /// then context, then segments — so this is a change of envelope rather
        /// than of prompt.</para>
        /// </summary>
        public static BuiltPrompt BuildForBatch(
            SupervertalerGeneralSettings settings,
            string sourceLangCode,
            string targetLangCode,
            MTRequestMetadata metadata,
            IReadOnlyList<DocumentMemory.Pair> recalled,
            IReadOnlyList<TermIndex.Match> ownTerms,
            string instructions,
            string kbContext = null)
        {
            var built = Build(new TranslationBundle { Source = SegmentBuilder.CreateFromString(" ") },
                settings, sourceLangCode, targetLangCode, metadata, recalled, ownTerms, instructions,
                kbContext);

            // Build appends a "Source segment:" trailer; a batch supplies its own
            // segments, so drop it.
            var user = built.User ?? string.Empty;
            var cut = user.LastIndexOf("Source segment:", StringComparison.Ordinal);
            if (cut >= 0) user = user.Substring(0, cut).TrimEnd();

            return new BuiltPrompt { System = built.System, User = user };
        }

        // ---- context sections -------------------------------------------------

        private static void AppendMetaInfo(StringBuilder sb, TranslationBundle bundle)
        {
            var items = PlainTextOfKind(bundle, ContextKinds.MetaInfo)
                .Where(i => !string.IsNullOrWhiteSpace(i.Text1))
                .ToList();
            if (items.Count == 0) return;

            sb.AppendLine("Project context:");
            foreach (var i in items)
            {
                sb.AppendLine(string.IsNullOrWhiteSpace(i.Text2)
                    ? "- " + i.Text1
                    : "- " + i.Text1 + ": " + i.Text2);
            }
            sb.AppendLine();
        }

        /// <summary>
        /// Project context from <see cref="MTRequestMetadata"/> — the channel that
        /// actually works for an MT plugin. Client, Domain and Subject are exactly
        /// the sort of steer a patent or technical translation needs, and memoQ
        /// fills them from the project's own metadata.
        /// </summary>
        private static void AppendRequestMetadata(StringBuilder sb, MTRequestMetadata metadata)
        {
            if (metadata == null) return;

            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(metadata.Client)) lines.Add("- Client: " + metadata.Client.Trim());
            if (!string.IsNullOrWhiteSpace(metadata.Domain)) lines.Add("- Domain: " + metadata.Domain.Trim());
            if (!string.IsNullOrWhiteSpace(metadata.Subject)) lines.Add("- Subject: " + metadata.Subject.Trim());

            if (lines.Count == 0) return;

            sb.AppendLine("Project context:");
            foreach (var l in lines) sb.AppendLine(l);
            sb.AppendLine();
        }

        /// <summary>
        /// Segments the translator has already confirmed in this document, pulled
        /// from <see cref="DocumentMemory"/>.
        ///
        /// These are worth more than the neighbouring segments IRichSession2 would
        /// have supplied: a neighbour is merely nearby, whereas each of these is a
        /// choice the translator actually made and approved. They are the closest
        /// thing we have to terminology enforcement while ContextKinds.Terminology
        /// stays out of reach.
        /// </summary>
        private static void AppendRecalled(StringBuilder sb, IReadOnlyList<DocumentMemory.Pair> recalled)
        {
            if (recalled == null || recalled.Count == 0) return;

            sb.AppendLine("Segments you already translated in this document, as the translator approved them.");
            sb.AppendLine("Follow their terminology and style:");
            foreach (var p in recalled)
            {
                sb.AppendLine("- " + p.Source);
                sb.AppendLine("  -> " + p.Target);
            }
            sb.AppendLine();
        }

        /// <summary>
        /// The kind we tag memoQ's forwarded fuzzy TM match with. Our own string
        /// rather than one of <see cref="ContextKinds"/>, and safe to invent:
        /// memoQ never populates <c>SegmentContext</c> for a third-party plugin,
        /// because the rich lookup path is reserved for its own AGT plugin. The
        /// list is therefore ours alone and there is nothing to collide with.
        /// </summary>
        public const string FuzzyMatchKind = "SupervertalerFuzzyMatch";


        /// <summary>
        /// Carries memoQ's translation state for the row being translated. Rides
        /// on the bundle for the same reason the fuzzy match does: it reaches
        /// PromptBuilder without a new parameter on every method between here and
        /// the session, and <see cref="SegmentContextItem"/> already has a numeric
        /// field to put it in.
        /// </summary>
        public const string RowStatusKind = "SupervertalerRowStatus";

        /// <summary>
        /// The best fuzzy translation-memory match, when the user has routed it to
        /// us under memoQ's <em>Send best fuzzy TM match to</em>. It is a rendering
        /// of nearly this same sentence that a human wrote and approved, so it is
        /// presented as the thing to adapt rather than as background reading, and
        /// it comes before everything else for the same reason.
        /// </summary>
        private static void AppendFuzzyMatch(StringBuilder sb, TranslationBundle bundle)
        {
            var match = SegmentsOfKind(bundle, FuzzyMatchKind)
                .FirstOrDefault(s => s.SourceSegment != null && s.TargetSegment != null
                                     && !s.SourceSegment.IsEmptyText && !s.TargetSegment.IsEmptyText);

            if (match == null) return;

            sb.AppendLine("Closest approved translation from the client's translation memory. A human "
                + "wrote and approved it for a nearly identical source, so follow it: keep its wording "
                + "and terminology wherever the source agrees, and change only what the segment to "
                + "translate actually differs in.");
            sb.AppendLine("- " + TagBridge.ToPlainText(match.SourceSegment));
            sb.AppendLine("  -> " + TagBridge.ToPlainText(match.TargetSegment));
            sb.AppendLine();
        }

        /// <summary>
        /// Asks for a fragment to be translated as a fragment.
        ///
        /// MatchPatch is why this exists: memoQ sends only the substring that
        /// differs from a TM hit, and a model given a phrase with a prompt written
        /// for sentences returns it capitalised and full-stopped, which is then
        /// spliced into the middle of a translation. There is no flag identifying
        /// a MatchPatch request, so the test is on the text, which means headings,
        /// list items and table cells get the same benefit.
        ///
        /// Deliberately conservative: anything ending in sentence punctuation, and
        /// anything long enough to be a sentence without it, is left alone.
        /// </summary>
        private static void AppendFragmentRule(StringBuilder sb, Segment source)
        {
            var text = source?.PlainText;
            if (string.IsNullOrWhiteSpace(text)) return;

            var trimmed = text.Trim();
            if (trimmed.Length > 60 || trimmed.IndexOf('\n') >= 0) return;
            if (".!?:;\u3002\uFF01\uFF1F".IndexOf(trimmed[trimmed.Length - 1]) >= 0) return;

            sb.AppendLine("This source is a fragment rather than a complete sentence, and may be spliced "
                + "into the middle of an existing translation. Translate it as a fragment: add no final "
                + "punctuation the source does not have, and capitalise the first word only if the "
                + "source's first word is capitalised.");
            sb.AppendLine();
        }

        /// <summary>
        /// What memoQ says about the row itself. Only one state changes the
        /// request: a rejected row means the translator read a rendering and
        /// turned it down, and a model asked again without being told that
        /// reliably offers the same thing back.
        ///
        /// Confirmed rows are deliberately not treated as "skip". memoQ only
        /// sends one when the user's Pre-translate scope asked for it, and
        /// overriding that choice from inside a plugin would be wrong.
        /// </summary>
        private static void AppendRowState(StringBuilder sb, TranslationBundle bundle)
        {
            var state = SegmentsOfKind(bundle, RowStatusKind).FirstOrDefault();
            if (state == null) return;

            if (!RowStatus.IsRejected((int)state.NumericValue)) return;

            sb.AppendLine("The translator has rejected a previous translation of this segment. Do not "
                + "repeat it. Produce a genuinely different rendering: reconsider the terminology, the "
                + "sentence structure and the register rather than paraphrasing what was refused.");
            sb.AppendLine();
        }

        private static void AppendSurroundingSegments(StringBuilder sb, TranslationBundle bundle)
        {
            // TextFlowContext: neighbouring source segments (untranslated).
            // TranslationPair: neighbouring segments that already have a confirmed
            // target — far more valuable, so they go in as pairs and come first.
            var pairs = SegmentsOfKind(bundle, ContextKinds.TranslationPair)
                .Where(s => s.SourceSegment != null && s.TargetSegment != null
                            && !s.SourceSegment.IsEmptyText && !s.TargetSegment.IsEmptyText)
                .ToList();

            if (pairs.Count > 0)
            {
                sb.AppendLine("Already-translated segments from this document (match their style and terminology):");
                foreach (var p in pairs)
                {
                    sb.AppendLine("- " + TagBridge.ToPlainText(p.SourceSegment));
                    sb.AppendLine("  -> " + TagBridge.ToPlainText(p.TargetSegment));
                }
                sb.AppendLine();
            }

            var flow = SegmentsOfKind(bundle, ContextKinds.TextFlowContext)
                .Where(s => s.SourceSegment != null && !s.SourceSegment.IsEmptyText)
                .ToList();

            if (flow.Count > 0)
            {
                sb.AppendLine("Surrounding source text (context only – do not translate):");
                foreach (var s in flow)
                    sb.AppendLine("- " + TagBridge.ToPlainText(s.SourceSegment));
                sb.AppendLine();
            }
        }

        private static void AppendTerminology(
            StringBuilder sb, TranslationBundle bundle, IReadOnlyList<TermIndex.Match> ownTerms)
        {
            var terms = new List<string>();

            if (ownTerms != null)
            {
                // Longest first and capped. A patent termbase legitimately holds
                // whole boilerplate clauses as "terms"; a segment matching several
                // would otherwise turn one request into thousands of tokens of
                // glossary.
                foreach (var m in ownTerms
                    .Where(x => x?.Entry != null && !x.Entry.Forbidden)
                    .OrderByDescending(x => x.Length)
                    .Take(MaxTermsInPrompt))
                {
                    terms.Add("- " + m.Entry.Source + " -> " + m.Entry.Target);
                }
            }

            foreach (var i in PlainTextOfKind(bundle, ContextKinds.Terminology))
            {
                if (string.IsNullOrWhiteSpace(i.Text1) || string.IsNullOrWhiteSpace(i.Text2)) continue;
                terms.Add("- " + i.Text1.Trim() + " -> " + i.Text2.Trim());
            }

            foreach (var s in SegmentsOfKind(bundle, ContextKinds.Terminology))
            {
                if (s.SourceSegment == null || s.TargetSegment == null) continue;
                var src = TagBridge.ToPlainText(s.SourceSegment).Trim();
                var trg = TagBridge.ToPlainText(s.TargetSegment).Trim();
                if (src.Length == 0 || trg.Length == 0) continue;
                terms.Add("- " + src + " -> " + trg);
            }

            if (terms.Count == 0) return;

            // Deliberately "preferred", not "required". A glossary entry can be
            // correct in general and wrong in a particular sentence: a real patent
            // termbase renders "applications" as "aanvragen", which is right for a
            // filing and wrong for "Mashup applications". Told the term was
            // mandatory, the model dutifully produced "Mashup-aanvragen".
            //
            // A human translator treats a termbase as a strong steer they may
            // override with reason, and the model should too. Forbidden terms are
            // the opposite and stay absolute — see AppendForbiddenTerms.
            //
            // This wording lives here rather than in the user's editable
            // instructions on purpose: it must hold however they have rewritten
            // their prompt.
            //
            // The second sentence exists because a prompt written by AutoPrompt
            // carries its own PROJECT-SPECIFIC GLOSSARY table, and this block is
            // usually the same terms again: the glossary file is exported from
            // that very table. Two lists of the same terms in one prompt, in two
            // notations, with nothing saying how they relate, is the state the
            // Trados plugin is in, and its own notes record the resulting
            // contradictions as an open question.
            //
            // The file wins, and the reason is not arbitrary. The terminology
            // plugin re-reads it whenever it changes, so it is what the translator
            // curated most recently; the prompt is a document they would have to
            // regenerate. Saying which one governs costs a sentence and removes
            // the whole class of conflict.
            sb.AppendLine("Client terminology. These are the glossary entries that occur in this segment,");
            sb.AppendLine("taken from the live glossary file. Where the instructions above also carry a");
            sb.AppendLine("glossary, this is the same terminology filtered to what is in front of you; if");
            sb.AppendLine("the two ever disagree, follow these, because this file is the one the translator");
            sb.AppendLine("edits. Use these renderings unless one is clearly wrong for this particular");
            sb.AppendLine("sentence – an entry can be right in general and wrong in context:");
            foreach (var t in terms.Distinct()) sb.AppendLine(t);
            sb.AppendLine();
        }

        private static void AppendForbiddenTerms(
            StringBuilder sb, TranslationBundle bundle, IReadOnlyList<TermIndex.Match> ownTerms)
        {
            var own = ownTerms == null
                ? Enumerable.Empty<string>()
                : ownTerms.Where(x => x?.Entry != null && x.Entry.Forbidden).Select(x => x.Entry.Target);

            var forbidden = own.Concat(PlainTextOfKind(bundle, ContextKinds.ForbiddenTerm)
                .Select(i => (i.Text2 ?? i.Text1 ?? string.Empty).Trim()))
                .Concat(SegmentsOfKind(bundle, ContextKinds.ForbiddenTerm)
                    .Select(s => TagBridge.ToPlainText(s.TargetSegment ?? s.SourceSegment).Trim()))
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();

            if (forbidden.Count == 0) return;

            // Absolute, and above everything the instructions say. A prompt's own
            // glossary can lock a rendering that the translator has since banned in
            // the file, and without this the model is handed both with no
            // tiebreaker.
            sb.AppendLine("Forbidden terms. These are absolute – never use them, in any form, even if they");
            sb.AppendLine("seem to fit and even if the instructions above name one as the locked rendering:");
            foreach (var t in forbidden) sb.AppendLine("- " + t);
            sb.AppendLine();
        }

        // ---- helpers ----------------------------------------------------------

        private static IEnumerable<PlainTextContextItem> PlainTextOfKind(TranslationBundle bundle, string kind)
        {
            return bundle?.PlainTextContext == null
                ? Enumerable.Empty<PlainTextContextItem>()
                : bundle.PlainTextContext.Where(i => i != null && string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<SegmentContextItem> SegmentsOfKind(TranslationBundle bundle, string kind)
        {
            return bundle?.SegmentContext == null
                ? Enumerable.Empty<SegmentContextItem>()
                : bundle.SegmentContext.Where(i => i != null && string.Equals(i.Kind, kind, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// memoQ hands over codes like "eng" / "nld" (three-letter) or "en-GB".
        /// A model does better with the name, and CultureInfo does not know the
        /// three-letter form, so map the ones that actually come up and fall back
        /// to the raw code — which a model still handles acceptably.
        /// </summary>
        internal static string DescribeLanguage(string langCode)
        {
            if (string.IsNullOrWhiteSpace(langCode)) return "the target language";

            var code = langCode.Trim();

            if (KnownLanguages.TryGetValue(code, out var exact)) return exact;

            var main = code.Split('-', '_')[0];
            if (KnownLanguages.TryGetValue(main, out var byMain)) return byMain;

            try
            {
                return System.Globalization.CultureInfo.GetCultureInfo(code.Replace('_', '-')).EnglishName;
            }
            catch
            {
                return code;
            }
        }

        private static readonly Dictionary<string, string> KnownLanguages =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "eng", "English" },  { "en", "English" },
                { "nld", "Dutch" },    { "nl", "Dutch" },   { "dut", "Dutch" },
                { "deu", "German" },   { "de", "German" },  { "ger", "German" },
                { "fra", "French" },   { "fr", "French" },  { "fre", "French" },
                { "spa", "Spanish" },  { "es", "Spanish" },
                { "ita", "Italian" },  { "it", "Italian" },
                { "por", "Portuguese" }, { "pt", "Portuguese" },
                { "pol", "Polish" },   { "pl", "Polish" },
                { "rus", "Russian" },  { "ru", "Russian" },
                { "jpn", "Japanese" }, { "ja", "Japanese" },
                { "zho", "Chinese" },  { "zh", "Chinese" }, { "chi", "Chinese" },
                { "kor", "Korean" },   { "ko", "Korean" },
                { "swe", "Swedish" },  { "sv", "Swedish" },
                { "dan", "Danish" },   { "da", "Danish" },
                { "nor", "Norwegian" },{ "no", "Norwegian" },
                { "fin", "Finnish" },  { "fi", "Finnish" },
                { "hun", "Hungarian" },{ "hu", "Hungarian" },
                { "ces", "Czech" },    { "cs", "Czech" },   { "cze", "Czech" },
                { "tur", "Turkish" },  { "tr", "Turkish" },
            };
    }
}
