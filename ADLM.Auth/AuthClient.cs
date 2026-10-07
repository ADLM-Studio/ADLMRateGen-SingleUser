using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ADLMRateGen.Helpers;

namespace ADLMRateGen.ADLM.Auth
{
    public sealed class AuthOptions
    {
        public string BaseUrl { get; set; } = AppEnvironment.ApiBaseUrl;
        public string ProductKey { get; set; } = AppEnvironment.ProductKey;
        public TimeSpan AccessSkew { get; set; } = TimeSpan.FromSeconds(30);
        public Func<string>? DeviceFingerprintProvider { get; set; }

        /// <summary>
        /// Supplies the legacy (v1) fingerprint so the server can migrate an
        /// existing binding to the v2 value instead of failing with DEVICE_MISMATCH.
        /// </summary>
        public Func<string>? LegacyDeviceFingerprintProvider { get; set; }

        public int TimeoutMs { get; set; } = 90000;
    }

    /// <summary>Simple DPAPI store for small secrets.</summary>
    internal sealed class SecureStore
    {
        private readonly string _name;
        public SecureStore(string name) { _name = name; }

        public void Save(string clear)
        {
            var bytes = Encoding.UTF8.GetBytes(clear ?? "");
            var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(FilePath, enc);
        }

        public string? Load()
        {
            if (!File.Exists(FilePath)) return null;
            var enc = File.ReadAllBytes(FilePath);
            var dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }

        public void Clear()
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }

