using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Supervertaler.Core;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Removes the machine-translation origin from a Trados return package
    /// (.sdlrpx) that memoQ created, or from a single .sdlxliff, so that Trados
    /// no longer shows the client AT on the rows.
    ///
    /// <para><b>What Trados reads.</b> Each segment's <c>&lt;sdl:seg&gt;</c> in
    /// the .sdlxliff carries its origin. memoQ writes <c>origin="mt"</c> for a
    /// row filled by machine translation, which Trados shows as AT, and for a
    /// row edited afterwards <c>origin="interactive"</c> with an
    /// <c>&lt;sdl:prev-origin origin="mt" origin-system="MT / Supervertaler"&gt;</c>
    /// inside, which Trados shows as a faded AT (measured 2026-11-23, memoQ
    /// 12.6 and Trados Studio).</para>
    ///
    /// <para><b>What memoQ itself does when the marks are gone.</b> Cleaning
    /// the documents in memoQ (Remove MT marks, remove the package, import the
    /// clean copies) and exporting the return package again changes exactly
    /// two things: the origin becomes <c>interactive</c>, and the MT
    /// prev-origin goes. This class makes those same edits directly, and on the
    /// test package its .sdlxliff files came out byte for byte identical to
    /// memoQ's, and Trados accepted the package with no AT on any row.</para>
    ///
    /// <para><b>Edited as text</b>, like <see cref="MtMarks"/>: only the
    /// segment definitions change, byte for byte. The package's .sdlproj
    /// records each file's size, which is updated to match.</para>
    /// </summary>
    internal static class TradosMtMarks
    {
        /// <summary>The origins Trados and memoQ write for machine translation: classic, neural and adaptive MT.</summary>
        private static readonly HashSet<string> MtOrigins = new HashSet<string>(StringComparer.Ordinal) { "mt", "nmt", "amt" };

        /// <summary>The label for an MT record that does not name its engine, which is how memoQ writes the current origin.</summary>
        internal const string UnnamedEngine = "machine translation";

        // A segment definition with content (its earlier origins). The head
        // must not be self-closing, and the body must not run into the next
        // segment: without both guards a self-closing seg swallows everything up
        // to some later </sdl:seg> (caught by the prototype on a real package).
        private static readonly Regex SegElement =
            new Regex(@"<sdl:seg(\s[^>]*?[^/])>((?:(?!<sdl:seg\s).)*?)</sdl:seg>", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex SegSelfClosing =
            new Regex(@"<sdl:seg(\s[^>]*?)\s*/>", RegexOptions.Compiled);

        // A prev-origin with no prev-origin inside it. Earlier origins nest, one
        // inside the other, so the chain is undone from the innermost outwards.
        private static readonly Regex InnermostPrevOrigin =
            new Regex(@"<sdl:prev-origin(\s[^>]*?)?(?:\s*/>|>((?:(?!<sdl:prev-origin[\s>/]).)*?)</sdl:prev-origin>)",
                RegexOptions.Singleline | RegexOptions.Compiled);

        // Stands in for an earlier origin that stays while the chain is walked.
        // Control characters, which XML cannot contain.
        private const char HeldMark = '\u0002';
        private static readonly Regex Held = new Regex("\u0002(\\d+)\u0002", RegexOptions.Compiled);

        // Strict, so a file that is not valid UTF-8 is refused rather than
        // written back with replacement characters where its text was.
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static bool IsReturnPackage(string path) =>
            string.Equals(Path.GetExtension(path), ".sdlrpx", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Cleans <paramref name="path"/>, a .sdlrpx or a .sdlxliff, into a copy
        /// of the same name in a Clean folder beside it (<see cref="MtMarks.CleanPath"/>),
        /// and never changes the original. Writes nothing when the file holds no
        /// MT marks. Throws <see cref="InvalidDataException"/> for a file that is
        /// not one of those, and <see cref="IOException"/> when the copy cannot
        /// be written.
        /// </summary>
        internal static MtMarks.Result Clean(string path)
        {
            var bytes = AtomicFile.ReadAllBytes(path);
            var result = new MtMarks.Result { Packaged = IsReturnPackage(path) };

            if (result.Packaged && !IsZip(bytes))
                throw new InvalidDataException(Path.GetFileName(path) + " is not a Trados return package: it is not a zip file.");

            var output = result.Packaged
                ? CleanPackage(bytes, result)
                : CleanDocument(bytes, result, Path.GetFileName(path));

            if (result.Removed == 0)
                return result;

            var target = MtMarks.CleanPath(path);
            string why = null;
            if (AtomicFile.Write(target, output, replaceExisting: true, log: m => why = m) != AtomicFile.Outcome.Written)
                throw new IOException(why ?? Path.GetFileName(target) + " could not be written.");

            result.WrittenTo = target;
            return result;
        }

        /// <summary>The .sdlxliff text with its MT origins removed, counted into <paramref name="result"/>.</summary>
        internal static string CleanText(string text, MtMarks.Result result)
        {
            // Segments with earlier origins first, then the self-closing ones.
            // The second pass cannot meet a segment the first one rewrote as
            // self-closing, because that one no longer has an MT origin.
            text = SegElement.Replace(text, m => CleanSegElement(m, result));
            return SegSelfClosing.Replace(text, m =>
            {
                var attributes = CleanSegAttributes(m.Groups[1].Value, result);
                return attributes == null ? m.Value : "<sdl:seg" + attributes + " />";
            });
        }

        private static string CleanSegElement(Match m, MtMarks.Result result)
        {
            var removedBefore = result.Removed;
            var body = CleanPrevOrigins(m.Groups[2].Value, result);
            var attributes = CleanSegAttributes(m.Groups[1].Value, result) ?? m.Groups[1].Value;
            if (result.Removed == removedBefore)
                return m.Value;

            // A segment left with nothing inside is written as Trados and memoQ
            // write one that never had an earlier origin.
            return body.Trim().Length == 0
                ? "<sdl:seg" + attributes + " />"
                : "<sdl:seg" + attributes + ">" + body + "</sdl:seg>";
        }

        /// <summary>
        /// A segment's attributes with an MT origin made <c>interactive</c>, and
        /// the engine and percentage that belong to the MT origin dropped; null
        /// when the origin is not MT.
        /// </summary>
        private static string CleanSegAttributes(string attributes, MtMarks.Result result)
        {
            var origin = Attribute(attributes, "origin");
            if (origin == null || !MtOrigins.Contains(origin))
                return null;

            Count(result, Attribute(attributes, "origin-system"));
            attributes = Regex.Replace(attributes, @"(\sorigin="")[^""]*("")", "${1}interactive${2}");
            attributes = Regex.Replace(attributes, @"\sorigin-system=""[^""]*""", "");
            return Regex.Replace(attributes, @"\spercent=""[^""]*""", "");
        }

        /// <summary>
        /// The body of a segment definition with every MT earlier origin taken
        /// out. An earlier origin nested inside an MT one (a TM match the MT
        /// replaced, say) stays, moved up a level, so the chain keeps everything
        /// that was not machine translation.
        /// </summary>
        private static string CleanPrevOrigins(string body, MtMarks.Result result)
        {
            if (body.IndexOf("<sdl:prev-origin", StringComparison.Ordinal) < 0)
                return body;

            var kept = new List<string>();
            while (true)
            {
                var changed = false;
                body = InnermostPrevOrigin.Replace(body, m =>
                {
                    changed = true;
                    var attributes = m.Groups[1].Value;
                    var origin = Attribute(attributes, "origin");
                    if (origin != null && MtOrigins.Contains(origin))
                    {
                        Count(result, Attribute(attributes, "origin-system"));
                        return m.Groups[2].Value;
                    }
                    kept.Add(m.Value);
                    return HeldMark + (kept.Count - 1).ToString() + HeldMark;
                });
                if (!changed)
                    break;
            }

            // Put back what stayed, innermost last: each one may hold another.
            while (body.IndexOf(HeldMark) >= 0)
                body = Held.Replace(body, m => kept[int.Parse(m.Groups[1].Value)]);
            return body;
        }

        private static void Count(MtMarks.Result result, string engine)
        {
            var key = string.IsNullOrEmpty(engine) ? UnnamedEngine : engine;
            result.Engines.TryGetValue(key, out var count);
            result.Engines[key] = count + 1;
            result.Removed++;
        }

        private static string Attribute(string attributes, string name)
        {
            var m = Regex.Match(attributes, @"(?<![\w:-])" + Regex.Escape(name) + "=\"([^\"]*)\"");
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
        }

        /// <summary>One .sdlxliff, as bytes. Returns the input itself when there was nothing to remove.</summary>
        private static byte[] CleanDocument(byte[] bytes, MtMarks.Result result, string name)
        {
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var start = bom ? 3 : 0;

            string text;
            try
            {
                text = Utf8.GetString(bytes, start, bytes.Length - start);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException(name + " is not a Trados .sdlxliff file: it is not valid UTF-8 text.");
            }

            if (text.IndexOf("http://sdl.com/FileTypes/SdlXliff/1.0", StringComparison.Ordinal) < 0)
                throw new InvalidDataException(name + " is not a Trados .sdlxliff file.");

            var removedBefore = result.Removed;
            var cleaned = CleanText(text, result);
            if (result.Removed == removedBefore)
                return bytes;

            var body = Utf8.GetBytes(cleaned);
            if (!bom)
                return body;

            var withBom = new byte[body.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
            return withBom;
        }

        /// <summary>
        /// A .sdlrpx: every .sdlxliff cleaned, the .sdlproj told their new
        /// sizes, every other entry copied as it was.
        /// </summary>
        private static byte[] CleanPackage(byte[] bytes, MtMarks.Result result)
        {
            // Only the .sdlxliff files are held in memory as text; every other
            // entry (a return package can carry large ones) is copied across as
            // a stream in the second pass.
            var documents = new List<(string Name, byte[] Data)>();
            string projectEntry = null;
            try
            {
                using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith(".sdlproj", StringComparison.OrdinalIgnoreCase))
                            projectEntry = entry.FullName;
                        if (IsDocument(entry.FullName))
                            documents.Add((entry.FullName, ReadEntry(entry)));
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException("This return package is damaged and cannot be read: " + ex.Message, ex);
            }

            if (projectEntry == null)
                throw new InvalidDataException("This is not a Trados return package: it holds no .sdlproj.");
            if (documents.Count == 0)
                throw new InvalidDataException("This return package holds no .sdlxliff files.");

            var cleaned = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var document in documents)
            {
                var removedBefore = result.Removed;
                var data = CleanDocument(document.Data, result, document.Name);
                if (result.Removed != removedBefore)
                    cleaned[document.Name] = data;
            }
            if (cleaned.Count == 0)
                return bytes;

            // Every entry is written afresh, as for an .mqxlz: a zip entry that
            // kept its old header after its content changed is what memoQ's own
            // reader refused on 2026-10-23.
            var output = new MemoryStream();
            using (var source = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var entry in source.Entries)
                {
                    var copy = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    copy.LastWriteTime = entry.LastWriteTime;
                    using (var target = copy.Open())
                    {
                        if (cleaned.TryGetValue(entry.FullName, out var data))
                            target.Write(data, 0, data.Length);
                        else if (entry.FullName == projectEntry)
                        {
                            var project = UpdateSizes(ReadEntry(entry), cleaned);
                            target.Write(project, 0, project.Length);
                        }
                        else
                            using (var stream = entry.Open())
                                stream.CopyTo(target);
                    }
                }
            }
            return output.ToArray();
        }

        /// <summary>
        /// The .sdlproj with each cleaned file's recorded size made its new size.
        /// Files are matched by their path in the package, not their name: the
        /// same document appears once per target language.
        /// </summary>
        internal static byte[] UpdateSizes(byte[] project, IDictionary<string, byte[]> cleaned)
        {
            var bom = project.Length >= 3 && project[0] == 0xEF && project[1] == 0xBB && project[2] == 0xBF;
            var text = Utf8.GetString(project, bom ? 3 : 0, project.Length - (bom ? 3 : 0));
            var changed = false;
            text = Regex.Replace(text, @"<FileVersion\s[^>]*>", m =>
            {
                var physical = Attribute(m.Value, "PhysicalPath");
                if (physical == null || !cleaned.TryGetValue(physical.Replace('\\', '/'), out var data))
                    return m.Value;
                changed = true;
                return Regex.Replace(m.Value, @"(\sSize="")\d+("")", "${1}" + data.Length + "${2}");
            });
            if (!changed)
                return project;
            var body = Utf8.GetBytes(text);
            if (!bom)
                return body;
            var withBom = new byte[body.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
            return withBom;
        }

        private static byte[] ReadEntry(ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static bool IsDocument(string entryName) =>
            entryName.EndsWith(".sdlxliff", StringComparison.OrdinalIgnoreCase);

        private static bool IsZip(byte[] bytes) =>
            bytes.Length >= 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K' && bytes[2] == 3 && bytes[3] == 4;
    }
}
