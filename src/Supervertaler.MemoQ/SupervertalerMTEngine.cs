using System;
using System.Drawing;
using MemoQ.MTInterfaces;
using Supervertaler.MemoQ.Core;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ
{
    /// <summary>
    /// One engine per language pair per project. memoQ creates it via
    /// <see cref="SupervertalerMTPluginDirector.CreateEngine"/> and then asks it
    /// for sessions — one session per unit of work, several possibly live at once.
    ///
    /// The engine owns the <see cref="EngineContext"/>: the settings, the language
    /// pair, and the document memoQ is currently working in. Sessions are cheap and
    /// disposable; the context outlives them, which is what lets the store session
    /// and the translate session refer to the same document.
    /// </summary>
    internal class SupervertalerMTEngine : EngineBase, IEngine2, IEngineCommon, IParallelEngine
    {
        private readonly EngineContext _context;

        public SupervertalerMTEngine(SupervertalerSettings settings, string sourceLangCode, string targetLangCode)
        {
            _context = new EngineContext(settings, sourceLangCode, targetLangCode);
        }

        public override Image SmallIcon => IconLoader.Small;

        /// <summary>Paired with <c>SupportFuzzyForwarding</c> on the director — see the note there.</summary>
        public override bool SupportsFuzzyCorrection => false;

        /// <summary>
        /// How many sessions memoQ may run at once. Clamped: a batch run at 32
        /// parallel requests hits provider rate limits and turns a slow job into a
        /// failed one.
        /// </summary>
        public override int MaxDegreeOfParallelism
        {
            get
            {
                var configured = _context.General.MaxParallelRequests;
                return Math.Max(1, Math.Min(16, configured));
            }
        }

        /// <summary>
        /// The path memoQ actually uses, for both interactive lookup and
        /// Pre-translate. The returned session also implements
        /// <see cref="ISessionWithMetadata"/>, which is where project and document
        /// identity arrives.
        /// </summary>
        public override ISession CreateLookupSession()
        {
            PluginLog.Write("CreateLookupSession (ISession + ISessionWithMetadata)");
            return new SupervertalerSession(_context);
        }

        /// <summary>
        /// Kept implemented, but memoQ has never been observed to call it for a
        /// third-party MT plugin — neither the bundled ModernMT plugin nor the
        /// current Lara plugin implements it at all. If it ever does fire, we get
        /// memoQ's own terminology and neighbouring-segment context for free, so it
        /// is worth keeping and worth logging loudly.
        /// </summary>
        public override IRichSession CreateRichLookupSession()
        {
            PluginLog.Write("CreateRichLookupSession CALLED — memoQ is offering rich context. "
                + "This has not been seen before; check the 'context:' line that follows.");
            return new SupervertalerRichSession(_context);
        }

        /// <summary>
        /// Every segment the translator confirms. Requires
        /// <c>StoringTranslationSupported</c> on the director.
        /// </summary>
        public override ISessionForStoringTranslations CreateStoreTranslationSession()
        {
            PluginLog.Write("CreateStoreTranslationSession (confirmed segments will be captured)");
            return new SupervertalerStoreSession(_context);
        }

        public override void SetProperty(string name, string value)
        {
            // memoQ uses this to push ad-hoc engine properties. None are known to
            // apply to us; log so an unexpected one shows up rather than vanishing.
            PluginLog.Write($"SetProperty: {name} = {value}");
        }

        public override void Dispose()
        {
            // No unmanaged state: the HttpClient is static and shared, and
            // DocumentMemory deliberately outlives the engine.
        }
    }
}
