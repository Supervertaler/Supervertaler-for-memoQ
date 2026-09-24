using System.Linq;
using MemoQ.Addins.Common.DataStructures;
using MemoQ.MTInterfaces;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// A row whose source is only tags, whitespace or punctuation: its
    /// translation is the source itself, and no model is asked.
    ///
    /// <para>Both translation paths used to treat "no text" as "nothing here" and
    /// answer with an empty segment - and memoQ writes what it is told. A row
    /// holding a single placeholder (a product name, a variable) therefore came
    /// out of Pre-translate EMPTY, the tag gone, on five rows of one live job
    /// while the neighbouring rows with a tag and some text were filled
    /// correctly. Copying the source keeps the tags, in their order, and costs
    /// nothing.</para>
    ///
    /// <para>Digits count as text on purpose. "1,000.5" becomes "1.000,5" in
    /// Dutch, so a number-only row still goes to the model.</para>
    /// </summary>
    internal static class NothingToTranslate
    {
        /// <summary>True when <paramref name="plainText"/> has no letter or digit in it.</summary>
        public static bool In(string plainText)
        {
            return !(plainText ?? "").Any(char.IsLetterOrDigit);
        }

        /// <summary>
        /// True for a segment with something in it - a tag, a space, a dash - but
        /// nothing a model could translate. A segment with nothing at all is not
        /// this: that one stays empty.
        /// </summary>
        public static bool Applies(Segment source)
        {
            return source != null && !source.IsEmpty && In(source.PlainText);
        }

        /// <summary>The source, handed back as its own translation.</summary>
        public static TranslationResult Copy(Segment source)
        {
            return new TranslationResult
            {
                Translation = TagBridge.FromTaggedText(TagBridge.ToTaggedText(source), source),
                Info = "Copied from the source - nothing to translate"
            };
        }
    }
}
