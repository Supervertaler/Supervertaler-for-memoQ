using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Which document a caller means. One answer for every tool that takes a
    /// <c>document</c>.
    ///
    /// <para>get_project hands out two spellings of the same document: the live
    /// view's <c>documentGuid</c> (8-4-4-4-12) and the capture's <c>key</c>
    /// (32 hex digits, an underscore, the language pair). Each tool used to
    /// accept its own subset of them, so a key that worked in get_segments was
    /// refused by the QA checks, ignored by compare_staged_to_grid and answered
    /// with nothing by get_confirmed_pairs. Here every spelling is accepted
    /// everywhere, and so is the document's name.</para>
    ///
    /// <para>A key that names nothing is never quietly replaced by another
    /// document or by all of them: the caller gets a problem that says which key
    /// it was, what is accepted, and what is there. Pure functions over the lists
    /// they are given; nothing is kept.</para>
    /// </summary>
    internal static class DocumentKeys
    {
        /// <summary>How many documents a problem lists by name before it says "and n more".</summary>
        private const int MaxListed = 10;

        /// <summary>
        /// The document GUID inside <paramref name="key"/>: a GUID in any of its
        /// usual forms, or 32 hex digits followed by <c>_&lt;language pair&gt;</c>.
        /// Null when it is something else - a name, or a capture bucket with no
        /// document of its own.
        /// </summary>
        public static Guid? GuidOf(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            key = key.Trim();

            if (Guid.TryParse(key, out var whole)) return whole;

            if (key.Length > 33 && key[32] == '_'
                && Guid.TryParseExact(key.Substring(0, 32), "N", out var prefix))
                return prefix;

            return null;
        }

        /// <summary>
        /// The open document <paramref name="key"/> names, from the live view's
        /// list (one part per document, in the caller's order). No key means the
        /// first of the list. Null with a <paramref name="problem"/> otherwise.
        /// </summary>
        public static PreviewStore.Part Live(string key, IList<PreviewStore.Part> open, out string problem)
        {
            problem = null;
            open = open ?? new PreviewStore.Part[0];

            if (string.IsNullOrWhiteSpace(key))
            {
                if (open.Count > 0) return open[0];
                problem = "No document has been seen yet. Open one in memoQ and click into a segment.";
                return null;
            }

            key = key.Trim();

            var guid = GuidOf(key);
            if (guid.HasValue)
            {
                var byGuid = open.FirstOrDefault(d => d.DocumentGuid == guid.Value);
                if (byGuid != null) return byGuid;
            }

            var byName = ByName(key, open, out problem);
            if (byName != null || problem != null) return byName;

            problem = NotFound(key, open, null);
            return null;
        }

        /// <summary>
        /// The captured document <paramref name="key"/> names. No key means the
        /// first of <paramref name="captured"/> (null, and no problem, when
        /// nothing is captured). <paramref name="open"/> is the live view's list,
        /// used to turn a name into a document; it may be empty.
        /// </summary>
        public static CaptureStore.DocumentCapture Captured(
            string key,
            IList<CaptureStore.DocumentCapture> captured,
            IList<PreviewStore.Part> open,
            out string problem)
        {
            problem = null;
            captured = captured ?? new CaptureStore.DocumentCapture[0];
            open = open ?? new PreviewStore.Part[0];

            if (string.IsNullOrWhiteSpace(key)) return captured.FirstOrDefault();

            key = key.Trim();

            // The capture's own key first: it is the only spelling the buckets
            // without a document (visited rows, requests memoQ did not name a
            // document for) have.
            var exact = captured.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var guid = GuidOf(key);
            string openName = null;

            if (!guid.HasValue)
            {
                var live = ByName(key, open, out problem);
                if (problem != null) return null;
                if (live != null) { guid = live.DocumentGuid; openName = live.DocumentName; }
            }

            if (!guid.HasValue)
            {
                // The name get_project shows for a captured document, which
                // comes from memoQ's project folder rather than the live view.
                var named = captured
                    .Where(d => d.DocumentId != Guid.Empty && string.Equals(NameOnDisk(d.DocumentId), key, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (named.Select(d => d.DocumentId).Distinct().Count() > 1)
                {
                    problem = "More than one captured document is named \"" + key + "\". Pass its key from get_project instead: "
                            + string.Join("; ", named.Take(MaxListed).Select(d => d.Key)) + ".";
                    return null;
                }

                if (named.Count > 0) return named[0];
            }

            if (guid.HasValue)
            {
                var byGuid = captured.FirstOrDefault(d => d.DocumentId == guid.Value);
                if (byGuid != null) return byGuid;

                var live = open.FirstOrDefault(d => d.DocumentGuid == guid.Value);
                if (live != null || openName != null)
                {
                    problem = "\"" + (openName ?? live.DocumentName ?? key) + "\" is open in memoQ, but memoQ has not sent the plugin "
                            + "a translation request for it, so nothing is captured for it: no captured segments and no "
                            + "confirmed pairs. That needs Supervertaler as the MT provider (and as Self-learning MT for "
                            + "confirmed pairs) and one Pre-translate or some visited segments.";
                    return null;
                }
            }

            problem = NotFound(key, open, captured);
            return null;
        }

        /// <summary>
        /// The one open document called <paramref name="name"/>, ignoring case.
        /// Null with no problem when none is; null with a problem when several
        /// are, because picking one of two documents with the same name is a
        /// guess, and a check run on the wrong one reads exactly like a check
        /// run on the right one.
        /// </summary>
        private static PreviewStore.Part ByName(string name, IList<PreviewStore.Part> open, out string problem)
        {
            problem = null;
            var named = open.Where(d => string.Equals(d.DocumentName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count <= 1) return named.FirstOrDefault();

            problem = named.Count + " open documents are named \"" + name + "\". Pass the documentGuid from get_project instead: "
                    + string.Join("; ", named.Take(MaxListed).Select(d => d.DocumentGuid.ToString("D"))) + ".";
            return null;
        }

        private static string NameOnDisk(Guid documentId)
        {
            try { return DocumentNames.Resolve(documentId)?.Document; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// What to tell a caller whose key named nothing. <paramref name="captured"/>
        /// is null for a tool that only works on the live view.
        /// </summary>
        private static string NotFound(string key, IList<PreviewStore.Part> open, IList<CaptureStore.DocumentCapture> captured)
        {
            var text = "No document matches \"" + Clip(key) + "\". Accepted: the documentGuid or the key that get_project returns "
                     + "(8-4-4-4-12, or 32 hex digits with or without the language suffix), or the document's name. ";

            text += open.Count == 0
                ? "No document is open in the live view."
                : "Open in memoQ now: " + Listed(open.Select(d => "\"" + (d.DocumentName ?? "?") + "\" (" + d.DocumentGuid.ToString("D") + ")").ToList()) + ".";

            if (captured != null)
            {
                text += captured.Count == 0
                    ? " Nothing has been captured from translation requests."
                    : " Captured keys: " + Listed(captured.Select(d => d.Key).ToList()) + ".";
            }

            return text;
        }

        private static string Listed(IList<string> items)
        {
            var shown = string.Join("; ", items.Take(MaxListed));
            return items.Count > MaxListed ? shown + "; and " + (items.Count - MaxListed) + " more" : shown;
        }

        private static string Clip(string s) => s.Length > 120 ? s.Substring(0, 120) + "…" : s;
    }
}
