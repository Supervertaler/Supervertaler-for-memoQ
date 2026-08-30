using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Disk backing for <see cref="DocumentMemory"/>, so what the translator has
    /// confirmed survives closing memoQ.
    ///
    /// One append-only TSV file per document, under
    /// <c>%LocalAppData%\Supervertaler.memoQ\document-memory\</c>. Append-only
    /// because a confirm happens on the translator's critical path: adding one
    /// short line is cheap and cannot lose earlier work if the process dies
    /// mid-write, whereas rewriting a 500-entry file on every keystroke-adjacent
    /// event is neither.
    ///
    /// Re-confirmations are handled by replay order rather than by editing the
    /// file: a later line for the same source simply wins on load. The file is
    /// compacted once it grows past twice the in-memory cap.
    ///
    /// <para><b>This is confidential client text on disk.</b> It lives under
    /// LocalAppData, which is per-user and not synced, and never leaves the
    /// machine. <see cref="Forget"/> deletes it; the options dialog exposes that
    /// as a button.</para>
    /// </summary>
    internal static class DocumentMemoryStore
    {
        /// <summary>Files untouched for this long are pruned at startup.</summary>
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);

        private const int MaxFiles = 200;

        private static readonly object _ioLock = new object();
        private static bool _pruned;

        internal static string Directory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Supervertaler.memoQ", "document-memory");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// Everything recorded for this key, oldest first. Later duplicates of the
        /// same source are left in place; the caller applies them in order, so the
        /// newest wins naturally.
        /// </summary>
        public static List<DocumentMemory.Pair> Load(string key)
        {
            var result = new List<DocumentMemory.Pair>();
            if (string.IsNullOrEmpty(key)) return result;

            try
            {
                lock (_ioLock)
                {
                    PruneOnce();

                    var path = PathFor(key);
                    if (!File.Exists(path)) return result;

                    foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        if (string.IsNullOrEmpty(line)) continue;

                        var tab = line.IndexOf('\t');
                        if (tab <= 0) continue;

                        var src = Unescape(line.Substring(0, tab));
                        var trg = Unescape(line.Substring(tab + 1));
                        if (src.Length == 0 || trg.Length == 0) continue;

                        result.Add(new DocumentMemory.Pair { Source = src, Target = trg });
                    }
                }
            }
            catch (Exception ex)
            {
                // A corrupt or unreadable cache must never stop translation. Losing
                // it costs recall quality, nothing else.
                PluginLog.Write($"DocumentMemoryStore: could not load '{key}'", ex);
                return new List<DocumentMemory.Pair>();
            }

            return result;
        }

        public static void Append(string key, string source, string target)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return;

            try
            {
                lock (_ioLock)
                {
                    var path = PathFor(key);
                    File.AppendAllText(path, Escape(source) + "\t" + Escape(target) + "\r\n", Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Write($"DocumentMemoryStore: could not append to '{key}'", ex);
            }
        }

        /// <summary>
        /// Rewrite a file from the caller's de-duplicated view. Called when replay
        /// history has grown well past what the in-memory cap will ever hold.
        /// </summary>
        public static void Compact(string key, IEnumerable<DocumentMemory.Pair> pairs)
        {
            if (string.IsNullOrEmpty(key) || pairs == null) return;

            try
            {
                lock (_ioLock)
                {
                    var path = PathFor(key);
                    var temp = path + ".tmp";

                    var sb = new StringBuilder();
                    foreach (var p in pairs)
                        sb.Append(Escape(p.Source)).Append('\t').Append(Escape(p.Target)).Append("\r\n");

                    File.WriteAllText(temp, sb.ToString(), Encoding.UTF8);

                    // Replace rather than overwrite: a crash mid-write then costs
                    // the temp file, not the real one.
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temp, path);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Write($"DocumentMemoryStore: could not compact '{key}'", ex);
            }
        }

        /// <summary>Deletes one document's stored pairs, or all of them when key is null.</summary>
        public static int Forget(string key = null)
        {
            try
            {
                lock (_ioLock)
                {
                    if (key != null)
                    {
                        var path = PathFor(key);
                        if (!File.Exists(path)) return 0;
                        File.Delete(path);
                        return 1;
                    }

                    var files = System.IO.Directory.GetFiles(Directory, "*.tsv");
                    foreach (var f in files) File.Delete(f);
                    return files.Length;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Write("DocumentMemoryStore: could not forget", ex);
                return 0;
            }
        }

        public static long TotalBytes()
        {
            try
            {
                lock (_ioLock)
                    return System.IO.Directory.GetFiles(Directory, "*.tsv")
                        .Sum(f => new FileInfo(f).Length);
            }
            catch { return 0; }
        }

        public static int FileCount()
        {
            try
            {
                lock (_ioLock) return System.IO.Directory.GetFiles(Directory, "*.tsv").Length;
            }
            catch { return 0; }
        }

        // ---- internals --------------------------------------------------------

        /// <summary>
        /// Once per process: drop stale files, and cap the total. Without this the
        /// folder accumulates one file per document ever opened — for a working
        /// translator that is thousands over a couple of years, holding client text
        /// long after the job shipped.
        /// </summary>
        private static void PruneOnce()
        {
            if (_pruned) return;
            _pruned = true;

            try
            {
                var files = System.IO.Directory.GetFiles(Directory, "*.tsv")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                var cutoff = DateTime.UtcNow - MaxAge;
                var removed = 0;

                for (var i = 0; i < files.Count; i++)
                {
                    var stale = files[i].LastWriteTimeUtc < cutoff;
                    var overCap = i >= MaxFiles;
                    if (!stale && !overCap) continue;

                    try { files[i].Delete(); removed++; } catch { }
                }

                if (removed > 0)
                    PluginLog.Write($"DocumentMemoryStore: pruned {removed} stale file(s)");
            }
            catch (Exception ex)
            {
                PluginLog.Write("DocumentMemoryStore: prune failed", ex);
            }
        }

        private static string PathFor(string key)
        {
            return Path.Combine(Directory, Sanitize(key) + ".tsv");
        }

        /// <summary>
        /// Keys are built from a GUID and language codes, so they are already tame
        /// — but never trust that when it is going into a file path.
        /// </summary>
        private static string Sanitize(string key)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(key.Length);
            foreach (var c in key) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            var name = sb.ToString();
            return name.Length > 120 ? name.Substring(0, 120) : name;
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }

                switch (s[++i])
                {
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'n': sb.Append('\n'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append('\\').Append(s[i]); break;
                }
            }
            return sb.ToString();
        }
    }
}
