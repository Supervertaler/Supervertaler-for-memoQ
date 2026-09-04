using System;
using System.Drawing;
using System.Windows.Forms;
using Supervertaler.MemoQ.Core;

namespace Supervertaler.MemoQ.Settings
{
    /// <summary>
    /// The memory-bank dropdown, in one place because it appears in two dialogs.
    ///
    /// <para>memoQ's own options dialog and the prompt editor's Translation
    /// settings are separate windows over the same shared file, and a picker
    /// implemented twice is a picker that will eventually disagree with itself
    /// about what "(none)" means or which project a choice belongs to. Both call
    /// this.</para>
    /// </summary>
    internal static class MemoryBankPicker
    {
        /// <summary>
        /// What no bank looks like in the list. Spelled out rather than left as a
        /// blank row: an empty line in a dropdown reads as a rendering fault, and
        /// choosing no bank is a real and often correct answer.
        /// </summary>
        public const string NoneItem = "(none)";

        /// <summary>
        /// Fills the list and selects <paramref name="selected"/>, or
        /// <see cref="NoneItem"/> when it is empty or names a bank that has since
        /// been renamed or deleted.
        ///
        /// <para><c>_shared</c> is deliberately absent. It is layered under
        /// whichever bank is chosen rather than being a bank you choose, and
        /// offering it would invite someone to select it and quietly lose the
        /// client half of their context.</para>
        /// </summary>
        public static void Fill(ComboBox combo, string selected)
        {
            if (combo == null) return;

            combo.BeginUpdate();
            try
            {
                combo.Items.Clear();
                combo.Items.Add(NoneItem);

                foreach (var name in global::Supervertaler.Core.MemoryBanks.List())
                {
                    if (global::Supervertaler.Core.MemoryBanks.IsSharedName(name)) continue;
                    combo.Items.Add(name);
                }

                var wanted = (selected ?? string.Empty).Trim();
                combo.SelectedIndex = 0;

                if (wanted.Length == 0) return;

                for (var i = 1; i < combo.Items.Count; i++)
                {
                    if (!string.Equals((string)combo.Items[i], wanted, StringComparison.OrdinalIgnoreCase))
                        continue;

                    combo.SelectedIndex = i;
                    return;
                }

                // A recorded bank that is no longer on disk. It stays in the list
                // rather than silently becoming "(none)", because the name is the
                // only clue to what was lost - and pressing OK on a dialog that
                // had quietly reset it would make the loss permanent.
                combo.Items.Add(wanted);
                combo.SelectedIndex = combo.Items.Count - 1;
            }
            finally
            {
                combo.EndUpdate();
            }
        }

        /// <summary>The chosen bank, or an empty string for none.</summary>
        public static string Chosen(ComboBox combo)
        {
            var text = combo?.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            return string.Equals(text, NoneItem, StringComparison.Ordinal) ? string.Empty : text.Trim();
        }

        /// <summary>
        /// Stores the choice: globally, because at any moment one bank is loaded,
        /// and against the project it was made in, so that leaving the job and
        /// coming back restores it rather than whatever the next job used.
        ///
        /// <para>An empty name is recorded too. Clearing a project's bank is a
        /// decision, and a decision that is not written down is one the next
        /// project switch will make differently.</para>
        /// </summary>
        public static void Save(string bankName)
        {
            var value = (bankName ?? string.Empty).Trim();
            SharedSettings.MemoryBank = value;

            Guid project;
            if (Guid.TryParse(SharedSettings.MemoryBankProject ?? string.Empty, out project))
                MemoryBankChoice.Remember(project, value);
        }

        /// <summary>
        /// What the dialog should say underneath the list: which project the
        /// choice will be remembered for, or why it will not be.
        ///
        /// <para>Worth saying plainly, because the behaviour is otherwise
        /// surprising in both directions - a choice made here follows one project
        /// and not the others, and a project with no bank recorded clears rather
        /// than inheriting.</para>
        /// </summary>
        public static string ProjectNote()
        {
            Guid project;
            if (!Guid.TryParse(SharedSettings.MemoryBankProject ?? string.Empty, out project)
                || project == Guid.Empty)
            {
                return "Remembered per project, once memoQ has translated something – it is memoQ "
                     + "that says which project is open, and it has not yet. Until then this is just "
                     + "the bank in force.";
            }

            var name = (SharedSettings.MemoryBankProjectName ?? string.Empty).Trim();

            return "Remembered for " + (name.Length > 0 ? "'" + name + "'" : "the last project "
                   + "translated in") + ". Other projects keep their own choice, and a project with none "
                   + "recorded uses no bank rather than inheriting this one.";
        }

        /// <summary>
        /// A hint label sized to the column, matching the other hints in both
        /// dialogs. Returned so the caller can advance its own layout by the
        /// height this actually needed, which varies with the display's scaling.
        /// </summary>
        public static Label Hint(string text, int left, int width, int top)
        {
            return new Label
            {
                Text = text,
                Left = left,
                Top = top,
                Width = width,
                AutoSize = false,
                Height = 34,
                ForeColor = SystemColors.GrayText
            };
        }
    }
}
