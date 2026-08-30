using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MemoQ.Addins.Common.Utils;
using MemoQ.MTInterfaces;
using Supervertaler.MemoQ.Settings;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Minimal LLM client for the vertical slice: one call, three providers, no
    /// streaming, no tool use, no usage accounting.
    ///
    /// DELIBERATELY THROWAWAY. The real client is
    /// <c>Supervertaler.Trados/Core/LlmClient.cs</c> (2,600 lines, already free of
    /// any Sdl.* dependency) and it moves into a shared <c>Supervertaler.Core</c>
    /// assembly once the memoQ side is proven to load. Everything here exists to
    /// answer one question — does a segment round-trip through memoQ into an LLM
    /// and back with its tags intact — without dragging the settings and model
    /// layers along for the ride. Do not grow it; replace it.
    ///
    /// JSON parsing uses memoQ's own <see cref="JSON"/> helper rather than a
    /// NuGet package. net48 has no System.Text.Json, and every assembly we do not
    /// drop into the Addins folder is one that cannot collide with memoQ's own
    /// copy of the same library.
    /// </summary>
    internal sealed class LlmClient : IDisposable
    {
        private static readonly HttpClient _http = CreateHttpClient();

        private readonly SupervertalerGeneralSettings _settings;
        private readonly string _apiKey;

        public LlmClient(SupervertalerGeneralSettings settings, string apiKey)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _apiKey = apiKey ?? string.Empty;
        }

        private static HttpClient CreateHttpClient()
        {
            // net48 picks its TLS version from ServicePointManager, and the
            // framework default still allows SSL3/TLS1.0 on some machines. All
            // three provider endpoints require 1.2 or better.
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { /* already set, or policy-locked */ }

            return new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public async Task<string> TranslateAsync(
            PromptBuilder.BuiltPrompt prompt,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException(
                    "No API key configured. Set one in Resources > Settings > MT, under Supervertaler.");

            switch (_settings.Provider)
            {
                case LlmProviders.OpenAI: return await CallOpenAiAsync(prompt, cancellationToken).ConfigureAwait(false);
                case LlmProviders.Google: return await CallGoogleAsync(prompt, cancellationToken).ConfigureAwait(false);
                default: return await CallAnthropicAsync(prompt, cancellationToken).ConfigureAwait(false);
            }
        }

        // ---- providers --------------------------------------------------------

        private async Task<string> CallAnthropicAsync(PromptBuilder.BuiltPrompt prompt, CancellationToken ct)
        {
            var url = Endpoint("https://api.anthropic.com/v1/messages");

            var body = "{" +
                "\"model\":" + Json(_settings.Model) + "," +
                "\"max_tokens\":4096," +
                "\"system\":" + Json(prompt.System) + "," +
                "\"messages\":[{\"role\":\"user\",\"content\":" + Json(prompt.User) + "}]" +
                "}";

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var json = await SendAsync(req, ct).ConfigureAwait(false);

                // { "content": [ { "type": "text", "text": "..." } ] }
                var content = Get(json, "content") as JSONArray;
                var text = content?.Values
                    .OfType<JSONObject>()
                    .Select(o => AsString(Get(o, "text")))
                    .FirstOrDefault(t => t != null);

                return text ?? throw new MTException(
                    "Supervertaler", "The Anthropic response contained no text.", null);
            }
        }

        private async Task<string> CallOpenAiAsync(PromptBuilder.BuiltPrompt prompt, CancellationToken ct)
        {
            var url = Endpoint("https://api.openai.com/v1/chat/completions");

            var body = "{" +
                "\"model\":" + Json(_settings.Model) + "," +
                "\"messages\":[" +
                    "{\"role\":\"system\",\"content\":" + Json(prompt.System) + "}," +
                    "{\"role\":\"user\",\"content\":" + Json(prompt.User) + "}" +
                "]}";

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var json = await SendAsync(req, ct).ConfigureAwait(false);

                // { "choices": [ { "message": { "content": "..." } } ] }
                var choices = Get(json, "choices") as JSONArray;
                var first = choices?.Values.OfType<JSONObject>().FirstOrDefault();
                var text = AsString(Get(Get(first, "message") as JSONObject, "content"));

                return text ?? throw new MTException(
                    "Supervertaler", "The OpenAI response contained no text.", null);
            }
        }

        private async Task<string> CallGoogleAsync(PromptBuilder.BuiltPrompt prompt, CancellationToken ct)
        {
            var baseUrl = string.IsNullOrWhiteSpace(_settings.Endpoint)
                ? "https://generativelanguage.googleapis.com/v1beta/models"
                : _settings.Endpoint.TrimEnd('/');

            var url = baseUrl + "/" + _settings.Model + ":generateContent?key=" + Uri.EscapeDataString(_apiKey);

            var body = "{" +
                "\"systemInstruction\":{\"parts\":[{\"text\":" + Json(prompt.System) + "}]}," +
                "\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":" + Json(prompt.User) + "}]}]" +
                "}";

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var json = await SendAsync(req, ct).ConfigureAwait(false);

                // { "candidates": [ { "content": { "parts": [ { "text": "..." } ] } } ] }
                var candidates = Get(json, "candidates") as JSONArray;
                var first = candidates?.Values.OfType<JSONObject>().FirstOrDefault();
                var parts = Get(Get(first, "content") as JSONObject, "parts") as JSONArray;
                var text = parts?.Values
                    .OfType<JSONObject>()
                    .Select(p => AsString(Get(p, "text")))
                    .FirstOrDefault(t => t != null);

                return text ?? throw new MTException(
                    "Supervertaler", "The Google response contained no text.", null);
            }
        }

        // ---- transport --------------------------------------------------------

        private async Task<JSONObject> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PluginLog.Write("HTTP request failed", ex);
                throw new MTException(
                    "Supervertaler", "Could not reach the AI provider: " + ex.Message, ex);
            }

            using (response)
            {
                var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    PluginLog.Write($"Provider returned {(int)response.StatusCode}: {Truncate(raw, 1000)}");
                    throw new MTException(
                        "Supervertaler",
                        $"The AI provider returned {(int)response.StatusCode} ({response.ReasonPhrase}). "
                        + "See %LocalAppData%\\Supervertaler.memoQ\\plugin.log for the full response.",
                        null);
                }

                var parsed = JSON.ParseJSON(raw) as JSONObject;
                if (parsed == null)
                {
                    PluginLog.Write("Could not parse provider response as JSON: " + Truncate(raw, 1000));
                    throw new MTException(
                        "Supervertaler", "The AI provider returned a response that was not valid JSON.", null);
                }

                return parsed;
            }
        }

        private string Endpoint(string fallback)
        {
            return string.IsNullOrWhiteSpace(_settings.Endpoint) ? fallback : _settings.Endpoint.Trim();
        }

        // ---- JSON helpers -----------------------------------------------------

        private static JSONValue Get(JSONObject obj, string key)
        {
            if (obj?.Pairs == null || key == null) return null;
            return obj.Pairs.TryGetValue(key, out var value) ? value : null;
        }

        private static string AsString(JSONValue value)
        {
            return (value as JSONString)?.Text;
        }

        /// <summary>Serialise a .NET string as a JSON string literal, quotes included.</summary>
        internal static string Json(string s)
        {
            if (s == null) return "null";

            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Escape control characters and anything above the BMP-safe
                        // range so the payload is pure ASCII on the wire — some
                        // corporate proxies mangle raw UTF-8 in request bodies.
                        if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            return s != null && s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        public void Dispose()
        {
            // _http is static and shared on purpose: a new HttpClient per session
            // exhausts sockets under a batch run. Nothing per-instance to release.
        }
    }
}