        private string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ADLM.Auth");
            }
        }

        private string FilePath
        {
            get { return Path.Combine(Dir, _name + ".bin"); }
        }
    }

    /// <summary>Persist/restore cookies for the refresh flow across restarts.</summary>
    internal sealed class CookieVault
    {
        private readonly SecureStore _store;
        private readonly Uri _baseUri;

        public CookieVault(string name, string baseUrl)
        {
            _store = new SecureStore(name);
            _baseUri = new Uri(FixBaseUrl(baseUrl));
        }

        public void Save(CookieContainer jar)
        {
            var list = new List<object>();
            foreach (Cookie c in jar.GetCookies(_baseUri))
            {
                list.Add(new
                {
                    c.Name,
                    c.Value,
                    c.Domain,
                    c.Path,
                    c.Secure,
                    c.HttpOnly,
                    Expires = c.Expires == DateTime.MinValue ? (DateTime?)null : c.Expires
                });
            }
            _store.Save(JsonSerializer.Serialize(list));
        }

        public void Restore(CookieContainer jar)
        {
            var raw = _store.Load();
            if (string.IsNullOrWhiteSpace(raw)) return;

            try
            {
                JsonElement arr = JsonSerializer.Deserialize<JsonElement>(raw);
                if (arr.ValueKind != JsonValueKind.Array) return;

                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string name = el.TryGetProperty("Name", out var n) ? (n.GetString() ?? "") : "";
                    string value = el.TryGetProperty("Value", out var v) ? (v.GetString() ?? "") : "";
                    string path = el.TryGetProperty("Path", out var p) ? (p.GetString() ?? "/") : "/";
                    string domain = el.TryGetProperty("Domain", out var d)
                        ? NormalizeDomain(d.GetString(), _baseUri.Host)
                        : _baseUri.Host;

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(domain))
                        continue;

                    var ck = new Cookie(name, value, path, domain)
                    {
                        Secure = el.TryGetProperty("Secure", out var s) && s.ValueKind == JsonValueKind.True,
                        HttpOnly = el.TryGetProperty("HttpOnly", out var h) && h.ValueKind == JsonValueKind.True
                    };

                    if (el.TryGetProperty("Expires", out var ex) &&
                        ex.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(ex.GetString(), out var dt))
                    {
                        ck.Expires = dt;
                    }

                    jar.Add(_baseUri, ck);
                }
            }
            catch
            {
                // ignore corrupt cookie store
            }
        }

        public void Clear() { _store.Clear(); }

        private static string NormalizeDomain(string d, string fallbackHost)
        {
            if (string.IsNullOrWhiteSpace(d)) return fallbackHost;
            return d.Trim().TrimStart('.');
        }

        private static string FixBaseUrl(string u)
        {
            if (string.IsNullOrWhiteSpace(u)) return "/";
            return u.EndsWith("/") ? u : (u + "/");
        }
    }

    public sealed class AuthClient : IDisposable
    {
        private readonly AuthOptions _opt;

        private readonly SecureStore _licenseStore;
        private readonly SecureStore _sessionStore = new SecureStore("session.jwt");
        private readonly CookieContainer _cookies = new CookieContainer();
        private readonly CookieVault _cookieVault;

        private string _accessToken = string.Empty;
        private DateTimeOffset _accessExpiryUtc;

        private readonly HttpClient _http;

        public AuthClient(AuthOptions options)
        {
            _opt = options ?? throw new ArgumentNullException(nameof(options));
            _licenseStore = new SecureStore((_opt.ProductKey ?? "rategen") + ".license");
            _cookieVault = new CookieVault("refresh.cookies", _opt.BaseUrl);

            // .NET Framework-safe TLS1.2
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = true,
                CookieContainer = _cookies,
                Proxy = null,
                UseProxy = false
            };

            _cookieVault.Restore(_cookies);

            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(_opt.TimeoutMs) };
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            // Advertise the fingerprint algorithm version so the server can run the
            // v1 to v2 migration path for users still bound to the MAC-based value.
            _http.DefaultRequestHeaders.Add(
                "x-adlm-fp-version",
                HardwareFingerprint.FingerprintVersion.ToString());

            TryLoadSession();

            if (!string.IsNullOrWhiteSpace(_accessToken))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _accessToken);
            }
        }

        /* ================ PUBLIC API ================ */

        public string AccessToken { get { return _accessToken ?? ""; } }
        public bool HasSession { get { return !string.IsNullOrWhiteSpace(_accessToken); } }
        public string GetCachedLicenseToken() { return _licenseStore.Load(); }
        public string BaseUrl { get { return _opt.BaseUrl; } }

        public async Task<bool> LoginAsync(string identifier, string password, CancellationToken ct = default(CancellationToken))
        {
            identifier = (identifier ?? "").Trim();
            password = password ?? string.Empty;

            string dfp = "";
            try
            {
                dfp = _opt.DeviceFingerprintProvider != null ? (_opt.DeviceFingerprintProvider() ?? "") : "";
            }
            catch
            {
                dfp = "";
            }

            string legacyDfp = "";
            try
            {
                legacyDfp = _opt.LegacyDeviceFingerprintProvider != null
                    ? (_opt.LegacyDeviceFingerprintProvider() ?? "")
                    : "";
            }
            catch
            {
                legacyDfp = "";
            }

            var body = new
            {
                identifier = identifier,
                password = password,
                productKey = _opt.ProductKey,
                device_fingerprint = string.IsNullOrWhiteSpace(dfp) ? null : dfp,
                fp_version = HardwareFingerprint.FingerprintVersion,
                // Lets the server match an existing v1 binding and re-bind it to the
                // stable v2 value rather than rejecting this device as a new one.
                device_fingerprint_legacy = string.IsNullOrWhiteSpace(legacyDfp) ? null : legacyDfp
            };

            string resText = await PostJsonAsync("/auth/login", JsonSerializer.Serialize(body), null, ct);
            if (string.IsNullOrWhiteSpace(resText)) return false;

            JsonElement root = JsonDocument.Parse(resText).RootElement;

            if (!root.TryGetProperty("accessToken", out var atProp))
                throw new InvalidOperationException("Login failed: accessToken missing in server response.");

            string token = atProp.GetString();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Login failed: accessToken empty.");

            _accessToken = token.Trim();
            _accessExpiryUtc = TryGetJwtExpiryUtc(_accessToken) ?? DateTimeOffset.UtcNow.AddMinutes(14);

            SaveSession();

            // persist refresh cookie
            _cookieVault.Save(_cookies);

            // set default header
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _accessToken);

            if (root.TryGetProperty("licenseToken", out var licProp))
            {
                string lic = licProp.GetString();
                if (!string.IsNullOrWhiteSpace(lic))
                    _licenseStore.Save(lic);
            }

            return true;
        }

        public async Task PingAsync(CancellationToken ct = default(CancellationToken))
        {
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    using (var req = new HttpRequestMessage(HttpMethod.Head, CombineUrl(_opt.BaseUrl, "/")))
                    {
                        await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// ✅ RESTORED: checks subscription/entitlement for a product.
        /// </summary>
        public async Task EnsureEntitledAsync(string productKey, CancellationToken ct = default(CancellationToken))
        {
            string at = await GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(at))
                throw new InvalidOperationException("Not signed in.");

            string raw = await GetJsonRawAsync("/me/entitlements", at, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) raw = "[]";

            JsonElement arr = JsonDocument.Parse(raw).RootElement;
            bool ok = false;

            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in arr.EnumerateArray())
                {
                    string key = e.TryGetProperty("productKey", out var pk) ? (pk.GetString() ?? "") : "";
                    if (!key.Equals(productKey ?? "", StringComparison.OrdinalIgnoreCase)) continue;

                    bool statusOk =
                        e.TryGetProperty("status", out var s) &&
                        string.Equals(s.GetString(), "active", StringComparison.OrdinalIgnoreCase);

                    bool expOk =
                        e.TryGetProperty("expiresAt", out var ex) &&
                        DateTimeOffset.TryParse(ex.GetString(), out var exp) &&
                        exp > DateTimeOffset.UtcNow;

                    if (statusOk && expOk) { ok = true; break; }
                }
            }

            if (!ok)
                throw new UnauthorizedAccessException("No active subscription for '" + productKey + "'.");
        }

        public async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct = default(CancellationToken))
        {
            string at = await GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(at)) throw new InvalidOperationException("Not signed in.");

            string raw = await GetJsonRawAsync(path, at, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) raw = "{}";
            return JsonDocument.Parse(raw);
        }

        public Task<JsonDocument> PutJsonAsync(string path, object body, CancellationToken ct = default(CancellationToken))
        {
            return SendJsonAsync(HttpMethod.Put, path, body, ct);
        }

        /// <summary>
        /// Sends a JSON body with the signed-in session (refreshed when it has expired) and
        /// returns the JSON reply. 401/403 throw UnauthorizedAccessException; other failures
        /// throw the same HTTP error the GET path does.
        /// </summary>
        public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object body, CancellationToken ct = default(CancellationToken))
        {
            string at = await GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(at)) throw new InvalidOperationException("Not signed in.");

            string json = JsonSerializer.Serialize(body ?? new { });
            string url = CombineUrl(_opt.BaseUrl, path);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using (var req = new HttpRequestMessage(method, url))
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", at);
                        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            cts.CancelAfter(TimeSpan.FromMilliseconds(_opt.TimeoutMs));

                            using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                            {
                                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                                {
                                    var ex = ExtractError(text);
                                    var msg = !string.IsNullOrWhiteSpace(ex.message) ? ex.message : "Unauthorized (401).";
                                    throw new UnauthorizedAccessException(msg);
                                }

                                if (resp.StatusCode == HttpStatusCode.Forbidden)
                                {
                                    var ex = ExtractError(text);
                                    string what = !string.IsNullOrWhiteSpace(ex.code) ? (ex.message + " (" + ex.code + ")") : ex.message;
                                    throw new UnauthorizedAccessException(what);
                                }

                                if (!resp.IsSuccessStatusCode)
                                    throw MakeHttpError(resp.StatusCode, text);

                                return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                            }
                        }
                    }
                }
                catch (TaskCanceledException) when (attempt == 1)
                {
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                }
            }

            throw new TimeoutException(method.Method + " request timed out.");
        }

        public void SignOut()
        {
            _licenseStore.Clear();
            _sessionStore.Clear();
            _cookieVault.Clear();

            _accessToken = null;
            _accessExpiryUtc = default(DateTimeOffset);

            _http.DefaultRequestHeaders.Authorization = null;
        }

        public void Dispose()
        {
            try { _cookieVault.Save(_cookies); } catch { }
            _http.Dispose();
        }

        /* ================ PRIVATE HELPERS ================ */

        private void SaveSession()
        {
            string payload = JsonSerializer.Serialize(new
            {
                access = _accessToken ?? "",
                expiry = _accessExpiryUtc.UtcDateTime.ToString("o")
            });

            _sessionStore.Save(payload);
        }

        private void TryLoadSession()
        {
            string raw = _sessionStore.Load();
            if (string.IsNullOrWhiteSpace(raw)) return;

            try
            {
                JsonElement root = JsonDocument.Parse(raw).RootElement;

                string tok = root.TryGetProperty("access", out var a) ? a.GetString() : null;
                string expStr = root.TryGetProperty("expiry", out var e) ? e.GetString() : null;

                if (!string.IsNullOrWhiteSpace(tok) &&
                    DateTimeOffset.TryParse(expStr, out var exp) &&
                    exp > DateTimeOffset.UtcNow)
                {
                    _accessToken = tok.Trim();
                    _accessExpiryUtc = exp;
                }
            }
            catch { }
        }

        private int _refreshFailCount;

        private async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) &&
                DateTimeOffset.UtcNow.Add(_opt.AccessSkew) < _accessExpiryUtc)
            {
                return _accessToken;
            }

            // Retry refresh up to 2 times with a short delay between attempts
            // to handle transient network / server issues before signing out.
            const int maxAttempts = 2;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    string resText = await PostJsonAsync("/auth/refresh", "{}", null, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(resText))
                    {
                        if (attempt < maxAttempts) { await Task.Delay(1500, ct).ConfigureAwait(false); continue; }
                        return null;
                    }

                    JsonElement root = JsonDocument.Parse(resText).RootElement;
                    if (!root.TryGetProperty("accessToken", out var atProp))
                        throw new InvalidOperationException("Refresh failed: accessToken missing in server response.");

                    string token = atProp.GetString();
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        if (attempt < maxAttempts) { await Task.Delay(1500, ct).ConfigureAwait(false); continue; }
                        return null;
                    }

                    _accessToken = token.Trim();
                    _accessExpiryUtc = TryGetJwtExpiryUtc(_accessToken) ?? DateTimeOffset.UtcNow.AddMinutes(14);

                    SaveSession();

                    try { _cookieVault.Save(_cookies); } catch { }

                    _http.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", _accessToken);

                    if (root.TryGetProperty("licenseToken", out var licProp))
                    {
                        string lic = licProp.GetString();
                        if (!string.IsNullOrWhiteSpace(lic))
                            _licenseStore.Save(lic);
                    }

                    _refreshFailCount = 0;
                    return _accessToken;
                }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts)
                {
                    // First failure may be transient — wait and retry once.
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    // Second consecutive 401/403 from refresh — session is truly invalid.
                    _refreshFailCount++;
                    if (_refreshFailCount >= 2)
                    {
                        SignOut();
                    }
                    return null;
                }
                catch (Exception) when (attempt < maxAttempts)
                {
                    // Transient network / timeout — retry once.
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Non-auth failure (network, timeout) — don't sign out,
                    // just return null so caller can show an error or retry later.
                    return null;
                }
            }

            return null;
        }

        private async Task<string> GetJsonRawAsync(string path, string bearer, CancellationToken ct)
        {
            string url = CombineUrl(_opt.BaseUrl, path);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (!string.IsNullOrWhiteSpace(bearer))
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            cts.CancelAfter(TimeSpan.FromMilliseconds(_opt.TimeoutMs));

                            using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                            {
                                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                                if (resp.StatusCode == HttpStatusCode.NoContent || resp.StatusCode == HttpStatusCode.NotFound)
                                    return "[]";

                                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                                {
                                    var ex = ExtractError(text);
                                    var msg = !string.IsNullOrWhiteSpace(ex.message) ? ex.message : "Unauthorized (401).";
                                    throw new UnauthorizedAccessException(msg);
                                }

                                if (resp.StatusCode == HttpStatusCode.Forbidden)
                                {
                                    var ex = ExtractError(text);
                                    string what = !string.IsNullOrWhiteSpace(ex.code) ? (ex.message + " (" + ex.code + ")") : ex.message;
                                    throw new UnauthorizedAccessException(what);
                                }

                                if (!resp.IsSuccessStatusCode)
                                    throw MakeHttpError(resp.StatusCode, text);

                                return text;
                            }
                        }
                    }
                }
                catch (TaskCanceledException) when (attempt == 1)
                {
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                }
            }

            throw new TimeoutException("GET request timed out.");
        }

        private async Task<string> PostJsonAsync(string path, string jsonBody, string bearer, CancellationToken ct)
        {
            string url = CombineUrl(_opt.BaseUrl, path);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        if (!string.IsNullOrWhiteSpace(bearer))
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

                        req.Content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json");

                        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            cts.CancelAfter(TimeSpan.FromMilliseconds(_opt.TimeoutMs));

                            using (HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                            {
                                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                                {
                                    var ex = ExtractError(text);
                                    var msg = !string.IsNullOrWhiteSpace(ex.message) ? ex.message : "Unauthorized (401).";
                                    throw new UnauthorizedAccessException(msg);
                                }

                                if (resp.StatusCode == HttpStatusCode.Forbidden)
                                {
                                    var ex = ExtractError(text);
                                    string what = !string.IsNullOrWhiteSpace(ex.code) ? (ex.message + " (" + ex.code + ")") : ex.message;
                                    throw new UnauthorizedAccessException(what);
                                }

                                if (!resp.IsSuccessStatusCode)
                                    throw MakeHttpError(resp.StatusCode, text);

                                return text;
                            }
                        }
                    }
                }
                catch (TaskCanceledException) when (attempt == 1)
                {
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                }
            }

            throw new TimeoutException("POST request timed out.");
        }

        private static Exception MakeHttpError(HttpStatusCode status, string body)
        {
            var ex = ExtractError(body);
            string msg = string.IsNullOrWhiteSpace(ex.message) ? "Request failed." : ex.message;
            return new InvalidOperationException(((int)status) + " " + status + ": " + msg);
        }

        private static (string message, string code) ExtractError(string body)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using (JsonDocument doc = JsonDocument.Parse(body))
                    {
                        JsonElement root = doc.RootElement;
                        string msg = root.TryGetProperty("error", out var e) ? (e.GetString() ?? "") : "";
                        string code = root.TryGetProperty("code", out var c) ? (c.GetString() ?? "") : "";
                        return (msg, code);
                    }
                }
            }
            catch { }
            return ("", "");
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return path ?? "";
            if (string.IsNullOrWhiteSpace(path)) return baseUrl;

            string b = baseUrl.EndsWith("/") ? baseUrl.TrimEnd('/') : baseUrl;
            string p = path.StartsWith("/") ? path : ("/" + path);
            return b + p;
        }

        private static DateTimeOffset? TryGetJwtExpiryUtc(string jwt)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jwt)) return null;
                string[] parts = jwt.Split('.');
                if (parts.Length < 2) return null;

                string payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                using (JsonDocument doc = JsonDocument.Parse(payloadJson))
                {
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("exp", out var expEl)) return null;

                    long expSec;
                    if (expEl.ValueKind == JsonValueKind.Number && expEl.TryGetInt64(out expSec))
                        return DateTimeOffset.FromUnixTimeSeconds(expSec);

                    if (expEl.ValueKind == JsonValueKind.String && long.TryParse(expEl.GetString(), out expSec))
                        return DateTimeOffset.FromUnixTimeSeconds(expSec);
                }
            }
            catch { }
            return null;
        }

        private static byte[] Base64UrlDecode(string s)
        {
            string padded = s.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }
    }
}
