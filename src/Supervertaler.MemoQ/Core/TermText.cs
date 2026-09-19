using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// A term as it should be stored: tidied on the way into the database, once,
    /// rather than worked around on every lookup afterwards.
    ///
    /// <para>This is not <see cref="ScriptChars"/> and the two must not be
    /// confused. The fold is applied at match time, maps one character to one
    /// character, and never changes what is stored. This runs at write time,
    /// changes lengths, and decides what is stored for ever.</para>
    ///
    /// <para><strong>Why it has to exist.</strong> A term picked up from a segment
    /// carries whatever invisible characters that segment held. A zero-width space
    /// is the common one - IDML-derived documents are full of them - and it
    /// survives everything: it is not whitespace, so <c>Trim</c> leaves it, and it
    /// is not in the fold, because a character that must be removed rather than
    /// replaced cannot be in a fold that has to preserve lengths. So a term stored
    /// with one inside it can never be matched again, by this product or by
    /// Supervertaler for Trados, with nothing on screen to say why. It is dead on
    /// arrival and stays dead.</para>
    ///
    /// <para>Trados has cleaned its writes for longer, in
    /// <c>TermbaseReader.SanitizeTermWhitespace</c>. This is the same treatment,
    /// so that a term written by either product is stored the same way. Reported
    /// by that side on 2026-09-19; it applies here because the Alt+Up shortcut
    /// takes a term straight from a segment.</para>
    /// </summary>
    internal static class TermText
    {
        /// <summary>
        /// <paramref name="text"/> fit to store: every kind of space written as an
        /// ordinary one, runs of them collapsed, zero-width characters removed,
        /// and the ends trimmed.
        ///
        /// <para>Apostrophes and sub- and superscripts are deliberately left
        /// alone. They are folded at match time instead, so the user's own
        /// spelling survives in the pane, the prompt and any export.</para>
        /// </summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var built = new StringBuilder(text.Length);
            var pendingSpace = false;

            foreach (var c in text)
            {
                if (IsInvisible(c)) continue;

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || ScriptChars.IsSpaceVariant(c))
                {
                    // Held rather than written, so a run collapses and a trailing
                    // one never reaches the string at all.
                    pendingSpace = built.Length > 0;
                    continue;
                }

                if (pendingSpace) { built.Append(' '); pendingSpace = false; }
                built.Append(c);
            }

            return built.ToString();
        }

        /// <summary>Whether <paramref name="text"/> would change if cleaned.</summary>
        public static bool NeedsCleaning(string text)
        {
            return !string.Equals(text ?? string.Empty, Clean(text), System.StringComparison.Ordinal);
        }

        /// <summary>
        /// A character that takes no space and shows nothing, so a term carrying
        /// one looks exactly like a term without it.
        /// </summary>
        private static bool IsInvisible(char c)
        {
            switch (c)
            {
                case '​':   // zero-width space
                case '⁠':   // word joiner
                case '﻿':   // byte order mark, zero-width no-break space
                    return true;
                default:
                    return false;
            }
        }
    }
}
