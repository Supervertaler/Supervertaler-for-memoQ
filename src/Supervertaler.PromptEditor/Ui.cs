using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// The window font, and the two greys the job panel draws with.
    ///
    /// <para>Why a font at all: WinForms' <c>Control.DefaultFont</c> is Microsoft
    /// Sans Serif 8.25pt, the 1998 default, and a program that never sets one gets
    /// it. That single fact was most of what made this window look old next to
    /// memoQ. <see cref="SystemFonts.MessageBoxFont"/> is what the shell itself
    /// uses for dialog text - Segoe UI 9pt on Windows 10 and 11 - so taking it
    /// means the editor follows the system rather than choosing for it, including
    /// when the user has set a larger UI font.</para>
    ///
    /// <para>Set on each Form rather than once globally because WinForms has no
    /// global font: a Form does not inherit from the form that opened it. Setting
    /// it first in the constructor lets every control created afterwards inherit
    /// it, which is why the line goes at the top rather than the bottom.</para>
    /// </summary>
    internal static class Ui
    {
        /// <summary>
        /// The shell's dialog font. Held for the life of the process rather than
        /// created per form: MessageBoxFont hands out a new Font each call, and
        /// nine forms asking for nine identical fonts is nine handles to leak.
        /// </summary>
        internal static Font Default { get; } = SystemFonts.MessageBoxFont ?? Control.DefaultFont;

        /// <summary>The border of a job-panel field at rest.</summary>
        internal static Color FieldEdge { get; } = Color.FromArgb(0xD4, 0xD4, 0xD4);

        /// <summary>The same border under the pointer, so the row says it is clickable.</summary>
        internal static Color FieldEdgeHot { get; } = Color.FromArgb(0x8C, 0x8C, 0x8C);
    }
}
