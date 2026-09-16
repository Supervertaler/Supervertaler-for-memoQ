using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Finds the assemblies this editor borrows from memoQ.
    ///
    /// <para><b>Why this is needed at all.</b> The repo rule is ship nothing:
    /// every memoQ reference is <c>Private=false</c>, so nothing of memoQ's is
    /// copied beside our binaries. That works for the plugin, which runs inside
    /// memoQ.exe and inherits its probing. It does not work for this editor,
    /// which is a separate process whose base directory is <c>Addins</c> while
    /// System.Data.SQLite.dll sits one level up in the memoQ install root -
    /// and .NET does not probe a parent directory.</para>
    ///
    /// <para>Measured 2026-09-16: without this, opening the Termbases dialog in
    /// the deployed editor threw <c>FileNotFoundException</c> for
    /// System.Data.SQLite 1.0.119.0. The plugin was unaffected, which is exactly
    /// why the harnesses did not catch it - they install a resolver of their
    /// own before loading anything.</para>
    /// </summary>
    internal static class MemoQAssemblies
    {
        // Names already looked for. A handler that calls LoadFrom re-enters
        // itself for the same name, and an unguarded one recurses until the
        // stack goes - which it duly did while this was being written.
        private static readonly HashSet<string> _tried =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _lock = new object();

        /// <summary>Install the resolver. Call before anything else in Main.</summary>
        internal static void Resolve()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var name = new AssemblyName(args.Name).Name;

                lock (_lock)
                {
                    if (!_tried.Add(name)) return null;
                }

                var root = MemoQRoot();
                if (root == null) return null;

                var candidate = Path.Combine(root, name + ".dll");
                if (!File.Exists(candidate)) return null;

                try
                {
                    return Assembly.LoadFrom(candidate);
                }
                catch
                {
                    // A failure here is one feature not working, never a
                    // failure to start: the editor's other eight panes have
                    // nothing to do with memoQ's assemblies.
                    return null;
                }
            };
        }

        /// <summary>
        /// memoQ's install directory, which is the parent of our own - the
        /// editor deploys into <c>Addins</c> because that is where the options
        /// dialog looks for it, not because memoQ ever loads it.
        /// </summary>
        private static string MemoQRoot()
        {
            try
            {
                var here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(here)) return null;

                var parent = Path.GetDirectoryName(here);
                return string.IsNullOrEmpty(parent) ? null : parent;
            }
            catch
            {
                return null;
            }
        }
    }
}
