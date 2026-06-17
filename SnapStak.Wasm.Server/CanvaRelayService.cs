// ─────────────────────────────────────────────────────────────────────────────
// CanvaRelayService.cs  —  SnapStak.Wasm.Server
//
// Server-side relay between SnapStak and the Canva Connect API.
//
// WHAT THIS DOES
//   1. Stores Canva OAuth tokens per user (access + refresh) in a local JSON
//      file alongside the existing OutputRoot structure.
//   2. Accepts a "send to Canva" request from the WASM client: reads the
//      .canva.pdf file that CanvaTranslatorPlugin wrote, POSTs it to
//      POST https://api.canva.com/rest/v1/imports, polls until success,
//      and returns the edit_url to the caller.
//   3. Handles OAuth 2.0 PKCE callback: exchanges the auth code for tokens
//      and stores them for the user.
//
// OAUTH SETUP (developer one-time)
//   1. Go to https://www.canva.com/developers/ → Create an integration.
//   2. Set the redirect URL to:  http://localhost:5174/api/canva/callback
//      (or your production URL)
//   3. Enable scope:  design:content:write
//   4. Set CANVA_CLIENT_ID and CANVA_CLIENT_SECRET in environment variables
//      or appsettings.json. Never commit them to source control.
//
// OAUTH FLOW FOR USERS
//   1. GET /api/canva/auth?userId={uuid}
//      → redirects user to Canva's authorization page.
//   2. Canva redirects back to GET /api/canva/callback?code=…&state=…
//      → exchanges code for tokens, stores them, redirects back to the
//        WASM app with ?canva=connected.
//   3. POST /api/canva/send  { "componentId": "…", "userId": "…" }
//      → reads the PDF, posts to Canva, returns { "editUrl": "…" }
//
// TOKEN STORAGE
//   Tokens are stored in: {OutputRoot}/{userId}/canva_tokens.json
//   This matches the existing per-user directory structure.
//   The file is NOT encrypted in this v1 implementation — add AES encryption
//   using the same PillarEncryption pattern from LicenceService for production.
//
// RATE LIMITS (Canva Connect API)
//   POST /v1/imports  → 20 requests per minute per user
//   GET  /v1/imports/{jobId} → 120 requests per minute per user
// ─────────────────────────────────────────────────────────────────────────────

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

// ── Configuration keys ────────────────────────────────────────────────────────

public static class CanvaConfig
{
    // Set these via environment variables or appsettings.json — never hardcode.
    public static string ClientId => Environment.GetEnvironmentVariable("CANVA_CLIENT_ID") ?? string.Empty;
    public static string ClientSecret => Environment.GetEnvironmentVariable("CANVA_CLIENT_SECRET") ?? string.Empty;

    // The URL Canva redirects to after user authorisation.
    // Must match exactly what is registered in the Canva Developer Portal.
    public static string RedirectUri => Environment.GetEnvironmentVariable("CANVA_REDIRECT_URI")
                                      ?? "http://localhost:5174/api/canva/callback";

    // Canva OAuth and API base URLs
    public const string AuthorizeUrl = "https://www.canva.com/api/oauth/authorize";
    public const string TokenUrl = "https://api.canva.com/rest/v1/oauth/token";
    public const string ImportUrl = "https://api.canva.com/rest/v1/imports";
    public const string ImportStatusUrl = "https://api.canva.com/rest/v1/imports/{0}"; // {0} = jobId

    // Required OAuth scope
    public const string Scope = "design:content:write";

    // Polling configuration
    public const int PollIntervalMs = 2000;  // 2 seconds between polls
    public const int PollMaxAttempts = 30;    // 60 seconds total timeout
}

// ── Token storage model ───────────────────────────────────────────────────────

public sealed class CanvaTokens
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = string.Empty;
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }  // Unix timestamp
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
}

// ── PKCE state storage (in-memory, short-lived) ───────────────────────────────

public static class PkceStore
{
    // Maps state → (codeVerifier, userId)
    // Entries expire after 10 minutes — OAuth flow must complete within that window.
    private static readonly Dictionary<string, (string Verifier, string UserId, DateTime Expiry)>
        _store = new();
    private static readonly object _lock = new();

    public static void Save(string state, string verifier, string userId)
    {
        lock (_lock)
        {
            Purge();
            _store[state] = (verifier, userId, DateTime.UtcNow.AddMinutes(10));
        }
    }

    public static (string Verifier, string UserId)? Consume(string state)
    {
        lock (_lock)
        {
            Purge();
            if (!_store.TryGetValue(state, out var entry)) return null;
            _store.Remove(state);
            return (entry.Verifier, entry.UserId);
        }
    }

    private static void Purge()
    {
        var now = DateTime.UtcNow;
        var stale = _store.Where(kvp => kvp.Value.Expiry < now)
                           .Select(kvp => kvp.Key).ToList();
        foreach (var k in stale) _store.Remove(k);
    }
}

// ── Main relay service ────────────────────────────────────────────────────────

