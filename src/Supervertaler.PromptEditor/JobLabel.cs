using System;

namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// Fitting a prompt, glossary or bank name into the width the job panel has.
    ///
    /// <para>Shortening is a last resort, not a house style. Most names are short
    /// enough to show whole, and two jobs in a row can have a prompt and a glossary
    /// named nothing like each other or like the project - so a rule that always
    /// trimmed would mangle the ordinary case to tidy the awkward one.</para>
    ///
    /// <para>When something genuinely does not fit, the first thing dropped is the
    /// part carrying no information. These four strings turn up together
    /// constantly, because AutoPrompt names the prompt after the project, Export
    /// glossary names itself after the prompt, and banks are named after
    /// projects:</para>
    ///
    /// <code>
    /// project    Acme (PROJ-001-AA-BB, PROJ-001-AA-CC)
    /// prompt     Acme (PROJ-001-AA-BB, PROJ-001-AA-CC) v3
    /// glossary   Acme (PROJ-001-AA-BB, PROJ-001-AA-CC) v3.txt
    /// bank       acme-proj-001-aa-bb-proj-001-aa-cc
    /// </code>
    ///
    /// <para>The project is named at the top of the panel already, so repeating it
    /// on every row spends the whole column on the one word the reader does not
    /// need. Dropping that run leaves "v3" and "v3.txt" - which is the entire
    /// difference between them - and only then, if it still does not fit, is the
    /// rest cut from the middle.</para>
    /// </summary>
    internal static class JobLabel
    {
        internal const string Ellipsis = "…";

        /// <summary>
        /// <paramref name="value"/> shortened to <paramref name="width"/> pixels.
        /// <paramref name="project"/> may be empty. <paramref name="measure"/> is
        /// the text width in pixels - a function so this can be exercised without
        /// a Graphics, and because the answer depends on the user's font.
        /// </summary>
        public static string Fit(string value, string project, int width, Func<string, int> measure)
        {
            if (measure == null) throw new ArgumentNullException(nameof(measure));

            value = (value ?? string.Empty).Trim();
            if (value.Length == 0 || width <= 0) return value;
            if (measure(value) <= width) return value;

            var shared = SharedPrefix(value, project);
            if (shared > 0)
            {
                var rest = value.Substring(shared).Trim();

                // Not when nothing is left of it. A bank named exactly after the
                // project would come out as a bare ellipsis, which says less than
                // a middle-cut name does.
                if (rest.Length > 0)
                {
                    var trimmed = Ellipsis + " " + rest;
                    if (measure(trimmed) <= width) return trimmed;
                    value = trimmed;
                }
            }

            return Middle(value, width, measure);
        }

        /// <summary>
        /// How much of the front of <paramref name="value"/> is the project name,
        /// in characters, or 0.
        ///
        /// <para>Compared with punctuation and case ignored, so the slug a memory
        /// bank folder carries - <c>acme-proj-001-aa-bb</c> against
        /// <c>Acme (PROJ-001-AA-BB)</c> - is recognised as the same words. Nothing
        /// is claimed on a partial word: matching "Acme (PROJ-001-AA-B" out of
        /// "…-BB" would leave a fragment that reads as a different case number.</para>
        /// </summary>
        internal static int SharedPrefix(string value, string project)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(project)) return 0;

            int vi = 0, pi = 0, matched = 0;

            while (vi < value.Length && pi < project.Length)
            {
                if (!Significant(value[vi])) { vi++; continue; }
                if (!Significant(project[pi])) { pi++; continue; }

                if (char.ToLowerInvariant(value[vi]) != char.ToLowerInvariant(project[pi])) return 0;

                vi++; pi++; matched = vi;
            }

            // The project name must have been consumed entirely; a value that
            // merely starts like it shares nothing worth dropping.
            while (pi < project.Length && !Significant(project[pi])) pi++;
            if (pi < project.Length) return 0;

            // And the value must break cleanly after it, or the next character
            // belongs to a word the project only half-covers.
            if (matched < value.Length && Significant(value[matched])) return 0;

            // Take the punctuation that closed the project name with it. The
            // match ends on the last letter or digit, so without this the bracket
            // and space of "…PROJ-001-AA-CC) v3" survive into the remainder and
            // it reads as ") v3".
            while (matched < value.Length && !Significant(value[matched])) matched++;

            return matched;
        }

        /// <summary>Letters and digits carry the name; brackets, spaces and hyphens do not.</summary>
        private static bool Significant(char c) => char.IsLetterOrDigit(c);

        /// <summary>
        /// Cut from the middle, so the beginning and the end both survive - which
        /// for these names is the client at one end and the version at the other.
        /// </summary>
        private static string Middle(string value, int width, Func<string, int> measure)
        {
            if (measure(value) <= width) return value;

            for (var keep = value.Length - 1; keep > 2; keep--)
            {
                var head = (keep + 1) / 2;
                var tail = keep - head;
                var candidate = value.Substring(0, head) + Ellipsis + value.Substring(value.Length - tail);

                if (measure(candidate) <= width) return candidate;
            }

            return Ellipsis;
        }
    }
}
