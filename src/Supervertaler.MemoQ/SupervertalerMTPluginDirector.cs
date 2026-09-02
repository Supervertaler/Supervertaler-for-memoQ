using System;
using System.Drawing;
using System.Windows.Forms;
using MemoQ.Addins.Common.Framework;
using MemoQ.MTInterfaces;
using Supervertaler.MemoQ.Core;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ
{
    /// <summary>
    /// The entry point memoQ discovers.
    ///
    /// memoQ scans every assembly in its <c>Addins</c> folder for a public type
    /// implementing <see cref="IModule"/> and one of the plugin director
    /// interfaces, instantiates it through its parameterless constructor, and
    /// calls <see cref="Initialize"/>. If the constructor throws, or a dependency
    /// fails to resolve, the engine simply does not appear in the MT settings list
    /// — with no error shown anywhere. That is why the constructor does nothing
    /// but log, and why every override below is defensive.
    ///
    /// <see cref="PluginDirectorBase"/> supplies nothing but the shape; every
    /// member here is abstract in the base.
    /// </summary>
    public class SupervertalerMTPluginDirector : PluginDirectorBase, IPluginDirector2, IPluginDirectorCommon, IModule
    {
        /// <summary>
        /// Stable identity, persisted into every project that uses this engine.
        /// Changing it orphans the MT settings of every existing project, so it is
        /// load-bearing — never rename it.
        /// </summary>
        public const string PluginId = "Supervertaler";

        private IEnvironment _environment;
        private IModuleEnvironment _moduleEnvironment;
        private bool _activated;

        public SupervertalerMTPluginDirector()
        {
            PluginLog.Write("SupervertalerMTPluginDirector constructed (v"
                + typeof(SupervertalerMTPluginDirector).Assembly.GetName().Version + ")");
        }

        // ---- IModule ----------------------------------------------------------

        public void Initialize(IModuleEnvironment environment)
        {
            // SharedSettings is compiled into the prompt editor as well, so it
            // cannot reference the plugin log directly. Inside memoQ it should.
            SharedSettings.ErrorSink = PluginLog.Write;

            _moduleEnvironment = environment;
            _activated = true;
            PluginLog.Write("Initialize: settings directory = "
                + (environment?.PluginSettingsDirectory ?? "(null)"));
        }

        public void Cleanup()
        {
            _activated = false;
            PluginLog.Write("Cleanup");
        }

        public bool IsActivated => _activated;

        // ---- identity ---------------------------------------------------------

        public override string PluginID => PluginId;

        public override string FriendlyName => "Supervertaler";

        public override string CopyrightText => "Copyright (c) 2026 Michael Beijer – supervertaler.com";

        public override Image DisplayIcon => IconLoader.Large;

        // ---- capabilities -----------------------------------------------------

        /// <summary>Pre-translate and other batch operations. This is where the rich (context-carrying) path is used.</summary>
        public override bool BatchSupported => true;

        /// <summary>Lookup while the cursor sits on a segment in the grid.</summary>
        public override bool InteractiveSupported => true;

        /// <summary>
        /// True: memoQ hands us every segment the translator confirms, via
        /// <see cref="ISessionForStoringTranslations"/>. Both the bundled ModernMT
        /// plugin and the current Lara plugin do this, and it is what puts an
        /// engine in the *Self-learning MT* dropdown.
        ///
        /// It also matters more here than it would elsewhere: since
        /// <c>IRichSession2</c> is unreachable, confirmed segments are the only
        /// in-document context we can get, and <see cref="DocumentMemory"/> feeds
        /// them back into later prompts.
        /// </summary>
        public override bool StoringTranslationSupported => true;

        /// <summary>
        /// False: we do not accept a fuzzy TM hit and repair it. If this were
        /// true, memoQ would pass the TM source and target into
        /// <c>TranslateCorrectSegment</c> and expect a corrected target back.
        /// </summary>
        public override bool SupportFuzzyForwarding => false;

        /// <summary>
        /// Always false. <see cref="Capabilities.AGT"/> (value: "AGT") is the only
        /// capability memoQ asks about, and it must NOT be claimed.
        ///
        /// Tested 2026-08-30. Returning true for "AGT" removed Supervertaler from
        /// the *Pre-translation* plugin dropdown in Edit machine translation
        /// settings > Settings — it read "No plugins selected" and the engine could
        /// no longer be picked at all. "AGT" evidently designates memoQ's own
        /// AI-guided-translation service rather than "this plugin can accept rich
        /// bundles", and claiming it moves the plugin out of the ordinary MT list
        /// into a surface a third party does not own.
        ///
        /// So the question it was meant to answer is still open: memoQ calls
        /// <c>CreateLookupSession</c> for everything, Pre-translate included, and
        /// never <c>CreateRichLookupSession</c> — which means no terminology, no
        /// forbidden terms and no neighbouring segments ever reach the prompt. What
        /// selects the rich path is not something we have found; ask memoQ.
        /// </summary>
        public override bool HasCapability(string what)
        {
            PluginLog.Write($"HasCapability(\"{what}\") -> false");
            return false;
        }

        /// <summary>
        /// An LLM handles any pair it has seen, and enumerating them would only
        /// produce a list that goes stale with every model release. Let the user
        /// try; a genuinely unsupported pair surfaces as a bad translation, not a
        /// crash.
        /// </summary>
        public override bool IsLanguagePairSupported(LanguagePairSupportedParams args) => true;

        // ---- lifecycle --------------------------------------------------------

        public override IEnvironment Environment
        {
            set { _environment = value; }
        }

        public override IEngine2 CreateEngine(CreateEngineParams args)
        {
            var settings = SupervertalerSettings.Load(args?.PluginSettings);

            PluginLog.Write($"CreateEngine: {args?.SourceLangCode} -> {args?.TargetLangCode}, "
                + $"provider={settings.GeneralSettings.Provider}, model={settings.GeneralSettings.Model}");

            return new SupervertalerMTEngine(
                settings,
                args?.SourceLangCode,
                args?.TargetLangCode);
        }

        public override PluginSettings EditOptions(IWin32Window parentForm, PluginSettings settings)
        {
            var current = SupervertalerSettings.Load(settings);

            using (var form = new OptionsForm(current))
            {
                var result = parentForm != null
                    ? form.ShowDialog(parentForm)
                    : form.ShowDialog();

                if (result != DialogResult.OK)
                {
                    PluginLog.Write("EditOptions: cancelled");
                    return settings;
                }

                PluginLog.Write("EditOptions: saved (provider=" + form.Result.GeneralSettings.Provider
                    + ", model=" + form.Result.GeneralSettings.Model + ")");
                return form.Result.GetSerializedSettings();
            }
        }
    }
}
