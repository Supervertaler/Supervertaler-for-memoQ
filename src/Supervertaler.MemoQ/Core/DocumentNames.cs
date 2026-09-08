using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Resolves a memoQ document GUID to its project and document name – and
    /// the project's own GUID – by looking at memoQ's project folders on disk.
    ///
    /// memoQ tells an MT plugin only the document's GUID. That is fine for
    /// keying stores, and useless for a picker: a translator looking at
    /// "d41feebc…" or "Patents (21 segments)" cannot tell which document it is.
    /// The project folder under <c>My memoQ Projects</c> contains
    /// <c>Documents\&lt;guid&gt;\ver1\majorVersionStore.info</c>, whose first
    /// string is the document's file name; the project's name is the folder;
    /// and the folder's <c>project.mprx</c> carries the project's GUID as
    /// <c>&lt;CoreInfo&gt;&lt;ID&gt;</c> – measured to be the same GUID memoQ
    /// sends as <c>ProjectGuid</c> in a translation request, on every project
    /// checked. That last fact is what lets the preview tool, which names only
    /// documents, tell the plugin which project is open.
    ///
    /// This reads a 2 KB file per document, once. Names are labels only; the
    /// project GUID is used for the memory-bank choice, and a wrong one there
    /// is caught by the GUID also arriving with the next translation request.
    /// </summary>
    internal static class DocumentNames
    {
        internal sealed class Names
        {
            public string Project;
            public string Document;
            /// <summary>The project's GUID from project.mprx, or Empty when the file could not be read.</summary>
            public Guid ProjectId;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<Guid, Names> _cache = new Dictionary<Guid, Names>();

        // A miss is remembered too, but not for ever: the preview tool reports a
        // document on every cursor move, and a document memoQ has just created
        // must be found once its folder exists rather than never.
        private static readonly Dictionary<Guid, DateTime> _misses = new Dictionary<Guid, DateTime>();
        internal static TimeSpan MissRetry = TimeSpan.FromSeconds(30);

        /// <summary>The folders to look in. Set by a harness to point at a folder of its own.</summary>
        internal static string[] RootsOverride;

        private static IList<string> Roots()
        {
            if (RootsOverride != null && RootsOverride.Length > 0) return RootsOverride;
            return MemoQProjects.Roots();
        }

        public static Names Resolve(Guid documentId) => Resolve(documentId, null);

        /// <summary>
        /// <paramref name="documentName"/> is the file name the preview tool
        /// reports. It is the only way to place a document of a project checked
        /// out from a server, whose documents are not stored under their id - see
        /// <see cref="MemoQProjects.ByDocumentName"/>.
        /// </summary>
        public static Names Resolve(Guid documentId, string documentName)
        {
            if (documentId == Guid.Empty) return null;

            lock (_lock)
            {
                if (_cache.TryGetValue(documentId, out var cached)) return cached;
                if (_misses.TryGetValue(documentId, out var missed) && DateTime.UtcNow - missed < MissRetry) return null;
            }

            Names found = null;
            try
            {
                found = Scan(documentId) ?? ByName(documentName);
            }
            catch (Exception ex)
            {
                PluginLog.Write("DocumentNames: scan failed", ex);
            }

            lock (_lock)
            {
                if (found != null) { _cache[documentId] = found; _misses.Remove(documentId); }
                else _misses[documentId] = DateTime.UtcNow;
            }
            return found;
        }

        /// <summary>
        /// The project that lists a document of this name. Only reached when no
        /// folder carries the document's id, which is the normal state of a
        /// project checked out from a server.
        /// </summary>
        private static Names ByName(string documentName)
        {
            if (string.IsNullOrWhiteSpace(documentName)) return null;

            var project = MemoQProjects.ByDocumentName(documentName);
            if (project == null) return null;

            return new Names
            {
                Project = string.IsNullOrWhiteSpace(project.Name) ? Path.GetFileName(project.Folder) : project.Name,
                ProjectId = project.Id != Guid.Empty ? project.Id : ProjectIdOf(Path.Combine(project.Folder, "project.mprx")),
                Document = documentName.Trim()
            };
        }

        private static Names Scan(Guid documentId)
        {
            var guidFolder = documentId.ToString("D");

            // memoQ's own register of its projects first: it names each project's
            // actual folder, so it finds a project wherever the user keeps it -
            // including the several places at once that moving the projects
            // folder leaves behind - and it carries the project's GUID, which is
            // the one memoQ sends with a translation request.
            if (RootsOverride == null || RootsOverride.Length == 0)
            {
                foreach (var project in MemoQProjects.Registered())
                {
                    if (string.IsNullOrWhiteSpace(project.Folder)) continue;

                    var registered = Path.Combine(project.Folder, "Documents", guidFolder);
                    if (!Directory.Exists(registered)) continue;   // stale rows outlive the folders they name

                    return new Names
                    {
                        Project = string.IsNullOrWhiteSpace(project.Name) ? Path.GetFileName(project.Folder) : project.Name,
                        ProjectId = project.Id != Guid.Empty ? project.Id : ProjectIdOf(Path.Combine(project.Folder, "project.mprx")),
                        Document = DocumentNameIn(registered)
                    };
                }
            }

            // Then whatever is on disk under the projects folders, for a project
            // the register does not list.
            foreach (var root in Roots())
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                foreach (var project in Directory.GetDirectories(root))
                {
                    var docDir = Path.Combine(project, "Documents", guidFolder);
                    if (!Directory.Exists(docDir)) continue;

                    var names = new Names
                    {
                        Project = Path.GetFileName(project),
                        ProjectId = ProjectIdOf(Path.Combine(project, "project.mprx"))
                    };

                    names.Document = DocumentNameIn(docDir);
                    return names;
                }
            }

            return null;
        }

        /// <summary>
        /// The document's own file name, out of memoQ's per-document store.
        /// Null when it cannot be read - a label is worth having, never worth
        /// failing over.
        /// </summary>
        private static string DocumentNameIn(string docDir)
        {
            try
            {
                // Newest version folder wins (ver1, ver2, …).
                string info = null;
                foreach (var ver in Directory.GetDirectories(docDir))
                {
                    var candidate = Path.Combine(ver, "majorVersionStore.info");
                    if (File.Exists(candidate)) info = candidate;
                }
                if (info == null) return null;

                // The first printable run that looks like a file name. The byte
                // before it is a length prefix, which the regex skips by
                // requiring printable ASCII.
                // ISO-8859-1: one byte per char, so ASCII survives and the regex
                // offsets stay honest. (Encoding.Latin1 is .NET 5+.)
                var text = System.Text.Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(info));
                var m = Regex.Match(text, @"[\x20-\x7e]{3,}\.[A-Za-z0-9]{2,6}(?=[^\x20-\x7e]|$)");
                if (!m.Success || m.Value.Contains("\\") || m.Value.Contains("/")) return null;

                var value = m.Value;

                // The byte before the string is its length, and for a name of
                // 32–126 characters that byte is itself printable — a
                // 36-character name arrives as "$Example…". If the first
                // character's code equals the length of what follows, it is the
                // prefix, not the name.
                if (value.Length > 1 && value[0] == value.Length - 1)
                    value = value.Substring(1);

                return value.Trim();
            }
            catch (Exception ex)
            {
                PluginLog.Write("DocumentNames: could not read the document name in " + docDir, ex);
                return null;
            }
        }

        /// <summary>
        /// <c>&lt;CoreInfo&gt;…&lt;ID&gt;</c> of a project.mprx. Read as text, not
        /// as XML: the file is memoQ's and the one element wanted is near the
        /// top, so a regex over the head of it is both cheaper and indifferent
        /// to whatever else the format grows.
        /// </summary>
        internal static Guid ProjectIdOf(string mprxPath)
        {
            try
            {
                if (string.IsNullOrEmpty(mprxPath) || !File.Exists(mprxPath)) return Guid.Empty;
                var text = File.ReadAllText(mprxPath);
                var core = text.IndexOf("<CoreInfo>", StringComparison.Ordinal);
                if (core < 0) return Guid.Empty;
                var m = Regex.Match(text.Substring(core), @"<ID>\s*([0-9A-Fa-f\-]{36})\s*</ID>");
                return m.Success && Guid.TryParse(m.Groups[1].Value, out var id) ? id : Guid.Empty;
            }
            catch (Exception ex)
            {
                PluginLog.Write("DocumentNames: could not read " + mprxPath, ex);
                return Guid.Empty;
            }
        }
    }
}
