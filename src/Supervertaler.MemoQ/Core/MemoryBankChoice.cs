using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Which memory bank each memoQ project uses.
    ///
    /// <para>The bank itself lives in one global setting
    /// (<see cref="SharedSettings.MemoryBank"/>) because at any moment there is
    /// one answer to "what is loaded". This file is the memory behind that
    /// setting: it records the choice against the project it was made in, so
    /// that leaving a job and coming back to it restores the same bank instead
    /// of whatever the last job used.</para>
    ///
    /// <para>Keyed on <c>MTRequestMetadata.ProjectGuid</c>, which is better than
    /// what the Trados plugin has to use. There a project is identified by its
    /// .sdlproj path, so moving or renaming the folder loses the association;
    /// memoQ hands us a GUID that survives both.</para>
    ///
    /// <para>The format is the same deliberately-boring one as
    /// <c>shared.txt</c> — one <c>guid=bank</c> per line, editable in Notepad —
    /// for the same reason: when a plugin with no UI gets this wrong, the file
    /// is the only place the user can look.</para>
    /// </summary>
    internal static class MemoryBankChoice
    {
        private static readonly object _lock = new object();

        /// <summary>Parsed contents, and the write time they were parsed from.</summary>
        private static Dictionary<string, string> _cache;
        private static DateTime _cacheStamp;
        private static long _cacheLength = -1;

        internal static string Path =>
            System.IO.Path.Combine(SharedSettings.Directory, "memory-bank-projects.txt");

        /// <summary>
        /// The bank recorded for a project, or an empty string when none is —
        /// which includes a project that has never been seen.
        ///
        /// <para>Those two cases deliberately answer the same. "No bank" is a
        /// real, safe answer; the alternative, treating an unknown project as
        /// "carry on with whatever was loaded", is the one that quietly feeds
        /// another client's terminology into a job.</para>
        /// </summary>
        public static string ForProject(Guid projectGuid)
        {
            if (projectGuid == Guid.Empty) return string.Empty;

            var map = Load();
            return map.TryGetValue(Key(projectGuid), out var bank) ? bank : string.Empty;
        }

        /// <summary>
        /// Records the bank a project should use. An empty or null name records
        /// "none", which is a choice like any other and must be remembered as
        /// one: a translator who clears the bank for a project means it.
        /// </summary>
        public static void Remember(Guid projectGuid, string bankName)
        {
            if (projectGuid == Guid.Empty) return;

            var key = Key(projectGuid);
            var value = (bankName ?? string.Empty).Trim();

            lock (_lock)
            {
                try
                {
                    var map = LoadLocked();
                    if (map.TryGetValue(key, out var existing)
                        && string.Equals(existing, value, StringComparison.Ordinal))
                        return;                       // already says this

                    map[key] = value;
                    WriteLocked(map);
                }
                catch (Exception ex)
                {
                    // A project whose choice cannot be written still works this
                    // session; it just will not be remembered for the next one.
                    SharedSettings.ReportError("Could not record the memory bank for this project", ex);
                }
            }
        }

        /// <summary>Forgets everything. Tests only.</summary>
        internal static void Reset()
        {
            lock (_lock)
            {
                _cache = null;
                _cacheStamp = default(DateTime);
                _cacheLength = -1;
            }
        }

        // ── the file ─────────────────────────────────────────────────────

        private static string Key(Guid projectGuid) => projectGuid.ToString("D");

        private static Dictionary<string, string> Load()
        {
            lock (_lock) return LoadLocked();
        }

        /// <summary>
        /// Re-reads only when the file has changed, so the translate path can
        /// call this per batch without a disk read per batch.
        ///
        /// <para>Size and write time together, rather than write time alone: a
        /// Notepad save inside the same clock tick is exactly the edit someone
        /// makes when trying to fix a wrong bank by hand, and it is the one a
        /// timestamp check misses.</para>
        /// </summary>
        private static Dictionary<string, string> LoadLocked()
        {
            var path = Path;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    // Cache the emptiness too. Until a bank is ever chosen this
                    // file does not exist, and that is the common case.
                    if (_cache == null || _cacheLength != -1)
                    {
                        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _cacheLength = -1;
                        _cacheStamp = default(DateTime);
                    }
                    return _cache;
                }

                if (_cache != null && _cacheLength == info.Length && _cacheStamp == info.LastWriteTimeUtc)
                    return _cache;

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.TrimStart('﻿').Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    var key = line.Substring(0, eq).Trim();
                    if (key.Length == 0) continue;

                    map[key] = line.Substring(eq + 1).Trim();
                }

                _cache = map;
                _cacheLength = info.Length;
                _cacheStamp = info.LastWriteTimeUtc;
                return _cache;
            }
            catch (Exception ex)
            {
                SharedSettings.ReportError("Could not read " + path, ex);

                // An unreadable file must not mean "the last bank is still
                // right". Answer "no bank recorded" for everything, and do not
                // cache that, so a transient lock does not stick.
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void WriteLocked(Dictionary<string, string> map)
        {
            var sb = new StringBuilder(256 + map.Count * 48);
            sb.AppendLine("# Which memory bank each memoQ project uses.");
            sb.AppendLine("# One project GUID per line. An empty value means no bank,");
            sb.AppendLine("# which is a recorded choice and not the same as being absent.");

            foreach (var pair in map)
            {
                // Values are folder names and GUIDs, so neither half can contain
                // a newline and the format needs no escaping. Guard anyway: a
                // hand-edited file is the one place something else could arrive.
                if (pair.Key.IndexOfAny(new[] { '\r', '\n', '=' }) >= 0) continue;
                if (pair.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0) continue;

                sb.Append(pair.Key).Append('=').AppendLine(pair.Value);
            }

            File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(false));

            // Adopt what was just written rather than forcing the next reader to
            // parse it back.
            var info = new FileInfo(Path);
            _cache = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            _cacheLength = info.Length;
            _cacheStamp = info.LastWriteTimeUtc;
        }
    }
}
