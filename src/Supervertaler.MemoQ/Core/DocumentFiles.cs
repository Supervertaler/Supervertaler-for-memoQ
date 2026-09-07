using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Where the original file of a memoQ document is, when memoQ cannot say.
    ///
    /// <para>No memoQ project folder holds the original document. A local project
    /// keeps the filename as a 0-byte placeholder; a project checked out from a
    /// server keeps only memoQ's own stores, and records the path the file had on
    /// the project manager's machine – <c>C:\_In\source\eng</c> on one job – which
    /// does not exist here. The Preview SDK hands over that same recorded path,
    /// so it is the truth for a local project and a dead end for a server one.</para>
    ///
    /// <para>This is the one-line answer to the dead end: the user points at the
    /// file once – the copy the project manager sent, wherever they put it – and
    /// it is remembered against memoQ's document key. Both the images panel and
    /// structure context read it, so locating a document once lights up both.</para>
    ///
    /// <para>File: <c>document-files.txt</c> beside <c>shared.txt</c>, one
    /// <c>key=path</c> per line. Kept as text for the same reason the other
    /// stores are: a wrong line can be seen and fixed in Notepad.</para>
    /// </summary>
    internal static class DocumentFiles
    {
        internal const string FileName = "document-files.txt";

        private static string Path => System.IO.Path.Combine(SharedSettings.Directory, FileName);

        private static readonly object _lock = new object();
        private static Dictionary<string, string> _cache;
        private static long _length = -1;
        private static DateTime _stamp;

        /// <summary>The remembered path for a document key, or null. Never throws.</summary>
        public static string ForDocument(string documentKey)
        {
            if (string.IsNullOrWhiteSpace(documentKey)) return null;
            var map = Load();
            return map.TryGetValue(documentKey.Trim(), out var path) && !string.IsNullOrWhiteSpace(path) ? path : null;
        }

        /// <summary>
        /// The path to use for a document: memoQ's own answer when the file is
        /// there, the remembered one otherwise, null when neither exists on disk.
        /// A recorded path that does not exist is treated as no answer rather than
        /// an error, because on a server project that is the normal case.
        /// </summary>
        public static string Resolve(string documentKey, string recordedPath)
        {
            if (!string.IsNullOrWhiteSpace(recordedPath) && File.Exists(recordedPath)) return recordedPath;

            var located = ForDocument(documentKey);
            return located != null && File.Exists(located) ? located : null;
        }

        /// <summary>Every entry whose key starts with <paramref name="prefix"/> - the files added by hand for one bank.</summary>
        public static List<KeyValuePair<string, string>> WithPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return new List<KeyValuePair<string, string>>();
            lock (_lock)
                return Load().Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>Records a path (empty forgets it). Returns false when the file could not be written.</summary>
        public static bool Remember(string documentKey, string path)
        {
            if (string.IsNullOrWhiteSpace(documentKey)) return false;
            if (SharedSettings.InHarness && !AllowInHarness) return true;

            lock (_lock)
            {
                try
                {
                    var map = Load();
                    var key = documentKey.Trim();

                    if (string.IsNullOrWhiteSpace(path)) map.Remove(key);
                    else map[key] = path.Trim();

                    var sb = new StringBuilder();
                    sb.Append("# Where the original file of each memoQ document is, when memoQ cannot say.\r\n");
                    sb.Append("# One document key per line. Used by the Images panel and by structure context.\r\n");
                    foreach (var kv in map)
                        sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");

                    Directory.CreateDirectory(SharedSettings.Directory);
                    File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(false));

                    _cache = null;
                    return true;
                }
                catch (Exception ex)
                {
                    SharedSettings.ReportError("DocumentFiles: could not write " + FileName, ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// A harness writes only to a key it made up, and run-harness.ps1 puts
        /// the file back – so unlike the settings seed, this is allowed through
        /// when the harness says so.
        /// </summary>
        internal static bool AllowInHarness = false;   // set by reflection from images-test.ps1

        private static Dictionary<string, string> Load()
        {
            lock (_lock)
            {
                try
                {
                    var path = Path;
                    if (!File.Exists(path))
                    {
                        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _length = -1;
                        return _cache;
                    }

                    var info = new FileInfo(path);
                    if (_cache != null && info.Length == _length && info.LastWriteTimeUtc == _stamp) return _cache;

                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                        var eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }

                    _cache = map;
                    _length = info.Length;
                    _stamp = info.LastWriteTimeUtc;
                    return map;
                }
                catch (Exception ex)
                {
                    SharedSettings.ReportError("DocumentFiles: could not read " + FileName, ex);
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }
}
