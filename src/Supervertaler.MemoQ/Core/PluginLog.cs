using System;
using System.IO;
using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Append-only diagnostic log at
    /// <c>%LocalAppData%\Supervertaler.memoQ\plugin.log</c>, mirrored to
    /// <c>%TEMP%\Supervertaler-memoQ.log</c>.
    ///
    /// This matters more here than it did in Trados. memoQ gives an add-in no UI
    /// of its own beyond the options dialog, so when something goes wrong during
    /// plugin discovery — a missing dependency, a type that failed to load, a
    /// constructor that threw — memoQ simply does not list the engine, with no
    /// message anywhere. The log is the only way to tell "never loaded" apart
    /// from "loaded but not selected".
    /// </summary>
    internal static class PluginLog
    {
        private static readonly object _lock = new object();
        private static bool _truncated;

        internal static string PrimaryPath
        {
            get
            {
                try
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Supervertaler.memoQ");
                    Directory.CreateDirectory(dir);

                    // A harness gets its own file. It builds engines from bare
                    // defaults with the key deliberately suppressed, so it
                    // produces direction warnings and 401s that are correct for a
                    // test and alarming in a log - and it trims on load, which
                    // discarded the record of a real 569-segment run minutes
                    // after it finished. Harmless while nobody but a developer
                    // read this file; not harmless now the activity window puts
                    // it in front of the user.
                    return Path.Combine(dir, SharedSettings.InHarness ? "harness.log" : "plugin.log");
                }
                catch { return null; }
            }
        }

        private static string FallbackPath
        {
            get
            {
                try { return Path.Combine(Path.GetTempPath(), "Supervertaler-memoQ.log"); }
                catch { return null; }
            }
        }

        // Why the primary write failed, if it did. Swallowing this silently cost
        // real time once: inside memoQ the %LocalAppData% log stayed stale while
        // the %TEMP% one filled up, and there was no way to tell whether memoQ was
        // writing elsewhere or not writing at all. Recorded once, into whichever
        // target does work.
        private static string _primaryFailure;

        // The model last written to the log, so a change is written once
        // rather than the model on every one of forty batch lines.
        private static string _lastModel;

        /// <summary>
        /// True when this provider and model differ from the last pair reported -
        /// and records them, so the next call with the same pair answers false.
        /// Separate from the write so it can be exercised without a log file.
        /// </summary>
        internal static bool ModelChanged(string provider, string model)
        {
            var key = (provider ?? "").Trim() + " / " + (model ?? "").Trim();
            lock (_lock)
            {
                if (string.Equals(_lastModel, key, StringComparison.Ordinal)) return false;
                _lastModel = key;
                return true;
            }
        }

        /// <summary>
        /// Names the model where the work is done (#2). The only line that named
        /// one used to be written when memoQ built the engine and never revised -
        /// so after a switch from Opus to Fable in the settings, the Activity
        /// window went on saying Opus for the rest of the session, while every
        /// segment was in fact translated by Fable. Written on change, not on
        /// every request: a mid-run switch shows up as one line at the moment it
        /// took effect.
        /// </summary>
        public static void ModelInUse(string provider, string model)
        {
            if (ModelChanged(provider, model))
                Write($"model: {provider} / {model} (in force from here on)");
        }

        public static void Write(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{Environment.CurrentManagedThreadId}] {message}\r\n";
            lock (_lock)
            {
                var primary = PrimaryPath;
                var fallback = FallbackPath;

                foreach (var path in new[] { primary, fallback })
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    try
                    {
                        if (!_truncated) File.WriteAllText(path, string.Empty, Encoding.UTF8);

                        // Surface a previously-failed primary write into whatever
                        // target is working, before the line it belongs with.
                        if (_primaryFailure != null && path == fallback)
                        {
                            var note = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [!] primary log unavailable ({primary ?? "path unresolved"}): {_primaryFailure}\r\n";
                            File.AppendAllText(path, note, Encoding.UTF8);
                            _primaryFailure = null;
                        }

                        File.AppendAllText(path, line, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        // Diagnostics must never break translation — but they should
                        // not vanish either.
                        if (path == primary && _primaryFailure == null)
                            _primaryFailure = ex.GetType().Name + ": " + ex.Message;
                    }
                }
                _truncated = true;
            }
        }

        public static void Write(string message, Exception ex)
        {
            Write(ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}");
        }
    }
}
