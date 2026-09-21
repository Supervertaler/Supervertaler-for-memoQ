using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

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
    /// EntryPointNotFoundException. memoQ ships this stack itself -
    /// Microsoft.Data.Sqlite 9.0.3.0, SQLitePCLRaw 2.1.10.2445 and
    /// e_sqlite3.dll - so every reference is Private=false and we use theirs.
    /// memoQ is not a bystander here: its own termbase engine, MemoQ.NGTB.dll,
    /// is built on the same stack.</para>
    ///
    /// <para><b>Why not System.Data.SQLite, which memoQ also ships.</b> It was
    /// the first choice and it was wrong. Measured 2026-09-16: memoQ's build of
    /// System.Data.SQLite 1.0.119.0 <i>has no FTS5 module</i> - it cannot read
    /// the six full-text indexes already in this file, and
    /// <c>create virtual table ... using fts5</c> fails outright, so it could
    /// never create this schema from scratch. That alone rules it out, because a
    /// translator who uses neither Trados nor Workbench has no database until
    /// this product makes one. It is also the slower of the two: a full scan of
    /// all 36,091 terms took 101 ms here against 217 ms there. Supervertaler for
    /// Trados is on Microsoft.Data.Sqlite as well, so both products now reach
    /// this file through one library rather than two.</para>
    ///
    /// <para><b>Measured, 2026-09-16:</b> the file is 2.25 GB - overwhelmingly
    /// translation memories (1,553,875 rows) and their indexes, not terms.
    /// Opening it costs nothing, the 84 termbases list in milliseconds, and
    /// every term in the file - all 36,091 of them - scans in 101 ms. Loading a
    /// whole selection up front is therefore fine.</para>
    ///
    /// <para><b>The scale assumption, stated out loud:</b> tested at 36,000
    /// terms, designed for that order. Nobody has measured ten times it, and a
    /// design that reads everything into memory is the one that stops being free
    /// first. What needs watching either way is the per-segment matching, which
    /// is <see cref="TermIndex"/>'s problem and not this class's.</para>
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

            /// <summary>
            /// Trados's project termbase. Used as memoQ's default until memoQ has
            /// an opinion of its own, so a translator who has already nominated
            /// one there does not nominate it twice.
            /// </summary>
            public bool IsProjectTermbase { get; set; }

            public override string ToString() => Name;
        }

        /// <summary>
        /// A harness points this somewhere disposable. Nothing in the product
        /// sets it: the writer's tests create, fill and delete termbases, and
        /// the one file they must never do that to is the real one.
        /// </summary>
        internal static string PathOverride;

        /// <summary>The path Supervertaler for Trados and Workbench share.</summary>
        internal static string Path =>
            !string.IsNullOrEmpty(PathOverride)
                ? PathOverride
                : System.IO.Path.Combine(global::Supervertaler.Core.SupervertalerPaths.Root,
                                         "resources", "supervertaler.db");

        internal static bool Exists => File.Exists(Path);

        private static bool _providerReady;
        private static readonly object _lock = new object();

        [DllImport("kernel32", EntryPoint = "LoadLibraryW", SetLastError = true,
                   CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadNativeLibrary(string path);

        /// <summary>
        /// Settle which native SQLite this process uses, then initialise the
        /// provider.
        ///
        /// <para><b>The load by absolute path is the point.</b> SQLitePCLRaw's
        /// dynamic_cdecl provider asks the operating system for "e_sqlite3" by
        /// name, and the answer depends on the search order of whichever process
        /// we are in - memoQ, or the prompt editor, or a harness. Taking memoQ's
        /// own copy by full path first means the module is already loaded when
        /// the provider asks, so there is nothing left to get wrong. This is the
        /// trick Supervertaler for Trados uses inside Studio, for the same
        /// reason and to the same end.</para>
        ///
        /// <para><b>Initialising twice is safe.</b> memoQ's own termbase engine
        /// uses this stack, so the provider may well be initialised before we
        /// ever run. Measured 2026-09-16: a second Init is a no-op rather than a
        /// throw, an Init after another component has set a provider is fine,
        /// and the connection works afterwards either way.</para>
        /// </summary>
        internal static void EnsureProvider()
        {
            lock (_lock)
            {
                if (_providerReady) return;

                // Guarded as a whole: a translator whose memoQ is laid out in
                // some way we did not foresee should lose the termbase list, not
                // have the editor fail to open.
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(
                        typeof(SqliteConnection).Assembly.Location);

                    if (!string.IsNullOrEmpty(dir))
                    {
                        var native = System.IO.Path.Combine(dir, "e_sqlite3.dll");
                        if (File.Exists(native)) LoadNativeLibrary(native);
                    }

                    SQLitePCL.Batteries_V2.Init();
                }
                catch (Exception ex)
                {
                    ErrorSink("SQLite provider could not be initialised", ex);
                }

                _providerReady = true;
            }
        }

        private static SqliteConnection Open()
        {
            EnsureProvider();

            // Read-only, and built rather than concatenated so that a data
            // folder with a semicolon or a quote in its path cannot change the
            // meaning of the string.
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Mode = SqliteOpenMode.ReadOnly
            };

            var connection = new SqliteConnection(builder.ToString());
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
                        "       count(tt.id) as terms, t.is_project_termbase " +
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
                                Terms = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5)),
                                IsProjectTermbase = Flag(reader, 6)
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
            return TermsIn(termbaseIds, null, null);
        }

        /// <summary>
        /// Every term in the given termbases, turned the way this job needs them.
        ///
        /// <para><b>Why direction matters here.</b> A termbase is stored one way
        /// round. Of the 84 in this database, 61 are nl-to-en and 19 are en-to-nl,
        /// and the 19 are just as useful on a Dutch-to-English job - read
        /// backwards. Until this existed they were silently dead: we matched the
        /// source column against the source segment, so an en-to-nl termbase was
        /// asked to find English words in Dutch text and answered almost nothing.
        /// The exception that made it look half-working was a word spelled the
        /// same in both languages - "water" - which appears in both columns and
        /// so matched by accident.</para>
        ///
        /// <para>Supervertaler for Trados turns them round; this now does too.
        /// A termbase whose pair matches neither way round is loaded as it is
        /// stored rather than dropped: the user ticked it deliberately, and
        /// language labels in this database are not always populated.</para>
        /// </summary>
        internal static IList<TermIndex.Entry> TermsIn(IEnumerable<long> termbaseIds,
                                                      string jobSource, string jobTarget)
        {
            var entries = new List<TermIndex.Entry>();
            if (!Exists || termbaseIds == null) return entries;

            var ids = new List<long>();
            foreach (var id in termbaseIds) ids.Add(id);
            if (ids.Count == 0) return entries;

            try
            {
                using (var connection = Open())
                {
                    var synonyms = SynonymsFor(connection, ids);

                    using (var command = connection.CreateCommand())
                    {
                    // The ids are our own longs, read from this same database, so
                    // there is nothing here a parameter would protect against -
                    // but the list is built from them rather than from any string
                    // that ever came from outside.
                    command.CommandText =
                        "select tt.source_term, tt.target_term, tt.forbidden, " +
                        "       tt.termbase_id, t.name, t.source_lang, t.target_lang, tt.id " +
                        "from termbase_terms tt " +
                        "join termbases t on t.id = tt.termbase_id " +
                        "where tt.termbase_id in (" + string.Join(",", ids.ConvertAll(i => i.ToString())) + ") " +
                        "  and coalesce(tt.is_nontranslatable, 0) = 0 " +
                        "  and tt.source_term is not null and tt.source_term <> ''";

                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            var source = Text(reader, 0);
                            var target = Text(reader, 1);

                            if (Reversed(Text(reader, 5), Text(reader, 6), jobSource, jobTarget))
                            {
                                var swap = source;
                                source = target;
                                target = swap;
                            }

                            if (source.Length == 0) continue;

                            var termId = reader.IsDBNull(7) ? 0L : reader.GetInt64(7);
                            var reversed = Reversed(Text(reader, 5), Text(reader, 6), jobSource, jobTarget);

                            // Which side of THIS JOB a synonym belongs to. The
                            // table records the side of the termbase as stored, so
                            // in a termbase read backwards a stored "source"
                            // synonym is a target-side one for this job. The wrong
                            // way round would index a translation as something to
                            // look for in the source text.
                            List<string> sourceSide = null, targetSide = null;
                            Synonyms syn;
                            if (termId != 0 && synonyms.TryGetValue(termId, out syn))
                            {
                                sourceSide = reversed ? syn.Target : syn.Source;
                                targetSide = reversed ? syn.Source : syn.Target;
                            }

                            var alsoAcceptable = targetSide != null && targetSide.Count > 0 ? targetSide : null;

                            entries.Add(new TermIndex.Entry
                            {
                                AlsoAcceptable = alsoAcceptable,
                                Source = source,
                                Target = target,
                                Forbidden = Flag(reader, 2),
                                TermbaseId = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),

                                // Carried so the terminology pane can say which
                                // termbase answered. With several selected, "a
                                // term matched" is much less useful than "this
                                // termbase says this".
                                Origin = Text(reader, 4)
                            });

                            // A source-side synonym is another spelling of the
                            // same term, so it is a thing to look for in its own
                            // right and gets its own entry with the same target.
                            if (sourceSide != null)
                                foreach (var also in sourceSide)
                                {
                                    if (string.IsNullOrWhiteSpace(also)) continue;

                                    entries.Add(new TermIndex.Entry
                                    {
                                        Source = also,
                                        Target = target,
                                        Forbidden = Flag(reader, 2),
                                        TermbaseId = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                                        Origin = Text(reader, 4),
                                        FromSynonym = true,
                                        AlsoAcceptable = alsoAcceptable
                                    });
                                }
                        }
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

        /// <summary>Both sides' synonyms for one term, as stored.</summary>
        internal sealed class Synonyms
        {
            public List<string> Source = new List<string>();
            public List<string> Target = new List<string>();
        }

        /// <summary>
        /// Every synonym belonging to the selected termbases, by term id.
        ///
        /// <para>One query rather than one per term: the live database here holds
        /// 1,147 synonyms against 37,359 terms, and a query per term would be tens
        /// of thousands of round trips to save a dictionary.</para>
        ///
        /// <para>This product ignored the table entirely until 2026-09-21 while
        /// Supervertaler for Trados wrote and matched it, so a synonym saved there
        /// was silently invisible here in the same termbase - and 1,081 of the
        /// 1,147 sit in the one termbase in daily use, which is the worst place
        /// for a silent gap.</para>
        /// </summary>
        private static Dictionary<long, Synonyms> SynonymsFor(SqliteConnection connection, List<long> ids)
        {
            var found = new Dictionary<long, Synonyms>();
            if (ids == null || ids.Count == 0) return found;

            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "select s.term_id, s.synonym_text, s.language " +
                        "from termbase_synonyms s " +
                        "join termbase_terms tt on tt.id = s.term_id " +
                        "where tt.termbase_id in (" + string.Join(",", ids.ConvertAll(i => i.ToString())) + ") " +
                        "  and s.synonym_text is not null and s.synonym_text <> '' " +
                        "order by s.display_order, s.id";

                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0)) continue;

                            var termId = reader.GetInt64(0);
                            var text = Text(reader, 1);
                            var side = Text(reader, 2);
                            if (text.Length == 0) continue;

                            Synonyms entry;
                            if (!found.TryGetValue(termId, out entry)) found[termId] = entry = new Synonyms();

                            // The column is constrained to these two values, but a
                            // file written by something else may still surprise us,
                            // and guessing a side would index a translation as a
                            // source term.
                            if (string.Equals(side, "source", StringComparison.OrdinalIgnoreCase)) entry.Source.Add(text);
                            else if (string.Equals(side, "target", StringComparison.OrdinalIgnoreCase)) entry.Target.Add(text);
                        }
                }
            }
            catch (Exception ex)
            {
                // A termbase file without the synonyms table, or an unreadable
                // one, must not stop the terms loading. Fewer hits, not no hits.
                ErrorSink("Synonyms could not be read; terms are unaffected", ex);
                return new Dictionary<long, Synonyms>();
            }

            return found;
        }

        /// <summary>
        /// Is this termbase stored the opposite way round from the job?
        ///
        /// <para>The comparison goes through Core's LanguageCodes, because memoQ
        /// says "dut-NL" where the database says "nl" and neither can be compared
        /// as it stands. That table is shared with Supervertaler for Trados: two
        /// copies of a language table is how two products come to disagree about
        /// what language something is in.</para>
        ///
        /// <para>Answers false whenever it cannot tell - an unlabelled termbase,
        /// or a job whose languages we were not given. Getting this wrong in the
        /// false direction costs the hits we already were not getting; getting it
        /// wrong in the true direction would show every translation backwards.</para>
        /// </summary>
        internal static bool Reversed(string tbSource, string tbTarget,
                                      string jobSource, string jobTarget)
        {
            var ts = global::Supervertaler.Core.LanguageCodes.Normalise(tbSource);
            var tt = global::Supervertaler.Core.LanguageCodes.Normalise(tbTarget);
            var js = global::Supervertaler.Core.LanguageCodes.Normalise(jobSource);
            var jt = global::Supervertaler.Core.LanguageCodes.Normalise(jobTarget);

            if (ts.Length == 0 || tt.Length == 0 || js.Length == 0) return false;
            if (ts == js) return false;          // already the right way round
            if (tt != js) return false;          // neither side is our source: leave it alone

            // Its target is our source. If its source is also our target this is
            // plainly the same pair backwards; if we were told no target, that is
            // still the best reading available.
            return jt.Length == 0 || ts == jt;
        }

        /// <summary>One term as the editor shows it: a row with an identity.</summary>
        internal sealed class Term
        {
            public long Id { get; set; }
            public string Source { get; set; }
            public string Target { get; set; }
            public bool Forbidden { get; set; }
            public string Notes { get; set; }
        }

        /// <summary>
        /// Every row of one termbase, with ids, in the order entered. For the
        /// term editor, which needs to say which row it changed.
        /// </summary>
        internal static IList<Term> TermsOf(long termbaseId)
        {
            var terms = new List<Term>();
            if (!Exists) return terms;

            try
            {
                using (var connection = Open())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "select id, source_term, target_term, forbidden, notes " +
                        "from termbase_terms where termbase_id = @id order by id";
                    command.Parameters.AddWithValue("@id", termbaseId);

                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            terms.Add(new Term
                            {
                                Id = reader.GetInt64(0),
                                Source = Text(reader, 1),
                                Target = Text(reader, 2),
                                Forbidden = Flag(reader, 3),
                                Notes = Text(reader, 4)
                            });
                }
            }
            catch (Exception ex)
            {
                ErrorSink("Terms could not be read for editing", ex);
            }

            return terms;
        }

        /// <summary>
        /// Something that changes whenever the terms of these termbases do -
        /// from any product. The lookup index reloads a selection when this
        /// moves, which is how an edit made in the editor, or a term added from
        /// Studio, reaches memoQ's grid without a restart.
        ///
        /// <para>Row count, highest id and latest modified_date together: a
        /// count catches deletes, the id catches an add-after-delete that leaves
        /// the count unchanged, the date catches an edit. One indexed query per
        /// check, on the three-second throttle the index already keeps.</para>
        /// </summary>
        internal static string ChangeStamp(IEnumerable<long> termbaseIds)
        {
            if (!Exists || termbaseIds == null) return string.Empty;

            var ids = new List<long>(termbaseIds);
            if (ids.Count == 0) return string.Empty;

            try
            {
                using (var connection = Open())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "select count(*), coalesce(max(id), 0), coalesce(max(modified_date), '') " +
                        "from termbase_terms where termbase_id in (" +
                        string.Join(",", ids.ConvertAll(i => i.ToString())) + ")";

                    using (var reader = command.ExecuteReader())
                        if (reader.Read())
                            return reader.GetValue(0) + "/" + reader.GetValue(1) + "/" + reader.GetValue(2);
                }
            }
            catch (Exception ex)
            {
                ErrorSink("Termbase change stamp could not be read", ex);
            }

            return string.Empty;
        }

        /// <summary>
        /// Every row of one termbase as a file would carry it, in the order it
        /// was entered. For Export.
        ///
        /// <para>Everything, including rows marked non-translatable, which
        /// <see cref="TermsIn"/> leaves out: an export that quietly dropped rows
        /// would be the wrong kind of surprise, and a non-translatable re-imported
        /// is merely a term whose translation is itself.</para>
        /// </summary>
        internal static IList<TermbaseFiles.Row> RowsOf(long termbaseId)
        {
            var rows = new List<TermbaseFiles.Row>();
            if (!Exists) return rows;

            try
            {
                using (var connection = Open())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "select source_term, target_term, forbidden, notes " +
                        "from termbase_terms where termbase_id = @id order by id";
                    command.Parameters.AddWithValue("@id", termbaseId);

                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            rows.Add(new TermbaseFiles.Row
                            {
                                Source = Text(reader, 0),
                                Target = Text(reader, 1),
                                Forbidden = Flag(reader, 2),
                                Notes = Text(reader, 3)
                            });
                }
            }
            catch (Exception ex)
            {
                ErrorSink("Terms could not be read for export", ex);
            }

            return rows;
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
