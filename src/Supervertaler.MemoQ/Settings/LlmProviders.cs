using System;
using Supervertaler.Core;

namespace Supervertaler.MemoQ.Settings
{
    /// <summary>
    /// Provider identifiers. Strings, not an enum, so an unknown value in an old
    /// settings file degrades rather than throws.
    ///
    /// In its own file because the prompt editor compiles it too: the editor
    /// offers the same choice of providers and must not carry a second copy of
    /// the list that can fall behind this one.
    /// </summary>
    public static class LlmProviders
    {
        public const string Anthropic = "Anthropic";
        public const string OpenAI = "OpenAI";
        public const string Google = "Google";

        public static readonly string[] All = { Anthropic, OpenAI, Google };

        /// <summary>
        /// The id core uses for the same provider, or null when there is none.
        ///
        /// memoQ names its three providers for the user; core names all nine for
        /// the wire, the model lists and the shared key file. One translation, in
        /// one place, because getting it wrong is silent both ways: an unmapped
        /// name shows as an empty model list, and a wrong one reads somebody
        /// else's key.
        ///
        /// <para><b>Google is the trap.</b> Core calls Gemini <c>gemini</c>, and
        /// the shared key file keeps <c>google</c> for Supervertaler Sidekick's
        /// Google Translate key - a different service entirely. Passing the
        /// user-facing word "Google" through unmapped would read that one.</para>
        /// </summary>
        public static string CoreKey(string provider)
        {
            if (string.Equals(provider, Anthropic, StringComparison.OrdinalIgnoreCase)) return LlmModels.ProviderClaude;
            if (string.Equals(provider, OpenAI, StringComparison.OrdinalIgnoreCase)) return LlmModels.ProviderOpenAi;
            if (string.Equals(provider, Google, StringComparison.OrdinalIgnoreCase)) return LlmModels.ProviderGemini;
            return null;
        }
    }
}
