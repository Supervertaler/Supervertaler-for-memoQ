using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Termbases as files: what Import reads and Export writes.
    ///
    /// <para><b>Two shapes are read, one of which is our own.</b> The glossary
    /// format this product has produced since Export glossary existed - a
    /// <c># name</c> line, a <c>#! source=… target=…</c> line, then
    /// <c>source⇥target[⇥forbidden]</c> rows - is read as it is, which is how the
    /// text glossaries in <c>memoq\glossaries</c> become termbases. And the shape
    /// everything else produces: a delimited file with a header row, from memoQ,
    /// Excel or Supervertaler for Trados, whose language columns are recognised
    /// by name or code and whose delimiter is whichever of tab, semicolon or comma
    /// the header actually uses.</para>
    ///
    /// <para><b>Two shapes are written, and the caller picks.</b> The glossary
    /// format again, because it round-trips into this product's own reader and
    /// into the prompt editor without a header row being mistaken for a term.
    /// And a header-row TSV, because that is what a spreadsheet or the Trados
    /// importer expects - the two products disagree on whether a line starting
    /// with <c>#</c> is a comment, so one file cannot serve both.</para>
    /// </summary>
    internal static class TermbaseFiles
    {
        /// <summary>One term pair as it travels through a file.</summary>
        internal sealed class Row
        {
            public string Source { get; set; }
            public string Target { get; set; }
            public bool Forbidden { get; set; }
            public string Notes { get; set; }
        }

        /// <summary>What a file declared about itself, and what it held.</summary>
        internal sealed class Contents
        {
            /// <summary>From the <c># name</c> line, or the file name without extension.</summary>
            public string Name { get; set; }

            /// <summary>As declared in the file - "dut-NL", "Dutch", "nl" - or empty when it did not say.</summary>
            public string SourceLang { get; set; }
            public string TargetLang { get; set; }

            public List<Row> Rows { get; } = new List<Row>();

            /// <summary>Lines that had something in them and were not understood.</summary>
            public int Unreadable { get; set; }
        }

        internal enum Shape
        {
            /// <summary>Our own: comment lines, <c>#!</c> declaration, no header row.</summary>
            Glossary,

            /// <summary>Header row naming the columns, tab-separated, no comment lines.</summary>
            HeaderRowTsv
        }

        // ---- reading -----------------------------------------------------------

        internal static Contents Read(string path)
        {
            var contents = new Contents { Name = Path.GetFileNameWithoutExtension(path) };
            var lines = File.ReadAllLines(path, Encoding.UTF8);

            char delimiter = '\t';
            var delimiterKnown = false;
            var headerSeen = false;
            var nameFromComment = false;
            int sourceCol = 0, targetCol = 1, forbiddenCol = -1, notesCol = -1;

            foreach (var raw in lines)
            {
                var line = (raw ?? string.Empty).TrimEnd('\r');
                if (line.Trim().Length == 0) continue;

                if (line.TrimStart().StartsWith("#"))
                {
                    ReadComment(line.Trim(), contents, ref nameFromComment);
                    continue;
                }

                if (!delimiterKnown)
                {
                    delimiter = Sniff(line);
                    delimiterKnown = true;
                }

                var cells = Split(line, delimiter);

                // A header row is a first data line whose cells are column names
                // rather than terms. The tell is a language on each of the first
                // two: no term pair is "Dutch" and "English".
                if (!headerSeen)
                {
                    headerSeen = true;
                    if (LooksLikeHeader(cells))
                    {
                        MapHeader(cells, contents, out sourceCol, out targetCol, out forbiddenCol, out notesCol);
                        continue;
                    }
                }

                if (cells.Length <= Math.Max(sourceCol, targetCol)) { contents.Unreadable++; continue; }

                var source = cells[sourceCol].Trim();
                var target = cells[targetCol].Trim();
                if (source.Length == 0 || target.Length == 0) { contents.Unreadable++; continue; }

                var row = new Row { Source = source, Target = target };

                // Forbidden: an explicit column when the header named one, else
                // the glossary convention of a third cell saying so.
                var flagCol = forbiddenCol >= 0 ? forbiddenCol : 2;
                if (flagCol < cells.Length) row.Forbidden = IsForbiddenFlag(cells[flagCol]);

                if (notesCol >= 0 && notesCol < cells.Length) row.Notes = cells[notesCol].Trim();
                else if (forbiddenCol < 0 && cells.Length > 3) row.Notes = cells[3].Trim();

                contents.Rows.Add(row);
            }

            return contents;
        }

        private static void ReadComment(string line, Contents contents, ref bool nameFromComment)
        {
            if (line.StartsWith("#!"))
            {
                foreach (var part in line.Substring(2).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = part.Substring(0, eq).Trim();
                    var value = part.Substring(eq + 1).Trim();
                    if (value.Length == 0) continue;
                    if (key.Equals("source", StringComparison.OrdinalIgnoreCase)) contents.SourceLang = value;
                    else if (key.Equals("target", StringComparison.OrdinalIgnoreCase)) contents.TargetLang = value;
                }
                return;
            }

            // The first plain comment is the name, by the convention Export
            // glossary established. Later ones are prose.
            if (!nameFromComment)
            {
                var text = line.TrimStart('#').Trim();
                if (text.Length > 0 && text.Length < 200)
                {
                    contents.Name = text;
                    nameFromComment = true;
                }
            }
        }

        private static char Sniff(string line)
        {
            if (line.IndexOf('\t') >= 0) return '\t';
            if (line.IndexOf(';') >= 0) return ';';
            return ',';
        }

        /// <summary>
        /// Split one line. Tab never needs quoting; for comma and semicolon a
        /// cell may be double-quoted, with a doubled quote for a literal one -
        /// which is what Excel writes and all this needs to read.
        /// </summary>
        private static string[] Split(string line, char delimiter)
        {
            if (delimiter == '\t') return line.Split('\t');

            var cells = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == delimiter) { cells.Add(cell.ToString()); cell.Clear(); }
                else cell.Append(c);
            }

            cells.Add(cell.ToString());
            return cells.ToArray();
        }

        private static bool LooksLikeHeader(string[] cells)
        {
            if (cells.Length < 2) return false;

            var a = cells[0].Trim();
            var b = cells[1].Trim();
            if (a.Length == 0 || b.Length == 0) return false;

            if (IsLanguageHeading(a) && IsLanguageHeading(b)) return true;

            // Or the plain words a person types into a header row.
            return IsWord(a, "source", "source term", "src", "term") && IsWord(b, "target", "target term", "tgt", "translation");
        }

        private static bool IsLanguageHeading(string cell)
        {
            // "Dutch", "nl", "dut-NL", "Dutch (nl)" - a name or code, possibly
            // with a code in brackets after it.
            var text = cell.Trim();
            var bracket = text.IndexOf('(');
            if (bracket > 0) text = text.Substring(0, bracket).Trim();
            return global::Supervertaler.Core.LanguageCodes.IsKnown(text);
        }

        private static bool IsWord(string cell, params string[] words)
        {
            foreach (var w in words)
                if (cell.Trim().Equals(w, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void MapHeader(string[] cells, Contents contents,
                                      out int sourceCol, out int targetCol, out int forbiddenCol, out int notesCol)
        {
            sourceCol = 0; targetCol = 1; forbiddenCol = -1; notesCol = -1;
            var languages = new List<int>();

            for (var i = 0; i < cells.Length; i++)
            {
                var h = cells[i].Trim();
                if (IsLanguageHeading(h)) { languages.Add(i); continue; }
                if (IsWord(h, "forbidden", "status", "flag", "forbidden term")) forbiddenCol = i;
                else if (IsWord(h, "notes", "note", "definition", "comment", "comments", "context")) notesCol = i;
            }

            if (languages.Count >= 2)
            {
                sourceCol = languages[0];
                targetCol = languages[1];
                if (string.IsNullOrEmpty(contents.SourceLang)) contents.SourceLang = LanguageOf(cells[sourceCol]);
                if (string.IsNullOrEmpty(contents.TargetLang)) contents.TargetLang = LanguageOf(cells[targetCol]);
            }
        }

        /// <summary>"Dutch (nl)" gives "nl"; "Dutch" gives "Dutch"; both are understood downstream.</summary>
        private static string LanguageOf(string heading)
        {
            var text = heading.Trim();
            var open = text.IndexOf('(');
            var close = text.IndexOf(')');
            if (open >= 0 && close > open) return text.Substring(open + 1, close - open - 1).Trim();
            return text;
        }

        private static bool IsForbiddenFlag(string cell)
        {
            var flag = (cell ?? string.Empty).Trim();
            if (flag.Length == 0) return false;
            if (flag.StartsWith("!", StringComparison.Ordinal)) return true;
            if (flag.IndexOf("forbidden", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return flag.Equals("true", StringComparison.OrdinalIgnoreCase)
                || flag.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || flag == "1";
        }

        // ---- writing -----------------------------------------------------------

        internal static void Write(string path, Shape shape, string name,
                                   string sourceLang, string targetLang, IEnumerable<Row> rows)
        {
            var sb = new StringBuilder();

            if (shape == Shape.Glossary)
            {
                sb.Append("# ").AppendLine(Clean(name));
                sb.Append("#! source=").Append(Clean(sourceLang)).Append(" target=").AppendLine(Clean(targetLang));
                sb.AppendLine("# Exported from a Supervertaler termbase. Tab-separated: source, target, optional 'forbidden', optional notes.");
            }
            else
            {
                sb.Append(Heading(sourceLang)).Append('\t').Append(Heading(targetLang))
                  .Append('\t').Append("Forbidden").Append('\t').AppendLine("Notes");
            }

            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Source)) continue;

                sb.Append(Clean(row.Source)).Append('\t').Append(Clean(row.Target));

                var notes = Clean(row.Notes);
                if (row.Forbidden || notes.Length > 0)
                {
                    sb.Append('\t').Append(row.Forbidden ? "forbidden" : string.Empty);
                    if (notes.Length > 0) sb.Append('\t').Append(notes);
                }

                sb.AppendLine();
            }

            // Written beside and moved into place, as the selection store is: a
            // half-written export is a puzzle, an absent one is a retry.
            var temp = path + ".tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>"Dutch (nl-NL)" - the name a person reads, and the code a program does.</summary>
        private static string Heading(string lang)
        {
            var code = Clean(lang);
            if (code.Length == 0) return "Source";
            var english = global::Supervertaler.Core.LanguageCodes.EnglishName(code);
            return english.Equals(code, StringComparison.OrdinalIgnoreCase) ? code : english + " (" + code + ")";
        }

        /// <summary>A cell can hold no tab and no line break, whatever a term contained.</summary>
        private static string Clean(string text) =>
            (text ?? string.Empty).Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
