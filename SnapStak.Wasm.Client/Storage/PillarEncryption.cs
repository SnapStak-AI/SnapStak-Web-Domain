using Microsoft.JSInterop;

namespace SnapStak.Wasm.Client.Storage;

/// <summary>
/// Client-side pillar encryption using the browser Web Crypto API (AES-GCM 256).
///
/// Dev  mode (enabled: false) — stores values with a "plain:" prefix. Readable.
/// Prod mode (enabled: true)  — stores Base64(IV[12] + Ciphertext + AuthTag[16]).
///
/// The AES key is derived from the user's subscription token via PBKDF2
/// (SHA-256, 100 000 iterations, random per-device salt in localStorage).
/// The derived key is held in memory only — never written to storage.
///
/// "plain:" prefixed values are always returned as plaintext regardless of mode,
/// so a dev database opened in prod never crashes — it simply reads unencrypted.
/// </summary>
public sealed class PillarEncryption
{
    private readonly IJSRuntime _js;
    private readonly bool       _enabled;

    private const string PlaintextPrefix = "plain:";

    public PillarEncryption(IJSRuntime js, bool enabled)
    {
        _js      = js;
        _enabled = enabled;
    }

    public bool IsEnabled => _enabled;

    public string Encrypt(string plaintext)
    {
        if (!_enabled) return PlaintextPrefix + plaintext;
        return _js.InvokeAsync<string>("__snapstak_crypto.encrypt", plaintext)
                  .GetAwaiter().GetResult();
    }

    public string Decrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (stored.StartsWith(PlaintextPrefix)) return stored[PlaintextPrefix.Length..];
        if (!_enabled) return stored;
        return _js.InvokeAsync<string>("__snapstak_crypto.decrypt", stored)
                  .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Derives and caches the AES key from the subscription token.
    /// Must be called once after the user subscribes and on every subsequent startup.
    /// No-op in dev mode.
    /// </summary>
    public async Task InitialiseKeyAsync(string subscriptionToken)
    {
        if (!_enabled) return;
        await _js.InvokeVoidAsync("__snapstak_crypto.initKey", subscriptionToken);
    }
}
