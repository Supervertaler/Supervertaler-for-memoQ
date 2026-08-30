using System;
using System.Collections.Generic;
using System.Linq;
using Supervertaler.Core.Models;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Resolves which instructions a translation request should use: a prompt
    /// from the shared Supervertaler library, or the instructions typed into the
    /// options dialog.
    ///
    /// The settings store a prompt's <em>relative path</em>, not its text. That is
    /// the whole point of pointing at the shared library rather than copying out
    /// of it: edit a prompt in the Trados plugin, in Workbench, or in a text
    /// editor, and memoQ picks up the change on the next segment. A copy would
    /// have gone stale the first time you improved it somewhere else.
    ///
    /// Falls back to the inline instructions whenever the library has nothing to
    /// offer — no prompt selected, the file deleted, the shared folder missing.
    /// A translator mid-job should never be stopped by a prompt that moved.
    /// </summary>
    internal static class PromptResolver
    {
        private static readonly object _lock = new object();
        private static DateTime _lastLoad = DateTime.MinValue;
        private static List<PromptTemplate> _cache = new List<PromptTemplate>();

        /// <summary>
        /// Reloading walks a directory and parses every prompt's frontmatter.
        /// Cheap, but not per-segment cheap.
        /// </summary>
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

        /// <summary>Sentinel stored when the user wants the typed instructions rather than a library prompt.</summary>
        public const string InlineInstructions = "";

        /// <summary>
        /// Prompts worth offering in memoQ: translation prompts not marked as
        /// belonging to another product.
        ///
        /// The <c>app:</c> frontmatter field predates this plugin and its known
        /// values are "workbench", "trados" and "both" (the default). Anything
        /// unmarked or "both" is shown; a prompt explicitly claimed by another
        /// product is not. New prompts can say <c>app: memoq</c>.
        /// </summary>
        public static IReadOnlyList<PromptTemplate> Available()
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _lastLoad < CacheFor) return _cache;
                _lastLoad = DateTime.UtcNow;

                try
                {
                    var library = new global::Supervertaler.Core.PromptLibrary();
                    _cache = library.GetAllPrompts()
                        .Where(p => p != null && !p.IsQuickLauncher)
                        .Where(IsTranslationPrompt)
                        .Where(p => IsForMemoQ(p.App))
                        .OrderBy(p => p.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(p => p.SortOrder)
                        .ThenBy(p => p.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    // A missing or unreadable shared folder is not an error worth
                    // surfacing here: the dialog simply offers nothing but the
                    // inline instructions.
                    PluginLog.Write("PromptResolver: could not read the prompt library", ex);
                    _cache = new List<PromptTemplate>();
                }

                return _cache;
            }
        }

        /// <summary>
        /// Translation prompts only.
        ///
        /// The library holds three kinds side by side — Translate, Proofread and
        /// QuickLauncher — and this dropdown sets the instructions for
        /// *translating a segment*. Offering "Default Proofreading Prompt" here
        /// would produce review commentary where a translation belongs, and the
        /// user would have no way to tell from the name that it could not work.
        ///
        /// Keyed on Category rather than the folder, because a prompt can be
        /// filed anywhere; the folder is used only when Category is unset.
        /// </summary>
        private static bool IsTranslationPrompt(PromptTemplate p)
        {
            var category = p.Category;

            if (string.IsNullOrWhiteSpace(category))
            {
                // Fall back to the top folder of the relative path.
                var rel = p.RelativePath ?? string.Empty;
                var slash = rel.IndexOfAny(new[] { '/', '\\' });
                category = slash > 0 ? rel.Substring(0, slash) : rel;
            }

            return category.Equals("Translate", StringComparison.OrdinalIgnoreCase)
                || category.StartsWith("Translate/", StringComparison.OrdinalIgnoreCase)
                || category.StartsWith("Translate\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsForMemoQ(string app)
        {
            if (string.IsNullOrWhiteSpace(app)) return true;
            return app.Equals("both", StringComparison.OrdinalIgnoreCase)
                || app.Equals("memoq", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The instructions to send, given the configured prompt path and the
        /// typed fallback.
        /// </summary>
        public static string Resolve(
            string promptRelativePath,
            string inlineInstructions,
            string sourceLanguage = null,
            string targetLanguage = null)
        {
            if (string.IsNullOrWhiteSpace(promptRelativePath)) return inlineInstructions;

            var match = Available().FirstOrDefault(p =>
                string.Equals(p.RelativePath, promptRelativePath, StringComparison.OrdinalIgnoreCase));

            if (match != null && !string.IsNullOrWhiteSpace(match.Content))
            {
                // Library prompts use the library's placeholders — {{SOURCE_LANGUAGE}},
                // {{TARGET_LANGUAGE}} and friends — not this plugin's {SOURCE_LANG}.
                // Without this the placeholder reached the model verbatim, so a
                // prompt that opened "You are a professional translator working
                // from {{SOURCE_LANGUAGE}} to {{TARGET_LANGUAGE}}" told it nothing
                // at all about the languages.
                return global::Supervertaler.Core.PromptLibrary.ApplyVariables(
                    match.Content, sourceLanguage, targetLanguage);
            }

            PluginLog.Write($"PromptResolver: '{promptRelativePath}' not found in the library — "
                + "using the instructions from the options dialog instead");
            return inlineInstructions;
        }

        /// <summary>Forces the next lookup to re-read the folder. For the options dialog.</summary>
        public static void Invalidate()
        {
            lock (_lock) _lastLoad = DateTime.MinValue;
        }
    }
}
