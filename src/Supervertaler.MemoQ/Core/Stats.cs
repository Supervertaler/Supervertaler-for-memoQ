using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Supervertaler.Core;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// The two calls the plugin makes to Supervertaler's own stats server, once
    /// per memoQ session, in the background. Both mirror Supervertaler for
    /// Trados, so one dashboard shows both products.
    ///
    /// <para><b>The usage ping</b> is opt-in: nothing is sent unless the
    /// translator said yes in the editor (see SharedSettings.UsageStats). It
    /// carries only what the privacy policy lists - a random id, the plugin
    /// version, the Windows version, the memoQ version, the system locale, and,
    /// from 0.1.33, the virtual machine Windows runs in, if any, and the
    /// computer's processor type (Plugin Core's MachineInfo). Those two show who
    /// runs memoQ on a Mac: "parallels", or "arm64" for Windows on ARM.
    /// The country is added by the server from the connection; no IP address is
    /// stored.</para>
    ///
    /// <para><b>The trial registration</b> is part of licensing, not analytics,
    /// exactly as in Trados: while there is no licence key, it tells the server
    /// this computer's trial start so the trial has one authoritative record.
    /// It sends the machine fingerprint the licence already uses, the plugin and
    /// memoQ versions, the locale and the trial's local start and status. The
    /// trial is one per computer and shared with Trados, so the server keeps one
    /// row per fingerprint and records which plugins that computer has used.</para>
    ///
    /// <para>Both fail silently and change nothing locally: no retry, no queue,
    /// no message, and never under a harness.</para>
    /// </summary>
    internal static class Stats
    {
        private const string Server = "https://supervertaler-stats.michaelbeijer-co-uk.workers.dev";

        private static readonly HttpClient Http = CreateClient();
        private static int _started;

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Supervertaler-memoQ/1.0");
            return http;
        }

        /// <summary>Once per process, from the director's Initialize.</summary>
        internal static void OnStart(Action<string> log)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            if (SharedSettings.InHarness) return;

            Task.Run(async () =>
            {
                await SendPingAsync().ConfigureAwait(false);
                await RegisterTrialAsync(log).ConfigureAwait(false);
            });
        }

        private static async Task SendPingAsync()
        {
            try
            {
                if (!SharedSettings.UsageStats) return;

                // The editor mints the id when the translator says yes. A yes
                // with no id means the file was edited by hand; minting one here
                // would make the plugin a second writer, so it sends nothing.
                var id = SharedSettings.UsageStatsId;
                if (id.Length == 0) return;

                var json = PingJson(id, PluginVersion(), MemoQVersion(), OsVersion(),
                                    CultureInfo.CurrentUICulture.Name,
                                    MachineInfo.VirtualizationHost(), MachineInfo.NativeArchitecture());
                await PostAsync("/ping", json).ConfigureAwait(false);
            }
            catch { /* silent: statistics never cost the translator anything */ }
        }

        private static async Task RegisterTrialAsync(Action<string> log)
        {
            try
            {
                var licence = SupervertalerLicence.Instance;
                if (licence.HasKey || licence.IsActivated) return;

                var start = licence.TrialStartedUtc;
                var json = TrialJson(MachineId.GetFingerprint(), PluginVersion(), MemoQVersion(),
                                     CultureInfo.CurrentUICulture.Name, start,
                                     TrialStatus(start, licence.State));
                var reply = await PostAsync("/trial/register", json).ConfigureAwait(false);

                // Observe-only, as in Trados: the server's start date is logged
                // for diagnostics and not used.
                if (reply != null && reply.Contains("trial_started_at"))
                    log?.Invoke("[Trial] registered with the licence server");
            }
            catch { /* silent: the trial works the same offline */ }
        }

        private static async Task<string> PostAsync(string path, string json)
        {
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await Http.PostAsync(Server + path, content).ConfigureAwait(false))
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// The trial's status as Trados reports it: "new" before the trial has a
        /// start, "trial" while it runs, "expired" once it has lapsed.
        /// </summary>
        internal static string TrialStatus(DateTime startUtc, LicenceState state) =>
            startUtc == DateTime.MinValue ? "new"
            : state == LicenceState.Trial ? "trial"
            : "expired";

        internal static string PingJson(string id, string pluginVersion, string memoqVersion,
                                        string osVersion, string locale, string vm, string arch) =>
            Serialize(new Ping
            {
                Id = id,
                Product = "memoq",
                PluginVersion = pluginVersion,
                // The server's column for the host program's version; both
                // plugins use it and the dashboard labels it by product.
                HostVersion = memoqVersion,
                OsVersion = osVersion,
                Locale = locale,
                Vm = vm,
                Arch = arch,
            });

        internal static string TrialJson(string fingerprint, string pluginVersion, string memoqVersion,
                                         string locale, DateTime startUtc, string status) =>
            Serialize(new TrialRegistration
            {
                Fingerprint = fingerprint,
                Product = "memoq",
                PluginVersion = pluginVersion,
                HostVersion = "memoQ " + memoqVersion,
                Locale = locale,
                ClaimedStart = startUtc == DateTime.MinValue
                    ? null
                    : startUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                Status = status,
            });

        private static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>"0.1.1": the add-in's version, three parts.</summary>
        internal static string PluginVersion()
        {
            try
            {
                var v = typeof(Stats).Assembly.GetName().Version;
                return v == null ? "unknown" : v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { return "unknown"; }
        }

        /// <summary>memoQ's own version, from the process the add-in runs in.</summary>
        private static string MemoQVersion()
        {
            try
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                return v == null ? "unknown" : v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { return "unknown"; }
        }

        private static string OsVersion()
        {
            try { return Environment.OSVersion.VersionString; }
            catch { return "unknown"; }
        }

        [DataContract]
        private sealed class Ping
        {
            [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
            [DataMember(Name = "product", Order = 1)] public string Product { get; set; }
            [DataMember(Name = "plugin_version", Order = 2)] public string PluginVersion { get; set; }
            [DataMember(Name = "trados_version", Order = 3)] public string HostVersion { get; set; }
            [DataMember(Name = "os_version", Order = 4)] public string OsVersion { get; set; }
            [DataMember(Name = "locale", Order = 5)] public string Locale { get; set; }
            [DataMember(Name = "vm", Order = 6)] public string Vm { get; set; }
            [DataMember(Name = "arch", Order = 7)] public string Arch { get; set; }
        }

        [DataContract]
        private sealed class TrialRegistration
        {
            [DataMember(Name = "fingerprint", Order = 0)] public string Fingerprint { get; set; }
            [DataMember(Name = "product", Order = 1)] public string Product { get; set; }
            [DataMember(Name = "plugin_version", Order = 2)] public string PluginVersion { get; set; }
            [DataMember(Name = "studio_version", Order = 3)] public string HostVersion { get; set; }
            [DataMember(Name = "locale", Order = 4)] public string Locale { get; set; }
            [DataMember(Name = "claimed_start", Order = 5, EmitDefaultValue = false)] public string ClaimedStart { get; set; }
            [DataMember(Name = "status", Order = 6)] public string Status { get; set; }
        }
    }
}
