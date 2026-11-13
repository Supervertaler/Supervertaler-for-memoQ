using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The live document as memoQ's Preview SDK reports it, fed by the
    /// Supervertaler preview tool (a separate process memoQ launches) over the
    /// bridge.
    ///
    /// This is the view the MT and TB SDKs never give a plugin: every row's
    /// target text, the row the cursor is on, the document's real name. It
    /// arrives push-style — a content update for each edited row, a highlight
    /// change for each cursor move — and here it is simply kept current, so
    /// the bridge can answer "what is the active segment?" from memory.
    ///
    /// Also carries the one command channel that goes the other way: the
    /// preview tool long-polls <see cref="TakeCommands"/> and executes what it
    /// finds (today: select a segment). Everything else about the document
    /// still flows memoQ → tool → here, never the reverse.
    /// </summary>
    internal static class PreviewStore
    {
        internal sealed class Part
        {
            public string PartId;
            public Guid DocumentGuid;
            public string DocumentName;
            public string ImportPath;
            public string SourceLangCode;
            public string TargetLangCode;
            public string Source;
            public string Target;
            public int WordCount;
            public int CharCount;
            public DateTime UpdatedUtc;
        }

        internal sealed class Active
        {
            public string PartId;
            public int SourceStart, SourceLength, TargetStart, TargetLength;
            public DateTime AtUtc;
        }

        internal sealed class Command
        {
            public string Type;      // "goto"
            public string PartId;
            /// <summary>Optional character range within the part's source: which sentence of the paragraph to land on.</summary>
            public int SourceStart, SourceLength;
            public DateTime QueuedUtc;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Part> _parts = new Dictionary<string, Part>(StringComparer.Ordinal);

        private static Active _active;
        private static bool _toolConnected;
        private static DateTime _toolSeenUtc;

        private static readonly Queue<Command> _commands = new Queue<Command>();
        private static readonly AutoResetEvent _commandSignal = new AutoResetEvent(false);

        private const int MaxParts = 50000;

        public static void Upsert(IEnumerable<Part> parts)
        {
            if (parts == null) return;
            lock (_lock)
            {
                foreach (var p in parts)
                {
                    if (p == null || string.IsNullOrEmpty(p.PartId)) continue;
                    if (!_parts.ContainsKey(p.PartId) && _parts.Count >= MaxParts) continue;
                    p.UpdatedUtc = DateTime.UtcNow;
                    _parts[p.PartId] = p;
                }
            }
        }

        /// <summary>
        /// memoQ's list of part ids: what is open in memoQ now. Every paragraph
        /// of a document that is not in it is dropped, and an empty list empties
        /// the store. Returns how many paragraphs were dropped.
        ///
        /// <para>Measured 2026-11-13 in memoQ 12.6.28: with two documents open
        /// the list held 137 ids (15 + 122); after one tab was closed, 15. The
        /// Preview SDK has no "document closed" event, so this list is the only
        /// thing that says a document has gone. Until 2026-11-13 it was stored
        /// and never read, so a closed document stayed in the live view for the
        /// rest of the memoQ session - listed, checked, and offered as the
        /// default.</para>
        ///
        /// <para>By document view, not by part: a paragraph that arrived after
        /// memoQ built the list stays as long as its document is listed. A
        /// paragraph of a closed document that arrives late (a pull answered
        /// after the tab closed) is stored again and goes with the next list;
        /// the tool asks for one every 45 seconds.</para>
        /// </summary>
        public static int KeepOnly(IEnumerable<string> partIds)
        {
            if (partIds == null) return 0;

            var open = new HashSet<string>(
                partIds.Where(id => !string.IsNullOrEmpty(id)).Select(ViewKeyOf), StringComparer.Ordinal);

            lock (_lock)
            {
                var gone = _parts.Keys.Where(id => !open.Contains(ViewKeyOf(id))).ToList();
                foreach (var id in gone) _parts.Remove(id);

                // The cursor was in a document that has closed: it is nowhere.
                if (_active != null && (_active.PartId == null || !_parts.ContainsKey(_active.PartId)))
                    _active = null;

                return gone.Count;
            }
        }

        public static void SetActive(Active active)
        {
            lock (_lock) _active = active;
        }

        public static void NoteTool(bool connected)
        {
            lock (_lock) { _toolConnected = connected; _toolSeenUtc = DateTime.UtcNow; }
        }

        public static bool ToolAlive
        {
            get { lock (_lock) return _toolConnected && (DateTime.UtcNow - _toolSeenUtc) < TimeSpan.FromSeconds(90); }
        }

        public static Active GetActive()
        {
            lock (_lock) return _active;
        }

        public static Part GetPart(string partId)
        {
            lock (_lock) return partId != null && _parts.TryGetValue(partId, out var p) ? p : null;
        }

        /// <summary>
        /// The document the cursor is in, or null when memoQ has not said where
        /// the cursor is (or it was in a document that has since closed).
        /// </summary>
        public static Guid? ActiveDocument()
        {
            lock (_lock) return ActiveDocumentLocked();
        }

        private static Guid? ActiveDocumentLocked()
        {
            return _active?.PartId != null && _parts.TryGetValue(_active.PartId, out var p) ? p.DocumentGuid : (Guid?)null;
        }

        /// <summary>
        /// Open documents, one part each: the one the cursor is in first, then
        /// most recently updated first.
        ///
        /// <para>The first is what a tool uses when no document is named. It
        /// was "most recently updated" alone, and the preview tool re-pulls
        /// every open document's text every 45 seconds, so with two documents
        /// open the default was whichever memoQ's answer happened to end
        /// with.</para>
        /// </summary>
        public static List<Part> Documents()
        {
            lock (_lock)
            {
                var active = ActiveDocumentLocked();
                return _parts.Values
                    .GroupBy(p => p.DocumentGuid)
                    .Select(g => g.OrderByDescending(p => p.UpdatedUtc).First())
                    .OrderByDescending(p => active.HasValue && p.DocumentGuid == active.Value)
                    .ThenByDescending(p => p.UpdatedUtc)
                    .ToList();
            }
        }

        /// <summary>
        /// A document's parts in document order.
        ///
        /// Measured: a part is a PARAGRAPH, not a segment — a three-sentence
        /// paragraph that memoQ shows as three grid rows is one part with one
        /// id — and memoQ's id list arrives in string order (1, 10, 11, 2, …),
        /// so the numeric tail of the id is the only reliable order. The list
        /// memoQ sends is kept for membership, not for sequence.
        /// </summary>
        public static List<Part> Rows(Guid documentGuid)
        {
            lock (_lock)
            {
                return _parts.Values
                    .Where(p => p.DocumentGuid == documentGuid)
                    .OrderBy(p => NumericTail(p.PartId))
                    .ThenBy(p => p.PartId, StringComparer.Ordinal)
                    .ToList();
            }
        }

        /// <summary>
        /// The rows of several documents, one document after another, each in its
        /// own order.
        ///
        /// <para>For a memoQ VIEW - several files merged into one editor tab.
        /// Measured over the live bridge on a real three-file view: memoQ reports
        /// it as three separate documents, each with its own DocumentGuid, its own
        /// import path AND its own view guid in its part ids. Nothing in the
        /// payload says "these three are one tab", so the set is named by the
        /// caller rather than inferred here. Within a document the part number is
        /// the order; between documents the caller's order stands, because memoQ
        /// offers none.</para>
        /// </summary>
        public static List<Part> RowsOfDocuments(IList<Guid> documentGuids)
        {
            var result = new List<Part>();
            if (documentGuids == null) return result;

            foreach (var guid in documentGuids)
            {
                if (guid == Guid.Empty) continue;
                result.AddRange(Rows(guid));
            }

            return result;
        }

        public static int Count(Guid documentGuid)
        {
            lock (_lock) return _parts.Values.Count(p => p.DocumentGuid == documentGuid);
        }

        // ── commands to the tool ─────────────────────────────────────────

        public static void Enqueue(Command c)
        {
            lock (_commands)
            {
                // Bound the queue: a tool that is gone should not accumulate a
                // backlog it then replays on reconnect.
                while (_commands.Count >= 20) _commands.Dequeue();
                c.QueuedUtc = DateTime.UtcNow;
                _commands.Enqueue(c);
            }
            _commandSignal.Set();
        }

        /// <summary>Waits up to <paramref name="wait"/> for commands, then returns whatever is queued.</summary>
        public static List<Command> TakeCommands(TimeSpan wait)
        {
            lock (_commands) if (_commands.Count > 0) return Drain();
            _commandSignal.WaitOne(wait);
            lock (_commands) return Drain();
        }

        private static List<Command> Drain()
        {
            var list = new List<Command>();
            while (_commands.Count > 0) list.Add(_commands.Dequeue());
            return list;
        }

        // ── id helpers ───────────────────────────────────────────────────

        /// <summary>"mQ-default-&lt;guid&gt;-17" → "mQ-default-&lt;guid&gt;".</summary>
        private static string ViewKeyOf(string partId)
        {
            var i = partId.LastIndexOf('-');
            return i > 0 ? partId.Substring(0, i) : partId;
        }

        public static int NumericTail(string partId)
        {
            var i = partId.LastIndexOf('-');
            return i >= 0 && int.TryParse(partId.Substring(i + 1), out var n) ? n : int.MaxValue;
        }

        public static int Clear()
        {
            lock (_lock)
            {
                var n = _parts.Count;
                _parts.Clear(); _active = null;
                return n;
            }
        }
    }
}
