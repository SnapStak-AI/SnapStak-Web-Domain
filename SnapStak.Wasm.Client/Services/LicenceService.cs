using Microsoft.JSInterop;
using Newtonsoft.Json;
using SnapStak.Wasm.Client.Storage;

namespace SnapStak.Wasm.Client.Services;

/// <summary>
/// Manages API key, subscription validation and encryption key lifecycle.
///
/// Subscription validation flow (every app startup):
///   1. Read subscriptionId from localStorage
///   2. POST https://subscriptions.snapstak.ai/api/validate
///   3. valid: true  → derive AES key, app unlocks
///   4. valid: false → app locks, reason shown, redirects to Get started
///
/// Fail-open policy: if the validation endpoint is unreachable (network down,
/// server error) the app continues working using the cached local state.
/// This prevents legitimate users being locked out during outages.
/// Stripe webhooks ensure the cache is invalidated immediately on payment
/// failure, so the next successful validation check locks the app correctly.
/// </summary>
public sealed class LicenceService
{
    private readonly IJSRuntime _js;
    private readonly PillarEncryption _crypto;

    private const string KeyApiKey = "snapstak_api_key";
    private const string KeyLicenceValid = "snapstak_licence_valid";
    private const string KeyUserUuid = "snapstak_user_uuid";
    private const string KeySubToken = "snapstak_sub_token";
    private const string KeySubId = "snapstak_sub_id";
    private const string KeySubStatus = "snapstak_sub_status";

    private const string ValidationEndpoint = "https://subscriptions.snapstak.ai/api/validate";
    private const string ChallengeEndpoint = "https://subscriptions.snapstak.ai/api/challenge";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    public LicenceService(IJSRuntime js, PillarEncryption crypto)
    {
        _js = js;
        _crypto = crypto;
    }

    // ── API key ───────────────────────────────────────────────────────────────

    public async Task<string?> GetApiKeyAsync()
        => await _js.InvokeAsync<string?>("localStorage.getItem", KeyApiKey);

    public async Task SetApiKeyAsync(string key)
        => await _js.InvokeVoidAsync("localStorage.setItem", KeyApiKey, key);

    public async Task ClearApiKeyAsync()
        => await _js.InvokeVoidAsync("localStorage.removeItem", KeyApiKey);

    private string? _cachedUuid;

    // ── User UUID ─────────────────────────────────────────────────────────────

    public async Task<string> GetUserUuidAsync()
    {
        if (!string.IsNullOrEmpty(_cachedUuid)) return _cachedUuid;
        var uuid = await _js.InvokeAsync<string?>("localStorage.getItem", KeyUserUuid);
        if (!string.IsNullOrEmpty(uuid)) { _cachedUuid = uuid; return uuid; }
        uuid = Guid.NewGuid().ToString("N");
        await _js.InvokeVoidAsync("localStorage.setItem", KeyUserUuid, uuid);
        _cachedUuid = uuid;
        return uuid;
    }

    // ── Subscription activation (called after Stripe checkout completes) ──────

    /// <summary>
    /// Called once after Stripe checkout completes.
    /// Stores the subscription ID and token, derives the encryption key.
    /// subscriptionId — the Stripe sub_xxx ID from the checkout session
    /// subscriptionToken — a server-issued opaque token used for key derivation
    /// </summary>
    public async Task ActivateLicenceAsync(string subscriptionId, string subscriptionToken)
    {
        await _js.InvokeVoidAsync("localStorage.setItem", KeyLicenceValid, "true");
        await _js.InvokeVoidAsync("localStorage.setItem", KeySubId, subscriptionId);
        await _js.InvokeVoidAsync("localStorage.setItem", KeySubToken, subscriptionToken);
        await _js.InvokeVoidAsync("localStorage.setItem", KeySubStatus, "active");
        await _crypto.InitialiseKeyAsync(subscriptionToken);
    }

    // ── Validation (called on every app startup) ───────────────────────────────

    /// <summary>
    /// Validates the subscription against the SnapStak API server on startup.
    /// Returns a ValidationResult describing the outcome.
    ///
    /// Fail-open: if the server is unreachable, returns the cached local state
    /// rather than locking out the user. Stripe webhooks handle the authoritative
    /// invalidation path.
    /// </summary>
    public async Task<ValidationResult> ValidateSubscriptionAsync()
    {
        var subId = await _js.InvokeAsync<string?>("localStorage.getItem", KeySubId);
        var subToken = await _js.InvokeAsync<string?>("localStorage.getItem", KeySubToken);

        // First-time user — no subscription stored
        if (string.IsNullOrEmpty(subId) || string.IsNullOrEmpty(subToken))
        {
            return new ValidationResult
            {
                Valid = false,
                Status = "no_subscription",
                Message = "No subscription found. Please subscribe to continue.",
                Source = "local",
            };
        }

        return await ValidateWithServer(subId);
    }

