using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Services;

// ── File record ───────────────────────────────────────────────────────────────

public enum FileTransferStatus
{
    Received,
    Decrypting,
    Ready,
    Error,
}

public sealed class IncomingFile
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonProperty("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonProperty("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonProperty("receivedAt")]
    public DateTime ReceivedAt { get; set; }

    [JsonProperty("status")]
    public FileTransferStatus Status { get; set; }

    [JsonProperty("outputPath")]
    public string? OutputPath { get; set; }

    [JsonProperty("errorMessage")]
    public string? ErrorMessage { get; set; }
}

// ── Sync result ───────────────────────────────────────────────────────────────

public sealed class SyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Client-side file transfer service.  The actual HTTP listener on port 5173
/// and all decryption logic run in the CON10X server.  This service polls
/// the server for incoming file state and exposes it to Transfer.razor.
/// </summary>
public sealed class MobileFileTransferService
{
    private readonly HttpClient _http;

    private const string ServerBase = "http://localhost:5174";

    public MobileFileTransferService()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(ServerBase),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    // ── File list ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all files received in this session, newest first.
    /// </summary>
    public async Task<List<IncomingFile>> GetFilesAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/mobile/files");
            var json = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<List<IncomingFile>>(json)
                   ?? new List<IncomingFile>();
        }
        catch
        {
            return new List<IncomingFile>();
        }
    }

    // ── Listener status ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the server's file-receive listener on port 5173
    /// is active.
    /// </summary>
    public async Task<bool> IsListeningAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/mobile/files/listening");
            var json = await response.Content.ReadAsStringAsync();
            dynamic? result = JsonConvert.DeserializeObject(json);
            return (bool?)(result?.listening) ?? false;
        }
        catch
        {
            return false;
        }
    }

    // ── Sync ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Instructs the CON10X server to discover the paired Mobile device on the
    /// LAN, pull all pending encrypted .snapstak files via GET /api/sync/files,
    /// decrypt each one, and write them to the output directory.
    /// Returns a SyncResult with success flag and a user-facing message.
    /// </summary>
    public async Task<SyncResult> SyncFromMobileAsync()
    {
        try
        {
            // Sync can take time — scanning LAN + downloading files.
            // Use a dedicated request with a longer timeout.
            using var syncClient = new HttpClient
            {
                BaseAddress = new Uri(ServerBase),
                Timeout = TimeSpan.FromSeconds(60),
            };
            var response = await syncClient.PostAsync("/api/mobile/files/sync", null);
            var json = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<SyncResult>(json)
                   ?? new SyncResult { Success = false, Message = "No response from server." };
        }
        catch (Exception ex)
        {
            return new SyncResult { Success = false, Message = $"Sync failed: {ex.Message}" };
        }
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes a file record from the server's in-memory list.
    /// The encrypted source file on disk is retained.
    /// </summary>
    public async Task DismissFileAsync(string fileId)
    {
        var body = JsonConvert.SerializeObject(new { fileId });
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync("/api/mobile/files/dismiss", content);
    }
}