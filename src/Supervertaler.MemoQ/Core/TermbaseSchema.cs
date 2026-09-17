using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The part of the shared database that termbases live in, as the database
    /// itself defines it.
    ///
    /// <para><b>Copied from the live file, not written.</b> Every statement below
    /// is the <c>sql</c> column of <c>sqlite_master</c> in a real
    /// <c>supervertaler.db</c> written by Supervertaler for Trados and the
    /// Workbench, taken on 2026-09-17 - odd trailing columns, inconsistent
    /// quoting and all. That is deliberate. This product is a second writer to a
    /// schema two other products already own, and the only honest way to be one
    /// is to create exactly what they create, so that a database this product
    /// makes from nothing is indistinguishable from one they made. The harness
    /// (<c>tools/termbase-writer-test.ps1</c>) compares a database built from
    /// these statements against the live one, object by object, and fails if
    /// either side drifts. That test is the whole safeguard; do not "tidy" the SQL.</para>
    ///
    /// <para><b>What is deliberately NOT here.</b> <c>termbase_activation</c> and
    /// <c>termbase_project_activation</c>: which termbases a host uses is that
    /// host's own business, kept in its own file, and those two tables are what
    /// happened when a host wrote its choices into the shared one - 5,139 rows
    /// keyed by project ids that join to nothing. Trados creates them when it
    /// needs them; this product never needs them.</para>
    ///
    /// <para><b>The one clause deliberately left out</b> - the single place
    /// "copied, not written" is overridden, and the reason is recorded here and
    /// enforced by the harness rather than left to memory. In the live file,
    /// <c>termbase_terms</c> ends with
    /// <c>FOREIGN KEY (tm_source_id) REFERENCES translation_units(id)</c>. That
    /// clause is the Workbench's: <c>translation_units</c> is its
    /// translation-memory table, which it creates and this product never will.
    /// Trados's own <c>CREATE TABLE</c> for <c>termbase_terms</c> has no such
    /// clause either. A database this product creates therefore keeps the
    /// <c>tm_source_id</c> column and drops the key, because a file carrying a
    /// foreign key to a table that does not exist is a defect of the file: any
    /// writer with enforcement on - and Microsoft.Data.Sqlite turns it on by
    /// default - fails on the first insert into <c>termbase_terms</c> with "no
    /// such table", and Trados's delete, which turns enforcement on deliberately
    /// for its cascades, would fail the same way. Found by this product's own
    /// harness on 2026-09-17; the Trados side confirmed their DDL and agreed the
    /// fix belongs in the created file, not in every writer. Issue #133.</para>
    ///
    /// <para><b>Additive-only, no version row.</b> Agreed with the Trados side:
    /// <c>PRAGMA user_version</c> stays 0, and a database made here is completed
    /// by whichever product opens it next adding the columns it needs. Nothing
    /// here alters a table that already exists.</para>
    /// </summary>
    internal static class TermbaseSchema
    {
        /// <summary>One schema object: what sqlite_master calls it, and its SQL.</summary>
        internal sealed class Item
        {
            public string Type { get; set; }
            public string Name { get; set; }
            public string Sql { get; set; }
        }

        /// <summary>
        /// The index columns a file this product creates has - the Workbench's.
        /// Declared before <see cref="Objects"/>, which uses it in its own
        /// initialiser; C# initialises statics in declaration order.
        /// </summary>
        internal static readonly string[] ThreeColumns = { "source_term", "target_term", "definition" };

        /// <summary>
        /// In the order the live database created them, which matters for one
        /// pair: the FTS table names <c>termbase_terms</c> as its content and must
        /// come after it.
        /// </summary>
        internal static readonly Item[] Objects =
        {
            new Item
            {
                Type = "table", Name = "termbases",
                Sql = @"CREATE TABLE termbases (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                description TEXT,
                source_lang TEXT,
                target_lang TEXT,
                project_id INTEGER,  -- NULL = global, set = project-specific
                is_global BOOLEAN DEFAULT 1,
                created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                modified_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            , priority INTEGER DEFAULT 50, is_project_termbase BOOLEAN DEFAULT 0, ranking INTEGER, read_only BOOLEAN DEFAULT 1, ai_inject BOOLEAN DEFAULT 0, case_sensitive INTEGER DEFAULT -1, voice_dictation_enabled BOOLEAN DEFAULT 0, superlookup_enabled BOOLEAN DEFAULT 1)"
            },
            new Item
            {
                Type = "table", Name = "termbase_terms",
                Sql = @"CREATE TABLE ""termbase_terms"" (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_term TEXT NOT NULL,
                target_term TEXT NOT NULL,
                source_lang TEXT DEFAULT 'unknown',
                target_lang TEXT DEFAULT 'unknown',
                termbase_id INTEGER NOT NULL,
                priority INTEGER DEFAULT 99,
                project_id TEXT,

                -- Terminology-specific fields
                synonyms TEXT,
                forbidden_terms TEXT,
                definition TEXT,
                context TEXT,
                part_of_speech TEXT,
                domain TEXT,
                case_sensitive BOOLEAN DEFAULT 0,
                forbidden BOOLEAN DEFAULT 0,

                -- Link to TM entry (optional)
                tm_source_id INTEGER,

                -- Metadata
                created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                modified_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                usage_count INTEGER DEFAULT 0,
                notes TEXT, project TEXT, client TEXT, term_uuid TEXT, note TEXT, is_nontranslatable BOOLEAN DEFAULT 0, source_abbreviation TEXT DEFAULT '', target_abbreviation TEXT DEFAULT '', url TEXT DEFAULT ''
            )"
            },
            new Item
            {
                Type = "index", Name = "idx_gt_termbase_id",
                Sql = @"CREATE INDEX idx_gt_termbase_id
            ON termbase_terms(termbase_id)
        "
            },
            new Item
            {
                Type = "index", Name = "idx_termbase_term_uuid",
                Sql = @"CREATE UNIQUE INDEX idx_termbase_term_uuid
                ON termbase_terms(term_uuid)
            "
            },
            new Item
            {
                Type = "table", Name = "termbase_synonyms",
                Sql = @"CREATE TABLE termbase_synonyms (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                term_id INTEGER NOT NULL,
                synonym_text TEXT NOT NULL,
                language TEXT NOT NULL CHECK(language IN ('source', 'target')),
                created_date TEXT DEFAULT (datetime('now')),
                modified_date TEXT DEFAULT (datetime('now')), display_order INTEGER DEFAULT 0, forbidden INTEGER DEFAULT 0,
                FOREIGN KEY (term_id) REFERENCES termbase_terms(id) ON DELETE CASCADE
            )"
            },
            new Item
            {
                Type = "index", Name = "idx_synonyms_term_id",
                Sql = @"CREATE INDEX idx_synonyms_term_id
            ON termbase_synonyms(term_id)
        "
            },
            new Item
            {
                Type = "index", Name = "idx_synonyms_text",
                Sql = @"CREATE INDEX idx_synonyms_text
            ON termbase_synonyms(synonym_text)
        "
            },
            new Item
            {
                Type = "index", Name = "idx_synonyms_language",
                Sql = @"CREATE INDEX idx_synonyms_language
            ON termbase_synonyms(language)
        "
            },
            // External-content FTS5: the index holds no text of its own and reads
            // rows back from termbase_terms by id. Consequence for every writer:
            // the database does NOT keep it up to date - there are no triggers in
            // the live file - so whoever inserts or deletes a term must tell the
            // index, or the Workbench's search silently stops finding those terms.
            new Item
            {
                Type = "table", Name = "termbase_terms_fts",
                Sql = @"CREATE VIRTUAL TABLE termbase_terms_fts
            USING fts5(
                source_term,
                target_term,
                definition,
                content=termbase_terms,
                content_rowid=id
            )"
            },
            // The triggers that keep that index current for EVERY writer - Trados,
            // Workbench, memoQ - so that no writer maintains it by hand and no
            // writer forgets to. Agreed text with the Trados side on 2026-09-17,
            // byte for byte; their install emits the same, their test asserts it,
            // and the harness here asserts that TriggerSql() reproduces it.
            new Item { Type = "trigger", Name = "termbase_terms_fts_ai", Sql = TriggerSql(ThreeColumns, Insert) },
            new Item { Type = "trigger", Name = "termbase_terms_fts_ad", Sql = TriggerSql(ThreeColumns, Delete) },
            new Item { Type = "trigger", Name = "termbase_terms_fts_au", Sql = TriggerSql(ThreeColumns, Update) }
        };

        // ---- triggers --------------------------------------------------------------

        internal const int Insert = 0, Delete = 1, Update = 2;

        // ThreeColumns is declared ABOVE Objects, near the top of the class:
        // Objects' initialiser calls TriggerSql(ThreeColumns, ...) and C#
        // initialises statics in declaration order, so declared here it would
        // still be null when Objects is built and the type initialiser would
        // throw - which it did, once.

        // Every column name an index in the field might have, in the order the
        // trigger text lists them. Old Trados-created files declared a fourth,
        // notes; a trigger written for three columns on such a file would leave
        // the notes tokens behind on every delete - quiet corruption FTS5's
        // integrity-check catches later. So the columns are READ from the file
        // (see EnsureCreated) and the text generated for exactly those.
        private static readonly string[] KnownIndexColumns = { "source_term", "target_term", "definition", "notes" };

        /// <summary>
        /// The trigger text for an index with these columns. For
        /// <see cref="ThreeColumns"/> this is the agreed text exactly, including
        /// its whitespace: two-space indents, one statement per line.
        /// </summary>
        internal static string TriggerSql(string[] columns, int kind)
        {
            var cols = string.Join(", ", columns);
            var news = string.Join(", ", Array.ConvertAll(columns, c => "new." + c));
            var olds = string.Join(", ", Array.ConvertAll(columns, c => "old." + c));

            var insertRow = "  INSERT INTO termbase_terms_fts(rowid, " + cols + ")\n"
                          + "  VALUES (new.id, " + news + ");\n";
            var deleteRow = "  INSERT INTO termbase_terms_fts(termbase_terms_fts, rowid, " + cols + ")\n"
                          + "  VALUES ('delete', old.id, " + olds + ");\n";

            switch (kind)
            {
                case Insert:
                    return "CREATE TRIGGER IF NOT EXISTS termbase_terms_fts_ai AFTER INSERT ON termbase_terms BEGIN\n"
                         + insertRow + "END;";
                case Delete:
                    return "CREATE TRIGGER IF NOT EXISTS termbase_terms_fts_ad AFTER DELETE ON termbase_terms BEGIN\n"
                         + deleteRow + "END;";
                default:
                    return "CREATE TRIGGER IF NOT EXISTS termbase_terms_fts_au AFTER UPDATE ON termbase_terms BEGIN\n"
                         + deleteRow + insertRow + "END;";
            }
        }

        /// <summary>
        /// The index's columns as the file actually declares them, in trigger
        /// order; <see cref="ThreeColumns"/> when the index is not there yet.
        /// </summary>
        internal static string[] IndexColumns(SqliteConnection connection)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "pragma table_info(termbase_terms_fts)";
                try
                {
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) present.Add(reader.GetString(1));
                }
                catch (SqliteException)
                {
                    // No such table: a fresh file, about to get the three-column index.
                }
            }

            if (present.Count == 0) return ThreeColumns;

            var found = new List<string>();
            foreach (var name in KnownIndexColumns)
                if (present.Contains(name)) found.Add(name);

            return found.Count == 0 ? ThreeColumns : found.ToArray();
        }

        /// <summary>
        /// Create whatever is missing, touch whatever exists not at all. Safe to
        /// call on every open; on the live database it does nothing.
        /// </summary>
        internal static void EnsureCreated(SqliteConnection connection)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "select name from sqlite_master";
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) present.Add(reader.GetString(0));
            }

            var triggersMade = false;

            foreach (var item in Objects)
            {
                if (present.Contains(item.Name)) continue;

                var sql = item.Sql;

                if (item.Type == "trigger")
                {
                    // For the index this file has, not the one we would make.
                    var kind = item.Name.EndsWith("_ai") ? Insert : item.Name.EndsWith("_ad") ? Delete : Update;
                    sql = TriggerSql(IndexColumns(connection), kind);
                    triggersMade = true;
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }
            }

            // A file that has terms but had no triggers until now has an index
            // that was maintained by hand, or not at all - Trados's writer never
            // touched it, and the Workbench rebuilt it once in a migration and
            // never again. Rebuild once, so the triggers take over from a correct
            // state and the delete trigger's old-values delete is right from the
            // first row. The same thing the Trados install does.
            if (triggersMade && Count(connection, "termbase_terms") > 0)
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "insert into termbase_terms_fts(termbase_terms_fts) values('rebuild')";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static long Count(SqliteConnection connection, string table)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "select count(*) from " + table;
                try { return Convert.ToInt64(command.ExecuteScalar()); }
                catch (SqliteException) { return 0; }
            }
        }

        /// <summary>
        /// The live file's <c>termbase_terms</c> DDL with the Workbench's
        /// translation-memory foreign key removed - the one clause this product
        /// does not reproduce (see the class remarks). The drift test applies this
        /// to the LIVE side before comparing, so that the deviation is a single,
        /// named exception rather than a reason to loosen the comparison.
        /// </summary>
        internal static string WithoutForeignTmKey(string sql)
        {
            if (sql == null) return string.Empty;

            return System.Text.RegularExpressions.Regex.Replace(
                sql,
                @",?\s*FOREIGN\s+KEY\s*\(\s*tm_source_id\s*\)\s*REFERENCES\s+translation_units\s*\(\s*id\s*\)(\s+ON\s+DELETE\s+SET\s+NULL)?",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// SQL as sqlite_master would store it, made comparable: quoting of
        /// identifiers dropped, whitespace collapsed, comments removed. Used by
        /// the drift test on both sides of the comparison, so that the live
        /// file's <c>"termbase_terms"</c> and this file's <c>termbase_terms</c>
        /// are the same statement, and a real difference - a column, a default,
        /// a type - is not.
        /// </summary>
        internal static string Comparable(string sql)
        {
            if (sql == null) return string.Empty;

            var text = new System.Text.StringBuilder(sql.Length);
            var inComment = false;

            for (var i = 0; i < sql.Length; i++)
            {
                var c = sql[i];

                if (inComment)
                {
                    if (c == '\n') inComment = false;
                    continue;
                }

                if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { inComment = true; continue; }

                // Identifier quoting only. A single quote is part of a default
                // value ('unknown', '') and a difference there is a real one.
                if (c == '"') continue;

                text.Append(char.IsWhiteSpace(c) ? ' ' : c);
            }

            var collapsed = System.Text.RegularExpressions.Regex.Replace(text.ToString(), @"\s+", " ").Trim();
            collapsed = collapsed.Replace(" ,", ",").Replace("( ", "(").Replace(" )", ")");
            collapsed = collapsed.ToLowerInvariant();

            // sqlite_master stores "create trigger x", not "create trigger if
            // not exists x" - the clause is stripped on the way in - and stores
            // a trigger up to its END without the terminating semicolon. So a
            // statement written idempotently and the same statement read back
            // differ by exactly those words and that character, and nothing else.
            return collapsed.Replace(" if not exists ", " ").TrimEnd(';').TrimEnd();
        }
    }
}
