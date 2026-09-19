using System;
using System.Windows.Forms;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Alt+Up in memoQ's grid: select a word, press it, select its translation,
    /// press it again, and the pair is offered for the project termbase.
    ///
    /// <para>The shortcut exists because memoQ offers a terminology plugin only
    /// one way in - right-clicking a hit in Translation results and choosing Add
    /// Selection As Alternative - and its own Add Term button, with its own
    /// keyboard shortcut, works with memoQ's term bases alone. Alt+Up is the key
    /// Supervertaler for Trados uses for the same thing, adding to the project's
    /// own termbase.</para>
    ///
    /// <para>The deciding lives in <see cref="QuickTermFlow"/>, which knows
    /// nothing of keyboards or memoQ. This is the wiring: what to read, what to
    /// show, and where to put the answer.</para>
    /// </summary>
    internal static class QuickTerm
    {
        private static readonly QuickTermFlow Flow = new QuickTermFlow();
        private static Guid _project;

        /// <summary>
        /// Starts listening. Called once, when memoQ starts, and does nothing on
        /// a machine where the hook cannot be installed.
        /// </summary>
        public static void Start()
        {
            HotkeyHook.Start(Keys.Up, () => SharedSettings.QuickTermHotkey, Pressed);
        }

        public static void Stop()
        {
            HotkeyHook.Stop();
        }

        private static void Pressed()
        {
            var text = CellSelection.Read();

            if (string.IsNullOrWhiteSpace(text))
            {
                // Silence here would be indistinguishable from a broken shortcut,
                // and this is the most likely thing a new user does wrong.
                Toast.Show("Select a word first, then press Alt+Up.");
                PluginLog.Write("Quick term: nothing was selected");
                return;
            }

            // A half-finished term belongs to the project it was started in.
            var project = TermbaseSelection.CurrentProject;
            if (project != _project) { Flow.Forget(); _project = project; }

            string segmentSource = null, segmentTarget = null, sourceLang = null, targetLang = null;
            ReadSegment(ref segmentSource, ref segmentTarget, ref sourceLang, ref targetLang);

            var side = QuickTermFlow.Classify(text, segmentSource, segmentTarget);
            var outcome = Flow.Press(text, side, DateTime.UtcNow);

            if (outcome.Step == QuickTermStep.Awaiting)
            {
                Toast.Show(Caught(outcome));
                PluginLog.Write("Quick term: holding " + Describe(side) + " \"" + text + "\"");
                return;
            }

            var into = TermbaseSelection.ProjectTermbaseFor(project);
            if (into == null)
            {
                Toast.Show("No termbase is ticked Project, so there is nowhere to put this."
                    + Environment.NewLine + "Tick one in memoQ → Termbases in the prompt editor.");
                PluginLog.Write("Quick term: no project termbase, so the pair was dropped");
                return;
            }

            PluginLog.Write("Quick term: offering \"" + outcome.Source + "\" → \"" + outcome.Target + "\"");

            var result = TermQuickAdd.Show(
                sourceLang ?? SharedSettings.SourceLang,
                targetLang ?? SharedSettings.TargetLang,
                outcome.Source, outcome.Target);

            PluginLog.Write("Quick term: " + result);
            if (result != null && result.StartsWith("added", StringComparison.Ordinal)) Toast.Show(result);
        }

        /// <summary>
        /// The segment the cursor is on, if the preview tool is connected. It is
        /// what tells a source word from a target one; without it the shortcut
        /// still works, and falls back to the order the two presses came in.
        /// </summary>
        private static void ReadSegment(ref string source, ref string target, ref string sourceLang, ref string targetLang)
        {
            try
            {
                if (!PreviewStore.ToolAlive) return;

                var active = PreviewStore.GetActive();
                var part = PreviewStore.GetPart(active?.PartId);
                if (part == null) return;

                source = part.Source;
                target = part.Target;
                sourceLang = part.SourceLangCode;
                targetLang = part.TargetLangCode;
            }
            catch (Exception ex)
            {
                // Never fatal: not knowing which side a word came from costs the
                // user nothing but the freedom to work in either order.
                PluginLog.Write("Quick term: could not read the active segment", ex);
            }
        }

        private static string Caught(QuickTermOutcome outcome)
        {
            var wanted = outcome.Wanted == TermSide.Source ? "Now select the source term."
                       : outcome.Wanted == TermSide.Target ? "Now select the translation."
                       : "Now select the other half.";

            return "“" + outcome.Held + "”" + Environment.NewLine + wanted;
        }

        private static string Describe(TermSide side)
        {
            return side == TermSide.Source ? "source" : side == TermSide.Target ? "target" : "an unplaced";
        }
    }
}