    // Called from GetStarted when user enters their subscription code for the first time
    public async Task<ValidationResult> ValidateSubscriptionAsync(string subscriptionCode)
    {
        if (string.IsNullOrWhiteSpace(subscriptionCode))
            return new ValidationResult { Valid = false, Message = "Please enter your subscription code." };

        // Store the code so ValidateWithServer can use it
        await _js.InvokeVoidAsync("localStorage.setItem", KeySubId, subscriptionCode);
        return await ValidateWithServer(subscriptionCode);
    }

    private async Task<ValidationResult> ValidateWithServer(string subId)
    {
        var subToken = await _js.InvokeAsync<string?>("localStorage.getItem", KeySubToken);

        try
        {
            var userUuid = await GetUserUuidAsync();

            // Step 1: get challenge from server
            var challengeResponse = await _http.PostAsync(ChallengeEndpoint, null);
            var challengeJson = await challengeResponse.Content.ReadAsStringAsync();
            dynamic? challenge = Newtonsoft.Json.JsonConvert.DeserializeObject(challengeJson);
            string serverNonce = (string)(challenge?.serverNonce ?? string.Empty);
            string issuedAt = ((long)(challenge?.issuedAt ?? 0L)).ToString();
            string challengeHmac = (string)(challenge?.challengeHmac ?? string.Empty);
            string clientNonce = Guid.NewGuid().ToString("N");

            // Step 2: validate with all required fields
            var body = JsonConvert.SerializeObject(new
            {
                subscriptionCode = subId,
                userUuid,
                clientNonce,
                serverNonce,
                issuedAt,
                challengeHmac,
            });
            var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(ValidationEndpoint, content);
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<ValidationResult>(json)
                           ?? new ValidationResult { Valid = false, Status = "parse_error" };

            await _js.InvokeVoidAsync("localStorage.setItem", KeySubStatus, result.Status ?? string.Empty);
            await _js.InvokeVoidAsync("localStorage.setItem", KeyLicenceValid, result.Valid ? "true" : "false");

            if (result.Valid)
            {
                if (!string.IsNullOrEmpty(result.SubscriptionToken))
                {
                    await _js.InvokeVoidAsync("localStorage.setItem", KeySubToken, result.SubscriptionToken);
                    await _crypto.InitialiseKeyAsync(result.SubscriptionToken);
                }
            }

            return result;
        }
        catch
        {
            var cached = await _js.InvokeAsync<string?>("localStorage.getItem", KeyLicenceValid);
            var status = await _js.InvokeAsync<string?>("localStorage.getItem", KeySubStatus);
            var valid = cached == "true";

            if (valid && !string.IsNullOrEmpty(subToken))
                await _crypto.InitialiseKeyAsync(subToken);

            return new ValidationResult
            {
                Valid = valid,
                Status = status ?? "unknown",
                Message = valid
                    ? "Subscription check temporarily unavailable — using cached state."
                    : "Subscription check failed and no valid local state found.",
                Source = "failopen",
            };
        }
    }

    // ── Restore encryption key (called on startup after successful validation) ─

    /// <summary>
    /// Re-derives the AES key from the stored subscription token.
    /// Called by ValidateSubscriptionAsync — not needed separately.
    /// Kept for compatibility.
    /// </summary>
    public async Task RestoreEncryptionKeyAsync()
    {
        if (!_crypto.IsEnabled) return;
        var token = await _js.InvokeAsync<string?>("localStorage.getItem", KeySubToken);
        if (!string.IsNullOrEmpty(token))
            await _crypto.InitialiseKeyAsync(token);
    }

    // ── Simple checks ─────────────────────────────────────────────────────────

    public async Task<bool> IsLicenceValidAsync()
    {
        var v = await _js.InvokeAsync<string?>("localStorage.getItem", KeyLicenceValid);
        return v == "true";
    }

    public async Task<string?> GetSubIdAsync()
        => await _js.InvokeAsync<string?>("localStorage.getItem", KeySubId);

    public async Task<bool> IsReadyAsync()
    {
        var licence = await IsLicenceValidAsync();
        var apiKey = await GetApiKeyAsync();
        return licence && !string.IsNullOrWhiteSpace(apiKey);
    }

    public async Task<string> GetSubStatusAsync()
    {
        var v = await _js.InvokeAsync<string?>("localStorage.getItem", KeySubStatus);
        return v ?? "unknown";
    }

    public async Task DeactivateLicenceAsync()
    {
        await _js.InvokeVoidAsync("localStorage.clear");
    }
}

// ── Validation result ─────────────────────────────────────────────────────────

public sealed class ValidationResult
{
    [JsonProperty("valid")]
    public bool Valid { get; set; }

    [JsonProperty("status")]
    public string? Status { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }

    [JsonProperty("source")]
    public string? Source { get; set; }

    [JsonProperty("subscriptionToken")]
    public string? SubscriptionToken { get; set; }
}