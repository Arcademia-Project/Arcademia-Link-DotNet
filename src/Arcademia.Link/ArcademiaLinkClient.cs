using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Arcademia.Link
{
    public sealed class ArcademiaLinkClient : IDisposable
    {
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly ArcademiaLinkOptions _options;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly ITokenStore _store;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private StoredTokens _tokens;

        public ArcademiaLinkClient(ArcademiaLinkOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.ClientId))
                throw new ArgumentException("ClientId is required.", nameof(options));
            if (options.BaseUrl == null)
                throw new ArgumentException("BaseUrl is required.", nameof(options));

            _ownsHttp = options.HttpClient == null;
            _http = options.HttpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _store = options.TokenStore ?? FileTokenStore.ForClient(options.ClientId);
        }

        public event EventHandler SessionExpired;

        public bool IsSignedIn => _tokens != null;

        public IReadOnlyList<string> GrantedScopes =>
            _tokens?.Scope?.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

        public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
        {
            _tokens = await _store.LoadAsync(ct).ConfigureAwait(false);
            return _tokens != null;
        }

        public async Task<ArcademiaUser> SignInAsync(CancellationToken ct = default)
        {
            var verifier = Base64Url(RandomBytes(32));
            var challenge = Base64Url(Sha256(verifier));
            var state = Base64Url(RandomBytes(16));

            using (var listener = new LoopbackListener(_options.RedirectPort, _options.RedirectPath))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_options.SignInTimeout);
                var redirectUri = listener.RedirectUri;
                var authorize = BuildUri(
                    "/link/authorize",
                    new Dictionary<string, string>
                    {
                        ["response_type"] = "code",
                        ["client_id"] = _options.ClientId,
                        ["redirect_uri"] = redirectUri,
                        ["scope"] = string.Join(" ", _options.Scopes ?? new List<string>()),
                        ["state"] = state,
                        ["code_challenge"] = challenge,
                        ["code_challenge_method"] = "S256",
                    }
                );

                var callbackTask = listener.WaitForCallbackAsync(
                    query =>
                    {
                        var ok = query.ContainsKey("code") && query.TryGetValue("state", out var s) && s == state;
                        var message = ok
                            ? "You're signed in. You can close this tab and go back to " + AppName + "."
                            : query.TryGetValue("error_description", out var d) && !string.IsNullOrEmpty(d)
                                ? d
                                : "Sign-in didn't complete. You can close this tab and try again from " + AppName + ".";
                        return (_options.CallbackPage ?? DefaultPage)(ok, message);
                    },
                    timeout.Token
                );

                await (_options.OpenBrowser ?? OpenSystemBrowser)(authorize, timeout.Token).ConfigureAwait(false);

                Dictionary<string, string> result;
                try
                {
                    result = await callbackTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ArcademiaSignInCancelledException("timeout", "Sign-in timed out before the browser returned.");
                }

                if (!result.TryGetValue("state", out var returnedState) || returnedState != state)
                    throw new ArcademiaSignInCancelledException("invalid_state", "The sign-in response didn't match this request.");
                if (result.TryGetValue("error", out var error))
                    throw new ArcademiaSignInCancelledException(
                        error,
                        result.TryGetValue("error_description", out var description) ? description : error
                    );
                if (!result.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
                    throw new ArcademiaSignInCancelledException("invalid_response", "The sign-in response had no code.");

                var tokens = await RequestTokensAsync(
                        new Dictionary<string, string>
                        {
                            ["grant_type"] = "authorization_code",
                            ["client_id"] = _options.ClientId,
                            ["code"] = code,
                            ["redirect_uri"] = redirectUri,
                            ["code_verifier"] = verifier,
                        },
                        ct
                    )
                    .ConfigureAwait(false);
                await SetTokensAsync(tokens, ct).ConfigureAwait(false);
            }

            return GrantedScopes.Contains(ArcademiaLinkOptions.ProfileScope)
                ? await GetUserAsync(ct).ConfigureAwait(false)
                : null;
        }

        public async Task SignOutAsync(CancellationToken ct = default)
        {
            var tokens = _tokens ?? await _store.LoadAsync(ct).ConfigureAwait(false);
            _tokens = null;
            await _store.ClearAsync(ct).ConfigureAwait(false);
            if (tokens?.RefreshToken == null)
                return;
            try
            {
                using (
                    var content = new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["client_id"] = _options.ClientId,
                            ["token"] = tokens.RefreshToken,
                        }
                    )
                )
                using (await _http.PostAsync(BuildUri("/api/Link/Revoke", null), content, ct).ConfigureAwait(false)) { }
            }
            catch (HttpRequestException) { }
        }

        public Task<ArcademiaUser> GetUserAsync(CancellationToken ct = default) =>
            GetAsync<ArcademiaUser>("/api/Link/v1/me", ct);

        public Task<AchievementSnapshot> GetAchievementsAsync(bool unlockedOnly = false, CancellationToken ct = default) =>
            GetAsync<AchievementSnapshot>(
                "/api/Link/v1/me/achievements" + (unlockedOnly ? "?unlockedOnly=true" : ""),
                ct
            );

        public Task<ArcademiaGame> GetGameAchievementsAsync(int gameId, CancellationToken ct = default) =>
            GetAsync<ArcademiaGame>($"/api/Link/v1/me/achievements/{gameId}", ct);

        private string AppName => string.IsNullOrWhiteSpace(_options.AppName) ? "the app" : _options.AppName;

        private async Task<T> GetAsync<T>(string path, CancellationToken ct)
        {
            if (_tokens == null)
                await TryRestoreSessionAsync(ct).ConfigureAwait(false);
            if (_tokens == null)
                throw new ArcademiaSignInRequiredException("Not signed in. Call SignInAsync first.");

            if (_tokens.ExpiresAt - DateTimeOffset.UtcNow < RefreshMargin)
                await RefreshAsync(_tokens, ct).ConfigureAwait(false);

            var response = await SendAsync(path, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var stale = _tokens;
                response.Dispose();
                await RefreshAsync(stale, ct).ConfigureAwait(false);
                response = await SendAsync(path, ct).ConfigureAwait(false);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await ExpireSessionAsync(ct).ConfigureAwait(false);
                    throw new ArcademiaSignInRequiredException("The session is no longer valid. Sign in again.");
                }
                if (!response.IsSuccessStatusCode)
                    throw ToException(body, (int)response.StatusCode);
                return JsonSerializer.Deserialize<T>(body, Json);
            }
        }

        private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path, null));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            try
            {
                return await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ArcademiaLinkException("network_error", "Couldn't reach Arcademia: " + ex.Message, 0, ex);
            }
        }

        private async Task RefreshAsync(StoredTokens stale, CancellationToken ct)
        {
            await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_tokens == null)
                    throw new ArcademiaSignInRequiredException("Not signed in. Call SignInAsync first.");
                if (!ReferenceEquals(_tokens, stale))
                    return;

                StoredTokens refreshed;
                try
                {
                    refreshed = await RequestTokensAsync(
                            new Dictionary<string, string>
                            {
                                ["grant_type"] = "refresh_token",
                                ["client_id"] = _options.ClientId,
                                ["refresh_token"] = stale.RefreshToken,
                            },
                            ct
                        )
                        .ConfigureAwait(false);
                }
                catch (ArcademiaLinkException ex) when (ex.Error == "invalid_grant" || ex.Error == "invalid_client")
                {
                    await ExpireSessionAsync(ct).ConfigureAwait(false);
                    throw new ArcademiaSignInRequiredException("The session was revoked or has expired. Sign in again.");
                }
                await SetTokensAsync(refreshed, ct).ConfigureAwait(false);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task ExpireSessionAsync(CancellationToken ct)
        {
            _tokens = null;
            await _store.ClearAsync(ct).ConfigureAwait(false);
            SessionExpired?.Invoke(this, EventArgs.Empty);
        }

        private async Task SetTokensAsync(StoredTokens tokens, CancellationToken ct)
        {
            await _store.SaveAsync(tokens, ct).ConfigureAwait(false);
            _tokens = tokens;
        }

        private async Task<StoredTokens> RequestTokensAsync(Dictionary<string, string> form, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                using (var content = new FormUrlEncodedContent(form))
                    response = await _http.PostAsync(BuildUri("/api/Link/Token", null), content, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ArcademiaLinkException("network_error", "Couldn't reach Arcademia: " + ex.Message, 0, ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw ToException(body, (int)response.StatusCode);

                var token = JsonSerializer.Deserialize<TokenResponse>(body, Json);
                return new StoredTokens
                {
                    AccessToken = token.AccessToken,
                    RefreshToken = token.RefreshToken,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
                    Scope = token.Scope,
                };
            }
        }

        private static ArcademiaLinkException ToException(string body, int status)
        {
            try
            {
                var error = JsonSerializer.Deserialize<ErrorResponse>(body, Json);
                if (!string.IsNullOrEmpty(error?.Error))
                    return new ArcademiaLinkException(error.Error, error.ErrorDescription ?? error.Error, status);
            }
            catch (JsonException) { }
            return new ArcademiaLinkException(
                status == 429 ? "rate_limited" : "http_error",
                status == 429 ? "Too many requests. Wait a minute and try again." : $"Arcademia returned HTTP {status}.",
                status
            );
        }

        private Uri BuildUri(string path, Dictionary<string, string> query)
        {
            var root = _options.BaseUrl.AbsoluteUri.TrimEnd('/');
            var suffix = query == null
                ? ""
                : "?" + string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")));
            return new Uri(root + path + suffix);
        }

        private static Task OpenSystemBrowser(Uri uri, CancellationToken ct)
        {
            var url = uri.AbsoluteUri;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
            return Task.CompletedTask;
        }

        private static string DefaultPage(bool success, string message) =>
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Arcademia</title>"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#18171c;color:#ecebf0;"
            + "font-family:system-ui,-apple-system,Segoe UI,sans-serif}main{max-width:26rem;padding:2rem;text-align:center;"
            + "border:1.5px solid #34323c;border-radius:.75rem;background:#211f27}h1{font-size:1.25rem;margin:0 0 .6rem;color:"
            + (success ? "#4ade80" : "#f87171")
            + "}p{margin:0;color:#a9a6b3;line-height:1.5}</style></head><body><main><h1>"
            + (success ? "Signed in with Arcademia" : "Sign-in not completed")
            + "</h1><p>"
            + WebUtility.HtmlEncode(message)
            + "</p></main></body></html>";

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }

        private static byte[] Sha256(string value)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.ASCII.GetBytes(value));
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public void Dispose()
        {
            _refreshLock.Dispose();
            if (_ownsHttp)
                _http.Dispose();
        }

        private sealed class TokenResponse
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; }

            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; }

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }

            [JsonPropertyName("scope")]
            public string Scope { get; set; }
        }

        private sealed class ErrorResponse
        {
            [JsonPropertyName("error")]
            public string Error { get; set; }

            [JsonPropertyName("error_description")]
            public string ErrorDescription { get; set; }
        }
    }
}
