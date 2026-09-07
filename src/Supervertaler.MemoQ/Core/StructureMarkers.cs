using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Supervertaler.Core;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Word's list markers – <c>a)</c>, <c>b)</c>, <c>1.</c> – for the segments
    /// memoQ hands us (memoQ #7, Trados #109).
    ///
    /// <para>The letters on the steps of a method claim are not text. Word stores
    /// them as a paragraph property pointing into <c>numbering.xml</c>, so the
    /// segment grid does not contain them and neither does anything the Preview
    /// SDK sends: measured, a lettered step arrives as its bare sentence. The model,
    /// shown six unlabelled sentences and then "steps a. to f.", flagged the
    /// reference as a possible source defect. That false positive recurs on every
    /// lettered list in every document of this kind.</para>
    ///
    /// <para>The Preview SDK does give the original document's path. Core's
    /// <see cref="DocxStructure"/> reads the markers out of it, counted over the
    /// whole document; this class maps them onto memoQ's paragraphs, which are the
    /// preview parts, and onto the first segment of each. The marker goes into the
    /// prompt as a <c>[#e)]</c> sentinel – context, never text – and is stripped
    /// from every reply before it reaches the document.</para>
    ///
    /// <para>Trados joins by character offset, because Studio publishes each
    /// paragraph's offset into <c>document.xml</c>. memoQ has only the text, so it
    /// matches on text with order as the tiebreak – which is what
    /// <see cref="Match"/> does, and the only part of this file with a right
    /// answer.</para>
    /// </summary>
    internal static class StructureMarkers
    {
        // -- one read per document ------------------------------------------

        private sealed class Cached
        {
            public DateTime WriteTimeUtc;
            public long Length;
            public List<DocxParagraph> Paragraphs;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Cached> _byPath =
            new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The document's paragraphs with their markers, read once per path and
        /// re-read when the file changes. Null when there is nothing to read: no
        /// path, no file, not a .docx, or a package the reader does not recognise –
        /// none of which is an error, since a document with no lists is the common
        /// case.
        /// </summary>
        internal static List<DocxParagraph> ParagraphsOf(string importPath)
        {
            if (string.IsNullOrWhiteSpace(importPath)) return null;
            if (!importPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) return null;

            try
            {
                if (!File.Exists(importPath)) return null;
                var info = new FileInfo(importPath);

                lock (_lock)
                {
                    if (_byPath.TryGetValue(importPath, out var hit)
                        && hit.WriteTimeUtc == info.LastWriteTimeUtc
                        && hit.Length == info.Length)
                        return hit.Paragraphs;
                }

                List<DocxParagraph> paragraphs;
                using (var stream = File.OpenRead(importPath))
                    paragraphs = DocxStructure.ReadParagraphs(stream);

                lock (_lock)
                {
                    _byPath[importPath] = new Cached
                    {
                        WriteTimeUtc = info.LastWriteTimeUtc,
                        Length = info.Length,
                        Paragraphs = paragraphs
                    };
                }

                return paragraphs;
            }
            catch (Exception ex)
            {
                PluginLog.Write("structure: could not read list numbering from " + importPath, ex);
                return null;
            }
        }

        // -- what a request gets ----------------------------------------------

        /// <summary>
        /// Everything one batch or one segment needs, gathered once: the mode to
        /// tell the prompt, and a marker per preview paragraph. Cheap when the
        /// setting is off – one property read and nothing else.
        /// </summary>
        internal sealed class Plan
        {
            public StructureContextMode Mode = StructureContextMode.Off;

            /// <summary>Plain paragraph text (tags stripped, whitespace collapsed) → marker.</summary>
            public List<KeyValuePair<string, string>> Paragraphs = new List<KeyValuePair<string, string>>();

            /// <summary>Why the mode is what it is, for the log.</summary>
            public string Reason = "";

            /// <summary>
            /// The marker for a segment, or null. Only the first segment of a
            /// paragraph gets it: a marker on every sentence of a lettered step
            /// would tell the model the step had several letters.
            /// </summary>
            public string MarkerFor(string segmentPlainText)
            {
                if (Mode != StructureContextMode.Markers) return null;

                var seg = Normalise(segmentPlainText);
                if (seg.Length == 0) return null;

                foreach (var p in Paragraphs)
                {
                    if (p.Value == null) continue;
                    if (p.Key.StartsWith(seg, StringComparison.Ordinal)) return p.Value;
                }

                return null;
            }
        }

        /// <summary>
        /// Decides the mode and, when it is <see cref="StructureContextMode.Markers"/>,
        /// maps every preview paragraph of the current document to its marker.
        ///
        /// <para><c>Unavailable</c> is a real answer, not a failure: the setting is
        /// on but there is nothing to give – the preview tool has not connected,
        /// so there is no path; or the document is not a .docx; or it has no
        /// lists. The prompt then carries the fallback rule so the model does not
        /// go looking for markers that will never come.</para>
        /// </summary>
        public static Plan PlanFor(EngineContext context)
        {
            var plan = new Plan();
            if (context == null || !SharedSettings.StructureContext) { plan.Reason = "off"; return plan; }

            var document = context.LastMetadata?.DocumentID ?? Guid.Empty;
            var rows = document == Guid.Empty ? null : PreviewStore.Rows(document);

            if (rows == null || rows.Count == 0)
            {
                plan.Mode = StructureContextMode.Unavailable;
                plan.Reason = "the preview tool has not reported this document, so its file is not known";
                return plan;
            }

            var path = rows.Select(r => r.ImportPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            var paragraphs = ParagraphsOf(path);

            if (paragraphs == null)
            {
                plan.Mode = StructureContextMode.Unavailable;
                plan.Reason = string.IsNullOrWhiteSpace(path)
                    ? "the preview tool gave no file path"
                    : "not a readable .docx: " + path;
                return plan;
            }

            if (!DocxStructure.HasMarkers(paragraphs))
            {
                plan.Mode = StructureContextMode.Unavailable;
                plan.Reason = "the document has no numbered or lettered lists";
                return plan;
            }

            var parts = rows.Select(r => Normalise(TagBridge.StripTagMarkers(r.Source ?? ""))).ToList();
            var markers = Match(paragraphs, parts);

            for (var i = 0; i < parts.Count; i++)
                plan.Paragraphs.Add(new KeyValuePair<string, string>(parts[i], markers[i]));

            plan.Mode = StructureContextMode.Markers;
            plan.Reason = markers.Count(m => m != null) + " of " + parts.Count + " paragraphs carry a marker";
            return plan;
        }

        // -- one log line per answer, not per request ---------------------------

        private static string _lastLogged;

        /// <summary>
        /// Says what the plan decided, once, and again only when the answer
        /// changes - a new document, the preview tool connecting, the file
        /// appearing. Forty batches with the same answer produce one line.
        /// </summary>
        public static void LogOnce(Plan plan)
        {
            if (plan == null) return;

            var line = plan.Mode + " | " + plan.Reason;
            lock (_lock)
            {
                if (string.Equals(line, _lastLogged, StringComparison.Ordinal)) return;
                _lastLogged = line;
            }

            switch (plan.Mode)
            {
                case StructureContextMode.Markers:
                    PluginLog.Write("structure: list markers go to the model as [#…] context – " + plan.Reason);
                    break;
                case StructureContextMode.Unavailable:
                    PluginLog.Write("structure: no list markers to send – " + plan.Reason
                        + ". The model is told numbering is supplied by the document, so it does not flag its absence.");
                    break;
                default:
                    PluginLog.Write("structure: off (structurecontext=0 in shared.txt)");
                    break;
            }
        }

        // -- the matching -------------------------------------------------------

        /// <summary>
        /// One marker (or null) per preview part, matched against the document's
        /// paragraphs by text, in order.
        ///
        /// <para>A cursor walks the document's paragraphs. For each part, the next
        /// paragraph whose text equals the part's – or begins with it, since a
        /// paragraph memoQ split across parts at a page break has a prefix here –
        /// is its match, and the cursor moves past it. A part with no match ahead
        /// of the cursor leaves the cursor where it is: memoQ produces parts the
        /// document has no paragraph for (image alt-text) and drops paragraphs the
        /// document has (empty ones), and a wrong step in either direction would
        /// shift every marker after it by one. Empty document paragraphs are
        /// skipped for matching and still count for numbering, which the reader
        /// has already done.</para>
        ///
        /// <para>Returns bullet markers as null: a bullet is structure the model
        /// does not need to be told about, and "[#•]" would be noise.</para>
        /// </summary>
        internal static List<string> Match(IList<DocxParagraph> paragraphs, IList<string> parts)
        {
            var result = new List<string>(parts.Count);
            var cursor = 0;

            foreach (var raw in parts)
            {
                var part = Normalise(raw);
                string marker = null;

                if (part.Length > 0)
                {
                    for (var i = cursor; i < paragraphs.Count; i++)
                    {
                        var text = Normalise(paragraphs[i].Text);
                        if (text.Length == 0) continue;

                        if (text.Equals(part, StringComparison.Ordinal)
                            || text.StartsWith(part, StringComparison.Ordinal))
                        {
                            var p = paragraphs[i];
                            marker = p.IsBullet ? null : p.Marker;
                            cursor = i + 1;
                            break;
                        }
                    }
                }

                result.Add(marker);
            }

            return result;
        }

        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>Tabs, breaks and runs of spaces collapse to one space; ends trimmed.</summary>
        internal static string Normalise(string text) =>
            text == null ? "" : Spaces.Replace(text, " ").Trim();
    }
}
