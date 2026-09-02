using System;
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
        private MTRequestMetadata _lastMetadata;

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
                stored.BridgeMode, stored.SystemPrompt);
        }

        public SupervertalerSettings Settings { get; }
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

        public void NoteMetadata(MTRequestMetadata metadata)
        {
            if (metadata == null) return;

            lock (_lock)
            {
                _lastMetadata = metadata;
                if (metadata.DocumentID != Guid.Empty) _currentDocument = metadata.DocumentID;
            }
        }
    }
}
