using System;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace CrossPlatformPatcher.Core
{
    /// <summary>
    /// Loopback HTTP client for the Linux-native Vosk sidecar
    /// (<c>vosk-sidecar.py</c>). Lets the 32-bit app process use 64-bit
    /// libvosk without loading it in-process.
    ///
    /// Fail-closed by contract: every failure (unreachable server, timeout,
    /// malformed reply) returns null/false without throwing, so callers keep
    /// the existing Vosk-unavailable path. No exception from this class ever
    /// reaches the recognition pipeline.
    ///
    /// Mid-window finals surface at lock end via <see cref="GetFinal"/> –
    /// same text, slightly later than the native per-chunk Result path.
    /// </summary>
    public sealed class VoskSidecarClient : IDisposable
    {
        public const string DefaultBaseUrl = "http://127.0.0.1:18080/";
        public const string UrlVariable = "PAICOM_VOSK_SIDECAR_URL";

        private readonly string _baseUrl;
        private readonly Action<string>? _log;
        private readonly HttpClient _http;
        private int _failureCount;
        private bool _disposed;

        public VoskSidecarClient(string? baseUrl = null, Action<string>? logger = null, HttpMessageHandler? handler = null)
        {
            _baseUrl = NormalizeBaseUrl(ResolveBaseUrl(baseUrl));
            _log = logger;
            _http = handler != null
                ? new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) }
                : new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        }

        public string BaseUrl
        {
            get { return _baseUrl; }
        }

        public static string ResolveBaseUrl(string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return configured.Trim();
            var env = Environment.GetEnvironmentVariable(UrlVariable);
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();
            return DefaultBaseUrl;
        }

        public bool CheckHealth()
        {
            try
            {
                using (var response = _http.GetAsync(_baseUrl + "health").GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return false;
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return body != null && body.Contains("\"ok\"");
                }
            }
            catch (Exception ex)
            {
                LogFailure("health", ex);
                return false;
            }
        }

        public bool Init(float sampleRate, string[]? grammar)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"samplerate\":");
                sb.Append(sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"grammar\":[");
                if (grammar != null)
                {
                    bool first = true;
                    foreach (var term in grammar)
                    {
                        if (string.IsNullOrWhiteSpace(term))
                            continue;
                        if (!first)
                            sb.Append(',');
                        first = false;
                        sb.Append('\"');
                        sb.Append(EscapeJson(term));
                        sb.Append('\"');
                    }
                }
                sb.Append("]}");
                using (var content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json"))
                using (var response = _http.PostAsync(_baseUrl + "init", content).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return false;
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return body != null && body.Contains("\"ok\"");
                }
            }
            catch (Exception ex)
            {
                LogFailure("init", ex);
                return false;
            }
        }

        public bool Accept(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0)
                return false;
            try
            {
                string payload = "{\"pcm_b64\":\"" + Convert.ToBase64String(pcm) + "\"}";
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                using (var response = _http.PostAsync(_baseUrl + "accept", content).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return false;
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return body != null && body.Contains("\"ok\"");
                }
            }
            catch (Exception ex)
            {
                LogFailure("accept", ex);
                return false;
            }
        }

        public string? GetPartial()
        {
            try
            {
                using (var response = _http.GetAsync(_baseUrl + "partial").GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return ExtractField(body, "partial");
                }
            }
            catch (Exception ex)
            {
                LogFailure("partial", ex);
                return null;
            }
        }

        public string? GetFinal()
        {
            try
            {
                using (var response = _http.GetAsync(_baseUrl + "final").GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return ExtractField(body, "text");
                }
            }
            catch (Exception ex)
            {
                LogFailure("final", ex);
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _http.Dispose();
            }
            catch
            {
                // Dispose must never throw.
            }
        }

        public static string NormalizeBaseUrl(string url)
        {
            string trimmed = (url ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return DefaultBaseUrl;
            return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
        }

        public static string? ExtractField(string? body, string field)
        {
            if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(field))
                return null;
            string token = "\"" + field + "\":\"";
            int start = body.IndexOf(token, StringComparison.Ordinal);
            if (start < 0)
                return null;
            start += token.Length;
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < body.Length; i++)
            {
                char ch = body[i];
                if (ch == '\\' && i + 1 < body.Length)
                {
                    char esc = body[i + 1];
                    if (esc == 'n') sb.Append('\n');
                    else if (esc == 't') sb.Append('\t');
                    else if (esc == 'r') sb.Append('\r');
                    else if (esc == 'u' && i + 5 < body.Length)
                    {
                        int code;
                        if (int.TryParse(body.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code))
                            sb.Append((char)code);
                        i += 5;
                    }
                    else sb.Append(esc);
                    i++;
                    continue;
                }
                if (ch == '"')
                    break;
                sb.Append(ch);
            }
            string value = sb.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void LogFailure(string operation, Exception ex)
        {
            int count = Interlocked.Increment(ref _failureCount);
            if (_log == null)
                return;
            if (count == 1 || count % 50 == 0)
            {
                try
                {
                    _log("[vosk-sidecar] " + operation + " failed (" + count + "): " +
                        ex.GetType().Name + ": " + ex.Message);
                }
                catch
                {
                    // Logging must never throw.
                }
            }
        }
    }
}
