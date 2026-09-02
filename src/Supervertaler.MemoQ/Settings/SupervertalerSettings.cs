using System;
using MemoQ.MTInterfaces;

namespace Supervertaler.MemoQ.Settings
{
    /// <summary>
    /// Everything the user configures, except the API key.
    /// XML-serialised by memoQ via <see cref="PluginSettingsObject{TG,TS}"/>, so
    /// every member must be a public field or property with a public setter and a
    /// parameterless constructor. Adding a member is safe; renaming one silently
    /// resets it for existing users.
    /// </summary>
    public class SupervertalerGeneralSettings
    {
        public string Provider { get; set; } = LlmProviders.Anthropic;

        public string Model { get; set; } = "claude-opus-5";

        /// <summary>Blank means "use the provider default". Set for Ollama or an OpenAI-compatible gateway.</summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>
        /// Relative path of a prompt in the shared Supervertaler library, or empty
        /// to use <see cref="SystemPrompt"/>.
        ///
        /// A path rather than the text: the library is the source of truth, so a
        /// prompt improved in the Trados plugin or in a text editor takes effect
        /// here on the next segment. See <see cref="Core.PromptResolver"/>.
        /// </summary>
        public string PromptPath { get; set; } = "";

        /// <summary>
        /// The translation instruction used when no library prompt is selected.
        /// memoQ supplies the language pair, so it does not name languages —
        /// <see cref="Core.PromptBuilder"/> substitutes them per request.
        /// </summary>
        public string SystemPrompt { get; set; } = DefaultSystemPrompt;

        /// <summary>How many segments go into one LLM request during a batch (Pre-translate) run.</summary>
        public int BatchSize { get; set; } = 20;

        /// <summary>Concurrent LLM requests. Surfaced to memoQ as IParallelEngine.MaxDegreeOfParallelism.</summary>
        public int MaxParallelRequests { get; set; } = 4;

        /// <summary>Feed memoQ's own termbase hits into the prompt (ContextKinds.Terminology).</summary>
        public bool UseTerminologyContext { get; set; } = true;

        /// <summary>Feed memoQ's surrounding segments into the prompt (ContextKinds.TextFlowContext / TranslationPair).</summary>
        public bool UseDocumentContext { get; set; } = true;

        /// <summary>
        /// Pre-translate never calls the model. It still captures every segment
        /// memoQ shows it and still delivers translations staged over the MCP
        /// bridge — it just produces nothing of its own.
        ///
        /// Scoped to Pre-translate (the batch path) on purpose. Segment-by-segment
        /// lookups keep using the model, so a chat-driven job still gets live
        /// suggestions in the results pane for rows Claude has not staged. The
        /// setting therefore never needs toggling mid-job: one Pre-translate
        /// captures the document for free, Claude stages, a second Pre-translate
        /// pulls the translations in, and walking the document afterwards works
        /// as it always did.
        /// </summary>
        public bool BridgeMode { get; set; } = false;

        // CRLF, not LF. A WinForms multiline TextBox does not treat a bare \n as a
        // line break, so an LF-separated default renders as one run-on paragraph
        // in the options dialog — "…translator.Translate the source…". The prompt
        // still reaches the model correctly either way; this is purely so the
        // user can read and edit it.
        public const string DefaultSystemPrompt =
            "You are a professional {SOURCE_LANG} to {TARGET_LANG} translator.\r\n" +
            "Translate the source segment faithfully and idiomatically.\r\n" +
            "\r\n" +
            "Rules:\r\n" +
            "- Return ONLY the translation. No preamble, no explanation, no quotes around it.\r\n" +
            "- Reproduce every inline tag exactly as it appears in the source, in the\r\n" +
            "  equivalent position in the target. Never invent, drop or renumber a tag.\r\n" +
            "- Preserve leading and trailing whitespace.\r\n" +
            "- Supplied terminology is the client's preferred wording: follow it unless it\r\n" +
            "  is clearly wrong for the sentence at hand.\r\n" +
            "- Forbidden terms are absolute: never use one, in any form.";
    }

    /// <summary>
    /// The API key. memoQ stores this separately from the general settings and
    /// encrypts it, which is why it is its own type rather than one more field
    /// above — the split is what makes the key eligible for that treatment.
    /// </summary>
    public class SupervertalerSecureSettings
    {
        public string ApiKey { get; set; } = string.Empty;
    }

    /// <summary>Round-trips the pair through memoQ's <see cref="PluginSettings"/> envelope.</summary>
    /// <summary>
    /// Round-trips the pair through memoQ's <see cref="PluginSettings"/> envelope.
    ///
    /// Note that the inherited <c>GeneralSettings</c> and <c>SecureSettings</c> are
    /// readonly fields, not properties — they can only be supplied at construction.
    /// Hence <see cref="Create"/> rather than an object initialiser everywhere.
    /// </summary>
    public class SupervertalerSettings
        : PluginSettingsObject<SupervertalerGeneralSettings, SupervertalerSecureSettings>
    {
        public SupervertalerSettings() : base() { }

        public SupervertalerSettings(PluginSettings settings) : base(settings) { }

        public SupervertalerSettings(SupervertalerGeneralSettings general, SupervertalerSecureSettings secure)
            : base(general, secure) { }

        public static SupervertalerSettings Create(
            SupervertalerGeneralSettings general,
            SupervertalerSecureSettings secure)
        {
            return new SupervertalerSettings(
                general ?? new SupervertalerGeneralSettings(),
                secure ?? new SupervertalerSecureSettings());
        }

        /// <summary>
        /// Deserialise, tolerating a null or empty envelope (first run) and a
        /// malformed one (a settings file written by an older build). Either way
        /// the caller gets a fully populated object, never a null member.
        /// </summary>
        public static SupervertalerSettings Load(PluginSettings settings)
        {
            try
            {
                var loaded = settings == null
                    ? new SupervertalerSettings()
                    : new SupervertalerSettings(settings);

                if (loaded.GeneralSettings != null && loaded.SecureSettings != null)
                    return loaded;

                return Create(loaded.GeneralSettings, loaded.SecureSettings);
            }
            catch (Exception ex)
            {
                Core.PluginLog.Write("Settings.Load failed; falling back to defaults", ex);
                return Create(null, null);
            }
        }
    }
}
