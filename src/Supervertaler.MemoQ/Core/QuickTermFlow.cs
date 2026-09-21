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

        /// <summary>
        /// The sides were inferred rather than read off the segment, so the pair
        /// may be the wrong way round and the dialog should say so.
        /// </summary>
        public bool Guessed;
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
        private bool _heldStrong;
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
            return Press(text, side, true, nowUtc);
        }

        /// <summary>
        /// <paramref name="strong"/> is false when the side was inferred rather
        /// than read: the word occurs on both sides of the segment, or on
        /// neither. A weak side is still a side - it is better evidence than the
        /// order somebody happened to press in - but two weak ones mark the
        /// result as guessed so the dialog can say so.
        /// </summary>
        public QuickTermOutcome Press(string text, TermSide side, bool strong, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new QuickTermOutcome { Step = QuickTermStep.Nothing, Held = _held };

            text = text.Trim();

            var holding = _held != null && nowUtc - _heldAtUtc <= Forgets;

            // Two presses on the same side are not a term: the user has changed
            // their mind about that half. Replace it rather than pairing a word
            // with itself.
            //
            // Unless one of them is only a guess. Then the confident one keeps
            // the side and the guess takes the other, which is what rescues the
            // common real case: a word that appears in the source AND the target
            // of the same segment, pressed alongside one the live view has not
            // caught up with yet.
            if (holding && side != TermSide.Unknown && side == _heldSide)
            {
                if (strong && !_heldStrong) { _heldSide = Other(side); }
                else if (!strong && _heldStrong) { side = Other(side); strong = false; }
                else holding = false;
            }

            if (!holding)
            {
                _held = text;
                _heldSide = side;
                _heldStrong = strong;
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

            var guessed = !strong || !_heldStrong;
            Forget();

            return new QuickTermOutcome
            {
                Step = QuickTermStep.Ready,
                Source = source,
                Target = target,
                Guessed = guessed
            };
        }

        /// <summary>
        /// Which half a selected word came from, judged by the segment it was
        /// selected in. Unknown when the segment cannot tell them apart - a word
        /// spelled the same in both languages, a number, or a segment we have no
        /// copy of.
        /// </summary>
        public static TermSide Classify(string text, string segmentSource, string segmentTarget)
        {
            bool strong;
            return Classify(text, segmentSource, segmentTarget, out strong);
        }

        /// <summary>
        /// Which half a selected word came from, and how sure that is.
        ///
        /// <para>Found on one side only, it is that side, and <paramref name="strong"/>
        /// is true. The two uncertain cases are not equally uninformative and are
        /// no longer both thrown away as Unknown:</para>
        ///
        /// <list type="bullet">
        /// <item>On BOTH sides - an English term left untranslated in the target,
        /// which is routine in medical and technical work - it is still a word of
        /// the source. Weakly the source.</item>
        /// <item>On NEITHER - almost always because the live view has not caught
        /// up with the cell being edited, and the cell being edited is the target.
        /// Weakly the target.</item>
        /// </list>
        ///
        /// <para>Both of those happened at once on 2026-09-21: "Coronary Artery
        /// Disease" appeared in the source and in the target, the Dutch rendering
        /// beside it had just been typed, and the pair came out backwards because
        /// nothing was left but the order the keys were pressed in.</para>
        /// </summary>
        public static TermSide Classify(string text, string segmentSource, string segmentTarget, out bool strong)
        {
            strong = false;
            if (string.IsNullOrWhiteSpace(text)) return TermSide.Unknown;

            var inSource = Contains(segmentSource, text);
            var inTarget = Contains(segmentTarget, text);

            if (inSource && !inTarget) { strong = true; return TermSide.Source; }
            if (inTarget && !inSource) { strong = true; return TermSide.Target; }

            // Nothing to compare against at all: no live document. Then press
            // order really is all there is, and the caller is told it guessed.
            if (string.IsNullOrEmpty(segmentSource) && string.IsNullOrEmpty(segmentTarget))
                return TermSide.Unknown;

            return inSource ? TermSide.Source : TermSide.Target;
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
