using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Short-lived cache of prompt to translation, so the same request is not paid
    /// for twice.
    ///
    /// memoQ genuinely does ask twice. Observed in the log, two seconds apart on a
    /// single segment the translator merely landed on:
    ///
    /// <code>
    /// 20:37:56 translate: 199 src chars -> 239 target chars
    /// 20:37:58 translate: 199 src chars -> 237 target chars
    /// </code>
    ///
    /// Two API calls, two charges, and two slightly different answers for the same
    /// segment. Revisiting a segment later in the session costs another. Lara
    /// advertises the same idea as a feature ("any translation retrieved within the
    /// last 24 hours will not be charged again"), which suggests it is not
    /// something we can expect memoQ to stop doing.
    ///
    /// <para>The key is the <b>whole prompt</b>, not the source text. Terminology,
    /// recalled segments and project metadata all change what the right answer is,
    /// so a segment whose context has changed must not be served a stale
    /// translation. In practice the duplicate arrives within seconds with an
    /// identical prompt and hits; a genuinely re-contextualised segment misses, as
    /// it should.</para>
    ///
    /// <para><b>Memory only, never written to disk.</b> Unlike
    /// <see cref="DocumentMemory"/> — which holds translations a human approved and
    /// is worth persisting — this holds raw model output over confidential source
    /// text, for the sake of an API bill. It does not deserve to outlive the
    /// process.</para>
    /// </summary>
    internal static class TranslationCache
    {
        /// <summary>
        /// Long enough to cover memoQ's duplicate request and a translator moving
        /// back and forth over a passage; short enough that a changed glossary or
        /// prompt takes effect without anyone having to think about caching.
        /// </summary>
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Bounded so a long pre-translate cannot grow it without limit. Entries
        /// are small (a segment of text), so this is a handful of megabytes at
        /// worst.
        /// </summary>
        private const int MaxEntries = 500;

        private static readonly object _lock = new object();

        private sealed class Item
        {
            public string Value;
            public DateTime StoredUtc;
        }

        private static readonly Dictionary<string, Item> _items = new Dictionary<string, Item>(StringComparer.Ordinal);

        /// <summary>Insertion order, for evicting the oldest when full.</summary>
        private static readonly LinkedList<string> _order = new LinkedList<string>();

        private static long _hits;
        private static long _misses;

        public static long Hits { get { lock (_lock) return _hits; } }
        public static long Misses { get { lock (_lock) return _misses; } }

        public static string Key(string provider, string model, string endpoint, string system, string user)
        {
            // Hashed rather than stored whole: the prompt can run to thousands of
            // characters with a large glossary, and keeping hundreds of them as
            // dictionary keys would cost more memory than the cache saves.
            using (var sha = SHA256.Create())
            {
                var material = string.Join("", provider, model, endpoint, system, user);
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
                return Convert.ToBase64String(bytes);
            }
        }

        public static bool TryGet(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) return false;

            lock (_lock)
            {
                if (!_items.TryGetValue(key, out var item))
                {
                    _misses++;
                    return false;
                }

                if (DateTime.UtcNow - item.StoredUtc > Ttl)
                {
                    Remove(key);
                    _misses++;
                    return false;
                }

                value = item.Value;
                _hits++;
                return true;
            }
        }

        public static void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null) return;

            lock (_lock)
            {
                if (_items.ContainsKey(key)) Remove(key);

                _items[key] = new Item { Value = value, StoredUtc = DateTime.UtcNow };
                _order.AddLast(key);

                while (_order.Count > MaxEntries)
                {
                    var oldest = _order.First.Value;
                    _order.RemoveFirst();
                    _items.Remove(oldest);
                }
            }
        }

        /// <summary>Wired to the options dialog alongside the stored-context control.</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _items.Clear();
                _order.Clear();
                _hits = 0;
                _misses = 0;
            }
        }

        private static void Remove(string key)
        {
            _items.Remove(key);
            var node = _order.Find(key);
            if (node != null) _order.Remove(node);
        }
    }
}
