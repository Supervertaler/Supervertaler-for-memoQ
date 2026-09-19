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

            return outcome;
        }
    }
}
