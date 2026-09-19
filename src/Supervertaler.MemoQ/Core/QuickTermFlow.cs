using System;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>Which half of a segment a selected word came from.</summary>
    internal enum TermSide { Unknown, Source, Target }

    /// <summary>What a press of the shortcut amounts to.</summary>
    internal enum QuickTermStep
    {
        /// <summary>Nothing was selected, so nothing happened.</summary>
        Nothing,

        /// <summary>One half is held; the other is still wanted.</summary>
        Awaiting,

        /// <summary>Both halves are in hand.</summary>
        Ready
    }

    internal sealed class QuickTermOutcome
    {
        public QuickTermStep Step;

        /// <summary>The half still wanted, when <see cref="Step"/> is Awaiting.</summary>
        public TermSide Wanted;

        /// <summary>What is held so far, for the message shown to the user.</summary>
        public string Held;

        public string Source;
        public string Target;
    }

    /// <summary>
    /// Two presses of the shortcut make a term: one on the source word, one on
    /// its translation.
    ///
    /// <para>Which press is which is decided by looking at the segment rather
    /// than by counting: a word selected in the target cell is in the target
    /// text and usually not in the source, so the order the user works in does
    /// not matter. Where the segment cannot settle it - the same string on both
    /// sides, a number, or no live document to ask - the first press is taken as
    /// the source, which is the order the dialog reads in.</para>
    ///
    /// <para>All of the deciding is here, with the clock passed in, so it can be
    /// tested without a keyboard, a clipboard or memoQ.</para>
    /// </summary>
    internal sealed class QuickTermFlow
    {
        /// <summary>
        /// How long a half-finished term is kept. Long enough to look a word up
        /// mid-thought, short enough that a press tomorrow morning does not
        /// attach itself to a word from last night.
        /// </summary>
        public static readonly TimeSpan Forgets = TimeSpan.FromMinutes(2);

        private string _held;
        private TermSide _heldSide;
        private DateTime _heldAtUtc;

        /// <summary>Whatever is half-finished, dropped. Used when the project changes.</summary>
        public void Forget()
        {
            _held = null;
            _heldSide = TermSide.Unknown;
        }

        public bool IsHolding => _held != null;

        public QuickTermOutcome Press(string text, TermSide side, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new QuickTermOutcome { Step = QuickTermStep.Nothing, Held = _held };

            text = text.Trim();

            var holding = _held != null && nowUtc - _heldAtUtc <= Forgets;

            // Two presses on the same side are not a term: the user has changed
            // their mind about that half. Replace it rather than pairing a word
            // with itself.
            if (holding && side != TermSide.Unknown && side == _heldSide) holding = false;

            if (!holding)
            {
                _held = text;
                _heldSide = side;
                _heldAtUtc = nowUtc;

                return new QuickTermOutcome
                {
                    Step = QuickTermStep.Awaiting,
                    Held = text,
                    Wanted = Other(side)
                };
            }

            // The pair. Whichever side is known decides; when neither is, the
            // press order does.
            string source, target;

            if (_heldSide == TermSide.Target || side == TermSide.Source)
            {
                source = text;
                target = _held;
            }
            else
            {
                source = _held;
                target = text;
            }

            Forget();

            return new QuickTermOutcome { Step = QuickTermStep.Ready, Source = source, Target = target };
        }

        /// <summary>
        /// Which half a selected word came from, judged by the segment it was
        /// selected in. Unknown when the segment cannot tell them apart - a word
        /// spelled the same in both languages, a number, or a segment we have no
        /// copy of.
        /// </summary>
        public static TermSide Classify(string text, string segmentSource, string segmentTarget)
        {
            if (string.IsNullOrWhiteSpace(text)) return TermSide.Unknown;

            var inSource = Contains(segmentSource, text);
            var inTarget = Contains(segmentTarget, text);

            if (inSource && !inTarget) return TermSide.Source;
            if (inTarget && !inSource) return TermSide.Target;
            return TermSide.Unknown;
        }

        private static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private static TermSide Other(TermSide side)
        {
            if (side == TermSide.Source) return TermSide.Target;
            if (side == TermSide.Target) return TermSide.Source;
            return TermSide.Unknown;
        }
    }
}
