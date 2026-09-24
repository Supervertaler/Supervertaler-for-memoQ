using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Whether a model's reply for one segment is a translation, checked before
    /// it can reach the grid.
    ///
    /// <para>Until this existed, whatever came back was written into the
    /// document. On a live job that was a Dutch target followed by an English
    /// note, in markdown, explaining a choice to the translator - no marker, no
    /// line break, nothing a later step could have told apart from the
    /// translation. <see cref="OutputContract"/> says what a reply may contain;
    /// this is what makes that more than a request.</para>
    ///
    /// <para>What it looks for is commentary: text the model wrote ABOUT the
    /// translation rather than as it. Every signal is tested against the source
    /// as well, and fires only when the source does not have it - so a heading
    /// that really is "Note:", an English-to-English job that really does say
    /// "I changed", or markdown that was in the file all along, pass. A single
    /// <c>[[TC: ...]]</c> marker at the very end is the one sanctioned place for a
    /// comment and is set aside before anything else is judged.</para>
    ///
    /// <para>Tags are NOT a reason to refuse. The staging check came to the same
    /// conclusion for the same reason: a target that legitimately drops a
    /// formatting tag is not rare, and refusing would turn a judgement call into
    /// an empty row. <see cref="TagDifference"/> reports it for the log instead.</para>
    /// </summary>
    internal static class ReplyCheck
    {
        /// <summary>
        /// Past this, a reply with no marker to account for it is more likely to
        /// carry an explanation than to be a translation. Dutch and German run
        /// longer than English, but not two and a half times longer.
        /// </summary>
        public const double MaxLengthRatio = 2.5;

        /// <summary>
        /// Below this many characters of source the ratio means nothing - "OK" to
        /// "In orde" is 3.5 - so short segments are judged on the other signals
        /// only.
        /// </summary>
        public const int MinSourceForRatio = 20;

        // Both bracket forms. memoQ prompts use [[TC: ...]]; prompts written for
        // Trados before September 2026 used the white square brackets, and one of
        // those selected in memoQ must not have its comment mistaken for stray text.
        private static readonly Regex Marker =
            new Regex(@"(?:\[\[|⟦)\s*TC\s*:.*?(?:\]\]|⟧)", RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex TagMarker =
            new Regex(@"</?([A-Za-z_][A-Za-z0-9_]*)(?:\s[^>]*)?/?>", RegexOptions.Compiled);

        // A label that introduces commentary. Only a problem AFTER some
        // translation - at the very start it is far more likely to be the
        // translation of a source label ("Opmerking:" is "Note:"). Capitalised
        // only: "see note: ..." inside a sentence is ordinary text, whereas a
        // model starting a remark writes "Note:".
        private static readonly Regex Label = new Regex(
            @"\b(?:Note|NB|N\.B\.|Translation|Translator'?s note|Explanation|Comment|Remark)\s*:",
            RegexOptions.Compiled);

        // The model talking about what it did. The capital I is the point.
        private static readonly Regex FirstPerson = new Regex(
            @"\bI(?:'ve|\s+have)?\s+(?:followed|changed|kept|used|translated|rendered|chose|opted|left|retained|corrected|adjusted|replaced|preserved|assumed|added|omitted|removed)\b",
            RegexOptions.Compiled);

        // The model talking about the job's own machinery.
        private static readonly Regex Machinery = new Regex(
            @"\b(?:approved|fuzzy|TM)\s+match(?:es)?\b|\bmistranslat",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Markdown = new Regex(@"\*\*|__|`|^\s*#{1,6}\s", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// Why <paramref name="reply"/> is not a clean translation of
        /// <paramref name="source"/>, in a few words for the log, or null when it is.
        /// Both are tagged text as the model saw and wrote them.
        /// </summary>
        public static string Problem(string source, string reply)
        {
            source = source ?? "";
            reply = (reply ?? "").Trim();
            if (reply.Length == 0) return null;   // an empty reply is someone else's problem

            var markers = Marker.Matches(reply);
            if (markers.Count > 1) return "more than one [[TC]] marker";

            var body = reply;
            if (markers.Count == 1)
            {
                var m = markers[0];
                if (reply.Substring(m.Index + m.Length).Trim().Length > 0)
                    return "a [[TC]] marker that is not at the end";
                body = reply.Substring(0, m.Index).TrimEnd();
            }

            if (Markdown.IsMatch(body) && !Markdown.IsMatch(source))
                return "markdown";

            if (Lines(body) > Lines(source))
                return "a line break the source does not have";

            if (FirstPerson.IsMatch(body) && !FirstPerson.IsMatch(source))
                return "the model writing about its own choices";

            if (Machinery.IsMatch(body) && !Machinery.IsMatch(source))
                return "a remark about the translation memory or the translation";

            var label = Label.Match(body);
            if (label.Success && LeadingText(body, label.Index) && !Label.IsMatch(source))
                return "a \"" + label.Value.TrimEnd(':', ' ') + ":\" after the translation";

            var sourceLength = Plain(source).Length;
            if (sourceLength >= MinSourceForRatio && Plain(body).Length > sourceLength * MaxLengthRatio)
                return "far longer than the source";

            return null;
        }

        /// <summary>
        /// How the tags in <paramref name="reply"/> differ from the source's, or
        /// null when they match. Names are the identity, as in the QA and staging
        /// checks: memoQ hands a plugin text markers, not tag objects.
        /// </summary>
        public static string TagDifference(string source, string reply)
        {
            var body = Marker.Replace(reply ?? "", "");
            var want = Names(source ?? "");
            var got = Names(body);
            if (want.SequenceEqual(got)) return null;
            return "source has " + want.Count + " tag(s), reply has " + got.Count;
        }

        private static List<string> Names(string text)
        {
            return TagMarker.Matches(text).Cast<Match>()
                .Select(m => m.Groups[1].Value.ToLowerInvariant())
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        private static int Lines(string text)
        {
            return (text ?? "").Trim().Count(c => c == '\n');
        }

        private static string Plain(string text)
        {
            return TagMarker.Replace(text ?? "", "").Trim();
        }

        /// <summary>True when there is real text before <paramref name="index"/>, tags aside.</summary>
        private static bool LeadingText(string body, int index)
        {
            return Plain(body.Substring(0, index)).Trim('*', '_', ' ', '(', '[').Length > 0;
        }
    }
}
