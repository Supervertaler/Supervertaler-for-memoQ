using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Markdown-aware colouring for a <see cref="RichTextBox"/>, plus the part
    /// that actually matters here: placeholder validation.
    ///
    /// Not a Markdown renderer. Prompts are read by a language model, not a
    /// browser, so what an author needs to see is the structure they are writing
    /// — headings, emphasis, lists, code — while still editing the literal text
    /// the model will receive. Rendering it to formatted output would hide the
    /// characters that do the work.
    /// </summary>
    internal static class MarkdownHighlighter
    {
        // WM_SETREDRAW around the re-colour. Without it the control repaints on
        // every one of the several hundred selection changes a highlight pass
        // makes, which reads as a full-window flicker on each keystroke.
        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETSCROLLPOS = 0x04DD;
        private const int EM_SETSCROLLPOS = 0x04DE;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

        public static readonly Color HeadingColor     = Color.FromArgb(0x1F, 0x4E, 0x79);
        public static readonly Color EmphasisColor    = Color.FromArgb(0x20, 0x20, 0x20);
        public static readonly Color CodeColor        = Color.FromArgb(0xA3, 0x15, 0x15);
        public static readonly Color ListMarkerColor  = Color.FromArgb(0x88, 0x66, 0x00);
        public static readonly Color QuoteColor       = Color.FromArgb(0x50, 0x50, 0x50);
        public static readonly Color PlaceholderColor = Color.FromArgb(0x0B, 0x5C, 0xAD);
        public static readonly Color UnknownColor     = Color.FromArgb(0xC0, 0x00, 0x00);

        // Heading, blockquote and list marker are anchored to line starts.
        private static readonly Regex Heading   = new Regex(@"^\#{1,6}[ \t].*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex Quote     = new Regex(@"^>.*$",            RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex ListItem  = new Regex(@"^[ \t]*([-*+]|\d+\.)[ \t]", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex Fence     = new Regex(@"^```.*?^```", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex InlineCode= new Regex(@"`[^`\r\n]+`",  RegexOptions.Compiled);
        private static readonly Regex Bold      = new Regex(@"\*\*[^\*\r\n]+\*\*", RegexOptions.Compiled);
        private static readonly Regex Italic    = new Regex(@"(?<![\*\w])\*[^\*\r\n]+\*(?![\*\w])", RegexOptions.Compiled);

        /// <summary>Both placeholder dialects: {{UPPER_SNAKE}} and the legacy {lower_snake}.</summary>
        private static readonly Regex Placeholder = new Regex(@"\{\{[A-Za-z_][A-Za-z0-9_]*\}\}|\{[a-z_][a-z0-9_]*\}", RegexOptions.Compiled);

        /// <summary>
        /// Re-colours the whole control and returns the placeholder tokens found,
        /// in document order, with duplicates kept — the caller reports on them.
        /// </summary>
        public static List<string> Apply(RichTextBox box, Font baseFont)
        {
            var found = new List<string>();
            var text = box.Text ?? "";

            var selStart = box.SelectionStart;
            var selLength = box.SelectionLength;
            var scroll = new Point();
            SendMessage(box.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);
            SendMessage(box.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);

            try
            {
                box.SelectAll();
                box.SelectionColor = box.ForeColor;
                box.SelectionFont = baseFont;

                var headingFont = new Font(baseFont, FontStyle.Bold);
                foreach (Match m in Heading.Matches(text))
                    Style(box, m, HeadingColor, headingFont);

                foreach (Match m in Quote.Matches(text))
                    Style(box, m, QuoteColor, new Font(baseFont, FontStyle.Italic));

                foreach (Match m in ListItem.Matches(text))
                    Style(box, m.Groups[1], ListMarkerColor, new Font(baseFont, FontStyle.Bold));

                foreach (Match m in Fence.Matches(text))
                    Style(box, m, CodeColor, baseFont);

                foreach (Match m in InlineCode.Matches(text))
                    Style(box, m, CodeColor, baseFont);

                foreach (Match m in Bold.Matches(text))
                    Style(box, m, EmphasisColor, new Font(baseFont, FontStyle.Bold));

                foreach (Match m in Italic.Matches(text))
                    Style(box, m, EmphasisColor, new Font(baseFont, FontStyle.Italic));

                // Placeholders last so they win over anything they sit inside —
                // a placeholder in a bulleted line is still a placeholder, and
                // whether it is spelled correctly matters more than the bullet.
                foreach (Match m in Placeholder.Matches(text))
                {
                    found.Add(m.Value);
                    var known = Placeholders.IsKnown(m.Value);
                    Style(box, m, known ? PlaceholderColor : UnknownColor,
                          new Font(baseFont, FontStyle.Bold));
                }
            }
            finally
            {
                box.SelectionStart = selStart;
                box.SelectionLength = selLength;
                box.SelectionColor = box.ForeColor;
                box.SelectionFont = baseFont;
                box.SelectionStart = selStart;
                box.SelectionLength = selLength;

                SendMessage(box.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);
                SendMessage(box.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                box.Invalidate();
            }

            return found;
        }

        private static void Style(RichTextBox box, Capture c, Color color, Font font)
        {
            box.SelectionStart = c.Index;
            box.SelectionLength = c.Length;
            box.SelectionColor = color;
            box.SelectionFont = font;
        }
    }
}
