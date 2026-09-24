using System;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Puts Supervertaler's built-in prompts into the shared prompt library,
    /// and keeps them current.
    ///
    /// <para>Until 2026-09-24 only the Trados plugin ever did this. A translator
    /// with both products never noticed, because Trados had filled the shared
    /// library long ago - but a memoQ-only customer would never have had the
    /// Default Translation Prompt, or anything in a Default folder, and nor
    /// would the updates core makes to those prompts ever have reached them.
    /// Core's <c>EnsureDefaultPrompts</c> writes only what is missing and
    /// refreshes only files still flagged default, so running it from memoQ as
    /// well as Trados is safe, and it is quick.</para>
    ///
    /// <para>Called by the plugin when memoQ loads it, so the prompt list in
    /// memoQ's own dialog is complete even for someone who never opens the
    /// editor, and by the editor when it starts. Never under a harness, which
    /// must not write the translator's real library.</para>
    /// </summary>
    internal static class DefaultPrompts
    {
        public static void Ensure(Action<string> log)
        {
            if (SharedSettings.InHarness) return;

            try
            {
                new global::Supervertaler.Core.PromptLibrary().EnsureDefaultPrompts();
            }
            catch (Exception ex)
            {
                // Never worth failing a start over: a missing default prompt is
                // an inconvenience, a plugin that will not load is not.
                log?.Invoke("Could not put the built-in prompts in place: " + ex.Message);
            }
        }
    }
}
