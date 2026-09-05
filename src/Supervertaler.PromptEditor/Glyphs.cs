using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Toolbar icons, drawn from the icon font Windows already has.
    ///
    /// <para>Windows 11 ships Segoe Fluent Icons and Windows 10 shipped Segoe
    /// MDL2 Assets, so the glyphs are on the machine already: no image files to
    /// keep in step with a theme, no NuGet package, nothing to redistribute.
    /// That last point is why this is possible here at all - the plugin may ship
    /// nothing, because it loads into memoQ's own process and would compete with
    /// memoQ's copy of any library, but the editor is a separate program memoQ
    /// never loads.</para>
    ///
    /// <para>A codepoint the installed font does not have is NOT blank: GDI+
    /// falls back to another font and draws its "missing glyph" box, which has
    /// ink and looks deliberate enough to survive a glance. Two approaches to
    /// spotting that were tried and thrown away - checking for ink, and
    /// comparing against the same character in Segoe UI - because the first
    /// cannot see a fallback box at all and the second is defeated by the two
    /// fonts having different metrics, so a shared fallback glyph still lands a
    /// pixel apart.</para>
    ///
    /// <para>So ask the font. WPF's GlyphTypeface exposes the font's own
    /// character-to-glyph map, which is the actual answer rather than an
    /// inference from what was drawn. PresentationCore ships with the .NET
    /// Framework, so this is a reference rather than a dependency - and this is
    /// the editor, a program of ours, not the plugin that loads into memoQ's
    /// process and may carry nothing.</para>
    /// </summary>
    internal static class Glyphs
    {
        // Segoe MDL2 codepoints, which Segoe Fluent Icons carries too. Named for
        // what they are used for here rather than for the font's own names, since
        // the font's names ("Diagnostic", "Library") mean nothing at the call site.
        public const string NewPrompt   = "\uE710";   // Add
        public const string Save        = "\uE74E";   // Save
        public const string Placeholder = "\uE943";   // Code
        public const string AutoPrompt  = "\uE945";   // LightningBolt
        public const string Mcp         = "\uE8F2";   // ChatBubbles
        public const string Activity    = "\uE9D9";   // Diagnostic
        public const string Settings    = "\uE713";   // Settings
        public const string Prompt      = "\uE8A5";   // Document
        public const string Glossary    = "\uE8FD";   // List
        public const string Bank        = "\uE8F1";   // Library

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Image> _cache = new Dictionary<string, Image>();
        private static string _family;
        private static bool _familyResolved;

        /// <summary>
        /// The icon font on this machine, or null when neither is installed - in
        /// which case every caller gets null and the UI stays text-only.
        /// </summary>
        private static string Family
        {
            get
            {
                lock (_lock)
                {
                    if (_familyResolved) return _family;
                    _familyResolved = true;

                    foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                    {
                        try
                        {
                            using (var f = new Font(name, 12f))
                            {
                                // A missing family silently substitutes another
                                // font rather than throwing, so the name has to be
                                // compared back.
                                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                                {
                                    _family = name;
                                    return _family;
                                }
                            }
                        }
                        catch (Exception) { }
                    }

                    return _family;
                }
            }
        }

        /// <summary>
        /// One glyph as an image, or null when it cannot be drawn. Cached per
        /// glyph, size and colour: a toolbar rebuild must not redraw them, and
        /// there are ten of them for the life of the process.
        /// </summary>
        public static Image Render(string glyph, Color colour, int size)
        {
            if (string.IsNullOrEmpty(glyph) || Family == null || size <= 0) return null;
            if (!FontHas(glyph)) return null;

            var key = glyph + "|" + size + "|" + colour.ToArgb();
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached)) return cached;

                Image image = null;
                try { image = Draw(glyph, colour, size); }
                catch (Exception) { }

                _cache[key] = image;
                return image;
            }
        }

        private static Image Draw(string glyph, Color colour, int size)
        {
            // Drawn at about two-thirds of the box so the icon sits at the weight
            // of the text beside it rather than shouting over it.
            var bitmap = DrawWith(glyph, colour, size, Family);
            if (bitmap == null) return null;

            return IsBlank(bitmap) ? Disposed(bitmap) : (Image)bitmap;
        }

        private static Bitmap DrawWith(string glyph, Color colour, int size, string family)
        {
            var bitmap = new Bitmap(size, size);

            try
            {
                using (var font = new Font(family, size * 0.62f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(colour))
                using (var g = Graphics.FromImage(bitmap))
                using (var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                })
                {
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
                }

                return bitmap;
            }
            catch (Exception)
            {
                bitmap.Dispose();
                return null;
            }
        }

        private static Image Disposed(Bitmap bitmap)
        {
            bitmap.Dispose();
            return null;
        }

        private static bool IsBlank(Bitmap bitmap)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                for (var y = 0; y < bitmap.Height; y++)
                {
                    if (bitmap.GetPixel(x, y).A > 8) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Whether the icon font actually carries this codepoint, from the font's
        /// own character map. False also when the map cannot be read, which is
        /// the safe way round: no icon rather than a box.
        /// </summary>
        private static bool FontHas(string glyph)
        {
            if (string.IsNullOrEmpty(glyph) || Family == null) return false;

            try
            {
                var typeface = new System.Windows.Media.Typeface(Family);
                if (!typeface.TryGetGlyphTypeface(out var gt)) return false;

                foreach (var c in glyph)
                {
                    if (!gt.CharacterToGlyphMap.ContainsKey(c)) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The icon size for a given display scaling, in pixels.</summary>
        public static int SizeFor(int deviceDpi) => Math.Max(16, (int)Math.Round(16 * (deviceDpi / 96.0)));
    }
}
