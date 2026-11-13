using System;
using System.Collections.Specialized;
using System.Net;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The query of a bridge request, read as UTF-8.
    ///
    /// <para>.NET Framework's <c>HttpListenerRequest.QueryString</c> decodes
    /// percent-escapes with the request's content encoding, which for a GET is
    /// the computer's own code page, not UTF-8. The MCP server sends UTF-8, so
    /// every letter outside ASCII arrived as two wrong characters: a term with
    /// an accent was never found, and neither was a document or a memory bank
    /// named with one. The Trados bridge reads its queries the same way as this
    /// (its QueryUtf8).</para>
    ///
    /// <para>Nothing here throws. An escape that is not valid UTF-8 never becomes
    /// a guessed letter, so it matches nothing - the honest answer.</para>
    /// </summary>
    internal static class BridgeQuery
    {
        /// <summary>
        /// The values of <paramref name="rawUrl"/>'s query. Names ignore case, a
        /// missing name answers null, and a name given twice answers both values
        /// joined by a comma - all as <c>HttpListenerRequest.QueryString</c> does.
        /// </summary>
        public static NameValueCollection Parse(string rawUrl)
        {
            var result = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(rawUrl)) return result;

            var start = rawUrl.IndexOf('?');
            if (start < 0) return result;

            var query = rawUrl.Substring(start + 1);
            var fragment = query.IndexOf('#');
            if (fragment >= 0) query = query.Substring(0, fragment);

            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                var name = eq >= 0 ? pair.Substring(0, eq) : pair;
                var value = eq >= 0 ? pair.Substring(eq + 1) : "";
                result.Add(Decode(name), Decode(value));
            }

            return result;
        }

        private static string Decode(string escaped)
        {
            // The framework's own decoder: UTF-8, a plus is a space (as it was
            // before; a real plus arrives as %2B from every encoder), and it
            // never throws. Not Uri.UnescapeDataString, which is what the Trados
            // bridge uses: measured here, it takes 8 seconds on a 340,000-character
            // value where this takes milliseconds. Windows refuses an address
            // past about 16,000 characters, so that could not have been reached,
            // but a limit somebody else sets is not one to build on.
            try { return WebUtility.UrlDecode(escaped) ?? ""; }
            catch (Exception) { return escaped; }
        }
    }
}
