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
                Supervertaler.MemoQ.Core.Licence.Start(null);

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
