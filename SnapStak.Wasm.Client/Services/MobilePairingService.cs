using Microsoft.JSInterop;
using Newtonsoft.Json;
using System.Net.Http.Headers;

namespace SnapStak.Wasm.Client.Services;

// ── Pairing state ─────────────────────────────────────────────────────────────

public enum PairingState
{
    Idle,           // No PIN generated
    PinActive,      // PIN generated, countdown running, listener open
    InProgress,     // Mobile connected, verifying subscription
    Succeeded,      // Pairing complete
    Failed,         // Pairing failed with an error
}

public sealed class PairedDevice
{
    [JsonProperty("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonProperty("paystackUuid")]
    public string PaystackUuid { get; set; } = string.Empty;

    [JsonProperty("pairedAt")]
    public DateTime PairedAt { get; set; }

    [JsonProperty("label")]
    public string? Label { get; set; }

    [JsonProperty("mobileIp")]
    public string? MobileIp { get; set; }
}

public sealed class PairingStatus
{
    public PairingState State { get; set; } = PairingState.Idle;
    public string? Pin { get; set; }
    public int SecondsRemaining { get; set; }
    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public string? PairedDeviceId { get; set; }
    public DateTime? PairedAt { get; set; }
    public List<PairedDevice> PairedDevices { get; set; } = new();
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Client-side pairing service.  All heavy lifting (TCP listener, crypto,
/// subscription validation) runs in the CON10X server (localhost:5174).
/// This service is a typed proxy that calls the server endpoints and
/// translates responses into UI-friendly state for Settings.razor.
/// </summary>
public sealed class MobilePairingService
{
    private readonly HttpClient _http;
    private readonly LicenceService _licence;

    // Server base is always localhost:5174 — same origin as the WASM app.
    private const string ServerBase = "http://localhost:5174";

    public MobilePairingService(LicenceService licence)
    {
        _licence = licence;
        _http = new HttpClient
        {
            BaseAddress = new Uri(ServerBase),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    // ── PIN generation ────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the server to generate a new PIN and open the pairing listener
    /// on port 5172.  Returns the PIN on success, throws on failure.
    /// </summary>
    public async Task<string> GeneratePinAsync()
    {
        var userUuid = await _licence.GetUserUuidAsync() ?? string.Empty;
        var subscriptionCode = await _licence.GetSubIdAsync() ?? string.Empty;
        var apiKey = await _licence.GetApiKeyAsync() ?? string.Empty;

        var body = JsonConvert.SerializeObject(new
        {
            paystackUuid = userUuid,
            subscriptionCode,
            openRouterKey = apiKey,
        });

        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/api/mobile/pairing/generate-pin", content);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            dynamic? err = JsonConvert.DeserializeObject(json);
            string msg = (string?)(err?.error) ?? $"Server error {(int)response.StatusCode}";
            throw new InvalidOperationException(msg);
        }

        dynamic? result = JsonConvert.DeserializeObject(json);
        string pin = (string?)(result?.pin)
            ?? throw new InvalidOperationException("Server did not return a PIN.");
        return pin;
    }

    // ── Polling ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls the server for the current pairing state.  Called by Settings.razor
    /// on a 1-second timer while a PIN is active.
    /// </summary>
    public async Task<PairingStatus> GetStatusAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/mobile/pairing/status");
            var json = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<PairingStatus>(json)
                   ?? new PairingStatus { State = PairingState.Idle };
        }
        catch
        {
            return new PairingStatus { State = PairingState.Idle };
        }
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cancels the active PIN and closes the pairing listener.
    /// </summary>
    public async Task CancelPinAsync()
    {
        try { await _http.PostAsync("/api/mobile/pairing/cancel", null); }
        catch { /* best-effort */ }
    }

    // ── Paired devices ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all devices that have been paired with this Desktop instance.
    /// </summary>
    public async Task<List<PairedDevice>> GetPairedDevicesAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/mobile/pairing/devices");
            var json = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<List<PairedDevice>>(json)
                   ?? new List<PairedDevice>();
        }
        catch
        {
            return new List<PairedDevice>();
        }
    }

    /// <summary>
    /// Removes a paired device. Requires Mobile to be running on the same WiFi.
    /// Throws InvalidOperationException with a user-facing message if Mobile
    /// cannot be reached or the operation fails.
    /// </summary>
    public async Task RemoveDeviceAsync(string deviceId)
    {
        var body = JsonConvert.SerializeObject(new { deviceId });
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/api/mobile/pairing/remove-device-ui", content);

        if (!response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            dynamic? result = JsonConvert.DeserializeObject(json);
            string msg = (string?)(result?.error) ?? "Could not remove pairing.";
            throw new InvalidOperationException(msg);
        }
    }
}