// MobilePairingServer.cs
//
// Pairing logic only — no raw TCP listener.
// Mobile discovers Desktop by scanning port 5174 (the existing ASP.NET server).
// The /pair and /discover endpoints are registered in Program.cs via ASP.NET,
// so no extra ports, no firewall rules, no netsh commands required.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── Shared types ──────────────────────────────────────────────────────────────

public enum PairingState
{
    Idle,
    PinActive,
    InProgress,
    Succeeded,
    Failed,
}

public sealed class PairedDevice
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("paystackUuid")] public string PaystackUuid { get; set; } = string.Empty;
    [JsonPropertyName("pairedAt")] public DateTime PairedAt { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("mobileIp")] public string? MobileIp { get; set; }
}

public sealed class PairingStatus
{
    [JsonPropertyName("state")] public PairingState State { get; set; }
    [JsonPropertyName("pin")] public string? Pin { get; set; }
    [JsonPropertyName("secondsRemaining")] public int SecondsRemaining { get; set; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
    [JsonPropertyName("pairedDeviceId")] public string? PairedDeviceId { get; set; }
    [JsonPropertyName("pairedAt")] public DateTime? PairedAt { get; set; }
    [JsonPropertyName("pairedDevices")] public List<PairedDevice> PairedDevices { get; set; } = new();
}

// ── Service ───────────────────────────────────────────────────────────────────

public sealed class MobilePairingServer
{
    private const int PinLifetimeSeconds = 60;
    private const int Pbkdf2Iterations = 100_000;
    private const int KeyBytes = 32;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;

    private readonly object _lock = new();
    private string? _pin;
    private DateTime _pinExpiresAt;
    private bool _pinUsed;
    private PairingState _state = PairingState.Idle;
    private string? _lastPairedDeviceId;
    private DateTime? _lastPairedAt;
    private string? _errorMessage;
    private string _paystackUuid = string.Empty;
    private string _subscriptionCode = string.Empty;
    private string _openRouterKey = string.Empty;

    private readonly string _devicesFilePath;
    private readonly List<PairedDevice> _devices = new();
    private readonly object _devicesLock = new();

