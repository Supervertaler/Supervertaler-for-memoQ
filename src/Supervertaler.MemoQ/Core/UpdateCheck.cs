using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>What GitHub says the latest release is.</summary>
    [DataContract]
    internal sealed class ReleaseInfo
    {
        /// <summary>"0.1.2", from the tag "v0.1.2".</summary>
        [DataMember(Name = "version", Order = 0)] public string Version { get; set; }
        [DataMember(Name = "notes_url", Order = 1)] public string NotesUrl { get; set; }
        [DataMember(Name = "setup_url", Order = 2)] public string SetupUrl { get; set; }
        [DataMember(Name = "setup_size", Order = 3)] public long SetupSize { get; set; }
        /// <summary>"sha256:…" when GitHub publishes one for the asset, else empty.</summary>
        [DataMember(Name = "setup_digest", Order = 4)] public string SetupDigest { get; set; }
        [DataMember(Name = "checked_utc", Order = 5)] public DateTime CheckedUtc { get; set; }
    }

    /// <summary>
    /// Whether a newer Supervertaler for memoQ has been released. Compiled into
    /// the add-in and the editor, so it must not mention memoQ's types.
    ///
    /// <para>The answer comes from GitHub's latest release, asked at most once a
    /// day: the result is kept in <c>update-check.json</c> beside shared.txt,
    /// which either process may write - it is one file written whole, replaced
    /// rather than edited, and two writers can only ever write the same
    /// answer. The request carries a generic user agent and nothing else.</para>
    ///
    /// <para>Two places tell the translator. The editor offers the update in a
    /// dialog; and because some people never open the editor, the add-in adds a
    /// short notice to the Info line under its own hits in memoQ's Translation
    /// results (see <see cref="InfoSuffix"/>). A version skipped in the editor
    /// is quiet in both.</para>
    /// </summary>
    internal static class UpdateCheck
    {
        private const string LatestApi =
            "https://api.github.com/repos/Supervertaler/Supervertaler-for-memoQ/releases/latest";

        /// <summary>The installer's fixed name on every release (see tools/release.py).</summary>
        internal const string SetupName = "Supervertaler-for-memoQ-Setup.exe";

        private static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);
        private static readonly TimeSpan RereadEvery = TimeSpan.FromMinutes(5);

        private static readonly object Gate = new object();
        private static ReleaseInfo _known;
        private static DateTime _readUtc = DateTime.MinValue;

        private static string CachePath => Path.Combine(SharedSettings.Directory, "update-check.json");

        /// <summary>
        /// The latest release: from the cache while it is under a day old, else
        /// from GitHub. <paramref name="force"/> skips the cache (Help, Check for
        /// updates). Null when neither can say - offline, GitHub down, rate
        /// limited - which is never an error worth showing on its own.
        /// </summary>
        internal static async Task<ReleaseInfo> LatestAsync(bool force)
        {
            if (!force)
            {
                var cached = Cached();
                if (cached != null && DateTime.UtcNow - cached.CheckedUtc < CacheFor) return cached;
            }

            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Supervertaler-for-memoQ");
                    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    var json = await http.GetStringAsync(LatestApi).ConfigureAwait(false);
                    var info = FromGitHub(json, DateTime.UtcNow);
                    if (info == null) return null;
                    Remember(info);
                    return info;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// What the last check found, without touching the network. Cheap enough
        /// for the translation path: the file is re-read at most every few
        /// minutes, so the editor's check reaches a running memoQ too.
        /// </summary>
        internal static ReleaseInfo Cached()
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _readUtc < RereadEvery) return _known;
                _readUtc = DateTime.UtcNow;
                try
                {
                    if (File.Exists(CachePath))
                        _known = Deserialize<ReleaseInfo>(SettingsFile.ReadAllText(CachePath));
                }
                catch { /* a torn or foreign file is just no answer */ }
                return _known;
            }
        }

        private static void Remember(ReleaseInfo info)
        {
            lock (Gate)
            {
                _known = info;
                _readUtc = DateTime.UtcNow;
            }
            try
            {
                // File.Replace, used before, refuses outright while anyone has the
                // file open - and the plugin and the editor both read it.
                SettingsFile.WriteAllText(CachePath, Serialize(info), byteOrderMark: true);
            }
            catch
            {
                // Not written: the previous answer stands, and the next check
                // writes again. AtomicFile leaves no temporary file behind; the
                // old sweep here also deleted other processes' writes in flight.
            }
        }

        /// <summary>
        /// The release GitHub describes, or null when it is not a usable one: no
        /// version in the tag, or no installer under its fixed name.
        /// </summary>
        internal static ReleaseInfo FromGitHub(string json, DateTime checkedUtc)
        {
            GitHubRelease release;
            try { release = Deserialize<GitHubRelease>(json); }
            catch { return null; }
            if (release == null || release.Draft || release.Prerelease) return null;

            var version = (release.TagName ?? string.Empty).TrimStart('v', 'V').Trim();
            if (ParseVersion(version) == null) return null;

            foreach (var asset in release.Assets ?? new GitHubAsset[0])
            {
                if (!string.Equals(asset.Name, SetupName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(asset.Url) || asset.Size <= 0) return null;
                return new ReleaseInfo
                {
                    Version = version,
                    NotesUrl = release.HtmlUrl ?? string.Empty,
                    SetupUrl = asset.Url,
                    SetupSize = asset.Size,
                    SetupDigest = asset.Digest ?? string.Empty,
                    CheckedUtc = checkedUtc,
                };
            }
            return null;
        }

        /// <summary>"0.1.1": this build's version, three parts.</summary>
        internal static string CurrentVersion()
        {
            var v = typeof(UpdateCheck).Assembly.GetName().Version;
            return v == null ? "0.0.0" : v.Major + "." + v.Minor + "." + v.Build;
        }

        /// <summary>Whether <paramref name="latest"/> is a later version than <paramref name="current"/>.</summary>
        internal static bool IsNewer(string latest, string current)
        {
            var a = ParseVersion(latest);
            var b = ParseVersion(current);
            return a != null && b != null && a > b;
        }

        /// <summary>
        /// The release to offer: newer than this build and not skipped, or null.
        /// </summary>
        internal static ReleaseInfo Offer(ReleaseInfo latest, string current, string skipped)
        {
            if (latest == null || !IsNewer(latest.Version, current)) return null;
            if (string.Equals(latest.Version, (skipped ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            return latest;
        }

        /// <summary>
        /// Added to the Info line of the add-in's own hits while an update is
        /// waiting, or empty. From the cache only, never the network.
        /// </summary>
        internal static string InfoSuffix()
        {
            // A harness compares Info lines; a real release on GitHub must not
            // change what a test sees.
            if (SharedSettings.InHarness) return string.Empty;
            try
            {
                var offer = Offer(Cached(), CurrentVersion(), SharedSettings.UpdateSkipped);
                return offer == null
                    ? string.Empty
                    : " · Supervertaler for memoQ " + offer.Version + " is available: open the Supervertaler editor, Help, Check for updates";
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Downloads the installer <paramref name="release"/> names to
        /// <paramref name="path"/> and checks it. Returns null when the file is
        /// the one GitHub published, else why not - and then no file is left
        /// behind, so a half-download can never be run by mistake.
        /// </summary>
        internal static async Task<string> DownloadAsync(ReleaseInfo release, string path)
        {
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Supervertaler-for-memoQ");
                    using (var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var target = File.Create(path))
                            await source.CopyToAsync(target).ConfigureAwait(false);
                    }
                }

                var problem = Verify(path, release);
                if (problem != null) TryDelete(path);
                return problem;
            }
            catch (Exception ex)
            {
                TryDelete(path);
                return "the download failed (" + ex.Message + ")";
            }
        }

        /// <summary>
        /// Null when <paramref name="path"/> is the installer GitHub published:
        /// the size it states and, when GitHub gives one, the same SHA-256 digest.
        /// </summary>
        internal static string Verify(string path, ReleaseInfo release)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "the download is missing";
            if (info.Length != release.SetupSize)
                return "the download is " + info.Length + " bytes, not the " + release.SetupSize + " GitHub lists";

            var digest = release.SetupDigest ?? string.Empty;
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;

            string actual;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(path))
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            return string.Equals(actual, digest.Substring(7), StringComparison.OrdinalIgnoreCase)
                ? null
                : "the download does not match the checksum GitHub lists";
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static Version ParseVersion(string text)
        {
            var parts = (text ?? string.Empty).Split('.');
            if (parts.Length < 2 || parts.Length > 4) return null;
            return System.Version.TryParse(text, out var v) ? v : null;
        }

        private static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Deserialize<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        [DataContract]
        private sealed class GitHubRelease
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
            [DataMember(Name = "html_url")] public string HtmlUrl { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
            [DataMember(Name = "assets")] public GitHubAsset[] Assets { get; set; }
        }

        [DataContract]
        private sealed class GitHubAsset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
            [DataMember(Name = "browser_download_url")] public string Url { get; set; }
            [DataMember(Name = "digest")] public string Digest { get; set; }
        }
    }
}
