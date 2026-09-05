using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Supervertaler.Core.Models;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Picks the prompt memoQ will use, and the memory bank it will send with
    /// every request.
    ///
    /// <para>Both are named things chosen out of a folder that grows with the
    /// work, so both are rows for <see cref="ChooserForm"/> rather than dropdown
    /// menus. What lives here is only the part that is specific to each: what a
    /// row says, and what its grey detail line says.</para>
    ///
    /// <para>Only prompts memoQ can actually run are offered. Prompts marked for
    /// Trados are filtered out by the caller, because memoQ would fall back to
    /// its own Instructions box without saying so.</para>
    /// </summary>
    internal static class PromptChooserForm
    {
        /// <summary>
        /// Shows the prompt chooser. Returns the chosen relative path - empty for
        /// "use memoQ's own instructions" - or null when cancelled.
        /// </summary>
        public static string ChoosePrompt(IWin32Window owner, List<PromptTemplate> prompts, string current)
        {
            using (var dialog = new ChooserForm(
                "Choose the active prompt",
                "memoQ will send this prompt with every translation request.",
                "Type to filter by name or folder",
                PromptRows(prompts), current))
            {
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedValue : null;
            }
        }

        /// <summary>
        /// Shows the memory bank chooser. Returns the chosen bank name - empty
        /// for none - or null when cancelled.
        /// </summary>
        public static string ChooseBank(IWin32Window owner, IReadOnlyList<BankRow> banks, string current)
        {
            using (var dialog = new ChooserForm(
                "Choose the active memory bank",
                "The bank's brief, terminology and style go to the model with every request. "
                + "Each project remembers its own.",
                "Type to filter by name",
                BankRows(banks), current))
            {
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedValue : null;
            }
        }

        /// <summary>One bank, as the caller found it on disk.</summary>
        internal sealed class BankRow
        {
            public string Name;
            public int Articles;
        }

        // -- what each row says --------------------------------------------------

        internal static IReadOnlyList<ChooserForm.Row> PromptRows(List<PromptTemplate> prompts)
        {
            var rows = new List<ChooserForm.Row>
            {
                // First, because it is the state a new install is in and the only
                // way back to memoQ's own Instructions box.
                new ChooserForm.Row
                {
                    Value = "",
                    Display = "(use the instructions in memoQ's settings)",
                    Detail = "No library prompt. memoQ sends the Instructions box from its own settings."
                }
            };

            foreach (var p in prompts ?? new List<PromptTemplate>())
            {
                if (p == null) continue;

                var folder = string.IsNullOrWhiteSpace(p.Category) ? "" : p.Category + "  /  ";
                var display = folder + p.Name;

                // The language pair, unless the name already carries it. AutoPrompt
                // names a prompt after the project, which often spells out the
                // codes - so a generated one read "<name> dut-NL-eng-GB    dut-NL
                // to eng-GB": the same fact twice, in the one list whose whole
                // point is telling prompts apart at a glance.
                var pair = string.IsNullOrWhiteSpace(p.SourceLang) || string.IsNullOrWhiteSpace(p.TargetLang)
                    ? null
                    : p.SourceLang + " to " + p.TargetLang;

                if (pair != null && !NamesTheLanguages(p.Name, p.SourceLang, p.TargetLang))
                    display += "      " + pair;

                // Which product it targets. The tree marks this and the chooser did
                // not, which is the wrong way round: the tree is for browsing, and
                // this is the list you pick the prompt memoQ will actually run from.
                var only = ProductNote(p.App);
                if (only != null) display += "      " + only;

                rows.Add(new ChooserForm.Row
                {
                    Value = p.RelativePath,
                    Display = display,
                    Detail = p.Description,
                    Search = p.RelativePath
                });
            }

            return rows;
        }

        internal static IReadOnlyList<ChooserForm.Row> BankRows(IReadOnlyList<BankRow> banks)
        {
            var rows = new List<ChooserForm.Row>
            {
                // First, and a real answer rather than an absence. A project with
                // no bank is the correct state for most jobs, and it is the only
                // way to switch SuperMemory off for one.
                new ChooserForm.Row
                {
                    Value = "",
                    Display = "(none)",
                    Detail = "No memory bank. Nothing from SuperMemory is sent with a translation request."
                }
            };

            foreach (var b in banks ?? new List<BankRow>())
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Name)) continue;

                rows.Add(new ChooserForm.Row
                {
                    Value = b.Name,
                    Display = b.Name,
                    Detail = b.Articles == 1
                        ? "1 article, plus whatever the shared bank adds underneath it."
                        : b.Articles + " articles, plus whatever the shared bank adds underneath them."
                });
            }

            return rows;
        }

        /// <summary>
        /// True when a prompt's own name already spells out both language codes,
        /// in which case repeating them beside it tells the reader nothing.
        /// </summary>
        internal static bool NamesTheLanguages(string name, string sourceLang, string targetLang)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            return name.IndexOf(sourceLang ?? "", StringComparison.OrdinalIgnoreCase) >= 0
                && name.IndexOf(targetLang ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// What to append for a prompt that only one product can run. Nothing for
        /// a prompt available to both, which is most of them - so the note stands
        /// out rather than becoming wallpaper.
        ///
        /// A Trados prompt selected here would run: memoQ fills neither
        /// {{SELECTION}} nor {{PROJECT}}, and an unfilled placeholder becomes an
        /// empty string rather than an error, so the model receives an instruction
        /// with a hole in it and answers anyway.
        /// </summary>
        internal static string ProductNote(string app)
        {
            switch ((app ?? "").Trim().ToLowerInvariant())
            {
                case "memoq": return "·  memoQ only";
                case "trados": return "·  Trados only – memoQ cannot fill its placeholders";
                case "workbench": return "·  Workbench only";
                default: return null;
            }
        }
    }
}
