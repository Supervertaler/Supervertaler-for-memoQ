using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// A glossary file, read so it can be written back without losing anything
    /// the editor does not show.
    ///
    /// <para>The format is the one <c>TermIndex</c> reads: tab-separated, one entry
    /// per line, an optional <c>#!</c> header of <c>key=value</c> pairs at the top,
    /// and <c>#</c> comments anywhere.</para>
    ///
    /// <code>
    /// #! source=dut target=eng
    /// # exported from Acme (PROJ-001) v3
    /// elektrische module    electric module
    /// elektrische module    electrical module    forbidden
    /// </code>
    ///
    /// <para>Lines are kept in order as a list of items, each either a comment or
    /// an entry, and written back in that order. A grid that showed only the terms
    /// and then saved only the terms would silently delete the header that says
    /// which direction the glossary runs in, and the line saying which prompt it
    /// came from - both of which someone put there on purpose.</para>
    /// </summary>
    internal sealed class GlossaryDocument
    {
        internal sealed class Item
        {
            /// <summary>The line as it was, for anything that is not an entry.</summary>
            public string Comment;

            public string Source;
            public string Target;
            public bool Forbidden;

            public bool IsEntry => Comment == null;
        }

        private readonly List<Item> _items = new List<Item>();

        public string Path { get; private set; }

        /// <summary>The entries, in file order. Comments keep their places around them.</summary>
        public IEnumerable<Item> Entries => _items.Where(i => i.IsEntry);

        public int Count => _items.Count(i => i.IsEntry);

        /// <summary>The <c>#!</c> header's key=value pairs, or an empty map.</summary>
        public Dictionary<string, string> Header { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static GlossaryDocument Load(string path)
        {
            var doc = new GlossaryDocument { Path = path };
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return doc;

            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw ?? string.Empty;
                var trimmed = line.Trim();

                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    doc._items.Add(new Item { Comment = line });

                    if (trimmed.StartsWith("#!", StringComparison.Ordinal))
                        foreach (var part in trimmed.Substring(2)
                                     .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var eq = part.IndexOf('=');
                            if (eq > 0) doc.Header[part.Substring(0, eq)] = part.Substring(eq + 1);
                        }

                    continue;
                }

                var parts = trimmed.Split('\t');
                var source = parts[0].Trim();
                var target = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                var flag = parts.Length > 2 ? parts[2].Trim() : string.Empty;

                // A line with no target is not an entry anyone can use, but it is
                // also not ours to throw away - it is kept as written.
                if (source.Length == 0)
                {
                    doc._items.Add(new Item { Comment = line });
                    continue;
                }

                doc._items.Add(new Item
                {
                    Source = source,
                    Target = target,
                    Forbidden = flag.StartsWith("!", StringComparison.Ordinal)
                                || flag.Equals("forbidden", StringComparison.OrdinalIgnoreCase)
                });
            }

            return doc;
        }

        /// <summary>
        /// Replaces the entries with <paramref name="rows"/>, in the order given,
        /// and keeps every comment where it was relative to the entries around it.
        /// Rows beyond the original count are appended; entries the caller dropped
        /// are removed along with nothing else.
        /// </summary>
        public void SetEntries(IReadOnlyList<Item> rows)
        {
            var replaced = new List<Item>();
            var next = 0;

            foreach (var item in _items)
            {
                if (!item.IsEntry) { replaced.Add(item); continue; }
                if (next < rows.Count) replaced.Add(rows[next++]);
                // else: this entry was deleted, so it is simply not carried over.
            }

            for (; next < rows.Count; next++) replaced.Add(rows[next]);

            _items.Clear();
            _items.AddRange(replaced);
        }

        public string Render()
        {
            var sb = new StringBuilder();

            foreach (var item in _items)
            {
                if (!item.IsEntry) { sb.Append(item.Comment).Append("\r\n"); continue; }

                sb.Append(item.Source).Append('\t').Append(item.Target);
                if (item.Forbidden) sb.Append('\t').Append("forbidden");
                sb.Append("\r\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Writes the file. UTF-8 without a byte order mark, which is what
        /// <c>TermIndex</c> and the export path both produce - a BOM would show up
        /// as three characters at the front of the first source term.
        /// </summary>
        public void Save()
        {
            if (string.IsNullOrWhiteSpace(Path)) throw new InvalidOperationException("No path.");

            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(Path, Render(), new UTF8Encoding(false));
        }
    }
}
