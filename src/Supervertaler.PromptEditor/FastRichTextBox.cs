using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// A <see cref="RichTextBox"/> hosted on RichEdit 4.1+ (msftedit.dll,
    /// window class RICHEDIT50W) instead of the RichEdit 2.0 control WinForms
    /// picks by default.
    ///
    /// The API is identical; the difference is painting. The old control
    /// repaints the visible area from scratch on every scroll notch, and once a
    /// document carries a few hundred formatting runs — every bold span, inline
    /// code and placeholder in a 26 KB prompt is one — a mouse-wheel scroll
    /// visibly stutters. RichEdit 4.1 caches layout and repaints only what
    /// moved, which is the whole of the fix: no code above this class changes.
    ///
    /// Falls back to the default class if msftedit.dll cannot be loaded, which
    /// on any Windows since XP SP1 it can.
    /// </summary>
    internal sealed class FastRichTextBox : RichTextBox
    {
        private static readonly IntPtr MsftEdit = LoadLibrary("msftedit.dll");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (MsftEdit != IntPtr.Zero) cp.ClassName = "RICHEDIT50W";
                return cp;
            }
        }
    }
}
