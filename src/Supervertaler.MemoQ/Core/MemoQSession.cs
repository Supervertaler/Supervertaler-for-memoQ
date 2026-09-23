using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>How far the recorded "open project" can be trusted right now.</summary>
    internal enum ProjectFreshness
    {
        /// <summary>memoQ has never said which project is open.</summary>
        None,
        /// <summary>Reported by the memoQ that is running now.</summary>
        Current,
        /// <summary>Reported by an earlier memoQ session; the running one has not said anything yet.</summary>
        EarlierSession,
        /// <summary>No memoQ is running, so this is simply the last project it reported.</summary>
        MemoQClosed,
        /// <summary>memoQ is running but its start time could not be read, so it cannot be told apart.</summary>
        CannotTell,
    }

    /// <summary>
    /// Which memoQ session reported the open project. Issue #8: the editor
    /// showed a project from yesterday's memoQ as if it were open now, and a
    /// memory bank chosen at that moment was filed against the other client's
    /// project - found only because the heading looked wrong.
    ///
    /// <para>The plugin learns the project only when memoQ calls it, and an
    /// add-in cannot ask memoQ what is open. So until the first call of a
    /// session - no segment clicked into yet, or a project whose manager has
    /// switched MT plugins off - the recorded project is whatever the previous
    /// session said. What was missing was any way to tell. memoQ's process start
    /// time is that way: fixed for the life of a session, different for every
    /// session, and readable both by the plugin (it runs inside memoQ) and by
    /// the editor (which can see memoQ's process).</para>
    ///
    /// <para>Compiled into the editor as well as the plugin, so no memoQ types.</para>
    /// </summary>
    internal static class MemoQSession
    {
        /// <summary>
        /// This process's own session, as recorded. Only meaningful inside memoQ,
        /// which is the only place the plugin records a project. A process's
        /// start time never changes, so it is read once.
        /// </summary>
        private static readonly Lazy<string> ThisProcess = new Lazy<string>(() =>
        {
            try
            {
                using (var me = Process.GetCurrentProcess())
                    return Stamp(me.StartTime);
            }
            catch { return string.Empty; }
        });

        internal static string ThisProcessStamp() => ThisProcess.Value;

        internal static string Stamp(DateTime start) =>
            start.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        /// <summary>
        /// When each running memoQ started, or null for one whose start time
        /// could not be read. Every Process is disposed: each holds a handle, and
        /// this runs every time the editor window comes to the front.
        /// </summary>
        internal static List<DateTime?> RunningStarts()
        {
            var starts = new List<DateTime?>();
            Process[] running;
            try { running = Process.GetProcessesByName("memoQ"); }
            catch { return starts; }

            foreach (var p in running)
            {
                try { starts.Add(p.StartTime.ToUniversalTime()); }
                catch { starts.Add(null); }
                finally { p.Dispose(); }
            }
            return starts;
        }

        /// <summary>
        /// The whole decision, pure, so every case is tested without a memoQ.
        ///
        /// <para>Anything that cannot be proved current is not reported as
        /// current. A recording made before sessions were recorded at all has no
        /// stamp and so cannot be matched - that reads as an earlier session, which
        /// is the truth: nobody can say it is this one.</para>
        /// </summary>
        internal static ProjectFreshness Classify(bool hasProject, string recordedStamp, IList<DateTime?> running)
        {
            if (!hasProject) return ProjectFreshness.None;
            if (running == null || running.Count == 0) return ProjectFreshness.MemoQClosed;

            if (DateTime.TryParse(recordedStamp ?? string.Empty, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var recorded))
            {
                recorded = recorded.ToUniversalTime();
                foreach (var start in running)
                {
                    // Two seconds either way: the same process read from two
                    // processes gives the same instant, and nothing else starts
                    // memoQ within two seconds of an earlier one.
                    if (start.HasValue && Math.Abs((start.Value - recorded).TotalSeconds) < 2)
                        return ProjectFreshness.Current;
                }
            }

            foreach (var start in running)
                if (!start.HasValue) return ProjectFreshness.CannotTell;

            return ProjectFreshness.EarlierSession;
        }

        /// <summary>The recorded project, judged against the memoQ running now.</summary>
        internal static ProjectFreshness Freshness()
        {
            var hasProject = Guid.TryParse(SharedSettings.MemoryBankProject ?? string.Empty, out var project)
                             && project != Guid.Empty;
            return Classify(hasProject, SharedSettings.MemoryBankSession, RunningStarts());
        }
    }
}
