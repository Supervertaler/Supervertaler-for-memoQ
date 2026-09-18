using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Which of the shared termbases memoQ uses, and how - kept here rather than
    /// in the termbase database itself.
    ///
    /// <para><b>Why a file of our own.</b> The database carries Trados's answers
    /// to these questions: <c>termbase_activation</c> is Trados's Read flag,
    /// keyed by a hash of a Studio project, and <c>ai_inject</c>,
    /// <c>case_sensitive</c> and <c>is_project_termbase</c> are Trados's other
    /// four columns. Those are Trados's decisions about Trados's projects and
    /// memoQ has no business overwriting them - and could not use them anyway,
    /// since no memoQ project appears anywhere in that file. So memoQ keeps its
    /// own, and the database stays read-only.</para>
    ///
    /// <para><b>The flags, and why each has the scope it does.</b> Read is per
    /// memoQ project, because which terminology applies is a fact about the job.
    /// Rank, case sensitivity and AI are properties of the termbase itself and
    /// are the same wherever it is used - which is also how Trados holds
    /// them.</para>
    ///
    /// <para><b>Rank replaces Trados's Project column.</b> In Trados a project
    /// termbase is painted red because Trados has no ranking; memoQ shades a term
    /// hit by the rank of the termbase it came from, darker for higher, so in
    /// memoQ "the project's termbase" is simply the one ranked first and needs no
    /// flag of its own.</para>
    ///
    /// <para>Stored beside the glossaries in the Supervertaler data folder rather
    /// than under AppData, so that one file serves memoQ, the editor and anything
    /// else that needs it, on the drive the rest of this product's data lives
    /// on.</para>
    /// </summary>
    internal static class TermbaseSelection
    {
        /// <summary>What memoQ knows about one termbase. Ids come from the database.</summary>
        internal sealed class Flags
        {
            public long Id { get; set; }

            /// <summary>
            /// 1 for the project termbase, 0 for a background one.
            ///
            /// <para>Kept as a number rather than a bool because that is what is
            /// already written in <c>termbases.txt</c>, and because the ordering
            /// code below wants one. There is no rank 2: Michael's terminology is
            /// one project termbase against any number of background ones, and a
            /// scale of ten was a problem nobody had.</para>
            /// </summary>
            public int Rank { get; set; }

            /// <summary>The project termbase - at most one, enforced by the dialog.</summary>
            public bool IsProject
            {
                get { return Rank == 1; }
                set { Rank = value ? 1 : 0; }
            }

            public bool CaseSensitive { get; set; }

            /// <summary>Its terms reach the model: batch translate, single segments, AutoPrompt.</summary>
            public bool Ai { get; set; }

            /// <summary>The name as it was when written. For reading the file, never for matching.</summary>
            public string Name { get; set; }
        }

        internal static Action<string, Exception> ErrorSink = (message, ex) => { };

        internal static string Path => System.IO.Path.Combine(
            global::Supervertaler.Core.SupervertalerPaths.Root, "memoq", "termbases.txt");

        private static readonly object _lock = new object();

        // Re-read when the file changes rather than on every call: the TB plugin
        // asks per segment, and a stat is far cheaper than a parse.
        private static long _length = -1;
        private static DateTime _written;
        private static Dictionary<long, Flags> _flags = new Dictionary<long, Flags>();
        private static Dictionary<string, List<long>> _read =
            new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// When the file was last written, as a number; 0 when there is none.
        /// The lookup index folds this into its reload key: the editor rewrites
        /// this file on every OK, so an edit made in the editor reaches lookup
        /// even when it changed nothing the database's one-second timestamps can
        /// tell apart. One stat per check.
        /// </summary>
        internal static long FileStamp
        {
            get
            {
                try { return File.Exists(Path) ? File.GetLastWriteTimeUtc(Path).Ticks : 0; }
                catch { return 0; }
            }
        }

        /// <summary>
        /// The memoQ project in force, as the MT engine last recorded it when a
        /// translation request arrived. Neither SDK tells a plugin which project
        /// it is in any other way, so this is the one answer every consumer of
        /// the selection uses - lookup, the prompt, the bridge, the QA checks.
        /// Guid.Empty until the first segment of a session has been translated.
        /// </summary>
        internal static Guid CurrentProject
        {
            get
            {
                Guid project;
                return Guid.TryParse((SharedSettings.MemoryBankProject ?? string.Empty).Trim(), out project)
                    ? project : Guid.Empty;
            }
        }

        /// <summary>Every termbase memoQ has an opinion about, by id.</summary>
        internal static IDictionary<long, Flags> All()
        {
            Load();
            lock (_lock) return new Dictionary<long, Flags>(_flags);
        }

        /// <summary>
        /// The termbases to consult for this memoQ project, best rank first.
        ///
        /// <para>An empty answer for a project nobody has set up yet, which is the
        /// intended default: turning on 80 termbases because they happen to match
        /// the language pair would put tens of thousands of generic dictionary
        /// entries into terminology matching, and that is a known way to produce
        /// nothing but false positives.</para>
        /// </summary>
        internal static IList<long> ReadFor(Guid project)
        {
            Load();

            lock (_lock)
            {
                List<long> ids;
                if (!_read.TryGetValue(Key(project), out ids)) return new List<long>();

                // Ranked first, in rank order; then the rest, in the order they
                // were written, so an unranked selection still has a stable order.
                return ids
                    .OrderBy(id => RankOf(id) == 0 ? int.MaxValue : RankOf(id))
                    .ThenBy(id => ids.IndexOf(id))
                    .ToList();
            }
        }

        /// <summary>Those of <see cref="ReadFor"/> whose terms may also reach the model.</summary>
        internal static IList<long> AiFor(Guid project)
        {
            var flags = All();
            return ReadFor(project)
                .Where(id => flags.ContainsKey(id) && flags[id].Ai)
                .ToList();
        }

        private static int RankOf(long id)
        {
            Flags f;
            return _flags.TryGetValue(id, out f) ? f.Rank : 0;
        }

        /// <summary>
        /// Replace what is stored for one project, and the flags for every
        /// termbase. Written whole: the editor is the only writer and always has
        /// the complete picture, so a merge would be a way to lose an edit rather
        /// than to keep one.
        /// </summary>
        internal static void Save(Guid project, IEnumerable<long> readIds, IEnumerable<Flags> flags)
        {
            Load();

            lock (_lock)
            {
                if (flags != null)
                {
                    _flags = new Dictionary<long, Flags>();
                    foreach (var f in flags) if (f != null) _flags[f.Id] = f;
                }

                if (readIds != null)
                {
                    var ids = new List<long>();
                    foreach (var id in readIds) if (!ids.Contains(id)) ids.Add(id);
                    _read[Key(project)] = ids;
                }

                Write();
            }
        }

        /// <summary>
        /// A termbase that no longer exists has no business in this file: drop
        /// its flags and take it out of every project's Read list. Called after
        /// a delete, so that a stale id is not carried around forever answering
        /// nothing.
        /// </summary>
        internal static void Forget(long id)
        {
            Load();

            lock (_lock)
            {
                var changed = _flags.Remove(id);
                foreach (var ids in _read.Values)
                    changed |= ids.Remove(id);

                if (changed) Write();
            }
        }

        /// <summary>A project's identity in the file. Guid.Empty is "no project yet".</summary>
        private static string Key(Guid project) => project.ToString("D");

        private static void Load()
        {
            lock (_lock)
            {
                try
                {
                    var file = new FileInfo(Path);
                    if (!file.Exists)
                    {
                        // Absent is not an error: it is simply nobody having chosen
                        // anything yet. Remember that so we do not stat on every call.
                        _length = 0;
                        _written = DateTime.MinValue;
                        _flags = new Dictionary<long, Flags>();
                        _read = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
                        return;
                    }

                    if (file.Length == _length && file.LastWriteTimeUtc == _written) return;

                    var flags = new Dictionary<long, Flags>();
                    var read = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

                    foreach (var raw in File.ReadAllLines(Path, Encoding.UTF8))
                    {
                        var line = (raw ?? "").Trim();
                        if (line.Length == 0 || line[0] == '#') continue;

                        var parts = line.Split('\t');
                        if (parts.Length < 2) continue;

                        if (parts[0] == "tb")
                        {
                            var f = ParseFlags(parts);
                            if (f != null) flags[f.Id] = f;
                        }
                        else if (parts[0] == "read" && parts.Length >= 3)
                        {
                            read[parts[1].Trim()] = parts[2]
                                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(ParseId)
                                .Where(id => id > 0)
                                .ToList();
                        }
                    }

                    _flags = flags;
                    _read = read;
                    _length = file.Length;
                    _written = file.LastWriteTimeUtc;
                }
                catch (Exception ex)
                {
                    // A damaged file must not take terminology down with it. The
                    // cost of being wrong here is that a selection is forgotten,
                    // which the editor can restore; the cost of throwing is that
                    // memoQ gets an exception in the middle of a lookup.
                    ErrorSink("Termbase selection could not be read: " + Path, ex);
                    _flags = new Dictionary<long, Flags>();
                    _read = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
                    _length = -1;
                }
            }
        }

        private static Flags ParseFlags(string[] parts)
        {
            var id = ParseId(parts[1]);
            if (id <= 0) return null;

            var f = new Flags { Id = id };
            for (var i = 2; i < parts.Length; i++)
            {
                var bit = parts[i].Trim();
                if (bit.StartsWith("#")) { f.Name = bit.TrimStart('#').Trim(); continue; }

                var split = bit.IndexOf('=');
                if (split <= 0) continue;

                var key = bit.Substring(0, split).Trim();
                var value = bit.Substring(split + 1).Trim();

                if (key == "rank") f.Rank = (int)ParseId(value);
                else if (key == "cs") f.CaseSensitive = value == "1";
                else if (key == "ai") f.Ai = value == "1";
            }
            return f;
        }

        private static long ParseId(string text)
        {
            long value;
            return long.TryParse((text ?? "").Trim(), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static void Write()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("# Which shared termbases Supervertaler for memoQ uses, and how.");
                sb.AppendLine("#");
                sb.AppendLine("# Written by the prompt editor. The termbases themselves live in");
                sb.AppendLine("# " + TermbaseDb.Path + " and are never written from memoQ.");
                sb.AppendLine("#");
                sb.AppendLine("#   tb    <id>  rank=<n>  cs=<0|1>  ai=<0|1>  # <name>");
                sb.AppendLine("#   read  <memoQ project guid>  <id>,<id>,...");
                sb.AppendLine();

                foreach (var f in _flags.Values.OrderBy(f => f.Rank == 0 ? int.MaxValue : f.Rank)
                                               .ThenBy(f => f.Id))
                {
                    sb.Append("tb\t").Append(f.Id)
                      .Append("\trank=").Append(f.Rank)
                      .Append("\tcs=").Append(f.CaseSensitive ? 1 : 0)
                      .Append("\tai=").Append(f.Ai ? 1 : 0);
                    if (!string.IsNullOrWhiteSpace(f.Name)) sb.Append("\t# ").Append(f.Name);
                    sb.AppendLine();
                }

                sb.AppendLine();
                foreach (var pair in _read.OrderBy(p => p.Key))
                    sb.Append("read\t").Append(pair.Key).Append('\t')
                      .AppendLine(string.Join(",", pair.Value.Select(i => i.ToString(CultureInfo.InvariantCulture))));

                // Written beside and moved into place: the plugin may be reading
                // this file while the editor saves it, and a half-written file is
                // a forgotten selection rather than a parse error.
                var temp = Path + ".tmp";
                File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(Path)) File.Delete(Path);
                File.Move(temp, Path);

                var file = new FileInfo(Path);
                _length = file.Length;
                _written = file.LastWriteTimeUtc;
            }
            catch (Exception ex)
            {
                ErrorSink("Termbase selection could not be saved: " + Path, ex);
            }
        }
    }
}
