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
    }
}