public static class CanvaRelayService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Step 1: Build the authorization URL and redirect the user ─────────────

    /// <summary>
    /// Builds the Canva OAuth authorization URL for a given user.
    /// The caller should redirect the user's browser to this URL.
    /// </summary>
    public static string BuildAuthUrl(string userId, out string state)
    {
        // PKCE: generate code_verifier and code_challenge
        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        state = GenerateState();

        PkceStore.Save(state, verifier, userId);

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["client_id"] = CanvaConfig.ClientId;
        query["redirect_uri"] = CanvaConfig.RedirectUri;
        query["scope"] = CanvaConfig.Scope;
        query["state"] = state;
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = "S256";

        return $"{CanvaConfig.AuthorizeUrl}?{query}";
    }

    // ── Step 2: Exchange the auth code for tokens ─────────────────────────────

    /// <summary>
    /// Handles the OAuth callback. Exchanges the authorization code for
    /// access + refresh tokens, then stores them for the user.
    /// Returns the userId on success, throws on failure.
    /// </summary>
    public static async Task<string> HandleCallbackAsync(
        string code, string state, string outputRoot)
    {
        var pkce = PkceStore.Consume(state)
            ?? throw new InvalidOperationException("Invalid or expired OAuth state.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = CanvaConfig.RedirectUri,
            ["client_id"] = CanvaConfig.ClientId,
            ["client_secret"] = CanvaConfig.ClientSecret,
            ["code_verifier"] = pkce.Verifier,
        };

        var response = await _http.PostAsync(
            CanvaConfig.TokenUrl,
            new FormUrlEncodedContent(form)).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Canva token exchange failed ({(int)response.StatusCode}): {body}");

        var tokenResponse = JsonSerializer.Deserialize<CanvaTokenResponse>(body, _json)
            ?? throw new InvalidOperationException("Empty token response from Canva.");

        var tokens = new CanvaTokens
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + tokenResponse.ExpiresIn - 60,
            UserId = pkce.UserId,
        };

        SaveTokens(outputRoot, pkce.UserId, tokens);
        Console.WriteLine($"[Canva] ✅ OAuth connected for user {pkce.UserId}");
        return pkce.UserId;
    }

    // ── Step 3: Send a component PDF to Canva ────────────────────────────────

    /// <summary>
    /// Reads the .canva.pdf file produced by CanvaTranslatorPlugin,
    /// imports it into Canva via the Connect API, polls for completion,
    /// and returns the edit_url.
    /// </summary>
    public static async Task<CanvaSendResult> SendToCanvaAsync(
        string userId,
        string componentId,
        string outputRoot)
    {
        // ── Find the PDF file ─────────────────────────────────────────────────
        var componentDir = Path.Combine(outputRoot, "local", componentId);
        var pdfPath = Path.Combine(componentDir, $"{componentId}.canva.pdf");

        if (!File.Exists(pdfPath))
            return CanvaSendResult.Fail(
                $"PDF not found at {pdfPath}. Run a SnapStak capture first.");

        var pdfBytes = await File.ReadAllBytesAsync(pdfPath).ConfigureAwait(false);

        // ── Get a valid access token ──────────────────────────────────────────
        var tokens = LoadTokens(outputRoot, userId);
        if (tokens == null)
            return CanvaSendResult.Fail("User has not connected their Canva account.");

        tokens = await EnsureFreshTokenAsync(tokens, outputRoot).ConfigureAwait(false);
        if (tokens == null)
            return CanvaSendResult.Fail("Canva token refresh failed. Please reconnect.");

        // ── POST to Canva Design Import API ───────────────────────────────────
        // Title must be Base64-encoded (Canva requirement for UTF-8 safety)
        var title = $"{componentId} — SnapStak";
        var titleB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(title));
        var metadata = JsonSerializer.Serialize(new
        {
            title_base64 = titleB64,
            mime_type = "application/pdf",
        });

        var request = new HttpRequestMessage(HttpMethod.Post, CanvaConfig.ImportUrl)
        {
            Content = new ByteArrayContent(pdfBytes),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("Import-Metadata", metadata);

        var importResponse = await _http.SendAsync(request).ConfigureAwait(false);
        var importBody = await importResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!importResponse.IsSuccessStatusCode)
            return CanvaSendResult.Fail(
                $"Canva import request failed ({(int)importResponse.StatusCode}): {importBody}");

        var importResult = JsonSerializer.Deserialize<CanvaImportResponse>(importBody, _json);
        var jobId = importResult?.Job?.Id;
        if (string.IsNullOrEmpty(jobId))
            return CanvaSendResult.Fail("Canva did not return a job ID.");

        Console.WriteLine($"[Canva] 📤 Import job {jobId} started for {componentId}");

        // ── Poll for job completion ───────────────────────────────────────────
        for (int attempt = 0; attempt < CanvaConfig.PollMaxAttempts; attempt++)
        {
            await Task.Delay(CanvaConfig.PollIntervalMs).ConfigureAwait(false);

            var statusUrl = string.Format(CanvaConfig.ImportStatusUrl, jobId);
            var poll = new HttpRequestMessage(HttpMethod.Get, statusUrl);
            poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

            var pollResponse = await _http.SendAsync(poll).ConfigureAwait(false);
            var pollBody = await pollResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!pollResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Canva] ⚠️ Poll {attempt + 1} failed: {pollBody}");
                continue;
            }

            var pollResult = JsonSerializer.Deserialize<CanvaImportResponse>(pollBody, _json);
            var status = pollResult?.Job?.Status;

            Console.WriteLine($"[Canva] 🔄 Poll {attempt + 1}: {status}");

            if (status == "success")
            {
                var editUrl = pollResult?.Job?.Result?.Designs?.FirstOrDefault()?.Urls?.EditUrl;
                var viewUrl = pollResult?.Job?.Result?.Designs?.FirstOrDefault()?.Urls?.ViewUrl;
                Console.WriteLine($"[Canva] ✅ Import complete → {editUrl}");
                return CanvaSendResult.Ok(editUrl ?? string.Empty, viewUrl ?? string.Empty);
            }

            if (status == "failed")
                return CanvaSendResult.Fail(
                    $"Canva import job failed after {attempt + 1} polls.");
        }

        return CanvaSendResult.Fail(
            $"Canva import job timed out after {CanvaConfig.PollMaxAttempts} polls.");
    }

    // ── Token management ──────────────────────────────────────────────────────

    private static async Task<CanvaTokens?> EnsureFreshTokenAsync(
        CanvaTokens tokens, string outputRoot)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < tokens.ExpiresAt)
            return tokens; // still valid

        Console.WriteLine($"[Canva] 🔄 Refreshing access token for {tokens.UserId}");

        try
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken,
                ["client_id"] = CanvaConfig.ClientId,
                ["client_secret"] = CanvaConfig.ClientSecret,
            };

            var response = await _http.PostAsync(
                CanvaConfig.TokenUrl,
                new FormUrlEncodedContent(form)).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Canva] ❌ Token refresh failed: {await response.Content.ReadAsStringAsync()}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var tokenResponse = JsonSerializer.Deserialize<CanvaTokenResponse>(body, _json);
            if (tokenResponse == null) return null;

            tokens.AccessToken = tokenResponse.AccessToken;
            tokens.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                + tokenResponse.ExpiresIn - 60;
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
                tokens.RefreshToken = tokenResponse.RefreshToken;

            SaveTokens(outputRoot, tokens.UserId, tokens);
            Console.WriteLine($"[Canva] ✅ Token refreshed for {tokens.UserId}");
            return tokens;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Canva] ❌ Token refresh exception: {ex.Message}");
            return null;
        }
    }

    private static string TokenPath(string outputRoot, string userId) =>
        Path.Combine(outputRoot, "local", userId, "canva_tokens.json");

    private static void SaveTokens(string outputRoot, string userId, CanvaTokens tokens)
    {
        var path = TokenPath(outputRoot, userId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(tokens,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static CanvaTokens? LoadTokens(string outputRoot, string userId)
    {
        var path = TokenPath(outputRoot, userId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CanvaTokens>(File.ReadAllText(path)); }
        catch { return null; }
    }

    // ── PKCE helpers ──────────────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string GenerateState()
    {
        var bytes = new byte[16];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
               .TrimEnd('=')
               .Replace('+', '-')
               .Replace('/', '_');
}

// ── Result / response types ───────────────────────────────────────────────────

public sealed class CanvaSendResult
{
    public bool Success { get; private set; }
    public string EditUrl { get; private set; } = string.Empty;
    public string ViewUrl { get; private set; } = string.Empty;
    public string Error { get; private set; } = string.Empty;

    public static CanvaSendResult Ok(string editUrl, string viewUrl) =>
        new() { Success = true, EditUrl = editUrl, ViewUrl = viewUrl };

    public static CanvaSendResult Fail(string error) =>
        new() { Success = false, Error = error };
}

// ── Canva API response models ─────────────────────────────────────────────────

internal sealed class CanvaTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = string.Empty;
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("token_type")] public string TokenType { get; set; } = string.Empty;
}

internal sealed class CanvaImportResponse
{
    [JsonPropertyName("job")] public CanvaImportJob? Job { get; set; }
}

internal sealed class CanvaImportJob
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("result")] public CanvaImportJobResult? Result { get; set; }
}

internal sealed class CanvaImportJobResult
{
    [JsonPropertyName("designs")] public List<CanvaDesignSummary>? Designs { get; set; }
}

internal sealed class CanvaDesignSummary
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("urls")] public CanvaDesignUrls? Urls { get; set; }
}

internal sealed class CanvaDesignUrls
{
    [JsonPropertyName("edit_url")] public string EditUrl { get; set; } = string.Empty;
    [JsonPropertyName("view_url")] public string ViewUrl { get; set; } = string.Empty;
}