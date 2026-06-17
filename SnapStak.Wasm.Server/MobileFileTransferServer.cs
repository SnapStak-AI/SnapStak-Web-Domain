// MobileFileTransferServer.cs
//
// File queue and decryption only — no HttpListener.
// Files arrive via POST /receive in Program.cs (ASP.NET on port 5174).
// No extra ports, no firewall rules, no netsh commands required.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── File record ───────────────────────────────────────────────────────────────

public enum FileTransferStatus { Received, Decrypting, Ready, Error }

public sealed class IncomingFile
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("filename")] public string Filename { get; set; } = string.Empty;
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("receivedAt")] public DateTime ReceivedAt { get; set; }
    [JsonPropertyName("status")] public FileTransferStatus Status { get; set; }
    [JsonPropertyName("outputPath")] public string? OutputPath { get; set; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
}

// ── Mobile sync manifest entry (returned by Mobile's /api/sync/files) ─────────

/// <summary>
/// Lightweight descriptor for a pending encrypted file on the Mobile device.
/// Mobile returns a list of these from GET /api/sync/files.
/// </summary>
public sealed class MobileSyncFileEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("filename")] public string Filename { get; set; } = string.Empty;
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
}

// ── Service ───────────────────────────────────────────────────────────────────

public sealed class MobileFileTransferServer
{
    private readonly MobilePairingServer _pairing;
    private readonly string _outputRoot;
    private readonly ConcurrentDictionary<string, IncomingFile> _files = new();

    private static readonly System.Net.Http.HttpClient _syncHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    // IsListening is always true — we use ASP.NET on 5174, not a separate listener.
    public bool IsListening => true;

    public MobileFileTransferServer(MobilePairingServer pairing, string outputRoot)
    {
        _pairing = pairing;
        _outputRoot = outputRoot;
    }

    public void Start()
    {
        Console.WriteLine("[Mobile] File transfer ready. Files received via POST /receive on port 5174.");
    }

    // ── Sync: Desktop pulls files from Mobile ─────────────────────────────────

    /// <summary>
    /// Contacts the Mobile device at <paramref name="mobileIp"/>:5174, retrieves
    /// the list of pending encrypted files via GET /api/sync/files, downloads each
    /// one, decrypts with the device key, and writes to the output directory.
    ///
    /// Returns (count, userMessage).  count == -1 signals a hard failure.
    /// </summary>
    public async Task<(int pulled, string message)> SyncFromDeviceAsync(string deviceId, string mobileIp)
    {
        const int MobilePort = 5174;
        var baseUrl = $"http://{mobileIp}:{MobilePort}";

        // ── Step 1: fetch file manifest ────────────────────────────────────
        List<MobileSyncFileEntry> entries;
        try
        {
            var listResp = await _syncHttp.GetAsync($"{baseUrl}/api/sync/files");
            if (!listResp.IsSuccessStatusCode)
            {
                return (-1, $"Mobile returned {(int)listResp.StatusCode} on /api/sync/files. Make sure SnapStak Mobile is open.");
            }
            var listJson = await listResp.Content.ReadAsStringAsync();
            entries = JsonSerializer.Deserialize<List<MobileSyncFileEntry>>(listJson,
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? new List<MobileSyncFileEntry>();
        }
        catch (Exception ex)
        {
            return (-1, $"Could not reach SnapStak Mobile at {mobileIp}. Make sure both devices are on the same WiFi. ({ex.Message})");
        }

        if (entries.Count == 0)
            return (0, "No new files on Mobile.");

        // ── Step 2: download and decrypt each file ─────────────────────────
        int pulled = 0;
        int errors = 0;

        foreach (var entry in entries)
        {
            try
            {
                var fileResp = await _syncHttp.GetAsync($"{baseUrl}/api/sync/files/{entry.Id}");
                if (!fileResp.IsSuccessStatusCode) { errors++; continue; }

                var encryptedBytes = await fileResp.Content.ReadAsByteArrayAsync();
                EnqueueFile(deviceId, entry.Filename, encryptedBytes);
                pulled++;

                // Ask Mobile to mark the file as transferred
                _ = _syncHttp.PostAsync($"{baseUrl}/api/sync/files/{entry.Id}/ack", null)
                              .ContinueWith(_ => { }); // fire-and-forget
            }
            catch
            {
                errors++;
            }
        }

        var msg = pulled == 0
            ? $"Sync complete — 0 files transferred{(errors > 0 ? $", {errors} error(s)" : "")}."
            : $"Sync complete — {pulled} file{(pulled == 1 ? "" : "s")} transferred{(errors > 0 ? $", {errors} error(s)" : "")}.";

        return (pulled, msg);
    }

    // Called from POST /receive in Program.cs
    public string EnqueueFile(string deviceId, string filename, byte[] encryptedBytes)
    {
        var fileId = Guid.NewGuid().ToString("N");
        var record = new IncomingFile
        {
            Id = fileId,
            DeviceId = deviceId,
            Filename = SanitiseFilename(filename),
            SizeBytes = encryptedBytes.Length,
            ReceivedAt = DateTime.UtcNow,
            Status = FileTransferStatus.Received,
        };
        _files[fileId] = record;

        _ = Task.Run(() => DecryptAndDeliverAsync(record, encryptedBytes, deviceId));
        return fileId;
    }

    private async Task DecryptAndDeliverAsync(IncomingFile record, byte[] encryptedBytes, string deviceId)
    {
        record.Status = FileTransferStatus.Decrypting;
        try
        {
            var plaintextBytes = MobilePairingServer.DecryptAesGcmBytes(encryptedBytes, deviceId);
            var outputDir = Path.Combine(_outputRoot, "mobile", deviceId[..Math.Min(8, deviceId.Length)]);
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, record.Filename);
            await File.WriteAllBytesAsync(outputPath, plaintextBytes);
            record.OutputPath = outputPath;
            record.Status = FileTransferStatus.Ready;
            Console.WriteLine($"[Mobile] Decrypted '{record.Filename}' -> {outputPath}");
        }
        catch (CryptographicException ex)
        {
            record.Status = FileTransferStatus.Error;
            record.ErrorMessage = $"Decryption failed: {ex.Message}";
            Console.WriteLine($"[Mobile] Decryption failed for '{record.Filename}': {ex.Message}");
        }
        catch (Exception ex)
        {
            record.Status = FileTransferStatus.Error;
            record.ErrorMessage = $"Write failed: {ex.Message}";
            Console.WriteLine($"[Mobile] Write failed for '{record.Filename}': {ex.Message}");
        }
    }

    public List<IncomingFile> GetFiles()
        => _files.Values.OrderByDescending(f => f.ReceivedAt).ToList();

    public void DismissFile(string fileId)
        => _files.TryRemove(fileId, out _);

    private static string SanitiseFilename(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file.snapstak" : name;
    }
}