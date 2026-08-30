using System;
using System.Collections.Generic;
using System.Linq;
using MemoQ.Addins.Common.DataStructures;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Per-document memory of confirmed source/target pairs, built up as the
    /// translator works and used to give later segments the context memoQ will not
    /// hand us directly.
    ///
    /// Why this exists: <c>IRichSession2</c>'s <c>TranslationBundle</c> — the
    /// interface carrying terminology and neighbouring segments — is never invoked
    /// for a third-party MT plugin. memoQ's own bundled ModernMT and Intento
    /// plugins do not implement it either, nor does the current Lara plugin; all
    /// use <c>ISession</c> + <c>ISessionForStoringTranslations</c>. So context is
    /// assembled from what those interfaces do provide:
    ///
    ///   - <c>ISessionWithMetadata</c> supplies document and project identity;
    ///   - <c>ISessionForStoringTranslations</c> supplies every confirmed segment.
    ///
    /// Only confirmed pairs are stored. Feeding our own raw MT output back would
    /// compound its mistakes — the value here is that a human approved these.
    ///
    /// Held in memory for speed and mirrored to disk by
    /// <see cref="DocumentMemoryStore"/> so a day's work is not lost when memoQ
    /// closes. Static and process-wide because memoQ creates and discards engines
    /// and sessions freely.
    /// </summary>
    internal static class DocumentMemory
    {
        private const int MaxDocuments = 50;
        private const int MaxPairsPerDocument = 500;

        /// <summary>
        /// Replay history beyond this triggers a compaction of the file. Twice the
        /// in-memory cap, so a document has to be substantially re-confirmed before
        /// we pay for a rewrite.
        /// </summary>
        private const int CompactAboveReplayCount = MaxPairsPerDocument * 2;

        private static readonly object _lock = new object();

        private static readonly Dictionary<string, LinkedList<Pair>> _byDocument
            = new Dictionary<string, LinkedList<Pair>>(StringComparer.Ordinal);

        /// <summary>Keys whose disk file has already been replayed into memory.</summary>
        private static readonly HashSet<string> _loaded = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Lines appended since load, per key — drives compaction.</summary>
        private static readonly Dictionary<string, int> _replayCount
            = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Document access order, for evicting whole documents LRU-style.</summary>
        private static readonly LinkedList<string> _documentOrder = new LinkedList<string>();

        internal sealed class Pair
        {
            public string Source { get; set; }
            public string Target { get; set; }
        }

        /// <summary>
        /// Record a confirmed translation and persist it. Empty or whitespace-only
        /// pairs are dropped — memoQ confirms those too, and they are noise.
        /// </summary>
        public static void Record(string key, Segment source, Segment target)
        {
            var src = source?.PlainText?.Trim();
            var trg = target?.PlainText?.Trim();

            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(src) || string.IsNullOrEmpty(trg)) return;

            lock (_lock)
            {
                var pairs = EnsureLoaded(key);

                // Re-confirming replaces the earlier target rather than storing
                // both; the newest human decision is the one that counts. On disk
                // the append simply wins on replay, so no rewrite is needed here.
                var existing = pairs.FirstOrDefault(p => string.Equals(p.Source, src, StringComparison.Ordinal));
                if (existing != null) existing.Target = trg;
                else
                {
                    pairs.AddLast(new Pair { Source = src, Target = trg });
                    while (pairs.Count > MaxPairsPerDocument) pairs.RemoveFirst();
                }

                DocumentMemoryStore.Append(key, src, trg);

                _replayCount[key] = _replayCount.TryGetValue(key, out var n) ? n + 1 : 1;
                if (_replayCount[key] > CompactAboveReplayCount)
                {
                    DocumentMemoryStore.Compact(key, pairs.ToList());
                    _replayCount[key] = pairs.Count;
                }
            }
        }

        /// <summary>
        /// The pairs most worth showing the model for this source segment: those
        /// sharing the most vocabulary with it, newest first among equals.
        ///
        /// Word overlap rather than anything cleverer, deliberately. It runs on the
        /// translation path against at most a few hundred entries, and its job is
        /// to surface "you already translated something like this, here is how".
        /// A real fuzzy index belongs in Supervertaler.Core alongside the TM code.
        /// </summary>
        public static IReadOnlyList<Pair> GetRelevant(string key, Segment source, int max)
        {
            if (max <= 0 || string.IsNullOrEmpty(key)) return Array.Empty<Pair>();

            var src = source?.PlainText;
            if (string.IsNullOrWhiteSpace(src)) return Array.Empty<Pair>();

            List<Pair> snapshot;
            lock (_lock)
            {
                var pairs = EnsureLoaded(key);
                if (pairs.Count == 0) return Array.Empty<Pair>();
                snapshot = pairs.ToList();
            }

            var wanted = Tokenize(src);
            if (wanted.Count == 0) return Array.Empty<Pair>();

            return snapshot
                .Select((p, index) => new { Pair = p, Index = index, Score = Overlap(wanted, Tokenize(p.Source)) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Index)   // newer wins ties
                .Take(max)
                .Select(x => x.Pair)
                .ToList();
        }

        public static int CountFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            lock (_lock) return EnsureLoaded(key).Count;
        }

        /// <summary>Drops everything, memory and disk. Wired to a button in the options dialog.</summary>
        public static int ForgetEverything()
        {
            lock (_lock)
            {
                _byDocument.Clear();
                _documentOrder.Clear();
                _loaded.Clear();
                _replayCount.Clear();
            }
            return DocumentMemoryStore.Forget();
        }

        // ---- internals --------------------------------------------------------

        /// <summary>
        /// Returns the in-memory list for a key, replaying the disk file into it on
        /// first touch. Must be called under <see cref="_lock"/>.
        /// </summary>
        private static LinkedList<Pair> EnsureLoaded(string key)
        {
            if (_byDocument.TryGetValue(key, out var pairs))
            {
                Touch(key);
                return pairs;
            }

            pairs = new LinkedList<Pair>();
            _byDocument[key] = pairs;
            _documentOrder.AddLast(key);
            EvictDocumentsIfNeeded();

            if (!_loaded.Add(key)) return pairs;

            var stored = DocumentMemoryStore.Load(key);
            foreach (var p in stored)
            {
                // Replay in file order so a later line for the same source wins,
                // exactly as an in-session re-confirmation would.
                var existing = pairs.FirstOrDefault(x => string.Equals(x.Source, p.Source, StringComparison.Ordinal));
                if (existing != null) existing.Target = p.Target;
                else
                {
                    pairs.AddLast(p);
                    while (pairs.Count > MaxPairsPerDocument) pairs.RemoveFirst();
                }
            }

            _replayCount[key] = stored.Count;

            if (stored.Count > 0)
                PluginLog.Write($"DocumentMemory: restored {pairs.Count} confirmed pair(s) from disk "
                    + $"({stored.Count} line(s) replayed)");

            return pairs;
        }

        private static void Touch(string key)
        {
            var node = _documentOrder.Find(key);
            if (node == null) return;
            _documentOrder.Remove(node);
            _documentOrder.AddLast(node);
        }

        /// <summary>
        /// Evicts the least recently used document from memory only. The disk file
        /// stays, so returning to that document reloads it rather than losing it.
        /// </summary>
        private static void EvictDocumentsIfNeeded()
        {
            while (_documentOrder.Count > MaxDocuments)
            {
                var oldest = _documentOrder.First.Value;
                _documentOrder.RemoveFirst();
                _byDocument.Remove(oldest);
                _loaded.Remove(oldest);
                _replayCount.Remove(oldest);
            }
        }

        private static HashSet<string> Tokenize(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return set;

            var current = new System.Text.StringBuilder();
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c)) current.Append(c);
                else if (current.Length > 0) Add(set, current);
            }
            if (current.Length > 0) Add(set, current);
            return set;
        }

        private static void Add(HashSet<string> set, System.Text.StringBuilder sb)
        {
            // Single characters carry no signal and inflate every score equally.
            if (sb.Length > 1) set.Add(sb.ToString());
            sb.Clear();
        }

        private static int Overlap(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            var smaller = a.Count <= b.Count ? a : b;
            var larger = ReferenceEquals(smaller, a) ? b : a;
            return smaller.Count(larger.Contains);
        }
    }
}
