using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Read-only access to the Supervertaler termbase database - the same file
    /// Supervertaler for Trados and Supervertaler Workbench use.
    ///
    /// <para><b>Read-only, deliberately.</b> Trados and Workbench write to this
    /// file and this plugin does not, so nothing here can corrupt data another
    /// product depends on. Every connection asks for it explicitly.</para>
    ///
    /// <para><b>Nothing is shipped for it.</b> The rule in this repo is that an
    /// add-in adds no assembly to memoQ's Addins folder, because memoQ probes
    /// that folder for its own dependencies and a second copy of a library it
    /// already loads is how the Trados plugin earned an
    /// EntryPointNotFoundException. memoQ ships System.Data.SQLite itself
    /// (1.0.119.0 in memoQ 12), so the reference is Private=false and we use
    /// theirs. The native half, SQLite.Interop.dll, sits beside it and is found
    /// through <see cref="EnsureNativeOnPath"/>.</para>
    ///
    /// <para><b>Measured, 2026-09-16:</b> the file is 2.25 GB - overwhelmingly
    /// translation memories and their indexes, not terms. Opening it costs
    /// nothing, the 84 termbases list in milliseconds, and the largest single
    /// termbase (10,339 terms) reads fully into memory in 32 ms. Loading a whole
    /// selection up front is therefore fine; what needs watching is the
    /// per-segment matching, which is <see cref="TermIndex"/>'s problem and not
    /// this class's.</para>
    /// </summary>
    internal static class TermbaseDb
    {
        /// <summary>
        /// Where a failure is reported. The plugin points this at its log; the
        /// prompt editor compiles this same file and has no log, so it leaves it
        /// silent. Same arrangement as SharedSettings, and for the same reason:
        /// this file must not reference anything only one of the two has.
        /// </summary>
        internal static Action<string, Exception> ErrorSink = (message, ex) => { };

        /// <summary>One termbase, as the editor's table needs it.</summary>
        internal sealed class Termbase
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string SourceLang { get; set; }
            public string TargetLang { get; set; }
            public int Terms { get; set; }

            /// <summary>Trados's own flags, shown for information. memoQ keeps its own.</summary>
            public bool IsGlobal { get; set; }

            public override string ToString() => Name;
        }

        /// <summary>The path Supervertaler for Trados and Workbench share.</summary>
        internal static string Path =>
            System.IO.Path.Combine(global::Supervertaler.Core.SupervertalerPaths.Root,
                                   "resources", "supervertaler.db");

        internal static bool Exists => File.Exists(Path);

        private static bool _nativeReady;
        private static readonly object _lock = new object();

        /// <summary>
        /// Put memoQ's directory on PATH so System.Data.SQLite can find
        /// SQLite.Interop.dll, which it loads by name rather than by full path.
        ///
        /// <para>Done once, and additively: replacing PATH in a process that is
        /// memoQ would be a fine way to break something far from here.</para>
        /// </summary>
        private static void EnsureNativeOnPath()
        {
            lock (_lock)
            {
                if (_nativeReady) return;

                var dir = System.IO.Path.GetDirectoryName(
                    typeof(SQLiteConnection).Assembly.Location);

                if (!string.IsNullOrEmpty(dir))
                {
                    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (path.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0)
                        Environment.SetEnvironmentVariable("PATH", dir + ";" + path);
                }

                _nativeReady = true;
            }
        }

        private static SQLiteConnection Open()
        {
            EnsureNativeOnPath();

            // Read Only, and no write-ahead journal of our own: Trados or
            // Workbench may have this file open while we read it.
            var connection = new SQLiteConnection(
                "Data Source=" + Path + ";Version=3;Read Only=True;");
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Every termbase, with its term count, ordered by name.
        ///
        /// <para>Returns an empty list rather than throwing when the database is
        /// missing or unreadable: a translator who has never installed
        /// Supervertaler for Trados has no such file, and that is not an error
        /// condition - it is simply nothing to offer.</para>
        /// </summary>
        internal static IList<Termbase> All()
        {
            var found = new List<Termbase>();
            if (!Exists) return found;

            try
            {
                using (var connection = Open())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "select t.id, t.name, t.source_lang, t.target_lang, t.is_global, " +
                        "       count(tt.id) as terms " +
                        "from termbases t " +
                        "left join termbase_terms tt on tt.termbase_id = t.id " +
                        "group by t.id " +
                        "order by t.name collate nocase";

                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            found.Add(new Termbase
                            {
                                Id = reader.GetInt64(0),
                                Name = Text(reader, 1),
                                SourceLang = Text(reader, 2),
                                TargetLang = Text(reader, 3),
                                IsGlobal = Flag(reader, 4),
                                Terms = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5))
                            });
                }
            }
            catch (Exception ex)
            {
                ErrorSink("Termbase database could not be read: " + Path, ex);
                return new List<Termbase>();
            }

            return found;
        }

        /// <summary>
        /// Every term in the given termbases, as <see cref="TermIndex"/> entries.
        ///
        /// <para>A term marked non-translatable is skipped: it belongs to a
        /// different feature and would otherwise reach the prompt as a
        /// translation instruction saying to translate a word as itself.</para>
        /// </summary>
        internal static IList<TermIndex.Entry> TermsIn(IEnumerable<long> termbaseIds)
        {
            var entries = new List<TermIndex.Entry>();
            if (!Exists || termbaseIds == null) return entries;

            var ids = new List<long>();
            foreach (var id in termbaseIds) ids.Add(id);
            if (ids.Count == 0) return entries;

            try
            {
                using (var connection = Open())
                using (var command = connection.CreateCommand())
                {
                    // The ids are our own longs, read from this same database, so
                    // there is nothing here a parameter would protect against -
                    // but the list is built from them rather than from any string
                    // that ever came from outside.
                    command.CommandText =
                        "select source_term, target_term, forbidden " +
                        "from termbase_terms " +
                        "where termbase_id in (" + string.Join(",", ids.ConvertAll(i => i.ToString())) + ") " +
                        "  and coalesce(is_nontranslatable, 0) = 0 " +
                        "  and source_term is not null and source_term <> ''";

                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            var source = Text(reader, 0);
                            var target = Text(reader, 1);
                            if (source.Length == 0) continue;

                            entries.Add(new TermIndex.Entry
                            {
                                Source = source,
                                Target = target,
                                Forbidden = Flag(reader, 2)
                            });
                        }
                }
            }
            catch (Exception ex)
            {
                ErrorSink("Terms could not be read from the termbase database", ex);
                return new List<TermIndex.Entry>();
            }

            return entries;
        }

        private static string Text(IDataRecord row, int i) =>
            row.IsDBNull(i) ? "" : (row.GetValue(i) ?? "").ToString().Trim();

        /// <summary>
        /// SQLite has no boolean, and this database has been written by more than
        /// one product over time, so a flag arrives as 0/1, as "0"/"1", or as
        /// null. Anything that is not plainly true is false.
        /// </summary>
        private static bool Flag(IDataRecord row, int i)
        {
            if (row.IsDBNull(i)) return false;
            var value = row.GetValue(i);
            if (value is bool b) return b;
            if (value is long l) return l != 0;
            if (value is int n) return n != 0;

            var text = (value ?? "").ToString().Trim();
            return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
