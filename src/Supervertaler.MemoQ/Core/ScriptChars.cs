using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The characters that are written more than one way, folded to one form so
    /// that a term matches however it happens to be spelled.
    ///
    /// <para>Three families, and the rarest of them is the one that prompted this.
    /// Chemical sub- and superscripts reach the shared database in two shapes:
    /// Supervertaler for Trados converts sub- and superscript <em>formatting</em>
    /// to real Unicode when a term is saved, so it stores <c>ClO₃⁻</c>, while
    /// memoQ reads a selection through the clipboard as plain text and stores
    /// <c>ClO3-</c>. Space variants and apostrophe variants are far commoner in
    /// this work - a no-break space in a figure, a smart apostrophe in an English
    /// possessive - and Trados has folded those for far longer.</para>
    ///
    /// <para>Applied when a term is indexed <em>and</em> when a segment is read.
    /// With the fold on both sides the stored form stops mattering for lookup.</para>
    ///
    /// <para>Agreed character for character with Supervertaler for Trados on
    /// 2026-09-19, the way the database triggers were. Both products share one
    /// termbase, so a term saved in one must be findable in the other; a fold that
    /// differs by a single character makes some terms silently invisible in one
    /// product and not the other, which is the worst shape this failure can take.
    /// Taking only the chemistry in the first pass did exactly that to every
    /// Trados term containing a smart apostrophe. Change this only alongside the
    /// same change there.</para>
    ///
    /// <para>Every mapping is one character to one character, deliberately.
    /// Matching runs against the folded text while highlighting uses offsets into
    /// the original, and that only holds while folding cannot change a length.
    /// Characters that have to be <em>removed</em> rather than replaced therefore
    /// cannot live here; they are dealt with on the write path, in
    /// <see cref="TermText"/>.</para>
    /// </summary>
    internal static class ScriptChars
    {
        /// <summary>The radical dot every dot-like character folds to: U+00B7.</summary>
        private const char Dot = '·';

        /// <summary>
        /// A space that is not the ordinary one. Trados's list, verbatim.
        ///
        /// <para>The range stops at U+200A deliberately: U+200B is a zero-width
        /// character rather than a space, and folding it to a space would be
        /// wrong. Those are removed on the write path instead.</para>
        /// </summary>
        public static bool IsSpaceVariant(char c)
        {
            if (c >= ' ' && c <= ' ') return true;

            switch (c)
            {
                case ' ':   // no-break space
                case ' ':   // ogham space mark
                case ' ':   // narrow no-break space
                case ' ':   // medium mathematical space
                case '　':   // ideographic space
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>An apostrophe that is not the ordinary one. Trados's list, verbatim.</summary>
        public static bool IsApostropheVariant(char c)
        {
            switch (c)
            {
                case '‘':   // left single quotation mark
                case '’':   // right single quotation mark, the usual smart apostrophe
                case 'ʼ':   // modifier letter apostrophe
                case '＇':   // fullwidth apostrophe
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// <paramref name="text"/> with every variant character written plainly.
        /// Returns the same string when there is nothing to fold.
        /// </summary>
        public static string Fold(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            StringBuilder folded = null;

            for (var i = 0; i < text.Length; i++)
            {
                var plain = Plain(text[i]);
                if (plain == text[i]) { folded?.Append(text[i]); continue; }

                // Built only once something actually needs folding, which is the
                // rare case: this runs on every segment memoQ shows.
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

                // Superscript digits. One, two and three are the old Latin-1
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

                // Dots. A radical is written with whichever came to hand.
                case '∙': return Dot;   // bullet operator
                case '⋅': return Dot;   // dot operator
                case '•': return Dot;   // bullet

                default:
                    if (IsSpaceVariant(c)) return ' ';
                    if (IsApostropheVariant(c)) return '\'';
                    return c;
            }
        }
    }
}
