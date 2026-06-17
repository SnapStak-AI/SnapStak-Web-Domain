using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Services;

/// <summary>
/// Fetches the live model list from models.snapstak.ai before every generation.
///
/// This allows SnapStak to:
///   - Add, remove or deprecate models without shipping a new WASM build
///   - Promote a new recommended model instantly across all installations
///   - Track usage count for every model fetch (marketing analytics)
///
/// Falls back to a hardcoded default list if the endpoint is unreachable,
/// so offline or firewalled users are never blocked from generating.
///
/// The endpoint URL is: https://models.snapstak.ai/wasm/models/models.json
/// Query params appended: ?uuid={userUuid}&v={clientVersion}
/// </summary>
public sealed class ModelService
{
    private readonly HttpClient _http;
    private readonly LicenceService _licence;

    private const string ModelsEndpoint = "https://models.snapstak.ai/wasm/models/models.json";
    private const string ClientVersion = "1.0.0";

    // In-memory cache — refreshed on every generation call.
    // Null means not yet fetched this session.
    private ModelList? _cached;

    public ModelService(LicenceService licence)
    {
        _licence = licence;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the latest model list from the remote endpoint.
    /// Always fetches fresh — no caching — so the usage counter increments
    /// on every generation and model changes propagate immediately.
    /// Falls back to the built-in defaults if the request fails.
    /// </summary>
    public async Task<ModelList> FetchModelsAsync()
    {
        try
        {
            var userUuid = await _licence.GetUserUuidAsync();
            var url = $"{ModelsEndpoint}?uuid={userUuid}&v={ClientVersion}";

            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return Fallback();

            var json = await response.Content.ReadAsStringAsync();
            var list = JsonConvert.DeserializeObject<ModelList>(json);
            if (list?.Models == null || list.Models.Count == 0) return Fallback();

            // Only surface models the server says are enabled
            list.Models = list.Models.Where(m => m.Enabled).ToList();
            _cached = list;
            return list;
        }
        catch
        {
            // Network error, timeout, parse failure — fall back silently
            return _cached ?? Fallback();
        }
    }

    /// <summary>Returns the last successfully fetched list, or the fallback.</summary>
    public ModelList GetCached() => _cached ?? Fallback();

    // ── Fallback ──────────────────────────────────────────────────────────────
    // Hardcoded defaults used when the remote endpoint is unreachable.
    // These must always reflect the most current stable model set.

    private static ModelList Fallback() => new()
    {
        Version = "fallback",
        Default = "anthropic/claude-sonnet-4-6",
        Models =
        [
            new() { Id = "anthropic/claude-sonnet-4-6", Name = "Claude Sonnet 4.6", Provider = "Anthropic", Tag = "Constructor",     Description = "Code generation and component construction", Enabled = true, Recommended = true  },
            new() { Id = "openai/gpt-4.1-mini",         Name = "GPT-4.1 mini",      Provider = "OpenAI",    Tag = "Behaviour",        Description = "Behaviour and interaction analysis",        Enabled = true, Recommended = false },
        ],
    };
}

// ── Model types ───────────────────────────────────────────────────────────────

public sealed class ModelList
{
    [JsonProperty("version")]
    public string Version { get; set; } = string.Empty;

    [JsonProperty("updated")]
    public string? Updated { get; set; }

    [JsonProperty("default")]
    public string Default { get; set; } = string.Empty;

    [JsonProperty("models")]
    public List<ModelDefinition> Models { get; set; } = [];

    [JsonProperty("_meta")]
    public ModelMeta? Meta { get; set; }
}

public sealed class ModelDefinition
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonProperty("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonProperty("recommended")]
    public bool Recommended { get; set; }
}

public sealed class ModelMeta
{
    [JsonProperty("totalFetches")]
    public long TotalFetches { get; set; }

    [JsonProperty("servedAt")]
    public string ServedAt { get; set; } = string.Empty;
}