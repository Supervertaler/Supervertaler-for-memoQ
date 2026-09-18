using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
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
        /// cannot work - so this is true whenever the project has termbases ticked
        /// Read. There was once a second source, a glossary file, and for a day
        /// this asked only about that, which left a project with termbases and no
        /// file never offered the provider at all.
        /// </summary>
        public override bool PluginConfigured => AnyTermbaseSelected;

        /// <summary>
        /// Whether this project has termbases ticked. Never throws: this is asked
        /// while memoQ builds its list of providers.
        /// </summary>
        private static bool AnyTermbaseSelected
        {
            get
            {
                try
                {
                    return TermbaseSelection.ReadFor(CurrentProject).Count > 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The memoQ project in force, as the MT engine last recorded it. The TB
        /// SDK tells a terminology plugin nothing about projects, so this is the
        /// only way it can know which selection applies.
        /// </summary>
        internal static Guid CurrentProject => TermbaseSelection.CurrentProject;

        public override bool PluginEnabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        // ---- identity ---------------------------------------------------------

        public override string PluginID => PluginId;

        public override string FriendlyName => "Supervertaler terms";

        public override string CopyrightText => "Copyright (c) 2026 Michael Beijer – supervertaler.com";

        public override Image DisplayIcon => IconLoader.Large;

        /// <summary>
        /// A glossary file carries no language declaration, so we cannot honestly
        /// answer this. Saying yes lets the user decide; a mismatched glossary
        /// simply produces no hits.
        /// </summary>
        public override bool IsLanguagePairSupported(string srcLangName, string trgLangName) => true;

        /// <summary>
        /// memoQ's own Add Term button works with this term base. The SDK's
        /// contract is that memoQ hands over the selected source and target text
        /// and opens whatever URL <see cref="GetAddTermsUrl"/> returns - a design
        /// for web-based term bases. This plugin shows a small dialog of its own
        /// instead and returns no URL, so a term decided in the grid lands in the
        /// project termbase without a browser. Whether memoQ tolerates a null URL
        /// is the experiment the first build of this runs; the log records every
        /// call and its outcome.
        /// </summary>
        public override bool SupportsAddingNewTerms => true;

        /// <summary>Editing stays in the editor's Terms window; memoQ's edit path is a URL as well.</summary>
        public override bool SupportsModifyingExistingTerms => false;

        public override string GetAddTermsUrl(string externalId, string sourceLang, string sourceTerm, string targetLang, string targetTerm)
        {
            PluginLog.Write($"TB GetAddTermsUrl: {sourceLang} -> {targetLang}, source {(sourceTerm ?? "").Length} chars, "
                + $"target {(targetTerm ?? "").Length} chars, externalId={(string.IsNullOrEmpty(externalId) ? "(none)" : externalId)}, "
                + $"thread {Thread.CurrentThread.ManagedThreadId} {Thread.CurrentThread.GetApartmentState()}");

            try
            {
                var project = TermbaseSelection.CurrentProject;
                var into = TermbaseSelection.ProjectTermbaseFor(project);
                string outcome = "cancelled";

                // Its own STA thread with its own message loop: memoQ may call
                // this from a thread that cannot show a window, and even from its
                // UI thread a dialog it did not open cannot be parented to it.
                // Join blocks memoQ's thread until the dialog closes, which is
                // what a modal quick-add should do.
                var thread = new Thread(() =>
                {
                    try
                    {
                        using (var form = new QuickAddForm(sourceLang, targetLang, sourceTerm, targetTerm, into?.Name))
                        {
                            if (form.ShowDialog() != DialogResult.OK || into == null) return;

                            // One row through Import rather than AddTerm: Import turns
                            // the pair round when the termbase runs the other way from
                            // the project, and refuses a pair already there either way.
                            var row = new TermbaseFiles.Row
                            {
                                Source = form.Source, Target = form.Target,
                                Forbidden = form.Forbidden, Notes = form.Notes
                            };
                            var result = TermbaseWriter.Import(into.Id, new[] { row }, sourceLang, targetLang);
                            outcome = result.Added == 1
                                ? "added to " + into.Name + (result.Reversed ? " (turned round for it)" : "")
                                : "already in " + into.Name;
                        }
                    }
                    catch (Exception ex)
                    {
                        outcome = "failed: " + ex.Message;
                        MessageBox.Show(ex.Message, "Supervertaler – Add term", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                thread.Join();

                PluginLog.Write("TB add term: " + outcome);
            }
            catch (Exception ex)
            {
                PluginLog.Write("TB GetAddTermsUrl failed", ex);
            }

            return null;
        }

        public override IEngine CreateEngine(string srcLangName, string trgLangName)
        {
            PluginLog.Write($"TB CreateEngine: {srcLangName} -> {trgLangName}, "
                + $"termbases ticked for the project: {TermbaseSelection.ReadFor(CurrentProject).Count}");
            return new SupervertalerTBEngine(srcLangName, trgLangName);
        }

        /// <summary>
        /// Terminology is managed in the prompt editor - memoQ > Termbases... -
        /// so the plugin's options button simply opens it.
        /// </summary>
        public override void ShowOptionsForm(Form parentForm)
        {
            EditorLauncher.Open(parentForm);
        }
    }

    internal sealed class SupervertalerTBEngine : EngineBase
    {
        private readonly string _sourceLangName;
        private readonly string _targetLangName;

        public SupervertalerTBEngine(string sourceLangName, string targetLangName)
        {
            _sourceLangName = sourceLangName;
            _targetLangName = targetLangName;
        }

        public override ISession CreateSession() => new SupervertalerTBSession(_sourceLangName, _targetLangName);

        public override void Dispose() { }
    }

    /// <summary>
    /// Called by memoQ for each segment as the translator moves through the
    /// document. Must be fast and must never throw — it runs while the grid is
    /// being painted.
    /// </summary>
    internal sealed class SupervertalerTBSession : SessionBase
    {
        private readonly string _sourceLangName;
        private readonly string _targetLangName;

        public SupervertalerTBSession(string sourceLangName, string targetLangName)
        {
            _sourceLangName = sourceLangName;
            _targetLangName = targetLangName;
        }

        /// <summary>
        /// Two shades, because there are two kinds of termbase: the one project
        /// termbase and everything else.
        ///
        /// <para>This was briefly a four-step ramp, on the reasoning that memoQ
        /// shades its own term hits by termbase rank. That was a misreading -
        /// those shades are memoQ ranking ITS OWN termbases, which is not what
        /// this is. The distinction a translator actually works with is binary:
        /// the small, deliberate project termbase against any number of
        /// background ones. Four shades were an answer to a question nobody had
        /// asked.</para>
        ///
        /// <para>Blue rather than the soft green this started with, which was
        /// Trados's language: someone working in both products should not have
        /// to hold two colour schemes. Light enough for black text either way,
        /// because memoQ paints this BEHIND the source words rather than using
        /// it as their colour.</para>
        /// </summary>
        private static readonly Color ProjectColor = ColorTranslator.FromHtml("#7FB3E3");

        private static readonly Color BackgroundColor = ColorTranslator.FromHtml("#CCE3F8");

        /// <summary>
        /// Forbidden: a warning tint rather than memoQ's black. memoQ writes a
        /// forbidden term in black TEXT in its own pane, and this colour is a
        /// HIGHLIGHT painted behind the words in the source cell - black there
        /// would be black on black. The black belongs in the pane markup we
        /// author ourselves, which is where it now is.
        /// </summary>
        private static readonly Color ForbiddenColor = ColorTranslator.FromHtml("#F8D7DA");

        private static Color ColorFor(TermIndex.Entry entry)
        {
            if (entry.Forbidden) return ForbiddenColor;
            return entry.Rank == 1 ? ProjectColor : BackgroundColor;
        }

        public override TerminologyResult[] Lookup(Segment segment)
        {
            try
            {
                var plain = segment?.PlainText;
                if (string.IsNullOrWhiteSpace(plain)) return new TerminologyResult[0];

                // Second capture channel for the MCP bridge. memoQ asks this
                // plugin about every row the cursor lands on regardless of which
                // MT provider is selected — so a document pre-translated with
                // Google or from TM alone still becomes visible to Claude, one
                // visited row at a time. Costs a dictionary insert.
                CaptureStore.RecordVisited(_sourceLangName, _targetLangName, TagBridge.ToTaggedText(segment));

                // Set immediately before the lookup, never once at startup:
                // memoQ builds one session per language pair, and a plugin that
                // remembered "the last pair" would read a termbase backwards on
                // the second of two documents.
                TermIndex.UseLanguages(_sourceLangName, _targetLangName);

                var matches = TermIndex.Find(SupervertalerTBPluginDirector.CurrentProject, plain);
                if (matches.Count == 0) return new TerminologyResult[0];

                var results = new List<TerminologyResult>(matches.Count);

                // The exclusive end of the segment in formatted coordinates —
                // the hard bound no span may cross.
                var formattedEnd = segment.FormattedTextPosFromPlain(plain.Length, false);

                foreach (var m in matches)
                {
                    // TermIndex works in plain-text offsets; memoQ addresses the
                    // segment in its own coordinates, which differ whenever the
                    // segment carries inline tags.
                    //
                    // The "+ 1" on the length is EuroTermBank's convention
                    // (decompiled from MemoQ.EuroTermBank.ETBSession.Lookup), but
                    // it belongs on the LAST CHARACTER's position, not on the
                    // exclusive end. Mapping the exclusive end and then adding one
                    // put the span one position past the segment whenever a term
                    // ended exactly at the segment's end — harmless most of the
                    // time, but on a row with tracked changes memoQ converts every
                    // span through convertToChangeTrackedPos, which throws
                    // ArgumentOutOfRangeException("pos") instead of clamping, and
                    // the user gets an "Error processing terminology results"
                    // dialog mid-job.
                    var start = segment.FormattedTextPosFromPlain(m.Start, false);
                    var lastChar = segment.FormattedTextPosFromPlain(m.Start + m.Length - 1, false);
                    var length = lastChar - start + 1;

                    // Belt and braces: whatever the mapping did, never hand memoQ
                    // a span that leaves the segment.
                    if (start < 0 || start >= formattedEnd) continue;
                    length = Math.Min(length, formattedEnd - start);

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
                        Color = ColorFor(m.Entry),

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
                // Black, because that is what black means in memoQ: forbidden.
                sb.Append("<div style=\"color:#000000;font-weight:bold\">Do not use: ")
                  .Append("<span style=\"text-decoration:line-through\">").Append(Escape(entry.Target)).Append("</span>")
                  .Append("</div>");
            }
            else
            {
                sb.Append("<div style=\"color:#0f5132;font-weight:bold\">").Append(Escape(entry.Target)).Append("</div>");
            }

            sb.Append("<div style=\"color:#6c757d\">").Append(Escape(entry.Source)).Append("</div>");

            // Name what answered. With a glossary file and several termbases all
            // live at once, "a term matched" is much less use than knowing which
            // termbase said so - and it is the only way to tell, from the pane,
            // whether a freshly exported glossary or a standing termbase is the
            // one talking.
            var origin = (entry.Origin ?? string.Empty).Trim();

            sb.Append("<div style=\"color:#adb5bd;font-size:8pt\">Supervertaler")
              .Append(origin.Length > 0 ? " · " + Escape(origin) : string.Empty)
              .Append(entry.Rank == 1 ? " · project termbase" : string.Empty)
              .Append("</div>");
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
