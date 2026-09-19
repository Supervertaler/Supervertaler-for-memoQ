using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The write side of the shared termbase database: create a termbase, fill
    /// it, remove it. <see cref="TermbaseDb"/> stays read-only and is what every
    /// lookup goes through; this is what the editor's buttons go through.
    ///
    /// <para><b>This is a second writer to a schema two other products own, and
    /// it knows it.</b> The agreed home for the writer is <c>core/</c>, with
    /// Supervertaler for Trados's as the one that survives - see issue #133 in
    /// that repo. That move is not scheduled, and a translator who owns only this
    /// product cannot wait for it: with no writer they have no termbases at all.
    /// So this exists, kept deliberately small, and is the file that moves when
    /// the port happens. Two things make being a second writer honest rather than
    /// reckless: it creates exactly the schema the live database has
    /// (<see cref="TermbaseSchema"/>, checked against the real file by the
    /// harness), and it writes only what the other two write - the same
    /// defaults, the same date format, the same full-text index maintenance.</para>
    ///
    /// <para><b>The full-text index is kept by triggers, and this writer must
    /// never touch it.</b> <c>termbase_terms_fts</c> is external-content FTS5.
    /// For one day this writer maintained it by hand - an index insert paired
    /// with each row, a rebuild after a delete - because the live database had
    /// no triggers and Trados's writer never touched the index at all. Then the
    /// Trados side moved the maintenance into triggers on <c>termbase_terms</c>,
    /// installed by every writer's schema step (<see cref="TermbaseSchema"/>),
    /// so that all three products keep the index without any of them remembering
    /// to. With those in place a manual index insert is a double insert, and on
    /// an external-content table that is a corrupted index, not a duplicate row.
    /// So: nothing here writes to the index. The harness proves the triggers
    /// fire by MATCH, and runs FTS5's own integrity check after every
    /// operation - never by count, which reads the content table and proves
    /// nothing.</para>
    ///
    /// <para><b>Scale, stated:</b> an import inserts one row per term inside one
    /// transaction, and the insert trigger indexes each as it lands. Measured in
    /// the harness at 12,000 rows; designed for a termbase, not a translation
    /// memory. A delete is proportional to the termbase deleted. The one thing
    /// that grows with the whole file is the one-off rebuild when the triggers
    /// are first installed on a file that already has terms.</para>
    /// </summary>
    internal static class TermbaseWriter
    {
        internal static Action<string, Exception> ErrorSink = (message, ex) => { };

        internal sealed class ImportResult
        {
            public int Added { get; set; }

            /// <summary>Already in the termbase, in either orientation, or twice in the file.</summary>
            public int Duplicates { get; set; }

            /// <summary>The file's rows were stored turned round, because the termbase runs the other way.</summary>
            public bool Reversed { get; set; }
        }

        private static SqliteConnection Open()
        {
            TermbaseDb.EnsureProvider();

            var dir = Path.GetDirectoryName(TermbaseDb.Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var fresh = !File.Exists(TermbaseDb.Path);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = TermbaseDb.Path,
                Mode = SqliteOpenMode.ReadWriteCreate,

                // Off, explicitly. SQLite itself leaves foreign keys off, but
                // Microsoft.Data.Sqlite turns them ON for every connection it
                // opens. In a file the Workbench made, termbase_terms carries a
                // foreign key to its translation_units table; a file this
                // product makes no longer does (see TermbaseSchema), but the
                // Workbench's files are the ones this mostly writes into, and
                // nothing here wants the cascades anyway: every dependent row is
                // deleted by name in Delete(). Measured before it was written
                // down - with enforcement on, the first insert into a freshly
                // created file failed with "no such table: translation_units".
                ForeignKeys = false
            };

            var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            // Trados or the Workbench may be mid-write. Wait for them rather
            // than failing on the first locked page.
            Exec(connection, "pragma busy_timeout = 5000");

            // Write-ahead logging, as the live file has: readers keep reading
            // while a writer writes, which is the whole reason three products can
            // share one file. Set once, on creation; on an existing file it is
            // whatever the file already is.
            if (fresh) Exec(connection, "pragma journal_mode = wal");

            TermbaseSchema.EnsureCreated(connection);
            return connection;
        }

        /// <summary>
        /// A new, empty termbase. Returns its id.
        ///
        /// <para>The defaults are what Supervertaler for Trados writes when it
        /// creates one, read off rows it created: global, writable, ranked after
        /// every existing termbase, an empty string rather than NULL for the
        /// project id and description. Matching those is not fussiness - the
        /// Workbench queries this table too, and <c>NULL</c> and <c>''</c> are
        /// different answers to it.</para>
        /// </summary>
        internal static long Create(string name, string sourceLang, string targetLang, string description)
        {
            var cleanName = (name ?? string.Empty).Trim();
            if (cleanName.Length == 0) throw new ArgumentException("A termbase needs a name.");

            var source = global::Supervertaler.Core.LanguageCodes.Canonical(sourceLang);
            var target = global::Supervertaler.Core.LanguageCodes.Canonical(targetLang);

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                long ranking;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "select coalesce(max(ranking), 0) + 1 from termbases";
                    ranking = Convert.ToInt64(command.ExecuteScalar());
                }

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        "insert into termbases " +
                        "  (name, description, source_lang, target_lang, project_id, is_global, read_only, ranking) " +
                        "values (@name, @description, @source, @target, '', 1, 0, @ranking)";
                    command.Parameters.AddWithValue("@name", cleanName);
                    command.Parameters.AddWithValue("@description", (description ?? string.Empty).Trim());
                    command.Parameters.AddWithValue("@source", source);
                    command.Parameters.AddWithValue("@target", target);
                    command.Parameters.AddWithValue("@ranking", ranking);

                    try
                    {
                        command.ExecuteNonQuery();
                    }
                    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                    {
                        // Constraint: the only one on this insert is the name.
                        throw new InvalidOperationException(
                            "A termbase called “" + cleanName + "” already exists.", ex);
                    }
                }

                long id;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "select last_insert_rowid()";
                    id = Convert.ToInt64(command.ExecuteScalar());
                }

                transaction.Commit();
                return id;
            }
        }

        /// <summary>
        /// Add rows to a termbase, turned to its direction, skipping what it
        /// already holds.
        ///
        /// <para><paramref name="rowsSourceLang"/> and <paramref name="rowsTargetLang"/>
        /// say which way the rows are written; empty means "the same way as the
        /// termbase", which is the safe reading, since a wrong guess here would
        /// store every pair backwards.</para>
        ///
        /// <para>A duplicate is a pair the termbase already has in either
        /// orientation, compared without regard to case, or one the file itself
        /// repeats. That is Trados's rule for its own import, kept so that the
        /// same file imported through either product leaves the same termbase.</para>
        /// </summary>
        internal static ImportResult Import(long termbaseId, IEnumerable<TermbaseFiles.Row> rows,
                                            string rowsSourceLang, string rowsTargetLang)
        {
            var result = new ImportResult();
            if (rows == null) return result;

            using (var connection = Open())
            {
                string tbSource, tbTarget;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select source_lang, target_lang from termbases where id = @id";
                    command.Parameters.AddWithValue("@id", termbaseId);
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) throw new InvalidOperationException("That termbase no longer exists.");
                        tbSource = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                        tbTarget = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    }
                }

                result.Reversed = TermbaseDb.Reversed(tbSource, tbTarget, rowsSourceLang, rowsTargetLang);

                var seen = ExistingPairs(connection, termbaseId);

                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var row in rows)
                    {
                        if (row == null) continue;

                        // Cleaned, not merely trimmed: a term taken from a
                        // segment can carry a zero-width character, which Trim
                        // leaves in place and which would make the term
                        // unmatchable for ever, here and in Trados alike.
                        var source = TermText.Clean(row.Source);
                        var target = TermText.Clean(row.Target);
                        if (result.Reversed) { var swap = source; source = target; target = swap; }
                        if (source.Length == 0 || target.Length == 0) continue;

                        if (!Remember(seen, source, target)) { result.Duplicates++; continue; }

                        long id;
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText =
                                "insert into termbase_terms " +
                                "  (source_term, target_term, source_lang, target_lang, termbase_id, forbidden, notes, term_uuid) " +
                                "values (@source, @target, @slang, @tlang, @tb, @forbidden, @notes, @uuid); " +
                                "select last_insert_rowid()";
                            command.Parameters.AddWithValue("@source", source);
                            command.Parameters.AddWithValue("@target", target);
                            command.Parameters.AddWithValue("@slang", tbSource);
                            command.Parameters.AddWithValue("@tlang", tbTarget);
                            command.Parameters.AddWithValue("@tb", termbaseId);
                            command.Parameters.AddWithValue("@forbidden", row.Forbidden ? 1 : 0);
                            command.Parameters.AddWithValue("@notes", (object)NullIfEmpty(row.Notes) ?? DBNull.Value);
                            command.Parameters.AddWithValue("@uuid", Guid.NewGuid().ToString("D"));
                            id = Convert.ToInt64(command.ExecuteScalar());
                        }

                        // The search index is NOT touched here. The triggers
                        // TermbaseSchema installs on open keep it current, and a
                        // manual insert on top of a trigger corrupts an
                        // external-content FTS5 index rather than duplicating a
                        // row. This writer did maintain it by hand for one day;
                        // see the class remarks for why that stopped.
                        result.Added++;
                    }

                    if (result.Added > 0)
                    {
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = "update termbases set modified_date = CURRENT_TIMESTAMP where id = @id";
                            command.Parameters.AddWithValue("@id", termbaseId);
                            command.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                }
            }

            return result;
        }

        // ---- single terms, for the editor -------------------------------------------

        /// <summary>
        /// One pair added by hand, in the termbase's own direction - the editor
        /// shows which language each column is, so there is nothing to turn.
        /// Refused, with the reason in words, when the termbase already has the
        /// pair either way round.
        /// </summary>
        internal static long AddTerm(long termbaseId, string source, string target, bool forbidden, string notes)
        {
            var s = TermText.Clean(source);
            var t = TermText.Clean(target);
            if (s.Length == 0 || t.Length == 0) throw new ArgumentException("A term needs both a source and a target.");

            using (var connection = Open())
            {
                string tbSource, tbTarget;
                Languages(connection, termbaseId, out tbSource, out tbTarget);

                var seen = ExistingPairs(connection, termbaseId);
                if (!Remember(seen, s, t))
                    throw new InvalidOperationException("“" + s + "” → “" + t + "” is already in this termbase.");

                using (var transaction = connection.BeginTransaction())
                {
                    long id;
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText =
                            "insert into termbase_terms " +
                            "  (source_term, target_term, source_lang, target_lang, termbase_id, forbidden, notes, term_uuid) " +
                            "values (@source, @target, @slang, @tlang, @tb, @forbidden, @notes, @uuid); " +
                            "select last_insert_rowid()";
                        command.Parameters.AddWithValue("@source", s);
                        command.Parameters.AddWithValue("@target", t);
                        command.Parameters.AddWithValue("@slang", tbSource);
                        command.Parameters.AddWithValue("@tlang", tbTarget);
                        command.Parameters.AddWithValue("@tb", termbaseId);
                        command.Parameters.AddWithValue("@forbidden", forbidden ? 1 : 0);
                        command.Parameters.AddWithValue("@notes", (object)NullIfEmpty(notes) ?? DBNull.Value);
                        command.Parameters.AddWithValue("@uuid", Guid.NewGuid().ToString("D"));
                        id = Convert.ToInt64(command.ExecuteScalar());
                    }

                    Touch(connection, transaction, termbaseId);
                    transaction.Commit();
                    return id;
                }
            }
        }

        /// <summary>
        /// Change one row in place. The row keeps its id and its uuid - it is
        /// the same term, corrected - and the update trigger re-indexes it.
        /// </summary>
        internal static void UpdateTerm(long termId, string source, string target, bool forbidden, string notes)
        {
            var s = TermText.Clean(source);
            var t = TermText.Clean(target);
            if (s.Length == 0 || t.Length == 0) throw new ArgumentException("A term needs both a source and a target.");

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                long termbaseId;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "select termbase_id from termbase_terms where id = @id";
                    command.Parameters.AddWithValue("@id", termId);
                    var value = command.ExecuteScalar();
                    if (value == null || value is DBNull) throw new InvalidOperationException("That term no longer exists.");
                    termbaseId = Convert.ToInt64(value);
                }

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        "update termbase_terms set source_term = @source, target_term = @target, " +
                        "  forbidden = @forbidden, notes = @notes, modified_date = CURRENT_TIMESTAMP " +
                        "where id = @id";
                    command.Parameters.AddWithValue("@source", s);
                    command.Parameters.AddWithValue("@target", t);
                    command.Parameters.AddWithValue("@forbidden", forbidden ? 1 : 0);
                    command.Parameters.AddWithValue("@notes", (object)NullIfEmpty(notes) ?? DBNull.Value);
                    command.Parameters.AddWithValue("@id", termId);
                    command.ExecuteNonQuery();
                }

                Touch(connection, transaction, termbaseId);
                transaction.Commit();
            }
        }

        /// <summary>Remove one row and its synonyms. The delete trigger takes it out of the index.</summary>
        internal static void DeleteTerm(long termId)
        {
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                long termbaseId = 0;
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "select termbase_id from termbase_terms where id = @id";
                    command.Parameters.AddWithValue("@id", termId);
                    var value = command.ExecuteScalar();
                    if (value != null && !(value is DBNull)) termbaseId = Convert.ToInt64(value);
                }

                Exec(connection, transaction, "delete from termbase_synonyms where term_id = @id", termId);
                Exec(connection, transaction, "delete from termbase_terms where id = @id", termId);
                if (termbaseId != 0) Touch(connection, transaction, termbaseId);
                transaction.Commit();
            }
        }

        private static void Languages(SqliteConnection connection, long termbaseId, out string source, out string target)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "select source_lang, target_lang from termbases where id = @id";
                command.Parameters.AddWithValue("@id", termbaseId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidOperationException("That termbase no longer exists.");
                    source = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    target = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                }
            }
        }

        /// <summary>Every pair the termbase holds, remembered both ways round.</summary>
        private static HashSet<string> ExistingPairs(SqliteConnection connection, long termbaseId)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "select source_term, target_term from termbase_terms where termbase_id = @id";
                command.Parameters.AddWithValue("@id", termbaseId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        Remember(seen, reader.GetString(0), reader.GetString(1));
            }
            return seen;
        }

        /// <summary>The termbase was touched: say so where the other products look.</summary>
        private static void Touch(SqliteConnection connection, SqliteTransaction transaction, long termbaseId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "update termbases set modified_date = CURRENT_TIMESTAMP where id = @id";
                command.Parameters.AddWithValue("@id", termbaseId);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Remove a termbase and everything that hangs off it. Nothing here relies
        /// on the database's ON DELETE clauses: foreign-key enforcement is turned
        /// off on this product's connections (see <see cref="Open"/> for why), so
        /// each dependent table is cleared by name.
        /// </summary>
        internal static void Delete(long termbaseId)
        {
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                Exec(connection, transaction,
                    "delete from termbase_synonyms where term_id in (select id from termbase_terms where termbase_id = @id)", termbaseId);
                Exec(connection, transaction, "delete from termbase_terms where termbase_id = @id", termbaseId);

                // Trados's activation rows for it, when the table is there at all.
                // Its absence is normal in a database this product created.
                foreach (var table in new[] { "termbase_activation", "termbase_project_activation" })
                    if (TableExists(connection, table))
                        Exec(connection, transaction, "delete from " + table + " where termbase_id = @id", termbaseId);

                Exec(connection, transaction, "delete from termbases where id = @id", termbaseId);

                // The index follows through its delete trigger, one row at a
                // time, with the old values it needs. No rebuild: on a file the
                // size of Michael's that would re-index 36,000 terms to forget a
                // dozen, and with the triggers in place it would also be the
                // second writer to the index in one transaction.

                transaction.Commit();
            }

            TermbaseSelection.Forget(termbaseId);
        }

        // ---- helpers -------------------------------------------------------------

        /// <summary>Records the pair both ways round; false if it was already known.</summary>
        private static bool Remember(HashSet<string> seen, string source, string target)
        {
            var forward = source.Trim().ToLowerInvariant() + "\0" + target.Trim().ToLowerInvariant();
            var backward = target.Trim().ToLowerInvariant() + "\0" + source.Trim().ToLowerInvariant();
            if (seen.Contains(forward) || seen.Contains(backward)) return false;
            seen.Add(forward);
            return true;
        }

        private static string NullIfEmpty(string text)
        {
            var value = (text ?? string.Empty).Trim();
            return value.Length == 0 ? null : value;
        }

        private static bool TableExists(SqliteConnection connection, string name)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = @name";
                command.Parameters.AddWithValue("@name", name);
                return Convert.ToInt64(command.ExecuteScalar()) > 0;
            }
        }

        private static void Exec(SqliteConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static void Exec(SqliteConnection connection, SqliteTransaction transaction, string sql, long? id)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                if (id.HasValue) command.Parameters.AddWithValue("@id", id.Value);
                command.ExecuteNonQuery();
            }
        }
    }
}
