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
            public DateTime QueuedUtc;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Part> _parts = new Dictionary<string, Part>(StringComparer.Ordinal);

        /// <summary>Part ids in the order memoQ listed them, per document view guid. Canonical row order.</summary>
        private static readonly Dictionary<string, List<string>> _order = new Dictionary<string, List<string>>(StringComparer.Ordinal);

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

        /// <summary>The id list memoQ sent for a document view. Replaces any earlier order for that view.</summary>
        public static void SetOrder(IEnumerable<string> partIds)
        {
            if (partIds == null) return;
            lock (_lock)
            {
                foreach (var group in partIds.Where(id => !string.IsNullOrEmpty(id)).GroupBy(ViewKeyOf))
                    _order[group.Key] = group.ToList();
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

        /// <summary>Documents seen, most recently updated first.</summary>
        public static List<Part> Documents()
        {
            lock (_lock)
            {
                return _parts.Values
                    .GroupBy(p => p.DocumentGuid)
                    .Select(g => g.OrderByDescending(p => p.UpdatedUtc).First())
                    .OrderByDescending(p => p.UpdatedUtc)
                    .ToList();
            }
        }

        /// <summary>
        /// A document's rows in order. memoQ's own id list wins; rows never
        /// listed come after it, sorted by the numeric tail of the id.
        /// </summary>
        public static List<Part> Rows(Guid documentGuid)
        {
            lock (_lock)
            {
                var parts = _parts.Values.Where(p => p.DocumentGuid == documentGuid).ToList();
                if (parts.Count == 0) return parts;

                var view = ViewKeyOf(parts[0].PartId);
                var listed = _order.TryGetValue(view, out var order) ? order : new List<string>();
                var rank = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < listed.Count; i++) rank[listed[i]] = i;

                return parts
                    .OrderBy(p => rank.TryGetValue(p.PartId, out var r) ? r : int.MaxValue)
                    .ThenBy(p => NumericTail(p.PartId))
                    .ToList();
            }
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
                _parts.Clear(); _order.Clear(); _active = null;
                return n;
            }
        }
    }
}
