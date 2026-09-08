using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Where memoQ keeps its projects, asked of memoQ rather than assumed.
    ///
    /// <para>Two files under <c>C:\Users\&lt;you&gt;\AppData\Roaming\MemoQ</c>, both
    /// plain XML:</para>
    ///
    /// <list type="bullet">
    /// <item><c>ProjectRegistry.dat</c> – one block per project, carrying its
    /// <c>Name</c>, its <c>ID</c> and its <c>ProjectFolderFullPath</c>. The ID is
    /// the same GUID memoQ sends an MT plugin as <c>ProjectGuid</c>, verified
    /// against a project the plugin had already recorded. This is the precise
    /// source: it names each project's actual folder, so it copes with projects
    /// in several places at once, which is what a moved projects folder leaves
    /// behind.</item>
    /// <item><c>Preferences.xml</c> – <c>ProjectsCustomPath</c>, the folder set
    /// under Options → Locations → Projects when the user has chosen one. Empty
    /// when they have left it at the default.</item>
    /// </list>
    ///
    /// <para>The default, for anyone wondering, is
    /// <c>C:\Users\&lt;you&gt;\Documents\My memoQ projects</c> – the user profile,
    /// not Program Files.</para>
    ///
    /// <para>Registry rows go stale: a project folder that was moved by hand, or
    /// deleted, stays listed. Every path is therefore existence-checked at the
    /// point of use rather than trusted.</para>
    /// </summary>
    internal static class MemoQProjects
    {
        internal sealed class Entry
        {
            public string Name;
            public Guid Id;
            public string Folder;
        }

        /// <summary>Overridable so a harness can point at files of its own.</summary>
        internal static string RegistryPathOverride;
        internal static string PreferencesPathOverride;

        private static string AppData =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemoQ");

        internal static string RegistryPath => RegistryPathOverride ?? Path.Combine(AppData, "ProjectRegistry.dat");
        internal static string PreferencesPath => PreferencesPathOverride ?? Path.Combine(AppData, "Preferences.xml");

        internal static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My memoQ Projects");

        private static readonly object _lock = new object();
        private static List<Entry> _entries;
        private static long _length = -1;
        private static DateTime _stamp;

        private static readonly Regex BlockPattern =
            new Regex("<ProjectAdminInfoBlock>(.*?)</ProjectAdminInfoBlock>", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>Every project memoQ has registered. Empty when the file cannot be read; never throws.</summary>
        public static IList<Entry> Registered()
        {
            lock (_lock)
            {
                try
                {
                    var path = RegistryPath;
                    if (!File.Exists(path)) { _entries = new List<Entry>(); _length = -1; return _entries; }

                    var info = new FileInfo(path);
                    if (_entries != null && info.Length == _length && info.LastWriteTimeUtc == _stamp) return _entries;

                    var text = File.ReadAllText(path);
                    var list = new List<Entry>();

                    foreach (Match block in BlockPattern.Matches(text))
                    {
                        var body = block.Groups[1].Value;
                        var folder = Value(body, "ProjectFolderFullPath");
                        if (string.IsNullOrWhiteSpace(folder)) continue;

                        Guid id;
                        Guid.TryParse(Value(body, "ID") ?? "", out id);

                        list.Add(new Entry
                        {
                            Name = Value(body, "Name"),
                            Id = id,
                            Folder = folder.Trim()
                        });
                    }

                    _entries = list;
                    _length = info.Length;
                    _stamp = info.LastWriteTimeUtc;
                    return _entries;
                }
                catch (Exception ex)
                {
                    PluginLog.Write("MemoQProjects: could not read " + RegistryPath, ex);
                    return new List<Entry>();
                }
            }
        }

        /// <summary>
        /// The folders to look in for a project the registry does not list: the
        /// custom projects folder when one is set, and the default either way -
        /// a user who has just switched to a custom folder still has yesterday's
        /// projects in the old one.
        /// </summary>
        public static IList<string> Roots()
        {
            var roots = new List<string>();

            try
            {
                var prefs = PreferencesPath;
                if (File.Exists(prefs))
                {
                    var custom = Value(File.ReadAllText(prefs), "ProjectsCustomPath");
                    if (!string.IsNullOrWhiteSpace(custom)) roots.Add(custom.Trim());
                }
            }
            catch (Exception ex)
            {
                PluginLog.Write("MemoQProjects: could not read " + PreferencesPath, ex);
            }

            roots.Add(DefaultRoot);

            return roots
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>First value of an element, unescaped enough for a path or a name.</summary>
        private static string Value(string xml, string element)
        {
            var m = Regex.Match(xml ?? "", "<" + element + ">([^<]*)</" + element + ">");
            if (!m.Success) return null;
            return m.Groups[1].Value
                .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                .Replace("&quot;", "\"").Replace("&apos;", "'");
        }
    }
}
