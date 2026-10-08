using System;
using System.IO;
using System.Text;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// File.ReadAllText / ReadAllLines / WriteAllText for the settings files the
    /// plugin (inside memoQ) and the editor share, done through core's
    /// <see cref="global::Supervertaler.Core.AtomicFile"/>.
    ///
    /// <para>Both processes read and write these files. Written in place, a read
    /// that landed mid-write saw half a file - and shared.txt is read, changed by
    /// one line and written back, so the half file was then saved as the whole
    /// one and the rest of the settings were gone. AtomicFile writes a temporary
    /// file and swaps it in, so a reader sees the old file or the new one; and
    /// reads share delete access, so a read never blocks a write.</para>
    ///
    /// <para>Same contract as the File methods it replaces, including throwing
    /// when a write could not be made, so every caller's existing error handling
    /// still applies. The previous file is then left exactly as it was.</para>
    ///
    /// <para>Not a lock: two processes changing different keys at the same
    /// instant can still each write back the file they read, and one change is
    /// lost. That needs two saves within the same few milliseconds; a torn file
    /// needed only a read during a save.</para>
    /// </summary>
    internal static class SettingsFile
    {
        private static readonly TimeSpan AbandonedTempAge = TimeSpan.FromMinutes(10);

        public static string ReadAllText(string path) =>
            global::Supervertaler.Core.AtomicFile.ReadAllText(path);

        public static string[] ReadAllLines(string path) =>
            ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        /// <summary>Writes <paramref name="text"/> as UTF-8, with or without a byte-order mark, in one step.</summary>
        public static void WriteAllText(string path, string text, bool byteOrderMark)
        {
            var encoding = new UTF8Encoding(byteOrderMark);
            var body = encoding.GetBytes(text ?? string.Empty);
            var preamble = encoding.GetPreamble();
            var bytes = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);

            // Leftovers of a process killed mid-write; its own are removed by Write.
            global::Supervertaler.Core.AtomicFile.SweepAbandoned(path, AbandonedTempAge);

            string why = null;
            var outcome = global::Supervertaler.Core.AtomicFile.Write(path, bytes, replaceExisting: true, log: m => why = m);
            if (outcome != global::Supervertaler.Core.AtomicFile.Outcome.Written)
                throw new IOException(why ?? Path.GetFileName(path) + " was not written.");
        }
    }
}
