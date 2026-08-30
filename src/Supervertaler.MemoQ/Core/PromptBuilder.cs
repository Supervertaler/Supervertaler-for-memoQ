using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            IReadOnlyList<TermIndex.Match> ownTerms = null)
        {
            var system = (settings.SystemPrompt ?? SupervertalerGeneralSettings.DefaultSystemPrompt)
                .Replace("{SOURCE_LANG}", DescribeLanguage(sourceLangCode))
                .Replace("{TARGET_LANG}", DescribeLanguage(targetLangCode));

            var sb = new StringBuilder();

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

            sb.AppendLine("Source segment:");
            sb.AppendLine(TagBridge.ToTaggedText(bundle.Source));

            return new BuiltPrompt { System = system, User = sb.ToString() };
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
                sb.AppendLine("Surrounding source text (context only — do not translate):");
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

            sb.AppendLine("Required terminology (use the target term exactly as given):");
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

            sb.AppendLine("Forbidden terms (never use these in the translation):");
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
