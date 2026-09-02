using System.Collections.Generic;
using System.Linq;
using MemoQ.Addins.Common.DataStructures;
using MemoQ.Addins.Common.Utils;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Converts between memoQ's <see cref="Segment"/> and the tagged plain text an
    /// LLM sees, and back again.
    ///
    /// Unlike Trados — where <c>SegmentTagHandler</c> had to invent a tag
    /// representation and hand-roll the round trip — memoQ ships the converter
    /// itself. <see cref="SegmentXMLConverter"/> serialises a segment to XML with
    /// the inline tags in place, and parses a string back into a segment given the
    /// original tag list. So the tag contract we ask the model to honour is
    /// memoQ's own, and a malformed response fails at parse time rather than
    /// silently dropping formatting.
    /// </summary>
    internal static class TagBridge
    {
        /// <summary>
        /// Serialise a source segment for the prompt. Tags are included; character
        /// formatting is not — bold/italic runs survive as segment structure that
        /// memoQ re-applies, and asking a model to preserve them inflates the
        /// prompt for no gain.
        /// </summary>
        public static string ToTaggedText(Segment segment)
        {
            if (segment == null || segment.IsEmpty) return string.Empty;
            return SegmentXMLConverter.ConvertSegment2Xml(segment, includeTags: true, includeFormatting: false);
        }

        /// <summary>Plain text with no tags at all — used for context segments, where tags are noise.</summary>
        public static string ToPlainText(Segment segment)
        {
            if (segment == null || segment.IsEmpty) return string.Empty;
            return segment.PlainText ?? string.Empty;
        }

        /// <summary>
        /// Parse a model response back into a segment, reusing the inline tags of
        /// the source. <paramref name="source"/> supplies the tag inventory, so a
        /// tag the model echoed by name resolves to the real thing.
        /// </summary>
        public static Segment FromTaggedText(string text, Segment source)
        {
            if (string.IsNullOrEmpty(text)) return Segment.Empty;

            IList<InlineTag> tags = source?.ITags != null
                ? source.ITags.ToList()
                : new List<InlineTag>();

            try
            {
                return SegmentXMLConverter.ConvertXML2Segment(text, tags);
            }
            catch
            {
                // The model returned something that isn't valid memoQ segment XML —
                // most often a stray "&" or an unescaped "<". Falling back to a
                // plain-text segment loses the tags but still delivers the
                // translation, which is strictly better than failing the segment.
                PluginLog.Write($"TagBridge: XML parse failed, falling back to plain text. Raw: {Truncate(text, 400)}");
                return SegmentBuilder.CreateFromString(StripXmlTags(text));
            }
        }

        /// <summary>Tagged text with the tag markers removed. For analysis passes that want prose, not markup.</summary>
        public static string StripTagMarkers(string taggedText)
        {
            return string.IsNullOrEmpty(taggedText) ? taggedText ?? "" : StripXmlTags(taggedText);
        }

        private static string StripXmlTags(string s)
        {
            return System.Text.RegularExpressions.Regex.Replace(s, "<[^>]*>", string.Empty);
        }

        private static string Truncate(string s, int max)
        {
            return s != null && s.Length > max ? s.Substring(0, max) + "…" : s;
        }
    }
}
