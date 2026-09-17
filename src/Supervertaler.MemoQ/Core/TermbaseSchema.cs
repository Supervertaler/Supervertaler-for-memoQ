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
    /// needs them; this product never needs them. Likewise no
    /// <c>translation_units</c>: <c>termbase_terms</c> carries a foreign key to
    /// it, and the Workbench creates that table itself. The key is inert only
    /// because this product's writer turns foreign-key enforcement off - SQLite
    /// leaves it off, but Microsoft.Data.Sqlite turns it on, and with it on the
    /// first term inserted into a database without that table fails. Any other
    /// writer using the same provider against a file this product made will meet
    /// the same wall, which is written down in issue #133 in the Trados repo.</para>
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
                notes TEXT, project TEXT, client TEXT, term_uuid TEXT, note TEXT, is_nontranslatable BOOLEAN DEFAULT 0, source_abbreviation TEXT DEFAULT '', target_abbreviation TEXT DEFAULT '', url TEXT DEFAULT '',

                FOREIGN KEY (tm_source_id) REFERENCES translation_units(id) ON DELETE SET NULL
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
            }
        };

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

            foreach (var item in Objects)
            {
                if (present.Contains(item.Name)) continue;

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = item.Sql;
                    command.ExecuteNonQuery();
                }
            }
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
            return collapsed.ToLowerInvariant();
        }
    }
}
