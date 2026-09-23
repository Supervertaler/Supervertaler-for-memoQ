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

        /// <summary>
        /// The one colour in the window, and it means one thing: this is the item
        /// in use right now.
        ///
        /// <para>memoQ's own vermillion is #F05137, which is 3.5:1 on white and
        /// fails as text. This is the same colour darkened to 5.1:1 - the tint the
        /// product site uses for memoQ wherever the brand colour has to be read
        /// rather than just seen.</para>
        /// </summary>
        internal static Color Accent { get; } = Color.FromArgb(0xC9, 0x3A, 0x25);

        /// <summary>
        /// Something to check, not something wrong: a dark amber, about 5.9:1 on
        /// white, so it reads as text rather than decoration. Kept apart from the
        /// accent, which means "in use", and from red, which means an error - the
        /// project heading uses it when the project shown may be an earlier memoQ
        /// session's (#8). Under a high-contrast theme it gives way to the system
        /// text colour, which is the one guaranteed to be readable there.
        /// </summary>
        internal static Color Caution =>
            SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(0x8A, 0x5A, 0x00);

        /// <summary>
        /// The chrome: the toolbar, the menu, the band at the top and the window
        /// ground behind them. The tree and the editor stay white.
        ///
        /// <para>The cue is memoQ itself, which paints its frame a pale blue-grey,
        /// puts white panels on it and holds its orange back for the brand. That is
        /// why its dashboard does not read as grey although nearly all of it is
        /// neutral, and it is a better answer than tinting the chrome orange - which
        /// spends the accent on surfaces that mean nothing.</para>
        ///
        /// <para>Conditional, not hardcoded: the strip palette this joins is
        /// deliberately derived from the system colours so that a high-contrast or
        /// dark theme still gets readable chrome. Under one of those this returns
        /// the system colour and the tint never appears.</para>
        /// </summary>
        internal static Color Chrome
        {
            get
            {
                var w = SystemColors.Window;
                var light = w.R + w.G + w.B > 700;
                return light ? Color.FromArgb(0xED, 0xF1, 0xF8) : SystemColors.Control;
            }
        }
        /// <summary>The border of a job-panel field at rest.</summary>
        internal static Color FieldEdge { get; } = Color.FromArgb(0xD4, 0xD4, 0xD4);

        /// <summary>The same border under the pointer, so the row says it is clickable.</summary>
        internal static Color FieldEdgeHot { get; } = Color.FromArgb(0x8C, 0x8C, 0x8C);
    }
}
