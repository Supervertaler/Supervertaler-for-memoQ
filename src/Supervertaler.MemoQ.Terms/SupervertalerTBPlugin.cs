using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using MemoQ.Addins.Common.DataStructures;
using MemoQ.Addins.Common.Framework;
using MemoQ.TBInterfaces;
using Supervertaler.MemoQ.Core;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ
{
    /// <summary>
    /// Supervertaler as a memoQ terminology provider.
    ///
    /// This is where TermLens lands. memoQ gives an add-in no panel of its own, but
    /// a TB plugin gets something better for this purpose: memoQ's *own* terminology
    /// pane, filled with HTML we author (<see cref="TerminologyResult.PrettyPrintHtml"/>),
    /// with the matched words highlighted in the source in a colour we choose. In
    /// Trados that panel had to be built and painted by hand; here it comes for free.
    ///
    /// It also closes the terminology gap in the MT plugin. memoQ never populates
    /// <c>ContextKinds.Terminology</c> for a third-party MT plugin, so the only way
    /// to get terms into a prompt is to be the terminology source — which this is.
    /// Both directors ship in one assembly (<c>ModuleAttribute</c> is
    /// <c>AllowMultiple</c>), so <see cref="TermIndex"/> is simply shared between
    /// them in-process.
    ///
    /// Registered by the second <c>[assembly: Module]</c> entry in AssemblyModules.cs.
    /// </summary>
    public class SupervertalerTBPluginDirector : PluginDirectorBase, IPluginDirector, IModule, IModuleEx
    {
        /// <summary>
        /// Distinct from the MT plugin's id. Persisted wherever a project records
        /// its terminology providers, so never rename it.
        /// </summary>
        public const string PluginId = "SupervertalerTerms";

        private bool _activated;
        private bool _enabled = true;

        public SupervertalerTBPluginDirector()
        {
            PluginLog.Write("SupervertalerTBPluginDirector constructed");
        }

        // ---- IModule / IModuleEx ----------------------------------------------

        public override void Initialize(IModuleEnvironment env)
        {
            _activated = true;
            PluginLog.Write("TB Initialize");
        }

        public override void Cleanup()
        {
            _activated = false;
        }

        public override bool IsActivated => _activated;

        /// <summary>
        /// memoQ hides an unconfigured provider rather than offering something that
        /// cannot work, so this is tied to whether a glossary has actually been set.
        /// </summary>
        public override bool PluginConfigured => !string.IsNullOrWhiteSpace(SharedSettings.GlossaryPath);

        public override bool PluginEnabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        // ---- identity ---------------------------------------------------------

        public override string PluginID => PluginId;

        public override string FriendlyName => "Supervertaler terms";

        public override string CopyrightText => "Copyright (c) 2026 Michael Beijer — supervertaler.com";

        public override Image DisplayIcon => IconLoader.Large;

        /// <summary>
        /// A glossary file carries no language declaration, so we cannot honestly
        /// answer this. Saying yes lets the user decide; a mismatched glossary
        /// simply produces no hits.
        /// </summary>
        public override bool IsLanguagePairSupported(string srcLangName, string trgLangName) => true;

        /// <summary>
        /// Both false: the SDK's add and edit paths hand memoQ a *URL* to open
        /// (<c>GetAddTermsUrl</c> / <c>GetModifyTermsUrl</c>) rather than letting us
        /// show a dialog, which is a poor substitute for the Trados quick-add. Term
        /// editing belongs in the companion app, not behind a browser redirect.
        /// </summary>
        public override bool SupportsAddingNewTerms => false;

        public override bool SupportsModifyingExistingTerms => false;

        public override IEngine CreateEngine(string srcLangName, string trgLangName)
        {
            PluginLog.Write($"TB CreateEngine: {srcLangName} -> {trgLangName}, "
                + $"glossary={(string.IsNullOrWhiteSpace(SharedSettings.GlossaryPath) ? "(none)" : "set")}");
            return new SupervertalerTBEngine(trgLangName);
        }

        public override void ShowOptionsForm(Form parentForm)
        {
            using (var form = new GlossaryForm())
            {
                if (parentForm != null) form.ShowDialog(parentForm); else form.ShowDialog();
            }
        }
    }

    internal sealed class SupervertalerTBEngine : EngineBase
    {
        private readonly string _targetLangName;

        public SupervertalerTBEngine(string targetLangName)
        {
            _targetLangName = targetLangName;
        }

        public override ISession CreateSession() => new SupervertalerTBSession(_targetLangName);

        public override void Dispose() { }
    }

    /// <summary>
    /// Called by memoQ for each segment as the translator moves through the
    /// document. Must be fast and must never throw — it runs while the grid is
    /// being painted.
    /// </summary>
    internal sealed class SupervertalerTBSession : SessionBase
    {
        private readonly string _targetLangName;

        public SupervertalerTBSession(string targetLangName)
        {
            _targetLangName = targetLangName;
        }

        /// <summary>Approved term: the same soft green TermLens uses in Trados.</summary>
        private static readonly Color ApprovedColor = ColorTranslator.FromHtml("#D4EDDA");

        /// <summary>Forbidden term: red, because it is a warning and not a suggestion.</summary>
        private static readonly Color ForbiddenColor = ColorTranslator.FromHtml("#F8D7DA");

        public override TerminologyResult[] Lookup(Segment segment)
        {
            try
            {
                var plain = segment?.PlainText;
                if (string.IsNullOrWhiteSpace(plain)) return new TerminologyResult[0];

                var matches = TermIndex.Find(SharedSettings.GlossaryPath, plain);
                if (matches.Count == 0) return new TerminologyResult[0];

                var results = new List<TerminologyResult>(matches.Count);

                foreach (var m in matches)
                {
                    // TermIndex works in plain-text offsets; memoQ addresses the
                    // segment in its own coordinates, which differ whenever the
                    // segment carries inline tags.
                    //
                    // The "+ 1" on the length is not an accident and not mine: it is
                    // what memoQ's own EuroTermBank plugin does. LengthInSegment is
                    // evidently inclusive of the end position, so a plain end-start
                    // under-highlights every term by one character. Verified by
                    // decompiling MemoQ.EuroTermBank.ETBSession.Lookup.
                    var start = segment.FormattedTextPosFromPlain(m.Start, false);
                    var end = segment.FormattedTextPosFromPlain(m.Start + m.Length, false);
                    var length = end - start + 1;

                    results.Add(new TerminologyResult
                    {
                        SourceTerm = SegmentBuilder.CreateFromString(m.Entry.Source),
                        TargetTerm = SegmentBuilder.CreateFromString(m.Entry.Target),
                        StartPosInSegment = start,
                        LengthInSegment = Math.Max(1, length),

                        // EuroTermBank sets this from the language set it matched;
                        // memoQ uses it to decide which target column a hit belongs
                        // to when several languages are in play.
                        TargetLanguage = _targetLangName,
                        Color = m.Entry.Forbidden ? ForbiddenColor : ApprovedColor,

                        // memoQ shows this as the match quality. A glossary hit is
                        // exact by construction — it either occurs in the text or it
                        // does not — so anything less would be inventing doubt.
                        Confidence = 100,

                        PrettyPrintHtml = BuildHtml(m.Entry),
                        ExternalId = m.Entry.Source
                    });
                }

                return results.ToArray();
            }
            catch (Exception ex)
            {
                // A terminology lookup that throws would break the grid. Return
                // nothing and carry on.
                PluginLog.Write("TB Lookup failed", ex);
                return new TerminologyResult[0];
            }
        }

        /// <summary>
        /// The entry as memoQ will render it in its terminology pane. This is the
        /// one place a memoQ add-in gets to put its own markup inside the
        /// application, so it is worth making it read well.
        /// </summary>
        private static string BuildHtml(TermIndex.Entry entry)
        {
            var sb = new StringBuilder();
            sb.Append("<div style=\"font-family:Segoe UI,sans-serif;font-size:9pt\">");

            if (entry.Forbidden)
            {
                sb.Append("<div style=\"color:#842029;font-weight:bold\">Do not use: ")
                  .Append("<span style=\"text-decoration:line-through\">").Append(Escape(entry.Target)).Append("</span>")
                  .Append("</div>");
            }
            else
            {
                sb.Append("<div style=\"color:#0f5132;font-weight:bold\">").Append(Escape(entry.Target)).Append("</div>");
            }

            sb.Append("<div style=\"color:#6c757d\">").Append(Escape(entry.Source)).Append("</div>");
            sb.Append("<div style=\"color:#adb5bd;font-size:8pt\">Supervertaler</div>");
            sb.Append("</div>");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return (s ?? string.Empty)
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public override void Dispose() { }
    }
}
