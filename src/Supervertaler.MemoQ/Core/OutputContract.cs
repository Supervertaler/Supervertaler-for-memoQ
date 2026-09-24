using System;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// What every translation request promises about its reply, whichever prompt
    /// is selected.
    ///
    /// <para>memoQ has no base system prompt of its own - the library prompt IS the
    /// system prompt - so until this existed, what a reply could contain was
    /// whatever the selected prompt happened to say. The Default Translation Prompt
    /// said nothing firm, and one of its lines actively invited "a brief
    /// explanation in parentheses". On a live job the model appended an English
    /// note addressed to the translator, in markdown, to a Dutch target, and the
    /// plugin wrote it into the grid. Trados wraps every prompt in core's base
    /// prompt, which forbids commentary; memoQ had nothing equivalent.</para>
    ///
    /// <para>So this goes on the end of every system prompt, after the library
    /// prompt and the memory bank, where a prompt cannot switch it off by
    /// accident. A prompt may add to it - its own examples of good comments, a
    /// different language for them - but the shape of the reply is fixed here.
    /// It is the same text on every request of a job, so the provider's prompt
    /// cache covers it.</para>
    ///
    /// <para>The words are backed by <see cref="ReplyCheck"/>, which refuses a
    /// reply that breaks them. Instructions alone were what failed.</para>
    /// </summary>
    internal static class OutputContract
    {
        public const string Heading = "# OUTPUT CONTRACT";

        /// <summary>The block itself, heading included.</summary>
        public static readonly string Text = string.Join(Environment.NewLine, new[]
        {
            Heading,
            "",
            "Fixed. It applies whatever the instructions above say.",
            "",
            "- Return the translation of each segment you are given and nothing else: no preamble, no notes,",
            "  no explanations, no alternatives, no markdown, no quotation marks or code fences around it,",
            "  and never the source text again. When the request numbers the segments, start each",
            "  translation with its number as asked; otherwise return the translation alone.",
            "- Keep the number and order of segments exactly as delivered.",
            "- Reproduce every inline tag and placeholder exactly: the same tags, the same number of them,",
            "  each around the translated words that correspond to what it wrapped in the source.",
            "- If something must be brought to the translator's attention - a defect in the source, a real",
            "  ambiguity, a deliberate departure from a translation memory match or the terminology - add ONE",
            "  marker at the very end of that segment's translation, outside any tag:",
            "      [[TC: <text>]]",
            "  At most one per segment, 5 to 20 words. Write it as the translator's comment to the client,",
            "  in English unless the instructions above say otherwise: state the fact and the fix, with no",
            "  reasoning and no \"I\". Example: [[TC: \"100 000 miljoen euro\" means EUR 100 billion. Please check.]]",
            "- A segment with nothing to flag gets no marker.",
            "- Never put commentary anywhere else in the reply.",
        });

        /// <summary>
        /// Said once more, in front of the segment, when the first reply broke the
        /// contract and the request is being made again.
        /// </summary>
        public const string Reminder =
            "Your previous reply to this request was rejected because it contained text that is not the "
            + "translation. Return the translation only. If something must be flagged, put ONE [[TC: ...]] "
            + "marker at the very end, written to the client, and nothing else.";
    }
}
