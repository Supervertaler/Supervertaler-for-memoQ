using System;
using System.Collections.Generic;
using System.Linq;
using MemoQ.MTInterfaces;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Everything the plugin has seen of each document: source segments in the
    /// order memoQ first showed them, plus the latest project metadata.
    ///
    /// memoQ gives an MT plugin no way to ask for a document — it only ever
    /// pushes segments at us one lookup at a time. So the plugin writes down what
    /// passes through its hands, and after one Pre-translate pass that adds up to
    /// the entire document. The MCP bridge reads this store to answer
    /// "what is this project?", and AutoPrompt will read it later for the same
    /// reason: it is the only full-document view this side of the SDK.
    ///
    /// Keyed the same way as <see cref="DocumentMemory"/> (document GUID +
    /// language pair) so the two stores describe the same unit of work.
    /// </summary>
    internal static class CaptureStore
    {
        internal sealed class DocumentCapture
        {
            public string Key;
            public Guid DocumentId;
            public string SourceLangCode;
            public string TargetLangCode;
            public string Client;
            public string Domain;
            public string Subject;
            public string ProjectGuid;
            public DateTime LastSeenUtc;

            /// <summary>
            /// True when the segments arrived through terminology lookups rather
            /// than translation requests. That channel carries no document
            /// identity, so these buckets are per language pair and hold only
            /// the rows the cursor has visited.
            /// </summary>
            public bool ViaTerminology;

            /// <summary>Tagged source texts, in first-seen order.</summary>
            public List<string> Sources = new List<string>();

            /// <summary>Same strings, for O(1) dedup of memoQ's repeat lookups.</summary>
            public HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>
            /// memoQ's state for each row it asked about: not started,
            /// pre-translated, partially edited, confirmed, proofread, machine
            /// translated, rejected.
            ///
            /// <para>Keyed by the source text rather than held in a list beside
            /// <see cref="Sources"/>, deliberately. A parallel list has to stay
            /// aligned through every add, cap and copy, and the one that drifts
            /// reports the wrong status for every row after it - which reads as
            /// data rather than as a bug. A lookup cannot drift.</para>
            ///
            /// <para>Two rows with identical source text share one entry, which is
            /// honest: a plugin is told nothing that distinguishes them.</para>
            ///
            /// <para>Why this is worth keeping at all: a translator cannot
            /// otherwise tell a row that arrived pre-filled from a row nobody has
            /// touched. On the first production job the pre-filled targets were
            /// badly wrong and 231 rows of 799 needed changing, and the client's
            /// standing instruction is to review every matched row - which was
            /// unanswerable, because the plugin could not say which they were.</para>
            /// </summary>
            public Dictionary<string, int> StatusBySource = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, DocumentCapture> _docs =
            new Dictionary<string, DocumentCapture>(StringComparer.Ordinal);

        /// <summary>
        /// Caps chosen the same way as DocumentMemory's: big enough for any
        /// real document, small enough that a runaway session cannot grow
        /// without bound inside memoQ's process.
        /// </summary>
        private const int MaxSourcesPerDocument = 5000;
        private const int MaxDocuments = 50;

        public static void Record(EngineContext context, string taggedSource)
        {
            Record(context, taggedSource, null);
        }

        /// <summary>
        /// As above, remembering memoQ's state for the row when the request
        /// carried one. Only translation requests do; terminology lookups do not.
        /// </summary>
        public static void Record(EngineContext context, string taggedSource, int? status)
        {
            if (context == null || string.IsNullOrWhiteSpace(taggedSource)) return;

            lock (_lock)
            {
                DocumentCapture doc;
                if (!_docs.TryGetValue(context.MemoryKey, out doc))
                {
                    if (_docs.Count >= MaxDocuments)
                    {
                        var oldest = _docs.Values.OrderBy(d => d.LastSeenUtc).First();
                        _docs.Remove(oldest.Key);
                    }

                    doc = new DocumentCapture
                    {
                        Key = context.MemoryKey,
                        SourceLangCode = context.SourceLangCode,
                        TargetLangCode = context.TargetLangCode
                    };
                    _docs[context.MemoryKey] = doc;
                }

                doc.LastSeenUtc = DateTime.UtcNow;
                doc.DocumentId = context.CurrentDocument;

                var meta = context.LastMetadata;
                if (meta != null)
                {
                    // Overwrite only with substance: metadata arrives on some
                    // sessions and not others, and a null must not erase what a
                    // richer request already told us.
                    if (!string.IsNullOrWhiteSpace(meta.Client)) doc.Client = meta.Client;
                    if (!string.IsNullOrWhiteSpace(meta.Domain)) doc.Domain = meta.Domain;
                    if (!string.IsNullOrWhiteSpace(meta.Subject)) doc.Subject = meta.Subject;
                    if (meta.ProjectGuid != Guid.Empty) doc.ProjectGuid = meta.ProjectGuid.ToString("D");
                }

                if (doc.Seen.Add(taggedSource))
                {
                    if (doc.Sources.Count < MaxSourcesPerDocument)
                        doc.Sources.Add(taggedSource);

                // Outside the cap deliberately: a row's state is worth knowing
                // even once the source list is full, and it costs one entry.
                if (status.HasValue) doc.StatusBySource[taggedSource] = status.Value;
                    else
                        doc.Seen.Remove(taggedSource);
                }
            }
        }

        /// <summary>
        /// Records a segment seen through the terminology plugin. memoQ asks the
        /// TB plugin about every row the cursor lands on, whatever the MT
        /// provider is — so this channel captures a Google-translated or TM-only
        /// document as the translator walks through it. No document GUID
        /// arrives on this path, so the bucket is per language pair, keyed the
        /// same way EngineContext.MemoryKey degrades when it has no document.
        /// </summary>
        public static void RecordVisited(string sourceLangCode, string targetLangCode, string taggedSource)
        {
            if (string.IsNullOrWhiteSpace(taggedSource)) return;

            // Its own prefix, not the MT path's "nodoc_": the two channels must
            // never share a bucket, or the origin reported over the bridge
            // depends on which one happened to create it first.
            var key = "visited_" + (sourceLangCode ?? "?") + "-" + (targetLangCode ?? "?");

            lock (_lock)
            {
                DocumentCapture doc;
                if (!_docs.TryGetValue(key, out doc))
                {
                    if (_docs.Count >= MaxDocuments)
                    {
                        var oldest = _docs.Values.OrderBy(d => d.LastSeenUtc).First();
                        _docs.Remove(oldest.Key);
                    }

                    doc = new DocumentCapture
                    {
                        Key = key,
                        SourceLangCode = sourceLangCode,
                        TargetLangCode = targetLangCode,
                        ViaTerminology = true
                    };
                    _docs[key] = doc;
                }

                doc.LastSeenUtc = DateTime.UtcNow;

                if (doc.Seen.Add(taggedSource))
                {
                    if (doc.Sources.Count < MaxSourcesPerDocument)
                        doc.Sources.Add(taggedSource);
                    else
                        doc.Seen.Remove(taggedSource);
                }
            }
        }

        /// <summary>All captured documents, most recently active first. Snapshots — safe to hand across threads.</summary>
        public static List<DocumentCapture> Snapshot()
        {
            lock (_lock)
            {
                return _docs.Values
                    .OrderByDescending(d => d.LastSeenUtc)
                    .Select(Clone)
                    .ToList();
            }
        }

        /// <summary>One document by key, or the most recently active when key is empty. Null when nothing captured.</summary>
        public static DocumentCapture Get(string key)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(key))
                    return _docs.TryGetValue(key, out var doc) ? Clone(doc) : null;

                return _docs.Values.OrderByDescending(d => d.LastSeenUtc).Select(Clone).FirstOrDefault();
            }
        }

        private static DocumentCapture Clone(DocumentCapture d)
        {
            return new DocumentCapture
            {
                Key = d.Key,
                DocumentId = d.DocumentId,
                SourceLangCode = d.SourceLangCode,
                TargetLangCode = d.TargetLangCode,
                Client = d.Client,
                Domain = d.Domain,
                Subject = d.Subject,
                ProjectGuid = d.ProjectGuid,
                LastSeenUtc = d.LastSeenUtc,
                ViaTerminology = d.ViaTerminology,
                Sources = new List<string>(d.Sources),
                StatusBySource = new Dictionary<string, int>(d.StatusBySource, StringComparer.Ordinal)
            };
        }
    }
}
