using System;
using System.Threading;
using System.Windows.Forms;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Show the quick-add dialog and write what it returns to the project
    /// termbase.
    ///
    /// <para>Both ways in end here - the Alt+Up shortcut and memoQ's own Add
    /// Selection As Alternative - so a term added by either lands in the same
    /// place by the same rules, and there is one place to correct when those
    /// rules change.</para>
    /// </summary>
    internal static class TermQuickAdd
    {
        /// <summary>
        /// Offers <paramref name="source"/> and <paramref name="target"/> for
        /// correction and adds them. Returns the line written to the log, which
        /// is also what the caller may show the user.
        /// </summary>
        public static string Show(string sourceLang, string targetLang, string source, string target)
        {
            var project = TermbaseSelection.CurrentProject;
            var into = TermbaseSelection.ProjectTermbaseFor(project);
            var outcome = "cancelled";

            // Its own STA thread with its own message loop. memoQ calls the menu
            // path from a thread that may not be able to show a window, and even
            // on its UI thread a dialog it did not open cannot be parented to it.
            // Join blocks the caller until the dialog closes, which is what a
            // modal quick-add should do.
            var thread = new Thread(() =>
            {
                try
                {
                    using (var form = new QuickAddForm(sourceLang, targetLang, source, target, into?.Name))
                    {
                        if (form.ShowDialog() != DialogResult.OK || into == null) return;

                        // One row through Import rather than AddTerm: Import turns
                        // the pair round when the termbase runs the other way from
                        // the project, and refuses a pair already there either way.
                        var row = new TermbaseFiles.Row
                        {
                            Source = form.Source,
                            Target = form.Target,
                            Forbidden = form.Forbidden,
                            Notes = form.Notes
                        };

                        var result = TermbaseWriter.Import(into.Id, new[] { row }, sourceLang, targetLang);
                        outcome = result.Added == 1
                            ? "added to " + into.Name + (result.Reversed ? " (turned round for it)" : "")
                            : "already in " + into.Name;
                    }
                }
                catch (Exception ex)
                {
                    outcome = "failed: " + ex.Message;
                    MessageBox.Show(ex.Message, "Supervertaler – Add term", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();

            // Not a refresh: memoQ will not look the segment up again while the
            // cursor is on it (see AboutRefreshing below). This only means that
            // when the user does move back onto the segment, the term is there,
            // rather than up to three seconds later.
            if (outcome.StartsWith("added", StringComparison.Ordinal)) TermIndex.NoticeChange();

            return outcome;
        }

        /// <summary>
        /// Why a term added from the grid does not appear until you move off the
        /// segment and back - and what was tried.
        ///
        /// <para>memoQ asks a terminology plugin about a segment once and keeps
        /// the answer. The terminology SDK has nothing that says the answer has
        /// changed: its only refresh-shaped members concern term base domains and
        /// private collections, neither of which touches the results pane.</para>
        ///
        /// <para>The one channel that reaches memoQ from outside the plugin is
        /// the live document link, whose single outbound call selects a segment.
        /// Both readings of "select where you already are" were tried against
        /// memoQ 12.4 on 2026-09-19. Asking for the range memoQ already had was
        /// accepted and changed nothing, which is fair - there was nothing to
        /// change. Asking for one character inside the same sentence, a selection
        /// it genuinely had to apply, was also accepted and also produced no
        /// fresh lookup. So memoQ re-queries terminology when the cursor changes
        /// row, and not when a preview tool moves the selection within one.</para>
        ///
        /// <para>What is left is stepping to a neighbouring row and back, which
        /// works - it is what the user does by hand - and costs them their place
        /// in the target cell, mid-sentence, every time they add a term. That is
        /// not worth paying to see a highlight a few seconds earlier, when the
        /// dialog has already confirmed the term was added. Left undone on
        /// purpose; if memoQ ever exposes a real refresh, this is the note that
        /// says what to replace.</para>
        /// </summary>
        private static void AboutRefreshing() { }
    }
}
