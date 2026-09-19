using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Subscripts, superscripts and radical dots folded to their plain forms, so
    /// that a chemical formula matches however it was written.
    ///
    /// <para>The same formula reaches the database in several shapes. Trados
    /// converts sub- and superscript <em>formatting</em> into real Unicode when a
    /// term is saved, so it stores <c>ClO₃⁻</c>; memoQ reads a selection through
    /// the clipboard, which hands over plain text with the formatting gone, so it
    /// stores <c>ClO3-</c>. Documents mix both freely. Folding each to the same
    /// form on both sides - when a term is indexed and when a segment is read -
    /// makes the stored shape irrelevant to matching.</para>
    ///
    /// <para>Agreed character for character with Supervertaler for Trados on
    /// 2026-09-19, the way the database triggers were. Both products share one
    /// termbase, so a term saved in one must be findable in the other; a fold
    /// that differs by a single character produces terms that are silently
    /// invisible in the other product, which is the worst shape this failure can
    /// take. Change it here only alongside the same change there.</para>
    ///
    /// <para>Every mapping is one character to one character, deliberately.
    /// Matching runs against the folded text while highlighting uses offsets into
    /// the original, and that only holds while folding cannot change a length.</para>
    /// </summary>
    internal static class ScriptChars
    {
        /// <summary>The radical dot every dot-like character folds to: U+00B7.</summary>
        private const char Dot = '·';

        /// <summary>
        /// <paramref name="text"/> with sub- and superscript digits and signs
        /// written plainly, and every radical dot written as U+00B7. Returns the
        /// same string when there is nothing to fold.
        /// </summary>
        public static string Fold(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            StringBuilder folded = null;

            for (var i = 0; i < text.Length; i++)
            {
                var plain = Plain(text[i]);
                if (plain == text[i]) { folded?.Append(text[i]); continue; }

                // Only once something actually needs folding, which is almost
                // never: this runs on every segment memoQ shows.
                if (folded == null) folded = new StringBuilder(text, 0, i, text.Length);
                folded.Append(plain);
            }

            return folded == null ? text : folded.ToString();
        }

        /// <summary>Whether <paramref name="text"/> would change if folded.</summary>
        public static bool NeedsFolding(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (var i = 0; i < text.Length; i++) if (Plain(text[i]) != text[i]) return true;
            return false;
        }

        private static char Plain(char c)
        {
            switch (c)
            {
                // Subscript digits, U+2080 to U+2089.
                case '₀': return '0';
                case '₁': return '1';
                case '₂': return '2';
                case '₃': return '3';
                case '₄': return '4';
                case '₅': return '5';
                case '₆': return '6';
                case '₇': return '7';
                case '₈': return '8';
                case '₉': return '9';

                // Superscript digits. One and two and three are the old Latin-1
                // characters and sit nowhere near the rest.
                case '⁰': return '0';
                case '¹': return '1';
                case '²': return '2';
                case '³': return '3';
                case '⁴': return '4';
                case '⁵': return '5';
                case '⁶': return '6';
                case '⁷': return '7';
                case '⁸': return '8';
                case '⁹': return '9';

                // Signs, super and sub: the charge on an ion.
                case '⁺': return '+';
                case '₊': return '+';
                case '⁻': return '-';
                case '₋': return '-';

                // Dots. A radical is written with whichever of these came to hand.
                case '∙': return Dot;   // bullet operator
                case '⋅': return Dot;   // dot operator
                case '•': return Dot;   // bullet

                default: return c;
            }
        }
    }
}
