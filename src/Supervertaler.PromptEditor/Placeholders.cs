using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// The placeholder vocabulary a prompt may use, and — more usefully — which
    /// of them each host actually fills in.
    ///
    /// This is the reason the editor is worth having rather than editing the
    /// files in Notepad. <c>PromptLibrary.ApplyVariables</c> substitutes every
    /// placeholder it knows, and substitutes an empty string for the ones the
    /// caller had no value for. So a placeholder the host cannot supply does not
    /// survive into the request as literal text where you would notice it — it
    /// silently becomes nothing, and the instruction around it quietly stops
    /// meaning anything.
    ///
    /// memoQ is the sharp case. Its plugin resolves prompts through the
    /// two-argument overload, so it fills the language pair and nothing else: a
    /// prompt written around <c>{{SOURCE_SEGMENT}}</c> works in Trados and
    /// degrades to a blank in memoQ. The source segment is not missing there —
    /// it travels in the numbered batch request instead — so the fix is to write
    /// the prompt differently, which you can only do if you know.
    /// </summary>
    internal static class Placeholders
    {
        internal sealed class Info
        {
            public string Token;
            public string Meaning;

            /// <summary>False when the memoQ plugin leaves this one empty.</summary>
            public bool FilledByMemoQ;

            /// <summary>Shown greyed in the insert menu; still valid, still substituted.</summary>
            public bool Legacy;
        }

        /// <summary>
        /// Every token <c>ApplyVariables</c> replaces, in the order it replaces
        /// them. Kept deliberately close to that method: if a token is added
        /// there and not here the editor will flag it red as unknown, which is
        /// a visible failure rather than a silent one.
        /// </summary>
        public static readonly List<Info> All = new List<Info>
        {
            new Info { Token = "{{SOURCE_LANGUAGE}}",     Meaning = "Source language name",                    FilledByMemoQ = true  },
            new Info { Token = "{{TARGET_LANGUAGE}}",     Meaning = "Target language name",                    FilledByMemoQ = true  },
            new Info { Token = "{{SOURCE_SEGMENT}}",      Meaning = "The segment being translated",            FilledByMemoQ = false },
            new Info { Token = "{{TARGET_SEGMENT}}",      Meaning = "The current target text",                 FilledByMemoQ = false },
            new Info { Token = "{{SELECTION}}",           Meaning = "Text selected in the editor",             FilledByMemoQ = false },
            new Info { Token = "{{SURROUNDING_SEGMENTS}}",Meaning = "Segments either side of this one",        FilledByMemoQ = false },
            new Info { Token = "{{TM_MATCHES}}",          Meaning = "Formatted TM fuzzy matches",              FilledByMemoQ = false },
            new Info { Token = "{{PROJECT_NAME}}",        Meaning = "Name of the project",                     FilledByMemoQ = false },
            new Info { Token = "{{DOCUMENT_NAME}}",       Meaning = "Name of the document",                     FilledByMemoQ = false },
            new Info { Token = "{{PROJECT}}",             Meaning = "Whole-project text",                      FilledByMemoQ = false },
            new Info { Token = "{{SOURCE_TEXT}}",         Meaning = "Alias of {{SOURCE_SEGMENT}}",             FilledByMemoQ = false, Legacy = true },
            new Info { Token = "{{TARGET_TEXT}}",         Meaning = "Alias of {{TARGET_SEGMENT}}",             FilledByMemoQ = false, Legacy = true },
            new Info { Token = "{source_lang}",           Meaning = "Alias of {{SOURCE_LANGUAGE}}",            FilledByMemoQ = true,  Legacy = true },
            new Info { Token = "{target_lang}",           Meaning = "Alias of {{TARGET_LANGUAGE}}",            FilledByMemoQ = true,  Legacy = true },
        };

        private static readonly HashSet<string> Known =
            new HashSet<string>(All.Select(p => p.Token), StringComparer.Ordinal);

        public static bool IsKnown(string token) => Known.Contains(token);

        public static Info Find(string token) =>
            All.FirstOrDefault(p => string.Equals(p.Token, token, StringComparison.Ordinal));
    }
}
