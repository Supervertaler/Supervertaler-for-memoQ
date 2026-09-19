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

            if (outcome.StartsWith("added", StringComparison.Ordinal))
            {
                TermIndex.NoticeChange();
                AskForAFreshLookup();
            }

            return outcome;
        }

        /// <summary>
        /// Ask memoQ to select the segment it is already on, so that it looks it
        /// up again and the new term appears in the grid and the Translation
        /// results pane without the user moving away and back.
        ///
        /// <para>memoQ asks a terminology plugin about a segment once and keeps
        /// the answer; the terminology SDK has no way to say that answer has
        /// changed. The only thing that can reach memoQ from out here is the live
        /// document link, whose one outbound call selects a segment - so a
        /// refresh is spelled "select where you already are".</para>
        ///
        /// <para>Whether memoQ treats that as a fresh lookup or as nothing at all
        /// is memoQ's business and is not documented. It costs nothing to ask:
        /// the request names the segment the cursor is on, so if memoQ acts on it
        /// the cursor does not move, and if it ignores it the term appears the
        /// next time the user lands on the segment, exactly as before. With no
        /// preview tool connected nothing happens at all.</para>
        /// </summary>
        private static void AskForAFreshLookup()
        {
            try
            {
                if (!PreviewStore.ToolAlive)
                {
                    PluginLog.Write("Quick term: no live document link, so the pane will refresh when you next land on the segment");
                    return;
                }

                var active = PreviewStore.GetActive();
                if (active == null || string.IsNullOrEmpty(active.PartId))
                {
                    PluginLog.Write("Quick term: no active segment to re-select");
                    return;
                }

                PreviewStore.Enqueue(new PreviewStore.Command
                {
                    Type = "goto",
                    PartId = active.PartId,
                    SourceStart = active.SourceStart,
                    SourceLength = active.SourceLength
                });

                PluginLog.Write("Quick term: asked memoQ to re-select " + active.PartId
                    + " [" + active.SourceStart + "+" + active.SourceLength + "] for a fresh lookup");
            }
            catch (Exception ex)
            {
                // A refresh that fails costs the user one keystroke, so it must
                // never cost them the term they just added.
                PluginLog.Write("Quick term: asking for a fresh lookup failed", ex);
            }
        }
    }
}
