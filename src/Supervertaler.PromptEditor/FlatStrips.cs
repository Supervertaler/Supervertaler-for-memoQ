using System.Drawing;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// A flat look for the menu, toolbar and context bar.
    ///
    /// <para>WinForms' default is <see cref="ToolStripProfessionalRenderer"/>
    /// over the system colour table, which draws the blue-grey vertical
    /// gradients of Office 2003. Nothing else on a current Windows desktop looks
    /// like that any more, and it is most of why this window read as older than
    /// it is.</para>
    ///
    /// <para>Done by replacing the colour table rather than by overriding the
    /// paint methods. The renderer's own layout - where a check mark goes, how
    /// an overflow chevron is drawn, what happens at high DPI - is code worth
    /// keeping; only its palette was the problem.</para>
    /// </summary>
    internal sealed class FlatStrips : ToolStripProfessionalRenderer
    {
        public FlatStrips() : base(new FlatColours())
        {
            // Square corners. Rounded ones on a strip that spans the window read
            // as a floating panel rather than as part of the frame.
            RoundedEdges = false;
        }

        /// <summary>
        /// The renderer draws a border along every edge of a strip. Only the one
        /// between the strip and what is under it is wanted; the rest boxed each
        /// strip in and turned three stacked bars into three stacked panels.
        /// </summary>
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
            {
                base.OnRenderToolStripBorder(e);
                return;
            }

            using (var pen = new Pen(FlatColours.Hairline))
            {
                var y = e.AffectedBounds.Height - 1;
                e.Graphics.DrawLine(pen, 0, y, e.AffectedBounds.Width, y);
            }
        }

        /// <summary>
        /// The palette. Derived from the system colours rather than hardcoded, so
        /// a high-contrast theme or a dark one still produces readable chrome
        /// instead of a hand-picked light grey with light grey text on it.
        /// </summary>
        private sealed class FlatColours : ProfessionalColorTable
        {
            internal static Color Surface => SystemColors.Control;

            internal static Color Hairline => Blend(SystemColors.ControlDark, SystemColors.Control, 0.45);

            /// <summary>The hover wash: the accent colour, well diluted.</summary>
            private static Color Hover => Blend(SystemColors.Highlight, SystemColors.Window, 0.14);

            private static Color Pressed => Blend(SystemColors.Highlight, SystemColors.Window, 0.24);

            private static Color Blend(Color a, Color b, double weightOfA)
            {
                int Mix(int x, int y) => (int)System.Math.Round(x * weightOfA + y * (1 - weightOfA));
                return Color.FromArgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
            }

            // -- the strips themselves ------------------------------------------

            public override Color ToolStripGradientBegin => Surface;
            public override Color ToolStripGradientMiddle => Surface;
            public override Color ToolStripGradientEnd => Surface;
            public override Color ToolStripBorder => Hairline;
            public override Color ToolStripContentPanelGradientBegin => Surface;
            public override Color ToolStripContentPanelGradientEnd => Surface;
            public override Color ToolStripPanelGradientBegin => Surface;
            public override Color ToolStripPanelGradientEnd => Surface;

            public override Color MenuStripGradientBegin => Surface;
            public override Color MenuStripGradientEnd => Surface;

            // -- buttons ---------------------------------------------------------

            public override Color ButtonSelectedHighlight => Hover;
            public override Color ButtonSelectedHighlightBorder => Hairline;
            public override Color ButtonSelectedGradientBegin => Hover;
            public override Color ButtonSelectedGradientMiddle => Hover;
            public override Color ButtonSelectedGradientEnd => Hover;
            public override Color ButtonSelectedBorder => Hairline;

            public override Color ButtonPressedHighlight => Pressed;
            public override Color ButtonPressedGradientBegin => Pressed;
            public override Color ButtonPressedGradientMiddle => Pressed;
            public override Color ButtonPressedGradientEnd => Pressed;
            public override Color ButtonPressedBorder => Hairline;

            // A checked button - the MCP toggle - has to read as ON at a glance,
            // so it is the pressed wash and stays there rather than the hover one.
            public override Color ButtonCheckedHighlight => Pressed;
            public override Color ButtonCheckedGradientBegin => Pressed;
            public override Color ButtonCheckedGradientMiddle => Pressed;
            public override Color ButtonCheckedGradientEnd => Pressed;

            public override Color CheckBackground => Pressed;
            public override Color CheckSelectedBackground => Pressed;
            public override Color CheckPressedBackground => Pressed;

            // -- menus -----------------------------------------------------------

            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Surface;
            public override Color MenuItemPressedGradientMiddle => Surface;
            public override Color MenuItemPressedGradientEnd => Surface;
            public override Color MenuItemBorder => Hairline;
            public override Color MenuBorder => Hairline;

            public override Color ImageMarginGradientBegin => Surface;
            public override Color ImageMarginGradientMiddle => Surface;
            public override Color ImageMarginGradientEnd => Surface;

            public override Color SeparatorDark => Hairline;
            public override Color SeparatorLight => Surface;

            public override Color OverflowButtonGradientBegin => Surface;
            public override Color OverflowButtonGradientMiddle => Surface;
            public override Color OverflowButtonGradientEnd => Surface;
        }
    }
}
