using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Opens the prompt editor from inside memoQ. memoQ gives an add-in no UI of
    /// its own beyond an options dialog, so anything with a window - the prompt
    /// library, the termbases - lives in the editor, and a plugin's options
    /// button is a way to get there.
    ///
    /// <para>The editor is deployed beside the add-in DLLs, not because memoQ
    /// loads it (it never does) but because that is where this looks. It
    /// enforces a single instance itself and says so if a second is started,
    /// so this does not need to.</para>
    /// </summary>
    internal static class EditorLauncher
    {
        internal static void Open(IWin32Window owner)
        {
            var exe = Path.Combine(
                Path.GetDirectoryName(typeof(EditorLauncher).Assembly.Location) ?? string.Empty,
                "Supervertaler.PromptEditor.exe");

            if (!File.Exists(exe))
            {
                MessageBox.Show(owner,
                    "The prompt editor is not installed next to the add-in.\r\n\r\nExpected:\r\n" + exe,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                PluginLog.Write("Could not start the prompt editor", ex);
                MessageBox.Show(owner, "The prompt editor could not be started.\r\n\r\n" + ex.Message,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
