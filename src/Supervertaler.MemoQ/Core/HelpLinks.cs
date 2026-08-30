using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Context-sensitive help for the plugin's dialogs.
    ///
    /// WinForms gives us the affordance for free: setting
    /// <see cref="Form.HelpButton"/> puts a <c>?</c> in the title bar beside the
    /// close box, exactly where a Windows user expects it. It only appears when
    /// both <c>MinimizeBox</c> and <c>MaximizeBox</c> are false, which is already
    /// true of every dialog here.
    ///
    /// This matters more in memoQ than it would elsewhere. The options dialogs are
    /// the plugin's entire UI surface — there is no view part, no ribbon button and
    /// no menu item to hang a Help entry off — so if the docs are not reachable
    /// from the dialog, they are not reachable from inside memoQ at all.
    /// </summary>
    internal static class HelpLinks
    {
        private const string Base = "https://docs.supervertaler.com/memoq/";

        public const string GettingStarted = Base + "getting-started/";
        public const string GlossaryFormat = Base + "glossary-format/";
        public const string SelfLearning = Base + "self-learning/";
        public const string Troubleshooting = Base + "troubleshooting/";

        /// <summary>
        /// Adds the title-bar <c>?</c> to a dialog and points it at a docs page.
        /// </summary>
        public static void Attach(Form form, string url)
        {
            if (form == null || string.IsNullOrEmpty(url)) return;

            form.HelpButton = true;
            form.HelpButtonClicked += (sender, e) =>
            {
                // Cancel the event: without this, WinForms enters "help mode" and
                // waits for the user to click a control, which is not what the
                // button means any more on modern Windows.
                e.Cancel = true;
                Open(url);
            };
        }

        public static void Open(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // No browser, or a policy-locked machine. Not worth interrupting
                // the user over — they can reach the docs another way.
                PluginLog.Write($"Could not open help URL {url}", ex);
            }
        }
    }
}
