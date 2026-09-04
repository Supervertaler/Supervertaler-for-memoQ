using System;
using System.IO;
using MemoQ.MTInterfaces;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// State shared by every session an engine creates.
    ///
    /// It exists mainly to solve one awkwardness in the SDK:
    /// <see cref="ISessionForStoringTranslations"/> receives a
    /// <see cref="TranslationUnit"/> carrying only a source and a target — no
    /// document, no project, nothing to file it under. The document identity
    /// arrives on a different interface entirely
    /// (<see cref="ISessionWithMetadata"/>, via <see cref="MTRequestMetadata"/>).
    ///
    /// Both session kinds are created by the same engine, so the engine is the
    /// place where the two halves can be joined: the translate path records which
    /// document memoQ is currently working on, and the store path attributes
    /// confirmed segments to it.
    ///
    /// That coupling is a heuristic, not a guarantee — if memoQ ever interleaves
    /// work on two documents through one engine, some pairs could be filed under
    /// the wrong one. In practice a translator confirms segments in the document
    /// they are looking at, and the cost of a rare misfiling is one slightly
    /// off-topic example in a prompt. Worth knowing before trusting it for
    /// anything stricter.
    /// </summary>
    internal sealed class EngineContext
    {
        private readonly object _lock = new object();
        private Guid _currentDocument;
        private Guid _currentProject;
        private MTRequestMetadata _lastMetadata;

        /// <summary>
        /// How much of a memory bank travels with an ordinary translation
        /// request.
        ///
        /// <para>Deliberately a quarter of what AutoPrompt gets. AutoPrompt runs
        /// once and its output - a prompt - is reused for the whole job, so
        /// context there is bought once. This block is re-sent with every
        /// request memoQ makes, and memoQ makes one per ten segments during
        /// Pre-translate and one per row you land on while translating. On the
        /// 569-segment job this was measured against, that is 57 sends rather
        /// than one.</para>
        ///
        /// <para>What does not fit is dropped by priority, brief first. That is
        /// the reader's own rule and it is the right one here: the standing
        /// instructions for a client are worth more per token than a long
        /// terminology article, most of which will not apply to any one
        /// batch.</para>
        /// </summary>
        internal const int PerRequestTokenBudget = 6000;

        /// <summary>
        /// How much of a bank AutoPrompt gets: effectively all of it.
        ///
        /// <para>Not a round number for its own sake. A bank as this translator
        /// keeps them - a client folder of a few articles, over a <c>_shared</c>
        /// overlay of about eighty kilobytes - comes to roughly twenty-five
        /// thousand tokens, so this is set above what he actually has rather
        /// than at a figure that would quietly trim it. The whole point of
        /// drafting a prompt is that it happens once: whatever the bank knows
        /// about a client belongs in the prompt that will then govern every one
        /// of the job's requests, and paying for it twice is not the risk here -
        /// leaving it out is.</para>
        /// </summary>
        internal const int AutoPromptTokenBudget = 40000;

        // The bank's formatted block, and what it was built from. Rebuilt when
        // the bank changes, when the project changes, and when anything in the
        // bank is written - a translator who fixes a term in Obsidian mid-job
        // expects the next batch to know about it.
        private readonly object _kbLock = new object();
        private global::Supervertaler.Core.MemoryBankReader _kbReader;
        private string _kbReaderBank;
        private string _kbBlock;
        private string _kbBlockKey;
        private string _warnedMissingBank;
        private string _reportedBank;

        public EngineContext(SupervertalerSettings settings, string sourceLangCode, string targetLangCode)
        {
            Settings = settings ?? new SupervertalerSettings();
            SourceLangCode = sourceLangCode;
            TargetLangCode = targetLangCode;

            // memoQ has just handed us the settings resource. Copy anything the
            // shared file is missing out of it, once, so the prompt editor is
            // showing the same values this engine will use.
            var stored = Settings.GeneralSettings ?? new SupervertalerGeneralSettings();
            SharedSettings.SeedIfUnset(
                stored.Provider, stored.Model, stored.Endpoint, stored.PromptPath,
                stored.MaxParallelRequests, stored.BatchSize,
                stored.UseTerminologyContext, stored.UseDocumentContext,
                stored.BridgeMode, stored.SystemPrompt, Settings.SecureSettings?.ApiKey);
        }

        public SupervertalerSettings Settings { get; }
        /// <summary>
        /// The API key in force, from whichever of the three sources has one.
        /// Consumers ask for this rather than reaching into the secure settings,
        /// which now hold only the last of those sources.
        /// </summary>
        public string ApiKey => ApiKeys.Resolve(General.Provider, Settings.SecureSettings?.ApiKey).Key;

        public string SourceLangCode { get; }
        public string TargetLangCode { get; }

        /// <summary>
        /// The settings actually in force: what memoQ handed us from the MT
        /// settings resource, with anything the shared file carries laid over the
        /// top. Every consumer reads settings through here, so this is the only
        /// place that has to know the two stores exist.
        ///
        /// Resolved on each access rather than cached, so a change made in the
        /// prompt editor takes effect on the next segment instead of the next
        /// time memoQ builds an engine. The reads behind it are served from a
        /// parsed dictionary refreshed at most every few seconds, so the cost is
        /// a few dictionary lookups and one small allocation.
        /// </summary>
        public SupervertalerGeneralSettings General
        {
            get
            {
                var stored = Settings.GeneralSettings ?? new SupervertalerGeneralSettings();

                return new SupervertalerGeneralSettings
                {
                    Provider = SharedSettings.ProviderOr(stored.Provider),
                    Model = SharedSettings.ModelOr(stored.Model),
                    Endpoint = SharedSettings.EndpointOr(stored.Endpoint),
                    PromptPath = SharedSettings.PromptPathOr(stored.PromptPath),
                    SystemPrompt = SharedSettings.InstructionsOr(stored.SystemPrompt),
                    BatchSize = SharedSettings.BatchSizeOr(stored.BatchSize),
                    MaxParallelRequests = SharedSettings.ParallelOr(stored.MaxParallelRequests),
                    UseTerminologyContext = SharedSettings.UseTerminologyContextOr(stored.UseTerminologyContext),
                    UseDocumentContext = SharedSettings.UseDocumentContextOr(stored.UseDocumentContext),
                    BridgeMode = SharedSettings.BridgeModeOr(stored.BridgeMode)
                };
            }
        }

        /// <summary>
        /// The document memoQ most recently asked us to translate in, or
        /// <see cref="Guid.Empty"/> if it has never told us — which is the case
        /// whenever it uses the plain <see cref="ISession"/> overload without
        /// metadata. Everything keyed on this degrades to a single shared bucket
        /// in that case, which is still better than nothing.
        /// </summary>
        public Guid CurrentDocument
        {
            get { lock (_lock) return _currentDocument; }
        }

        public MTRequestMetadata LastMetadata
        {
            get { lock (_lock) return _lastMetadata; }
        }

        /// <summary>
        /// Key for <see cref="DocumentMemory"/> and its disk file: document plus
        /// language pair. The pair matters — the same document translated into a
        /// second target language is different work and must not share recall.
        ///
        /// With no document id (memoQ used the metadata-free overload) this
        /// degrades to one bucket per language pair, which is coarser but still
        /// better than nothing.
        /// </summary>
        public string MemoryKey
        {
            get
            {
                var doc = CurrentDocument;
                var pair = (SourceLangCode ?? "?") + "-" + (TargetLangCode ?? "?");
                return (doc == Guid.Empty ? "nodoc" : doc.ToString("N")) + "_" + pair;
            }
        }

        private static string _recordedPair;

        /// <summary>
        /// Notes this project's languages for the prompt editor, which cannot ask
        /// memoQ when memoQ is not running. Called from session creation rather
        /// than from the constructor, because memoQ builds throwaway engines whose
        /// language pair is not the user's.
        ///
        /// Guarded on change: this is on the path memoQ takes for every row, and a
        /// file write per lookup would be absurd.
        /// </summary>
        public void RecordLanguagePair()
        {
            // Not from a test run. The build's own smoke test creates an eng-nld
            // engine, and without this it wrote that pair into the user's settings,
            // where an export made with memoQ closed would have been stamped with
            // it. Same switch that already stops seeding and key resolution.
            if (SharedSettings.InHarness) return;

            var pair = (SourceLangCode ?? "?") + "|" + (TargetLangCode ?? "?");
            if (string.Equals(pair, _recordedPair, StringComparison.Ordinal)) return;
            _recordedPair = pair;

            if (!string.Equals(SharedSettings.SourceLang, SourceLangCode, StringComparison.OrdinalIgnoreCase))
                SharedSettings.SourceLang = SourceLangCode ?? string.Empty;

            if (!string.Equals(SharedSettings.TargetLang, TargetLangCode, StringComparison.OrdinalIgnoreCase))
                SharedSettings.TargetLang = TargetLangCode ?? string.Empty;
        }

        private static string _directionWarnedFor;

        /// <summary>
        /// Says so when the active glossary faces the wrong way. Silence was the
        /// old behaviour and it cost a whole comparison run: a glossary for the
        /// opposite direction produces no hits, no terms in the prompt and a clean
        /// terminology QA report, with nothing anywhere explaining why.
        ///
        /// Warned once per glossary and language pair, because this is called from
        /// the translation path.
        /// </summary>
        public void WarnIfGlossaryFacesTheWrongWay()
        {
            var path = SharedSettings.GlossaryPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            var relation = GlossaryDirection.Compare(
                SourceLangCode, TargetLangCode, TermIndex.DeclaredSource, TermIndex.DeclaredTarget);

            if (relation == GlossaryDirection.Relation.Aligned
                || relation == GlossaryDirection.Relation.Undeclared) return;

            var key = path + "|" + SourceLangCode + "|" + TargetLangCode;
            if (string.Equals(key, _directionWarnedFor, StringComparison.Ordinal)) return;
            _directionWarnedFor = key;

            PluginLog.Write("GLOSSARY DIRECTION: " + GlossaryDirection.Explain(
                relation, TermIndex.DeclaredSource, TermIndex.DeclaredTarget,
                SourceLangCode, TargetLangCode));
        }

        private static string _promptWarnedFor;

        /// <summary>
        /// Says so when the selected prompt was written for another language pair.
        /// A prompt names its languages in its role, locks terminology one way
        /// round and carries register rules for one target, so running it
        /// backwards produces a confident translation against instructions for the
        /// opposite job. The glossary already warns about this; the prompt is the
        /// larger half of the same mistake.
        ///
        /// Only prompts that declare a pair are checked, so nothing written before
        /// the declaration existed starts complaining.
        /// </summary>
        public void WarnIfPromptFacesTheWrongWay()
        {
            var path = General.PromptPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            if (!PromptResolver.TryGetLanguages(path, out var promptSource, out var promptTarget)) return;

            var relation = GlossaryDirection.Compare(
                SourceLangCode, TargetLangCode, promptSource, promptTarget);

            if (relation == GlossaryDirection.Relation.Aligned
                || relation == GlossaryDirection.Relation.Undeclared) return;

            var key = path + "|" + SourceLangCode + "|" + TargetLangCode;
            if (string.Equals(key, _promptWarnedFor, StringComparison.Ordinal)) return;
            _promptWarnedFor = key;

            PluginLog.Write($"PROMPT DIRECTION: the selected prompt '{path}' was written for "
                + $"{promptSource} to {promptTarget}, but this project is {SourceLangCode} to "
                + $"{TargetLangCode}. Its instructions, locked terminology and register rules are "
                + "for the other direction. Select a prompt for this pair, or draft one.");
        }

        public void NoteMetadata(MTRequestMetadata metadata)
        {
            if (metadata == null) return;

            Guid switchedTo;
            lock (_lock)
            {
                _lastMetadata = metadata;
                if (metadata.DocumentID != Guid.Empty) _currentDocument = metadata.DocumentID;

                switchedTo = metadata.ProjectGuid != Guid.Empty && metadata.ProjectGuid != _currentProject
                    ? metadata.ProjectGuid
                    : Guid.Empty;
                if (switchedTo != Guid.Empty) _currentProject = switchedTo;
            }

            // Outside the lock deliberately: this reads a file, writes a setting
            // and logs, and _lock is held on memoQ's translate threads.
            if (switchedTo != Guid.Empty) ApplyProjectMemoryBank(switchedTo);
        }

        /// <summary>
        /// The memoQ project the engine is working in, or <see cref="Guid.Empty"/>
        /// when memoQ has not said.
        /// </summary>
        public Guid CurrentProject
        {
            get { lock (_lock) return _currentProject; }
        }

        /// <summary>
        /// Point SuperMemory at the bank this project uses, when memoQ starts
        /// sending work from a different one.
        ///
        /// <para>A project with no bank recorded CLEARS to none rather than
        /// inheriting the last one used. A bank supplies one client's
        /// terminology and standing instructions to every request, so carrying
        /// the previous job's bank into a new one produces confident answers
        /// written to the wrong rules, with nothing on screen to say so. No bank
        /// is better than the wrong bank. This is the Trados plugin's rule,
        /// deliberately unchanged: a translator who has learnt it there must not
        /// have to learn a different one here.</para>
        ///
        /// <para>Either outcome is written to the log, so the activity window
        /// reports the change rather than it happening underneath you.</para>
        /// </summary>
        private void ApplyProjectMemoryBank(Guid project)
        {
            try
            {
                // What the two settings dialogs record a later choice against.
                // memoQ opens them from an MT settings resource and tells them
                // nothing about projects, so this is their only way to know.
                SharedSettings.MemoryBankProject = project.ToString("D");
                SharedSettings.MemoryBankProjectName = ProjectNameOrNull() ?? string.Empty;

                var wanted = MemoryBankChoice.ForProject(project) ?? string.Empty;
                var current = SharedSettings.MemoryBank ?? string.Empty;
                if (string.Equals(wanted, current, StringComparison.Ordinal)) return;

                SharedSettings.MemoryBank = wanted;
                DropKbCache();

                var where = ProjectLabel();
                PluginLog.Write(wanted.Length > 0
                    ? "SuperMemory: " + where + " uses memory bank " + Quote(wanted)
                    : "SuperMemory: no memory bank is set for " + where + ", so it contributes "
                      + "nothing. The previous project's bank is deliberately not carried over - "
                      + "it would supply another client's terminology without saying so.");
            }
            catch (Exception ex)
            {
                PluginLog.Write("Could not apply the memory bank for this project", ex);
            }
        }

        private static string Quote(string s) => "'" + s + "'";

        /// <summary>
        /// Something the translator recognises the project by, falling back to a
        /// bare phrase. The GUID is deliberately not used: it identifies nothing
        /// to a human reading the activity window.
        /// </summary>
        private string ProjectLabel()
        {
            var name = ProjectNameOrNull();
            return string.IsNullOrWhiteSpace(name) ? "this project" : "project " + Quote(name.Trim());
        }

        // -- the bank's contribution to a prompt --------------------------------

        /// <summary>
        /// The selected memory bank, formatted for a prompt, or null when no bank
        /// is selected or it has nothing to say.
        ///
        /// <para>No bank selected is the off switch, and the only one. There is
        /// deliberately no separate "use SuperMemory" checkbox to fall out of
        /// step with the picker.</para>
        ///
        /// <para>Deliberately does not take the segment text as a query, unlike
        /// the bridge's search. The block is then byte-identical for every
        /// request in a job, which is what lets the provider's prompt cache
        /// recognise it - and on the single-segment path, where the system
        /// prompt carries nothing else that varies, that turns a per-row cost
        /// into a per-job one.</para>
        /// </summary>
        public string KbContextBlock()
        {
            var bank = (SharedSettings.MemoryBank ?? string.Empty).Trim();
            if (bank.Length == 0) return null;


            var dir = global::Supervertaler.Core.MemoryBanks.DirFor(bank);
            if (dir == null)
            {
                WarnBankMissingOnce(bank);
                return null;
            }

            var key = string.Join("|", bank, SourceLangCode, TargetLangCode,
                                  NewestWrite(dir).ToString("O"));

            lock (_kbLock)
            {
                if (string.Equals(key, _kbBlockKey, StringComparison.Ordinal)) return _kbBlock;

                try
                {
                    if (_kbReader == null || !string.Equals(_kbReaderBank, bank, StringComparison.Ordinal))
                    {
                        _kbReader = new global::Supervertaler.Core.MemoryBankReader(dir);
                        _kbReaderBank = bank;
                    }
                    _kbReader.RefreshIndex();

                    var ctx = _kbReader.LoadContext(
                        ProjectNameOrNull(), null, SourceLangCode, TargetLangCode,
                        tokenBudget: PerRequestTokenBudget);

                    _kbBlock = ctx == null || !ctx.HasContent
                        ? null
                        : global::Supervertaler.Core.MemoryBankReader.FormatForPrompt(ctx);
                    _kbBlockKey = key;

                    if (_kbBlock != null) ReportBankOnce(bank, ctx);
                    return _kbBlock;
                }
                catch (Exception ex)
                {
                    // The bank is optional. A job must never fail because a
                    // markdown file could not be read.
                    PluginLog.Write("Could not load memory bank " + Quote(bank), ex);
                    _kbBlock = null;
                    _kbBlockKey = key;
                    return null;
                }
            }
        }

        /// <summary>
        /// The same bank, whole, for AutoPrompt.
        ///
        /// <para>Uncached and unshared with <see cref="KbContextBlock"/> on
        /// purpose: it is a different budget, it runs once per draft rather than
        /// once per batch, and letting the two share a slot would mean whichever
        /// ran last decided what every following translation request carried.
        /// </para>
        /// </summary>
        public string KbContextForAutoPrompt()
        {
            var bank = (SharedSettings.MemoryBank ?? string.Empty).Trim();
            if (bank.Length == 0) return null;

            var dir = global::Supervertaler.Core.MemoryBanks.DirFor(bank);
            if (dir == null)
            {
                WarnBankMissingOnce(bank);
                return null;
            }

            try
            {
                var reader = new global::Supervertaler.Core.MemoryBankReader(dir);
                reader.RefreshIndex();

                var ctx = reader.LoadContext(
                    ProjectNameOrNull(), null, SourceLangCode, TargetLangCode,
                    tokenBudget: AutoPromptTokenBudget);

                return ctx == null || !ctx.HasContent
                    ? null
                    : global::Supervertaler.Core.MemoryBankReader.FormatForPrompt(ctx);
            }
            catch (Exception ex)
            {
                PluginLog.Write("Could not load memory bank " + Quote(bank) + " for AutoPrompt", ex);
                return null;
            }
        }

        /// <summary>
        /// The newest write anywhere in the bank, and in the <c>_shared</c> bank
        /// layered under it. A bank is a handful of markdown files, so this is a
        /// cheap stat rather than a reason to add a timer - and it is what makes
        /// an edit in Obsidian take effect on the next batch.
        /// </summary>
        private static DateTime NewestWrite(string bankDir)
        {
            var newest = DateTime.MinValue;

            foreach (var dir in new[] { bankDir, SharedBankDir(bankDir) })
            {
                if (dir == null || !Directory.Exists(dir)) continue;

                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories))
                    {
                        var t = File.GetLastWriteTimeUtc(f);
                        if (t > newest) newest = t;
                    }
                }
                catch (Exception) { /* an unreadable bank rebuilds each time, which is safe */ }
            }

            return newest;
        }

        private static string SharedBankDir(string bankDir)
        {
            try
            {
                var root = Path.GetDirectoryName(bankDir);
                if (string.IsNullOrEmpty(root)) return null;

                var shared = Path.Combine(root, global::Supervertaler.Core.MemoryBankReader.SharedBankName);
                return string.Equals(shared, bankDir, StringComparison.OrdinalIgnoreCase) ? null : shared;
            }
            catch (Exception) { return null; }
        }

        private string ProjectNameOrNull()
        {
            try
            {
                var doc = CurrentDocument;
                return doc == Guid.Empty ? null : DocumentNames.Resolve(doc)?.Project;
            }
            catch (Exception) { return null; }
        }

        private void DropKbCache()
        {
            lock (_kbLock)
            {
                _kbBlock = null;
                _kbBlockKey = null;
                _warnedMissingBank = null;
                _reportedBank = null;
            }
        }

        /// <summary>
        /// Says once that the selected bank is not there. A name goes stale when
        /// the folder is renamed or deleted outside the plugin, and the symptom -
        /// prompts quietly losing their client rules - is otherwise invisible.
        /// </summary>
        private void WarnBankMissingOnce(string bank)
        {
            lock (_kbLock)
            {
                if (string.Equals(_warnedMissingBank, bank, StringComparison.Ordinal)) return;
                _warnedMissingBank = bank;
            }

            PluginLog.Write("SuperMemory: there is no memory bank called " + Quote(bank) + " under "
                + global::Supervertaler.Core.MemoryBanks.Root
                + " - nothing from it is reaching the model. Choose another in Translation settings.");
        }

        /// <summary>
        /// Says once per bank what is actually being sent, and what did not fit.
        /// At this budget trimming is normal rather than exceptional, which is
        /// exactly why it has to be visible: a rule the translator wrote down and
        /// cannot see being applied is worse than having no bank at all.
        /// </summary>
        private void ReportBankOnce(string bank, global::Supervertaler.Core.KbContext ctx)
        {
            lock (_kbLock)
            {
                if (string.Equals(_reportedBank, bank, StringComparison.Ordinal)) return;
                _reportedBank = bank;
            }

            var trimmed = ctx.TrimmedPaths != null && ctx.TrimmedPaths.Count > 0
                ? " | not sent, over the " + PerRequestTokenBudget.ToString("N0") + "-token budget: "
                  + string.Join(", ", ctx.TrimmedPaths)
                : "";

            // Characters over four is the same rough measure the reader trims by,
            // so the two numbers are at least consistent with each other.
            PluginLog.Write("SuperMemory: sending memory bank " + Quote(bank)
                + " with every request (~" + ((_kbBlock ?? string.Empty).Length / 4).ToString("N0")
                + " tokens)" + trimmed);
        }
    }
}
