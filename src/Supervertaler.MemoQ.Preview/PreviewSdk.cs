using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Supervertaler.MemoQ.Preview
{
    /// <summary>
    /// memoQ's Preview SDK, borrowed from where memoQ put it rather than copied
    /// into our own folder.
    ///
    /// <para><c>MemoQ.PreviewInterfaces.dll</c> is not part of memoQ. It ships
    /// inside memoQ's own preview tools - the PDF Preview tool, a free public
    /// download from memoQ's site - and whether we may redistribute it has never
    /// been answered. So we do not: the PDF Preview tool is a prerequisite, and
    /// this tool loads the assembly out of wherever that installed it. Nothing of
    /// memoQ's is shipped, and the download stays on memoQ's website, which is
    /// where a memoQ component belongs.</para>
    ///
    /// <para>The same goes for the four libraries the SDK brings with it. Two of
    /// them are Microsoft's and one is Newtonsoft's, all freely redistributable,
    /// so shipping those would be allowed - but borrowing them costs nothing and
    /// guarantees the versions are the ones the SDK was built against, which
    /// shipping our own would not.</para>
    ///
    /// <para>This is the same arrangement the memoQ add-in already lives under:
    /// every memoQ reference is marked not-private and resolved out of memoQ's
    /// own directory at run time.</para>
    /// </summary>
    internal static class PreviewSdk
    {
        /// <summary>Where memoQ's docs send people for the tool.</summary>
        public const string Download =
            "https://docs.memoq.com/current/en/memoQ-PDF-preview-tool/memoq-pdf-preview-tool.html";

        /// <summary>The one assembly that is actually memoQ's.</summary>
        private const string TheirAssembly = "MemoQ.PreviewInterfaces";

        private static string _directory;
        private static bool _looked;

        /// <summary>
        /// The folder holding the Preview SDK, or null when the PDF Preview tool
        /// is not installed. Looked for once.
        /// </summary>
        public static string Directory
        {
            get
            {
                if (!_looked) { _looked = true; _directory = Find(); }
                return _directory;
            }
        }

        /// <summary>
        /// Installs the resolver. Must run before anything touches a type from
        /// these assemblies, which in practice means the first line of Main and
        /// a separate method for everything after it - the runtime resolves the
        /// types a method mentions when it compiles that method, not when the
        /// line using them is reached.
        /// </summary>
        public static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        /// <summary>
        /// Names currently being loaded by this handler.
        ///
        /// <para>Without it, a file that exists under the requested name but does
        /// not satisfy the request - wrong identity, wrong architecture, a
        /// truncated download - makes LoadFrom raise this same event for the same
        /// name, which calls LoadFrom again, for ever. The process then dies of a
        /// StackOverflowException, which cannot be caught and prints nothing, so
        /// it looks exactly like being killed from outside.</para>
        /// </summary>
        private static readonly HashSet<string> InFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = null;

            try
            {
                var directory = Directory;
                if (directory == null) return null;

                // The simple name: "MemoQ.PreviewInterfaces, Version=1.0.0.0, ..."
                name = new AssemblyName(args.Name).Name;

                lock (InFlight) if (!InFlight.Add(name)) return null;

                var path = Path.Combine(directory, name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            }
            catch
            {
                // Returning null means "not mine", which is the right answer when
                // we cannot tell. Throwing from here would take down the load.
                return null;
            }
            finally
            {
                // In a finally, not after the load: an assembly that throws on
                // load would otherwise leave its name blocked for the rest of the
                // session, and the next honest request for it would be refused.
                if (name != null) lock (InFlight) InFlight.Remove(name);
            }
        }

        /// <summary>
        /// Where the PDF Preview tool installed itself. Both Program Files roots
        /// are searched, and any memoQ folder whose name mentions a preview, so
        /// that a tool named for a different preview kind - or a version stamp,
        /// the way memoQ stamps its own folders - is still found.
        /// </summary>
        private static string Find()
        {
            foreach (var root in Roots())
            {
                if (string.IsNullOrEmpty(root)) continue;

                var memoq = Path.Combine(root, "memoQ");
                if (!System.IO.Directory.Exists(memoq)) continue;

                // The known name first, so the ordinary machine costs one check.
                var known = Path.Combine(memoq, "memoQ PDF Preview");
                if (Holds(known)) return known;

                try
                {
                    foreach (var candidate in System.IO.Directory.GetDirectories(memoq))
                        if (candidate.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0 && Holds(candidate))
                            return candidate;
                }
                catch { }
            }

            return null;
        }

        private static string[] Roots()
        {
            return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
        }

        private static bool Holds(string directory)
        {
            return !string.IsNullOrEmpty(directory)
                && File.Exists(Path.Combine(directory, TheirAssembly + ".dll"));
        }
    }
}
