using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The translator's memory banks, served to whatever is on the other end of
    /// the bridge — in practice Claude or ChatGPT, mid-conversation, when the
    /// translator says "look at the memory bank for this project".
    ///
    /// This is the read-only half of SuperMemory and it is deliberately first:
    /// it needs no setting, no picker and no per-project state, because the
    /// caller names the bank it wants. Injecting a bank into every translation
    /// request is a larger question — it costs tokens on every batch — and can
    /// be answered separately.
    ///
    /// The wire format matches the Trados bridge exactly. One MCP server exe
    /// serves both products, so a field renamed here is a tool broken there.
    /// </summary>
    internal static class SuperMemory
    {
        /// <summary>
        /// Deliberately smaller than the 24k a system-prompt injection uses.
        ///
        /// A block injected into a system prompt happens once and is thrown
        /// away. This one is a tool RESULT: it lands in the client's
        /// conversation and is re-sent on every following turn, so an oversized
        /// answer keeps costing for the rest of the session. Callers that
        /// genuinely want the lot can ask for it.
        /// </summary>
        private const int DefaultTokenBudget = 6000;

        private const int MaxTokenBudget = 100000;

        // ── banks ────────────────────────────────────────────────────────

        public static BanksResponse Banks(string activeBank)
        {
            var names = global::Supervertaler.Core.MemoryBanks.List();
            var root = global::Supervertaler.Core.MemoryBanks.Root;

            if (names.Count == 0)
            {
                return new BanksResponse
                {
                    Available = false,
                    Root = root,
                    Note = "No memory banks found under " + root + "."
                };
            }

            var banks = names.Select(name =>
            {
                var shared = global::Supervertaler.Core.MemoryBanks.IsSharedName(name);
                return new BankInfo
                {
                    Name = name,
                    Role = shared ? "shared" : "bank",
                    Active = !shared && string.Equals(name, activeBank, StringComparison.OrdinalIgnoreCase),
                    AlwaysLoaded = shared,
                    Articles = CountArticles(name)
                };
            }).ToList();

            return new BanksResponse
            {
                Available = true,
                Root = root,
                ActiveBank = string.IsNullOrWhiteSpace(activeBank) ? null : activeBank,
                Banks = banks,
                Note = string.IsNullOrWhiteSpace(activeBank)
                    ? "No bank is selected for this project, so name the one you want in the bank argument. "
                    + "Do not guess: a bank supplies client-specific terminology and style, and the wrong one "
                    + "is worse than none."
                    : null
            };
        }

        private static int CountArticles(string bankName)
        {
            try
            {
                var dir = global::Supervertaler.Core.MemoryBanks.DirFor(bankName);
                if (dir == null) return 0;

                return Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories)
                    .Count(f => !global::Supervertaler.Core.MemoryBankReader.IsIgnoredSidecar(f));
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ── context ──────────────────────────────────────────────────────

        /// <summary>
        /// The formatted knowledge-base block for one bank.
        ///
        /// An unknown bank name is an error rather than a fall back to whatever
        /// is active. The response carries a bank name either way, so falling
        /// back looks exactly like success while supplying another client's
        /// terminology — the caller has no way to tell.
        /// </summary>
        public static ContextResponse Context(
            string bankName, string query, string domain, int tokenBudget,
            string sourceLang, string targetLang, string projectName)
        {
            var dir = global::Supervertaler.Core.MemoryBanks.DirFor(bankName);
            if (dir == null)
            {
                return new ContextResponse
                {
                    Available = false,
                    Bank = bankName,
                    Note = string.IsNullOrWhiteSpace(bankName)
                        ? "Name a bank in the bank argument. Call list_supermemory_banks to see them."
                        : "No memory bank called \"" + bankName + "\". Call list_supermemory_banks to see what exists."
                };
            }

            var budget = tokenBudget > 0 ? Math.Min(tokenBudget, MaxTokenBudget) : DefaultTokenBudget;

            var reader = new global::Supervertaler.Core.MemoryBankReader(dir);
            reader.RefreshIndex();

            var ctx = reader.LoadContext(projectName, domain, sourceLang, targetLang,
                                         tokenBudget: budget, queryText: query);

            if (ctx == null || !ctx.HasContent)
            {
                return new ContextResponse
                {
                    Available = false,
                    Bank = bankName,
                    Domain = domain,
                    Note = "The bank \"" + bankName + "\" has no content for this project, domain or language pair."
                };
            }

            var sources = new List<string>();
            if (!string.IsNullOrEmpty(ctx.ClientProfilePath)) sources.Add(ctx.ClientProfilePath);
            if (!string.IsNullOrEmpty(ctx.DomainArticlePath)) sources.Add(ctx.DomainArticlePath);
            if (!string.IsNullOrEmpty(ctx.StyleGuidePath)) sources.Add(ctx.StyleGuidePath);
            if (ctx.TerminologyPaths != null) sources.AddRange(ctx.TerminologyPaths);
            if (ctx.ExtraPaths != null) sources.AddRange(ctx.ExtraPaths);

            // What did not fit. The budget is deliberately small, so trimming is
            // normal rather than exceptional - which is exactly why it has to be
            // reported: a caller that silently receives two of a bank's three
            // articles translates against rules it was never shown, and neither
            // side finds out.
            var trimmed = ctx.TrimmedPaths != null && ctx.TrimmedPaths.Count > 0
                ? new List<string>(ctx.TrimmedPaths)
                : null;

            return new ContextResponse
            {
                Available = true,
                Bank = bankName,
                Client = ctx.ClientName,
                Domain = ctx.DomainName ?? domain,
                DetectionMethod = ctx.DetectionMethod,
                Context = global::Supervertaler.Core.MemoryBankReader.FormatForPrompt(ctx),
                Sources = sources,
                Trimmed = trimmed,
                Note = trimmed == null
                    ? null
                    : trimmed.Count + " article(s) did not fit the " + budget.ToString("N0")
                      + "-token budget and are listed under \"trimmed\". Ask again with a larger "
                      + "tokenBudget, or read one directly, if you need them."
            };
        }

        // ── search ───────────────────────────────────────────────────────

        /// <summary>
        /// Keyword search across one bank and the shared overlay.
        ///
        /// Reports which banks were actually searched, because otherwise a
        /// zero-hit answer is indistinguishable from "the translator never
        /// wrote that down" — the wrong conclusion to hand a model that is
        /// about to invent a term instead.
        /// </summary>
        public static SearchResponse Search(string bankName, string query, int limit)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new SearchResponse { Available = false, Note = "Give a query to search for." };

            var searched = new List<string>();
            var hits = new List<SearchHit>();
            var take = limit > 0 ? Math.Min(limit, 50) : 10;

            foreach (var name in new[] { bankName, global::Supervertaler.Core.MemoryBankReader.SharedBankName })
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (searched.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))) continue;

                var dir = global::Supervertaler.Core.MemoryBanks.DirFor(name);
                if (dir == null) continue;

                searched.Add(name);

                var reader = new global::Supervertaler.Core.MemoryBankReader(dir);
                reader.RefreshIndex();

                foreach (var h in reader.Search(query, take))
                {
                    hits.Add(new SearchHit
                    {
                        Bank = name,
                        Path = h.RelativePath,
                        Folder = h.Folder,
                        Title = h.Title,
                        Score = h.Score,
                        Snippet = h.Snippet
                    });
                }
            }

            if (searched.Count == 0)
            {
                return new SearchResponse
                {
                    Available = false,
                    Bank = bankName,
                    Note = string.IsNullOrWhiteSpace(bankName)
                        ? "Name a bank in the bank argument. Call list_supermemory_banks to see them."
                        : "No memory bank called \"" + bankName + "\"."
                };
            }

            return new SearchResponse
            {
                Available = true,
                Bank = bankName,
                BanksSearched = searched,
                Hits = hits.OrderByDescending(h => h.Score).Take(take).ToList(),
                Note = hits.Count == 0
                    ? "Nothing matched in " + string.Join(", ", searched)
                      + ". That means it is not written down there, not that it is unimportant."
                    : null
            };
        }

        // ── wire shapes, matching the Trados bridge field for field ──────

        [DataContract]
        internal class BankInfo
        {
            [DataMember(Name = "name", Order = 0)] public string Name { get; set; }
            [DataMember(Name = "role", Order = 1)] public string Role { get; set; }
            [DataMember(Name = "active", Order = 2)] public bool Active { get; set; }
            [DataMember(Name = "alwaysLoaded", Order = 3, EmitDefaultValue = false)] public bool AlwaysLoaded { get; set; }
            [DataMember(Name = "articles", Order = 4)] public int Articles { get; set; }
        }

        [DataContract]
        internal class BanksResponse
        {
            [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
            [DataMember(Name = "root", Order = 1, EmitDefaultValue = false)] public string Root { get; set; }
            [DataMember(Name = "activeBank", Order = 2, EmitDefaultValue = false)] public string ActiveBank { get; set; }
            [DataMember(Name = "banks", Order = 3, EmitDefaultValue = false)] public List<BankInfo> Banks { get; set; }
            [DataMember(Name = "note", Order = 4, EmitDefaultValue = false)] public string Note { get; set; }
        }

        [DataContract]
        internal class ContextResponse
        {
            [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
            [DataMember(Name = "bank", Order = 1, EmitDefaultValue = false)] public string Bank { get; set; }
            [DataMember(Name = "client", Order = 2, EmitDefaultValue = false)] public string Client { get; set; }
            [DataMember(Name = "domain", Order = 3, EmitDefaultValue = false)] public string Domain { get; set; }
            [DataMember(Name = "detectionMethod", Order = 4, EmitDefaultValue = false)] public string DetectionMethod { get; set; }
            [DataMember(Name = "context", Order = 5, EmitDefaultValue = false)] public string Context { get; set; }
            [DataMember(Name = "sources", Order = 6, EmitDefaultValue = false)] public List<string> Sources { get; set; }
            [DataMember(Name = "trimmed", Order = 7, EmitDefaultValue = false)] public List<string> Trimmed { get; set; }
            [DataMember(Name = "note", Order = 8, EmitDefaultValue = false)] public string Note { get; set; }
        }

        [DataContract]
        internal class SearchHit
        {
            [DataMember(Name = "bank", Order = 0, EmitDefaultValue = false)] public string Bank { get; set; }
            [DataMember(Name = "path", Order = 1)] public string Path { get; set; }
            [DataMember(Name = "folder", Order = 2, EmitDefaultValue = false)] public string Folder { get; set; }
            [DataMember(Name = "title", Order = 3, EmitDefaultValue = false)] public string Title { get; set; }
            [DataMember(Name = "score", Order = 4)] public int Score { get; set; }
            [DataMember(Name = "snippet", Order = 5, EmitDefaultValue = false)] public string Snippet { get; set; }
        }

        [DataContract]
        internal class SearchResponse
        {
            [DataMember(Name = "available", Order = 0)] public bool Available { get; set; }
            [DataMember(Name = "bank", Order = 1, EmitDefaultValue = false)] public string Bank { get; set; }
            [DataMember(Name = "banksSearched", Order = 2, EmitDefaultValue = false)] public List<string> BanksSearched { get; set; }
            [DataMember(Name = "hits", Order = 3, EmitDefaultValue = false)] public List<SearchHit> Hits { get; set; }
            [DataMember(Name = "note", Order = 4, EmitDefaultValue = false)] public string Note { get; set; }
        }
    }
}
