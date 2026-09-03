using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// The Supervertaler mark, for every window this program opens.
    ///
    /// It used to come from <c>Icon.ExtractAssociatedIcon</c>, which reads one
    /// frame out of the exe and hands back a single size Windows then rescales —
    /// and only the main window asked for it at all, so every dialog wore the
    /// default WinForms icon. Reading the multi-resolution .ico directly gives
    /// Windows the frame it actually wants, at whatever DPI the user runs.
    ///
    /// Never throws. A window with the wrong icon is a blemish; a window that
    /// fails to open because of one is a bug.
    /// </summary>
    internal static class AppIcon
    {
        private const string ResourceName = "Supervertaler.PromptEditor.Resources.sv-icon.ico";

        private static readonly Lazy<Icon> _icon = new Lazy<Icon>(Load);

        /// <summary>The window icon, or null when it could not be read.</summary>
        public static Icon Value => _icon.Value;

        /// <summary>Give a window the mark. Safe to call on any form.</summary>
        public static void Apply(Form form)
        {
            var icon = Value;
            if (form != null && icon != null) form.Icon = icon;
        }

        private static Icon Load()
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                    return stream == null ? null : new Icon(stream);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
