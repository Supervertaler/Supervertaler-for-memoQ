using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Supervertaler.Core;
using Supervertaler.MemoQ.Core;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Making a memory bank.
    ///
    /// <para>This existed only inside the FigureLens images dialog, offered as a
    /// link when a project had no bank and an image needed one. So the answer to
    /// "how do I start a bank for this job" was to open an unrelated dialog and
    /// notice a link that is hidden once a bank exists. Starting a new project -
    /// the moment you most want a fresh bank - was the one case with no route to
    /// it at all, short of making the folder by hand.</para>
    ///
    /// <para>The folder layout and the skeleton files are core's, so a bank made
    /// here is the same as one made by Trados or Workbench.</para>
    /// </summary>
    internal static class NewMemoryBank
    {
        /// <summary>
        /// Asks for a name, creates the bank and returns the folder's own name,
        /// or null if the user changed their mind or it could not be made.
        ///
        /// <para>Defaults to the open memoQ project's name, which is the answer
        /// nine times in ten and the reason the FigureLens link only ever offered
        /// that one. It is a default here, not the only choice.</para>
        /// </summary>
        public static string Ask(IWin32Window owner)
        {
            var suggestion = MemoryBanks.Sanitize((SharedSettings.MemoryBankProjectName ?? "").Trim());

            string raw;
            using (var ask = new TextInputDialog(
                "New memory bank",
                "A name for this client or project. An existing name reuses that bank; "
                + "nothing already written in it is overwritten.",
                suggestion))
            {
                if (ask.ShowDialog(owner) != DialogResult.OK) return null;
                raw = ask.Value;
            }

            if (raw == null) return null;

            var name = MemoryBanks.Sanitize(raw.Trim());
            if (name.Length == 0)
            {
                MessageBox.Show(owner, "That name has nothing in it that can be used for a folder.",
                    "New memory bank", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            // DirFor answers "no such bank" with null - it does not propose a
            // path - so it is asked only whether one is already there, and the
            // folder to create is built here. Handing its null to CreateDirectory
            // is a null-path exception on the one case this exists for.
            var existing = MemoryBanks.DirFor(name);
            var dir = existing ?? Path.Combine(MemoryBanks.Root, name);

            try
            {
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(Path.Combine(dir, "reference"));

                // Only what is missing. Reusing a name must not overwrite a brief
                // somebody has already written.
                foreach (var f in new[] { "brief.md", "terminology.md", "style.md" })
                {
                    var file = Path.Combine(dir, f);
                    if (!File.Exists(file))
                        File.WriteAllText(file, MemoryBanks.SkeletonBody(f, name), new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner,
                    "Could not create the memory bank folder:" + Environment.NewLine + dir
                    + Environment.NewLine + Environment.NewLine + ex.Message,
                    "New memory bank", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            // The folder's own name, which for a bank that already existed is
            // however it was actually spelt.
            return Path.GetFileName(dir);
        }
    }
}