    public MobilePairingServer(ServerSettings settings)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "SnapStak");
        Directory.CreateDirectory(dir);
        _devicesFilePath = Path.Combine(dir, "paired_devices.json");
    }

    public void Start()
    {
        LoadDevices();
        Console.WriteLine("[Mobile] Pairing service ready. Endpoints served on port 5174.");
    }

    // ── PIN generation ────────────────────────────────────────────────────────

    public string GeneratePin(string paystackUuid, string subscriptionCode, string openRouterKey)
    {
        lock (_lock)
        {
            _paystackUuid = paystackUuid;
            _subscriptionCode = subscriptionCode;
            _openRouterKey = openRouterKey;
            _pin = GenerateSecurePin();
            _pinExpiresAt = DateTime.UtcNow.AddSeconds(PinLifetimeSeconds);
            _pinUsed = false;
            _state = PairingState.PinActive;
            _errorMessage = null;
            _lastPairedDeviceId = null;
            _lastPairedAt = null;
        }
        Console.WriteLine("[Mobile] PIN generated — waiting for Mobile to connect.");
        return _pin!;
    }

    // ── Status ────────────────────────────────────────────────────────────────

    public PairingStatus GetStatus()
    {
        lock (_lock)
        {
            int secsRemaining = 0;
            if (_state == PairingState.PinActive)
            {
                secsRemaining = Math.Max(0, (int)(_pinExpiresAt - DateTime.UtcNow).TotalSeconds);
                if (secsRemaining == 0)
                {
                    _state = PairingState.Idle;
                    _errorMessage = null;
                }
            }

            return new PairingStatus
            {
                State = _state,
                Pin = _state == PairingState.PinActive ? _pin : null,
                SecondsRemaining = secsRemaining,
                ErrorMessage = _errorMessage,
                PairedDeviceId = _lastPairedDeviceId,
                PairedAt = _lastPairedAt,
                PairedDevices = GetPairedDevices(),
            };
        }
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    public void CancelPin()
    {
        lock (_lock)
        {
            _pin = null;
            _state = PairingState.Idle;
        }
    }

    // ── Pair request (called from POST /pair in Program.cs) ───────────────────

    public Task<(int StatusCode, string Body)> HandlePairAsync(string deviceId, string pin, string? mobileIp)
    {
        string paystackUuid;
        string subscriptionCode;
        string openRouterKey;

        lock (_lock)
        {
            if (_pin == null || _state == PairingState.Idle)
                return Task.FromResult((401, "{\"error\":\"No active PIN.\"}"));

            if (_pinUsed)
                return Task.FromResult((409, "{\"error\":\"PIN already used.\"}"));

            if (DateTime.UtcNow > _pinExpiresAt)
            {
                _state = PairingState.Idle;
                return Task.FromResult((410, "{\"error\":\"PIN expired.\"}"));
            }

            if (pin != _pin)
                return Task.FromResult((401, "{\"error\":\"Incorrect PIN.\"}"));

            _state = PairingState.InProgress;
            paystackUuid = _paystackUuid;
            subscriptionCode = _subscriptionCode;
            openRouterKey = _openRouterKey;
        }

        Console.WriteLine($"[Mobile] Pair request from {deviceId[..Math.Min(8, deviceId.Length)]}... validating subscription");

        bool subActive = !string.IsNullOrWhiteSpace(paystackUuid);

        if (!subActive)
        {
            lock (_lock) { _state = PairingState.Failed; _errorMessage = "No active SnapStak subscription."; _pinUsed = true; }
            return Task.FromResult((403, "{\"error\":\"No active SnapStak subscription. Please renew via Paystack.\"}"));
        }

        string encryptedToken;
        try
        {
            var payload = JsonSerializer.Serialize(new { paystackUuid, subscriptionCode, openRouterKey });
            encryptedToken = EncryptAesGcm(payload, deviceId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Mobile] Encryption failed: {ex.Message}");
            lock (_lock) { _state = PairingState.Failed; _errorMessage = "Encryption error."; _pinUsed = true; }
            return Task.FromResult((500, "{\"error\":\"Server encryption error.\"}"));
        }

        var record = new PairedDevice
        {
            DeviceId = deviceId,
            PaystackUuid = paystackUuid,
            PairedAt = DateTime.UtcNow,
            MobileIp = mobileIp,
        };
        lock (_devicesLock)
        {
            _devices.RemoveAll(d => d.DeviceId == deviceId);
            _devices.Add(record);
            SaveDevices();
        }

        lock (_lock)
        {
            _pinUsed = true;
            _state = PairingState.Succeeded;
            _lastPairedDeviceId = deviceId;
            _lastPairedAt = record.PairedAt;
        }

        Console.WriteLine($"[Mobile] Paired device {deviceId[..Math.Min(8, deviceId.Length)]}...");
        return Task.FromResult((200, JsonSerializer.Serialize(new { token = encryptedToken })));
    }

    // ── Paired devices ────────────────────────────────────────────────────────

    public List<PairedDevice> GetPairedDevices()
    {
        lock (_devicesLock) { return _devices.ToList(); }
    }

    public void RemoveDevice(string deviceId)
    {
        lock (_devicesLock)
        {
            _devices.RemoveAll(d => d.DeviceId == deviceId);
            SaveDevices();
        }
        Console.WriteLine($"[Mobile] Removed paired device {deviceId[..Math.Min(8, deviceId.Length)]}...");
    }

    private const int PairingPort = 5174;

    /// <summary>
    /// Notifies Mobile to clear its token then removes the Desktop record.
    /// Both apps must be running on the same WiFi.
    /// If MobileIp was not recorded (device paired before this feature),
    /// scans the LAN to find Mobile automatically.
    /// Desktop record is only removed AFTER Mobile confirms.
    /// </summary>
    public async Task<(bool success, string? error)> RemoveDeviceAndNotifyMobileAsync(string deviceId)
    {
        PairedDevice? device;
        lock (_devicesLock)
            device = _devices.FirstOrDefault(d => d.DeviceId == deviceId);

        if (device == null)
            return (false, "Device not found.");

        // Legacy device paired before MobileIp was recorded.
        // Cannot safely remove both sides without the Mobile IP.
        // User must re-pair so the IP gets recorded, then remove will work automatically.
        if (string.IsNullOrWhiteSpace(device.MobileIp))
            return (false, "This device was paired with an older version of SnapStak. To remove it cleanly, re-pair the device from SnapStak Mobile \u2192 Settings. The new pairing will record the Mobile IP so both sides can be removed together.");

        // Notify Mobile first — Desktop record only removed after Mobile confirms
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var payload = System.Text.Json.JsonSerializer.Serialize(new { deviceId });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(
                $"http://{device.MobileIp}:{PairingPort}/api/unpair", content);

            if (!resp.IsSuccessStatusCode)
                return (false, "SnapStak Mobile returned an error. Please try again.");
        }
        catch
        {
            return (false, "Cannot reach SnapStak Mobile. Make sure the app is open on the same WiFi and try again.");
        }

        // Mobile confirmed — remove Desktop record
        lock (_devicesLock)
        {
            _devices.RemoveAll(d => d.DeviceId == deviceId);
            SaveDevices();
        }
        Console.WriteLine($"[Mobile] Removed paired device {deviceId[..Math.Min(8, deviceId.Length)]}...");
        return (true, null);
    }

    public PairedDevice? FindDevice(string deviceId)
    {
        lock (_devicesLock) { return _devices.FirstOrDefault(d => d.DeviceId == deviceId); }
    }

    // ── AES-256-GCM ───────────────────────────────────────────────────────────

    public static string EncryptAesGcm(string plaintext, string keyInput)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(keyInput, salt);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var output = new byte[salt.Length + nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(salt, 0, output, 0, salt.Length);
        Buffer.BlockCopy(nonce, 0, output, salt.Length, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, salt.Length + nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, output, salt.Length + nonce.Length + tag.Length, ciphertext.Length);

        return Convert.ToBase64String(output);
    }

    public static string DecryptAesGcm(byte[] encryptedBytes, string keyInput)
    {
        var plaintext = DecryptAesGcmBytes(encryptedBytes, keyInput);
        return Encoding.UTF8.GetString(plaintext);
    }

    public static byte[] DecryptAesGcmBytes(byte[] encryptedBytes, string keyInput)
    {
        if (encryptedBytes.Length < SaltBytes + NonceBytes + 16)
            throw new CryptographicException("Encrypted data is too short.");

        var salt = encryptedBytes[..SaltBytes];
        var nonce = encryptedBytes[SaltBytes..(SaltBytes + NonceBytes)];
        var tag = encryptedBytes[(SaltBytes + NonceBytes)..(SaltBytes + NonceBytes + 16)];
        var ciphertext = encryptedBytes[(SaltBytes + NonceBytes + 16)..];

        var key = DeriveKey(keyInput, salt);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static byte[] DeriveKey(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(
               Encoding.UTF8.GetBytes(password),
               salt,
               Pbkdf2Iterations,
               HashAlgorithmName.SHA256,
               KeyBytes);

    // ── Persistence ───────────────────────────────────────────────────────────

    private void LoadDevices()
    {
        lock (_devicesLock)
        {
            if (!File.Exists(_devicesFilePath)) return;
            try
            {
                var raw = File.ReadAllText(_devicesFilePath);
                var store = JsonSerializer.Deserialize<DevicesStore>(raw, JsonOpts);
                if (store?.Devices != null)
                {
                    _devices.Clear();
                    _devices.AddRange(store.Devices);
                    Console.WriteLine($"[Mobile] Loaded {_devices.Count} paired device(s).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mobile] Could not load paired devices: {ex.Message}");
            }
        }
    }

    private void SaveDevices()
    {
        try
        {
            var store = new DevicesStore { Devices = _devices.ToList() };
            File.WriteAllText(_devicesFilePath, JsonSerializer.Serialize(store, JsonOpts));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Mobile] Could not save paired devices: {ex.Message}");
        }
    }

    private static string GenerateSecurePin()
    {
        Span<byte> buf = stackalloc byte[4];
        while (true)
        {
            RandomNumberGenerator.Fill(buf);
            uint val = BitConverter.ToUInt32(buf) & 0x7FFFFFFF;
            if (val < (uint.MaxValue / 1_000_000) * 1_000_000)
                return (val % 1_000_000).ToString("D6");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}

file sealed class DevicesStore
{
    [JsonPropertyName("devices")] public List<PairedDevice> Devices { get; set; } = new();
}