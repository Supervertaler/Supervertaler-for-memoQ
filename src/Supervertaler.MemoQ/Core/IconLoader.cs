using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The Supervertaler logo, loaded once from the embedded multi-resolution .ico.
    ///
    /// memoQ asks for these on the UI thread while painting its settings list, so
    /// both are cached and neither may throw — a null icon leaves a blank square,
    /// which is survivable; an exception during paint is not.
    /// </summary>
    internal static class IconLoader
    {
        private const string ResourceName = "Supervertaler.MemoQ.Resources.sv-icon.ico";

        // memoQ draws DisplayIcon at 48 logical pixels: the Translation results
        // pane stretches it to ScaleToHDPI(48) high (HitMetaControl), and the
        // MT and termbase settings lists stretch or zoom it into 40-64 px boxes.
        // A 32 px image was therefore enlarged and went soft, worst at high DPI.
        // Handing over 48 px at the screen's scale means the results pane draws
        // it 1:1 and every other place only ever shrinks it.
        private const int LargeLogicalSize = 48;

        private static readonly Lazy<Image> _large = new Lazy<Image>(() => LoadExact(ScaleToScreen(LargeLogicalSize)));
        private static readonly Lazy<Image> _small = new Lazy<Image>(() => Load(16));

        /// <summary>memoQ's DisplayIcon: the Translation results pane and the settings lists.</summary>
        public static Image Large => _large.Value;

        /// <summary>Shown against each hit in the Translation results pane.</summary>
        public static Image Small => _small.Value;

        public static Icon AppIcon
        {
            get
            {
                try
                {
                    using (var stream = Stream())
                        return stream == null ? null : new Icon(stream);
                }
                catch { return null; }
            }
        }

        private static Image Load(int size)
        {
            try
            {
                using (var stream = Stream())
                {
                    if (stream == null) return null;

                    var bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    // The .ico's frames are PNGs. Decoding the PNG directly is
                    // reliable; System.Drawing.Icon.ToBitmap was seen to turn
                    // them into noise outside memoQ, so it is only a fallback.
                    var png = PngFrame(bytes, size);
                    if (png != null)
                        return png;

                    // The .ico carries 16..256 px frames; asking for a size picks
                    // the nearest rather than rescaling a wrong one.
                    using (var ms = new MemoryStream(bytes))
                    using (var icon = new Icon(ms, new Size(size, size)))
                        return icon.ToBitmap();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Write($"IconLoader: could not load {size}px icon", ex);
                return null;
            }
        }

        /// <summary>
        /// The PNG-compressed frame of exactly <paramref name="size"/> pixels,
        /// decoded and copied into a bitmap that owns its pixels (a Bitmap made
        /// from a stream needs that stream kept open). Null when the .ico has no
        /// such frame, or it is not a PNG, so the caller can fall back.
        /// </summary>
        private static Bitmap PngFrame(byte[] ico, int size)
        {
            if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1) return null;
            int count = BitConverter.ToUInt16(ico, 4);
            for (int i = 0; i < count; i++)
            {
                int entry = 6 + 16 * i;
                if (entry + 16 > ico.Length) return null;
                int width = ico[entry] == 0 ? 256 : ico[entry];
                if (width != size) continue;

                int length = BitConverter.ToInt32(ico, entry + 8);
                int offset = BitConverter.ToInt32(ico, entry + 12);
                if (offset < 0 || length < 8 || offset + length > ico.Length) return null;
                if (ico[offset] != 0x89 || ico[offset + 1] != (byte)'P') return null;

                using (var ms = new MemoryStream(ico, offset, length))
                using (var decoded = new Bitmap(ms))
                {
                    var copy = new Bitmap(decoded.Width, decoded.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(copy))
                        g.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);
                    return copy;
                }
            }
            return null;
        }

        /// <summary>
        /// The logo at exactly <paramref name="size"/> pixels: the smallest frame
        /// of the .ico that is at least that big, shrunk with high-quality
        /// filtering. Shrinking keeps the "Sv" sharp; enlarging is what blurred it.
        /// </summary>
        private static Image LoadExact(int size)
        {
            int frame = 256;
            foreach (int candidate in new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 })
            {
                if (candidate >= size) { frame = candidate; break; }
            }

            var source = Load(frame) as Bitmap;
            if (source == null || source.Width == size) return source;

            try
            {
                var result = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(result))
                using (var attributes = new System.Drawing.Imaging.ImageAttributes())
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    // Without TileFlipXY the bicubic filter samples beyond the
                    // edge and leaves a faint grey rim around the circle.
                    attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                    g.DrawImage(source, new Rectangle(0, 0, size, size),
                        0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                }
                source.Dispose();
                return result;
            }
            catch (Exception ex)
            {
                PluginLog.Write($"IconLoader: could not resize icon to {size}px", ex);
                return source;
            }
        }

        /// <summary>
        /// Logical pixels to screen pixels at the system DPI, which is the DPI
        /// memoQ's own DPIHelper.ScaleToHDPI scales by. Falls back to 96 (no scaling).
        /// </summary>
        private static int ScaleToScreen(int logical)
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                    return Math.Max(logical, (int)Math.Round(logical * g.DpiY / 96f));
            }
            catch
            {
                return logical;
            }
        }

        private static Stream Stream()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        }
    }
}
