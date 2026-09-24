using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        /// <para>Started at 6,000 on the reasoning that this is re-sent with
        /// every request while AutoPrompt buys its context once. The reasoning
        /// was sound and the number was wrong: measured against a real shared
        /// bank it carried the brief and the terminology and dropped the style
        /// guide and the method notes - 76% of the material, including the one
        /// file nothing else in the pipeline supplies. A budget that silently
        /// discards the largest and least duplicated article is not a saving,
        /// it is a quiet loss of the thing the bank exists for.</para>
        ///
        /// <para>24,000 matched the Trados plugin and was still not enough:
        /// measured on a real job it carried 18,756 tokens and dropped
        /// _shared/method.md, because the client bank had grown and the two
        /// together came to about 24,500. 32,000 clears that with room for the
        /// banks to keep growing, which they do - every job adds rows.
        /// The cost is real and worth stating: on a job the size of the
        /// 569-segment one, 57 batches at 21,000 tokens is roughly 1.2M input
        /// tokens, about six dollars at Opus 5 rates, and interactive lookups
        /// add to it. Choosing no bank remains the way to spend none of it.</para>
        /// </summary>
        internal const int PerRequestTokenBudget = 32000;

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

        // The article choice for a large bank, made once per job (see
        // BankExtract) and reused while the bank and the document stay the same,
        // so the model is asked once per job rather than once per rebuild.
        private List<string> _articleChoice;
        private string _articleChoiceKey;

        /// <summary>
        /// Below this much document text the article choice is not asked: a model
        /// shown one sentence cannot say which notes the document needs. Until
        /// then every article is kept, as before - unless the whole document is
        /// already known, from the live link, in which case a short document is a
        /// complete sample and is asked about at once. Without that, a short job
        /// would never be asked at all, and short jobs are common.
        /// </summary>
        private const int MinCharsToChooseArticles = 3000;

        /// <summary>Whether enough of the document is known to ask which articles it needs.</summary>
        internal static bool CanChooseArticles(int documentLength, bool wholeDocument) =>
            wholeDocument || documentLength >= MinCharsToChooseArticles;
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

        private void ApplyProjectMemoryBank(Guid project, string name = null)
        {
            if (RecordProject(project, name ?? ProjectNameOrNull())) DropKbCache();
        }

        /// <summary>
        /// The live document link's way in: the preview tool has shown a document
        /// of <paramref name="project"/>. The same switch a translation request
        /// from it would cause, so the panel, the memory bank and the log follow
        /// whichever channel speaks first.
        /// </summary>
        public void NoteProject(Guid project, string name)
        {
            if (project == Guid.Empty) return;
            lock (_lock)
            {
                if (project != _currentProject) _currentProject = project;
            }
            if (RecordProject(project, name)) DropKbCache();
        }

        /// <summary>
        /// Writes which project is open and points SuperMemory at the bank this
        /// project uses. Returns true when the bank changed. Static because the
        /// bridge can learn the project from the preview tool before memoQ has
        /// built an engine – on a project with MT plugins disabled it never does.
        ///
        /// <para>Only a change of project touches the bank. This runs on every
        /// cursor move once the preview tool is connected, and the bank chooser
        /// writes the bank before it records the choice, so re-applying the
        /// recorded choice on every call would race the chooser and revert it.</para>
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
        public static bool RecordProject(Guid project, string name)
        {
            try
            {
                if (project == Guid.Empty) return false;
                var id = project.ToString("D");
                var label = (name ?? string.Empty).Trim();

                // Which memoQ session said so - BEFORE the same-project return
                // below. A new session reopening yesterday's project finds the
                // project unchanged and returns early, so stamping only on a
                // change would leave a genuinely current project looking stale.
                // Compared rather than remembered: this runs on every cursor move
                // once the live document link is connected, and reading one more
                // key is cheaper than a write and needs no flag to go stale.
                var session = MemoQSession.ThisProcessStamp();
                if (session.Length > 0
                    && !string.Equals(SharedSettings.MemoryBankSession ?? string.Empty, session, StringComparison.Ordinal))
                {
                    SharedSettings.MemoryBankSession = session;
                    PluginLog.Write("Project: " + (label.Length > 0 ? Quote(label) : "an unnamed project")
                        + " is open in this memoQ session. The editor now shows it as current.");
                }

                // What the two settings dialogs record a later choice against.
                // memoQ opens them from an MT settings resource and tells them
                // nothing about projects, so this is their only way to know.
                var sameProject = string.Equals(SharedSettings.MemoryBankProject ?? string.Empty, id, StringComparison.OrdinalIgnoreCase);
                if (sameProject)
                {
                    // The name can arrive later than the GUID (the folder was not
                    // found on the first request); fill it in, touch nothing else.
                    if (label.Length > 0 && !string.Equals((SharedSettings.MemoryBankProjectName ?? string.Empty).Trim(), label, StringComparison.Ordinal))
                        SharedSettings.MemoryBankProjectName = label;
                    return false;
                }

                SharedSettings.MemoryBankProject = id;
                SharedSettings.MemoryBankProjectName = label;

                var wanted = MemoryBankChoice.ForProject(project) ?? string.Empty;
                var current = SharedSettings.MemoryBank ?? string.Empty;
                if (string.Equals(wanted, current, StringComparison.Ordinal)) return false;

                SharedSettings.MemoryBank = wanted;

                var where = label.Length == 0 ? "this project" : "project " + Quote(label);
                PluginLog.Write(wanted.Length > 0
                    ? "SuperMemory: " + where + " uses memory bank " + Quote(wanted)
                    : "SuperMemory: no memory bank is set for " + where + ", so it contributes "
                      + "nothing. The previous project's bank is deliberately not carried over - "
                      + "it would supply another client's terminology without saying so.");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Write("Could not apply the memory bank for this project", ex);
                return false;
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

        private string _glossaryRuleSaidFor;

        /// <summary>
        /// Whether the project glossary's matches for this request go to the
        /// model.
        ///
        /// <para>Not when the selected prompt was drafted by AutoPrompt. Such a
        /// prompt already ends in a locked-terms table chosen for this document,
        /// so sending the glossary alongside it supplies the same job's
        /// terminology twice from two sources that were never written to agree -
        /// and where they disagree, nothing tells the model which to follow.
        /// One authority at translation time is the point.</para>
        ///
        /// <para>The glossary itself is untouched: it still drives the
        /// terminology pane, the QA check and AutoPrompt's own reading. This
        /// governs one thing only - whether its matches are pasted into a
        /// translation request.</para>
        ///
        /// <para>Forbidden terms are the exception and go through either way.
        /// They are a different kind of statement from the rest: a preferred
        /// rendering is advice, and two sources of advice can contradict each
        /// other confusingly, whereas "never use this word" is a constraint that
        /// cannot be contradicted into ambiguity - at worst it disagrees with
        /// the prompt loudly, which is a thing the translator wants to find out.
        /// They are also few. The alternative was that a term forbidden after a
        /// prompt was drafted stayed unenforced until the prompt was drafted
        /// again, which is a trap.</para>
        /// </summary>
        public IReadOnlyList<TermIndex.Match> GlossaryForModel(IReadOnlyList<TermIndex.Match> matched)
        {
            var general = General;

            // The setting names forbidden terms explicitly - "termbase hits AND
            // forbidden terms" - so off means off, including those.
            if (!general.UseTerminologyContext || matched == null) return null;

            var path = general.PromptPath;
            if (!PromptResolver.IsDrafted(path)) return matched;

            SayGlossaryRuleOnce(path);

            var forbidden = matched.Where(m => m?.Entry != null && m.Entry.Forbidden).ToList();
            return forbidden.Count == 0 ? null : forbidden;
        }

        private void SayGlossaryRuleOnce(string path)
        {
            lock (_kbLock)
            {
                if (string.Equals(_glossaryRuleSaidFor, path, StringComparison.Ordinal)) return;
                _glossaryRuleSaidFor = path;
            }

            PluginLog.Write("Terminology: the selected prompt was drafted by AutoPrompt and carries "
                + "its own locked terms, so the glossary's preferred renderings are not being sent to "
                + "the model as well - only its forbidden terms, which always go. The glossary still "
                + "drives the terminology pane and the QA check. Draft the prompt again to take in "
                + "changes made to it since.");
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
        /// <summary>
        /// Lets one request finish before the rest of the first wave go out, so
        /// they read the prompt cache instead of each writing it (#5).
        ///
        /// <para>A cache write is not readable until the request that wrote it
        /// completes, and memoQ calls the session on <c>Parallel.ForEach</c>
        /// workers - so on a measured 38-batch run the first four requests all
        /// raced, all missed, and all paid write rate. Three of those four writes
        /// were avoidable, worth about 79 cents on that document and more as the
        /// bank grows.</para>
        ///
        /// <para>One-shot and per engine. It lives here rather than in a static
        /// because the block being cached is this engine's system prompt: a new
        /// engine means a new prompt, a new cache entry, and a gate that has to
        /// close again. If it persisted, every batch would serialise and the run
        /// would take four times as long for nothing.</para>
        /// </summary>
        private readonly SemaphoreSlim _warmGate = new SemaphoreSlim(1, 1);
        private volatile bool _warmed;

        /// <summary>
        /// True when the caller must open the gate afterwards - meaning it is the
        /// one warming the cache. False when the cache is already warm, or when
        /// waiting for the warmer failed or was cancelled: in both of those the
        /// right answer is to go ahead unsynchronised rather than to stall.
        /// </summary>
        public async Task<bool> EnterWarmupAsync(CancellationToken cancellationToken)
        {
            if (_warmed) return false;

            try
            {
                await _warmGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A cancelled or faulted wait must not stop the translation. The
                // worst case is the old behaviour - a second cache write.
                return false;
            }

            // Re-checked inside the gate: everyone queued behind the warmer arrives
            // here after it has finished, and only the first of them should have
            // been the warmer.
            if (_warmed)
            {
                _warmGate.Release();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Opens the gate after the warming request, whatever became of it.
        ///
        /// <para>Called from a finally, never at the end of the happy path: if the
        /// warming request throws - a bad key, a refused model, a dropped
        /// connection - every other batch in the run is queued behind a request
        /// that will never complete, and the whole Pre-translate hangs. The gate
        /// opening is not conditional on the call having worked.</para>
        /// </summary>
        public void LeaveWarmup()
        {
            _warmed = true;
            try { _warmGate.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }

        public string KbContextBlock()
        {
            // Switched off: nothing from any bank goes, not even _shared. Said
            // once per change rather than per request, like the bank line itself.
            if (!SharedSettings.SendMemoryBank)
            {
                ReportBankOffOnce();
                return null;
            }
            _reportedOff = false;

            var bank = (SharedSettings.MemoryBank ?? string.Empty).Trim();
            var dir = BankDir(bank);
            if (dir == null) return null;

            // The document is part of the key, so a large bank is selected for
            // the job at hand (see BankExtract). Its text grows as memoQ sends
            // rows, but the key moves only when it has doubled: every rebuild
            // changes the system prompt and costs a cache write, so a job gets a
            // handful of them rather than one per row.
            var document = DocumentText(out var documentId, out var wholeDocument);
            var newest = NewestWrite(dir).ToString("O");
            var key = string.Join("|", dir, SourceLangCode, TargetLangCode, newest,
                                  documentId, GrowthStep(document));

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

                    // Untrimmed: the extract decides what goes before the budget
                    // does, and a small bank is trimmed below exactly as before.
                    var ctx = _kbReader.LoadContext(
                        ProjectNameOrNull(), null, SourceLangCode, TargetLangCode,
                        tokenBudget: 0,
                        // A translation use: notes marked audience: assistant stay
                        // out of it. The MCP tools (SuperMemory.Context) still load
                        // them - the assistants are who they are written for.
                        forTranslation: true);

                    if (ctx != null && ctx.HasContent
                        && ctx.EstimatedTokens > global::Supervertaler.Core.BankExtract.Threshold
                        && document != null)
                    {
                        var choiceKey = string.Join("|", dir, newest, documentId);
                        var earlier = string.Equals(choiceKey, _articleChoiceKey, StringComparison.Ordinal)
                            ? _articleChoice : null;

                        var extract = global::Supervertaler.Core.BankExtract.Build(
                            ctx, document, SourceLangCode, TargetLangCode, PerRequestTokenBudget,
                            CanChooseArticles(document.Length, wholeDocument)
                                ? ChooseArticles
                                : (Func<global::Supervertaler.Core.ArticleSelectionRequest, IList<string>>)null,
                            earlier,
                            chooserName: General.Provider + " / " + General.Model);

                        if (extract.ArticleChoice != null)
                        {
                            _articleChoice = extract.ArticleChoice;
                            _articleChoiceKey = choiceKey;
                        }

                        _kbBlock = extract.Context.HasContent
                            ? global::Supervertaler.Core.MemoryBankReader.FormatForPrompt(extract.Context)
                            : null;
                        _kbBlockKey = key;
                        _reportedBank = null;   // the plain "sending" line applies again if the bank shrinks

                        WriteExtract(extract, bank);
                        return _kbBlock;
                    }

                    ctx?.TrimToTokenBudget(PerRequestTokenBudget);

                    // Sent whole: no selection for the editor to point at.
                    if (!SharedSettings.InHarness && SharedSettings.BankExtract.Length > 0)
                        SharedSettings.BankExtract = string.Empty;

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

        private bool _reportedOff;

        /// <summary>
        /// This job's document as text, for selecting from a large bank: the live
        /// document link's rows when it is connected - the whole document at once
        /// - or the rows memoQ has sent so far, whichever holds more. Null when
        /// there is nothing yet.
        /// </summary>
        private string DocumentText(out string documentId, out bool wholeDocument)
        {
            wholeDocument = false;
            var id = CurrentDocument;
            documentId = id == Guid.Empty ? MemoryKey : id.ToString("N");

            string fromPreview = null;
            try
            {
                if (id != Guid.Empty)
                {
                    var rows = PreviewStore.Rows(id);
                    if (rows.Count > 0)
                        fromPreview = TagBridge.StripTagMarkers(string.Join("\n", rows.Select(r => r.Source)));
                }
            }
            catch { /* the live link is optional */ }

            string fromCapture = null;
            var capture = CaptureStore.Get(MemoryKey);
            if (capture != null && capture.Sources.Count > 0)
                fromCapture = TagBridge.StripTagMarkers(string.Join("\n", capture.Sources));

            // The live link hands over every part of the document when it
            // connects, so text from it is the whole document; text captured from
            // translation requests is only what memoQ has asked about so far.
            var usePreview = (fromPreview?.Length ?? 0) >= (fromCapture?.Length ?? 0);
            var best = usePreview ? fromPreview : fromCapture;
            wholeDocument = usePreview && !string.IsNullOrWhiteSpace(fromPreview);
            return string.IsNullOrWhiteSpace(best) ? null : best;
        }

        /// <summary>How many extract files are kept - one per document ever translated would grow forever.</summary>
        internal const int KeepExtracts = 200;

        /// <summary>An extract file not rewritten for this long belongs to a finished job.</summary>
        internal static readonly TimeSpan KeepExtractsFor = TimeSpan.FromDays(90);

        /// <summary>
        /// Keeps the newest <paramref name="keep"/> extract files and removes any
        /// older than <paramref name="maxAge"/>, never <paramref name="justWritten"/>.
        /// Returns how many went. A file that cannot be removed is left for next
        /// time - this is housekeeping, and must never fail the write it follows.
        /// </summary>
        internal static int PruneExtracts(string folder, int keep, TimeSpan maxAge, string justWritten)
        {
            var removed = 0;
            try
            {
                var files = new DirectoryInfo(folder).GetFiles("*.md")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                var cutoff = DateTime.UtcNow - maxAge;

                for (var i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    if (string.Equals(f.FullName, justWritten, StringComparison.OrdinalIgnoreCase)) continue;
                    if (i < keep && f.LastWriteTimeUtc >= cutoff) continue;

                    try { f.Delete(); removed++; }
                    catch { /* locked or in use: next time */ }
                }
            }
            catch { /* a folder we cannot list is not worth failing over */ }
            return removed;
        }

        /// <summary>Which doubling of the document's length this is: the key moves when it doubles.</summary>
        private static string GrowthStep(string document)
        {
            if (document == null) return "none";
            return ((int)Math.Floor(Math.Log(Math.Max(1, document.Length) / 500.0, 2))).ToString();
        }

        /// <summary>
        /// The article choice for a large bank: one request to the model in the
        /// translator's settings, once per job. Null - keep every article - under
        /// a harness, while AI is paused, or when anything goes wrong; a failed
        /// choice must never stop a translation.
        /// </summary>
        private IList<string> ChooseArticles(global::Supervertaler.Core.ArticleSelectionRequest request)
        {
            if (SharedSettings.InHarness || !Licence.AiAllowed) return null;

            try
            {
                var general = General;
                using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                using (var client = new global::Supervertaler.Core.LlmClient(
                           SessionRunner.MapProviderForCore(general.Provider), general.Model, ApiKey,
                           string.IsNullOrWhiteSpace(general.Endpoint) ? null : general.Endpoint.Trim()))
                {
                    var reply = client.SendPromptAsync(
                            global::Supervertaler.Core.BankExtract.SelectionUserPrompt(request),
                            global::Supervertaler.Core.BankExtract.SelectionSystemPrompt,
                            cancellationToken: cancel.Token)
                        .ConfigureAwait(false).GetAwaiter().GetResult();

                    var usage = client.LastUsage;
                    PluginLog.Write("SuperMemory: asked " + general.Model + " which of "
                        + request.Candidates.Count + " articles this document needs"
                        + (usage == null ? "" : " | tokens: in " + usage.RegularInputTokens.ToString("N0")
                                               + " out " + usage.OutputTokens.ToString("N0")));

                    return global::Supervertaler.Core.BankExtract.ParseSelection(reply, request.Candidates);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Write("SuperMemory: the article choice could not be made; every article is kept", ex);
                return null;
            }
        }

        /// <summary>
        /// Writes what this job sends from the banks where the translator can
        /// read it, and says so once in the log. Outside the bank folders, which
        /// may be synced or kept in Git. Never under a harness.
        /// </summary>
        private void WriteExtract(global::Supervertaler.Core.BankExtractResult extract, string bank)
        {
            var summary = extract.TokensAfter.ToString("N0") + " of " + extract.TokensBefore.ToString("N0") + " tokens";
            string path = null;

            if (!SharedSettings.InHarness)
            {
                try
                {
                    var folder = Path.Combine(global::Supervertaler.Core.SupervertalerPaths.Root, "memoq", "bank-extracts");
                    Directory.CreateDirectory(folder);
                    path = Path.Combine(folder, MemoryKey + ".md");

                    var label = (ProjectNameOrNull() ?? "this project")
                        + ", " + (bank.Length == 0 ? "shared defaults only" : "bank " + Quote(bank));
                    File.WriteAllText(path,
                        global::Supervertaler.Core.BankExtract.FormatFile(extract, label, DateTime.Now),
                        new System.Text.UTF8Encoding(false));

                    SharedSettings.BankExtract = path + "|" + summary;

                    var pruned = PruneExtracts(folder, KeepExtracts, KeepExtractsFor, path);
                    if (pruned > 0) PluginLog.Write("SuperMemory: removed " + pruned + " old extract file(s)");
                }
                catch (Exception ex)
                {
                    PluginLog.Write("SuperMemory: could not write this job's extract", ex);
                }
            }

            PluginLog.Write("SuperMemory: sending a selection for this job - " + summary + " | "
                + string.Join(" ", extract.Report) + (path == null ? "" : " | " + path));
        }

        private void ReportBankOffOnce()
        {
            if (_reportedOff) return;
            _reportedOff = true;

            // Forget the last block, so switching back on reports the bank again
            // and does not serve a block read before the switch was turned off.
            lock (_kbLock) { _kbBlock = null; _kbBlockKey = null; }

            PluginLog.Write("SuperMemory: not sending the memory bank - switched off in Translation settings");
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
            // The same switch: off means the bank leaves this machine for nothing.
            if (!SharedSettings.SendMemoryBank) return null;

            var dir = BankDir((SharedSettings.MemoryBank ?? string.Empty).Trim());
            if (dir == null) return null;

            try
            {
                var reader = new global::Supervertaler.Core.MemoryBankReader(dir);
                reader.RefreshIndex();

                // AutoPrompt writes a translation prompt, so it is a translation
                // use too: assistant-only notes are not part of it.
                var ctx = reader.LoadContext(
                    ProjectNameOrNull(), null, SourceLangCode, TargetLangCode,
                    tokenBudget: AutoPromptTokenBudget, forTranslation: true);

                return ctx == null || !ctx.HasContent
                    ? null
                    : global::Supervertaler.Core.MemoryBankReader.FormatForPrompt(ctx);
            }
            catch (Exception ex)
            {
                PluginLog.Write("Could not load the memory bank at " + dir + " for AutoPrompt", ex);
                return null;
            }
        }

        /// <summary>
        /// The folder to read, given the chosen bank - and <c>_shared</c> when
        /// nothing is chosen.
        ///
        /// <para>Choosing no bank does not mean sending nothing. <c>_shared</c>
        /// is where the translator keeps what applies to every job regardless of
        /// client, and the shared reader loads it alongside whatever bank is
        /// selected; sending it on its own when none is is the same rule with
        /// the client half empty. Supervertaler for Trados has always behaved
        /// this way - its <c>_shared</c> block sits outside the "does the
        /// selected bank exist" guard - and the two products disagreeing about
        /// what a memory bank contributes is worse than either answer.</para>
        ///
        /// <para>Pointing the reader AT <c>_shared</c> rather than through it is
        /// deliberate: the reader skips the shared overlay when the bank it was
        /// given is <c>_shared</c> itself, so the articles arrive once rather
        /// than twice.</para>
        /// </summary>
        private string BankDir(string bank)
        {
            if (bank.Length == 0)
                return global::Supervertaler.Core.MemoryBanks.DirFor(
                    global::Supervertaler.Core.MemoryBankReader.SharedBankName);

            var dir = global::Supervertaler.Core.MemoryBanks.DirFor(bank);
            if (dir == null) WarnBankMissingOnce(bank);
            return dir;
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
                if (string.Equals(_reportedBank, bank ?? string.Empty, StringComparison.Ordinal)) return;
                _reportedBank = bank ?? string.Empty;
            }

            var trimmed = ctx.TrimmedPaths != null && ctx.TrimmedPaths.Count > 0
                ? " | not sent, over the " + PerRequestTokenBudget.ToString("N0") + "-token budget: "
                  + string.Join(", ", ctx.TrimmedPaths)
                : "";
            if (ctx.AssistantOnlyPaths != null && ctx.AssistantOnlyPaths.Count > 0)
                trimmed += " | not sent, for the assistants only: " + string.Join(", ", ctx.AssistantOnlyPaths);

            // Characters over four is the same rough measure the reader trims by,
            // so the two numbers are at least consistent with each other.
            PluginLog.Write("SuperMemory: sending "
                + (bank.Length == 0
                    ? "the shared defaults only, with no client bank chosen,"
                    : "memory bank " + Quote(bank))
                + " with every request (~" + ((_kbBlock ?? string.Empty).Length / 4).ToString("N0")
                + " tokens)" + trimmed);
        }
    }
}
