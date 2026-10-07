using System;
using System.IO;
using Supervertaler.Core;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// What to tell the translator about the team folder: the folder a team
    /// shares for memory banks and prompts, set in config.json (by Supervertaler
    /// for Trados - memoQ has no setting of its own and follows whatever is there).
    ///
    /// <para>Core decides once per process whether the team folder is in use, and
    /// falls back to the person's own data folder when it does not answer. That
    /// fallback must never be silent: the AI would be handed the person's own
    /// banks while they believe it has the team's. memoQ has a twist Trados does
    /// not - the plugin (inside memoQ) and the editor are two processes that each
    /// decide for themselves, so one can be on the team folder while the other
    /// has fallen back. <see cref="Mismatch"/> is for that.</para>
    ///
    /// <para>Compiled into the plugin and the editor alike, so both describe the
    /// same state in the same words.</para>
    /// </summary>
    internal static class TeamFolderStatus
    {
        /// <summary>
        /// One sentence about the team folder, or null when none is set - with no
        /// team folder there is nothing to say, and nothing is said.
        /// </summary>
        public static string Line()
        {
            var team = SupervertalerPaths.TeamFolder;
            if (team == null) return null;
            return SupervertalerPaths.TeamFolderProblem
                ?? "Memory banks and prompts come from the team folder \"" + team + "\".";
        }

        /// <summary>True when a team folder is set but this process is not using it.</summary>
        public static bool FellBack =>
            SupervertalerPaths.TeamFolder != null && SupervertalerPaths.TeamFolderProblem != null;

        /// <summary>
        /// A warning when memoQ's plugin takes banks and prompts from a different
        /// folder than this process, or null when they agree or the plugin's
        /// answer is unknown.
        /// </summary>
        public static string Mismatch(string pluginContentRoot)
        {
            if (string.IsNullOrWhiteSpace(pluginContentRoot)) return null;
            var mine = SupervertalerPaths.ContentRoot;
            if (SamePath(pluginContentRoot, mine)) return null;
            return "memoQ is using memory banks and prompts from \"" + pluginContentRoot
                 + "\", but this editor is using \"" + mine + "\". The team folder answered for one and not the "
                 + "other. Close memoQ and the editor and start them again, so both use the same folder.";
        }

        internal static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return a == b;
            try
            {
                a = Path.GetFullPath(a).TrimEnd('\\', '/');
                b = Path.GetFullPath(b).TrimEnd('\\', '/');
            }
            catch (Exception) { /* compare as given */ }
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
