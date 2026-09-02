using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Resolves a memoQ document GUID to a human-readable project and document
    /// name, by looking at memoQ's own project folders on disk.
    ///
    /// memoQ tells an MT plugin only the document's GUID. That is fine for
    /// keying stores, and useless for a picker: a translator looking at
    /// "d41feebc…" or "Patents (21 segments)" cannot tell which document it is.
    /// The project folder under <c>My memoQ Projects</c> contains
    /// <c>Documents\&lt;guid&gt;\ver1\majorVersionStore.info</c>, whose first
    /// string is the document's file name; the project's name is the folder.
    ///
    /// This reads a 2 KB file per document, once, and falls back to nothing —
    /// the format is undocumented, so it is used only for labels, never for
    /// keys or for anything a wrong answer could damage.
    /// </summary>
    internal static class DocumentNames
    {
        internal sealed class Names
        {
            public string Project;
            public string Document;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<Guid, Names> _cache = new Dictionary<Guid, Names>();

        public static Names Resolve(Guid documentId)
        {
            if (documentId == Guid.Empty) return null;

            lock (_lock)
            {
                if (_cache.TryGetValue(documentId, out var cached)) return cached;
            }

            Names found = null;
            try
            {
                found = Scan(documentId);
            }
            catch (Exception ex)
            {
                PluginLog.Write("DocumentNames: scan failed", ex);
            }

            lock (_lock) _cache[documentId] = found;
            return found;
        }

        private static Names Scan(Guid documentId)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My memoQ Projects");
            if (!Directory.Exists(root)) return null;

            var guidFolder = documentId.ToString("D");

            foreach (var project in Directory.GetDirectories(root))
            {
                var docDir = Path.Combine(project, "Documents", guidFolder);
                if (!Directory.Exists(docDir)) continue;

                var names = new Names { Project = Path.GetFileName(project) };

                // Newest version folder wins (ver1, ver2, …).
                string info = null;
                foreach (var ver in Directory.GetDirectories(docDir))
                {
                    var candidate = Path.Combine(ver, "majorVersionStore.info");
                    if (File.Exists(candidate)) info = candidate;
                }

                if (info != null)
                {
                    var bytes = File.ReadAllBytes(info);
                    // The first printable run that looks like a file name. The
                    // byte before it is a length prefix, which the regex skips
                    // by requiring printable ASCII.
                    // ISO-8859-1: one byte per char, so ASCII survives and the
                    // regex offsets stay honest. (Encoding.Latin1 is .NET 5+.)
                    var text = System.Text.Encoding.GetEncoding(28591).GetString(bytes);
                    var m = Regex.Match(text, @"[\x20-\x7e]{3,}\.[A-Za-z0-9]{2,6}(?=[^\x20-\x7e]|$)");
                    if (m.Success && !m.Value.Contains("\\") && !m.Value.Contains("/"))
                    {
                        var value = m.Value;

                        // The byte before the string is its length, and for a
                        // name of 32–126 characters that byte is itself printable
                        // — a 36-character name arrives as "$Example…". If the
                        // first character's code equals the length of what
                        // follows, it is the prefix, not the name.
                        if (value.Length > 1 && value[0] == value.Length - 1)
                            value = value.Substring(1);

                        names.Document = value.Trim();
                    }
                }

                return names;
            }

            return null;
        }
    }
}
