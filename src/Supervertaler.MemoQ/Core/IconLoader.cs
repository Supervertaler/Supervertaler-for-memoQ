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

        private static readonly Lazy<Image> _large = new Lazy<Image>(() => Load(32));
        private static readonly Lazy<Image> _small = new Lazy<Image>(() => Load(16));

        /// <summary>Shown next to the engine name in the MT settings list.</summary>
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

                    // The .ico carries 16..256 px frames; asking for a size picks
                    // the nearest rather than rescaling a wrong one.
                    using (var icon = new Icon(stream, new Size(size, size)))
                        return icon.ToBitmap();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Write($"IconLoader: could not load {size}px icon", ex);
                return null;
            }
        }

        private static Stream Stream()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        }
    }
}
