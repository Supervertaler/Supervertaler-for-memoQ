using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Starts the live document link the first time memoQ loads Supervertaler,
    /// so nobody has to find and run it by hand.
    ///
    /// <para>Until 2026-09-24 the installer put the tool in place and nothing
    /// started it: the documentation told the translator to run an exe out of
    /// Program Files once. It has to be the plugin, not the installer - the
    /// installer only runs with memoQ closed, and the first start must happen
    /// while memoQ is running, because that is when memoQ asks, once, whether to
    /// allow the connection. After that memoQ starts the tool itself.</para>
    ///
    /// <para>Once, and only when it can work. Started while memoQ's PDF Preview
    /// tool is missing, the link can only show a message saying so, and doing
    /// that at every memoQ start would be exactly the nagging this exists to
    /// remove - so nothing happens until that tool is installed. And after the
    /// one start, never again: a translator who declined memoQ's connection
    /// request has answered, and should not be asked at every start. Starting a
    /// copy that memoQ is also starting is harmless - the tool allows one
    /// instance and the second exits.</para>
    /// </summary>
    internal static class LiveLink
    {
        internal const string ExeName = "Supervertaler.MemoQ.Preview.exe";

        public static void StartOnce(Action<string> log)
        {
            if (SharedSettings.InHarness) return;

            try
            {
                var tool = FindTool();
                var prerequisite = PrerequisitePresent();
                var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Length > 0;

                if (!ShouldStart(SharedSettings.LiveLinkStarted, running, tool, prerequisite)) return;

                Process.Start(new ProcessStartInfo(tool)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(tool)
                });

                SharedSettings.LiveLinkStarted = true;
                log?.Invoke("Live document link: started for the first time - memoQ asks once whether to allow it; "
                    + "after that memoQ starts it itself (" + tool + ")");
            }
            catch (Exception ex)
            {
                log?.Invoke("Live document link: could not be started - " + ex.Message);
            }
        }

        /// <summary>
        /// Whether to start it now: never started before, not already running,
        /// the tool is where the installer put it, and memoQ's PDF Preview tool is
        /// there to make it work.
        /// </summary>
        internal static bool ShouldStart(bool startedBefore, bool running, string tool, bool prerequisite)
        {
            return !startedBefore && !running && !string.IsNullOrEmpty(tool) && prerequisite;
        }

        /// <summary>
        /// Where the tool is: the Supervertaler data folder a developer build
        /// deploys to, or the installer's folder under Program Files. Null when
        /// neither has it. The data folder first: a customer has only the
        /// installer's copy, but a machine that has had both keeps the freshest
        /// build there, and whichever copy starts first is the one memoQ
        /// registers to start from then on.
        /// </summary>
        internal static string FindTool()
        {
            var candidates = new[]
            {
                Path.Combine(global::Supervertaler.Core.SupervertalerPaths.Root, "memoq", "preview", ExeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Supervertaler for memoQ", ExeName)
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// memoQ's PDF Preview tool, which carries the interface the link talks
        /// through. Found the way the link itself finds it: any folder under
        /// memoQ's Program Files folder with "preview" in its name that holds the
        /// interface assembly.
        /// </summary>
        internal static bool PrerequisitePresent()
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     })
            {
                try
                {
                    var memoq = Path.Combine(root ?? "", "memoQ");
                    if (!Directory.Exists(memoq)) continue;
                    if (Directory.GetDirectories(memoq)
                        .Where(d => d.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0)
                        .Any(d => File.Exists(Path.Combine(d, "MemoQ.PreviewInterfaces.dll"))))
                        return true;
                }
                catch { /* an unreadable folder is not the one we want */ }
            }
            return false;
        }
    }
}
