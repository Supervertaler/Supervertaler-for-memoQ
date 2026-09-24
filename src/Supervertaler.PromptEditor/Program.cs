using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    internal static class Program
    {
        /// <summary>
        /// Optional argument: the relative path of a prompt to open on startup,
        /// so a host can launch straight into the prompt the user had selected.
        /// </summary>
        /// <summary>
        /// %LocalAppData%\Supervertaler.memoQ\editor.log. Never throws: a log that
        /// can stop the editor is worse than no log.
        /// </summary>
        private static void EditorLog(string line)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Supervertaler.memoQ");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "editor.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // Before anything else: this editor borrows System.Data.SQLite
            // from memoQ, which lives one directory up from our own and is
            // therefore not on any probing path of ours.
            MemoQAssemblies.Resolve();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // A single instance keeps two windows from writing the same file.
            // The library is a folder of files with no locking of its own, so
            // two editors open on it is a lost-edit waiting to happen.
            bool createdNew;
            using (var only = new System.Threading.Mutex(true, "Supervertaler.PromptEditor.Single", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "The Supervertaler prompt editor is already open.",
                        "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Inside the single-instance check, so a second editor that is
                // about to exit does not start an online licence check first.
                //
                // With a log, not null. The licence code reports the one thing
                // that tells us a save failed or fell back to the slower method,
                // and with no hook that message went nowhere: a failure in the
                // editor was silent while the same failure in memoQ was logged.
                // Its own file beside plugin.log rather than plugin.log itself,
                // so two processes are never appending to one file. The lines are
                // rare - failures and the once-per-process fallback - so it needs
                // no rotation.
                Supervertaler.MemoQ.Core.Licence.Start(EditorLog);

                // Before the window is built, so the library it shows already
                // has the built-in prompts in it (see DefaultPrompts).
                Supervertaler.MemoQ.Core.DefaultPrompts.Ensure(EditorLog);

                try
                {
                    Application.Run(new MainForm(args != null && args.Length > 0 ? args[0] : null));
                }
                catch (Exception ex)
                {
                    // Launched from a plugin's options dialog, so there is no
                    // console for a stack trace to fall out of.
                    MessageBox.Show(
                        "The prompt editor could not start.\r\n\r\n" + ex,
                        "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Debug.WriteLine(ex);
                }
            }
        }
    }
}
