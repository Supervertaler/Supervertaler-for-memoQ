using System;
using System.Threading;
using System.Threading.Tasks;
using Supervertaler.Core;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// memoQ's side of the one Supervertaler licence. The licence itself lives in
    /// core and is shared with Supervertaler for Trados: one key, one trial, one
    /// activation per computer, whichever product is used.
    ///
    /// <para>The policy is Trados's, deliberately, so the two products never
    /// disagree about the same licence: <b>only a licence known to have lapsed
    /// stops anything</b>, and what it stops is the AI - translation, the tools
    /// Claude and ChatGPT call, and AutoPrompt. A trial, an active licence, and a
    /// licence that could not be read all keep everything working. The
    /// terminology, the termbases, the prompts and the memory banks are never
    /// touched, because they are the translator's own work whatever the licence
    /// says.</para>
    ///
    /// <para>Compiled into the prompt editor as well as the plugin, so it must not
    /// mention memoQ's types.</para>
    /// </summary>
    internal static class Licence
    {
        /// <summary>
        /// Whether the AI features run in <paramref name="state"/>. The whole
        /// policy, and pure, so it is tested for every state without anybody's
        /// real licence being read.
        ///
        /// <para>Unknown is a yes on purpose. It means the licence could not be
        /// read - no file, a file that would not open, a clock that moved - and a
        /// missing answer must never lock out a paying customer.</para>
        /// </summary>
        internal static bool AllowsAi(LicenceState state) => state != LicenceState.Expired;

        /// <summary>
        /// The live answer. Fails open: anything going wrong in here is not the
        /// translator's fault, and it is never a reason to stop their work.
        /// </summary>
        internal static bool AiAllowed
        {
            get
            {
                // A harness reads nobody's licence. The policy is tested through
                // AllowsAi instead.
                if (SharedSettings.InHarness) return true;
                try { return AllowsAi(SupervertalerLicence.Instance.State); }
                catch { return true; }
            }
        }

        /// <summary>
        /// What the translator is told when the AI is paused. Two different
        /// situations reach Expired, and they need opposite advice: a trial that
        /// has ended wants a key, and a key that has gone 30 days unconfirmed wants
        /// a connection, not a purchase.
        /// </summary>
        internal static string PausedMessage
        {
            get
            {
                bool hasKey;
                try { hasKey = !SharedSettings.InHarness && SupervertalerLicence.Instance.HasKey; }
                catch { hasKey = false; }

                return hasKey
                    ? "Supervertaler's licence has not been confirmed online for 30 days, so AI " +
                      "translation is paused. Connect to the internet, then open the Supervertaler " +
                      "editor and choose Settings, Licence, Check now. Your termbases, prompts and " +
                      "memory banks are unaffected."
                    : "Supervertaler's free trial has ended on this computer, so AI translation is " +
                      "paused. Open the Supervertaler editor and choose Settings, Licence to enter a " +
                      "licence key. Your termbases, prompts and memory banks are unaffected.";
            }
        }

        private static int _started;

        /// <summary>
        /// Once per process: route core's licence messages into
        /// <paramref name="log"/>, then confirm the licence online in the
        /// background.
        ///
        /// <para>The log has to be set before anything touches the licence, which
        /// is why this is the first thing the plugin does rather than something
        /// done on first use. The online check never blocks start-up and never
        /// runs under a harness, where it would send the translator's real key to
        /// the licence server from a test.</para>
        /// </summary>
        internal static void Start(Action<string> log)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;

            SupervertalerLicence.Log = log;
            if (SharedSettings.InHarness) return;

            Task.Run(async () =>
            {
                try
                {
                    var licence = SupervertalerLicence.Instance;
                    if (licence.HasKey)
                        await licence.ValidateOnlineAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try { log?.Invoke("[Licence] start-up check failed: " + ex.Message); } catch { }
                }
            });
        }
    }
}
