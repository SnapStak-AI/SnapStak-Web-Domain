using Microsoft.JSInterop;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapStak.Wasm.Client.Services;

// ─────────────────────────────────────────────────────────────────────────────
// PluginSettingsService
//
// Stores translator plugin preferences in the browser's localStorage.
// Each plugin has two settings:
//   • Enabled (bool)   — whether the plugin runs during a capture
//   • OutputPath (str) — folder where the plugin writes its output file
//
// The SnapStak output root path (where .svg, .json etc. are written) is also
// stored here, separate from the individual plugin paths.
//
// All values are persisted as JSON under a single localStorage key so they
// survive page refresh and browser restart.
//
// The server is notified whenever settings change via
// POST /api/settings — the server writes the same config to a local JSON file
// so Program.cs can read it on startup without needing the browser open.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class PluginSettingsService
{
    private readonly IJSRuntime _js;
    private readonly System.Net.Http.HttpClient _http;

    private const string StorageKey = "snapstak_plugin_settings";
    private const string ServerUrl = "http://localhost:5174/api/settings";

    // Default output paths — match the current hardcoded OutputRoot in Program.cs
    public const string DefaultOutputRoot = @"C:\ConteX Dev\SnapStak.Wasm\output";
    public const string DefaultPenpotPath = @"C:\ConteX Dev\SnapStak.Wasm\output\Penpot";
    public const string DefaultCanvaPath = @"C:\ConteX Dev\SnapStak.Wasm\output\Canva";
    public const string DefaultFigmaPath = @"C:\ConteX Dev\SnapStak.Wasm\output\Figma";

    private PluginSettings? _cache;

    public PluginSettingsService(IJSRuntime js, System.Net.Http.HttpClient http)
    {
        _js = js;
        _http = http;
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public async Task<PluginSettings> LoadAsync()
    {
        if (_cache != null) return _cache;

        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrEmpty(json))
            {
                _cache = JsonSerializer.Deserialize<PluginSettings>(json)
                      ?? PluginSettings.Defaults();
                // Ensure any new fields added since last save have default values
                _cache.ApplyDefaults();
                return _cache;
            }
        }
        catch { /* first run or corrupted — fall through to defaults */ }

        _cache = PluginSettings.Defaults();
        return _cache;
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    public async Task SaveAsync(PluginSettings settings)
    {
        _cache = settings;
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = false,
        });

        await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);

        // Best-effort server sync — the server persists to disk so it can
        // read the paths on startup. Failure is non-fatal.
        try
        {
            using var req = new System.Net.Http.StringContent(
                json, System.Text.Encoding.UTF8, "application/json");
            await _http.PostAsync(ServerUrl, req);
        }
        catch { /* server may not be running — non-fatal */ }
    }

    // ── Convenience ───────────────────────────────────────────────────────────

    public async Task<bool> IsPluginEnabledAsync(string pluginKey)
    {
        var s = await LoadAsync();
        return pluginKey switch
        {
            "penpot" => s.PenpotEnabled,
            "canva" => s.CanvaEnabled,
            "figma" => s.FigmaEnabled,
            _ => true, // unknown plugins default to enabled
        };
    }

    public async Task<string> GetOutputRootAsync()
        => (await LoadAsync()).OutputRoot;

    public async Task<string> GetPluginOutputPathAsync(string pluginKey)
    {
        var s = await LoadAsync();
        return pluginKey switch
        {
            "penpot" => s.PenpotOutputPath,
            "canva" => s.CanvaOutputPath,
            "figma" => s.FigmaOutputPath,
            _ => s.OutputRoot,
        };
    }
}

// ── Settings model ────────────────────────────────────────────────────────────

public sealed class PluginSettings
{
    // SnapStak master output root — where .svg, .json, .html files are written
    [JsonPropertyName("outputRoot")]
    public string OutputRoot { get; set; } = PluginSettingsService.DefaultOutputRoot;

    // Penpot
    [JsonPropertyName("penpotEnabled")]
    public bool PenpotEnabled { get; set; } = true;

    [JsonPropertyName("penpotOutputPath")]
    public string PenpotOutputPath { get; set; } = PluginSettingsService.DefaultPenpotPath;

    // Canva
    [JsonPropertyName("canvaEnabled")]
    public bool CanvaEnabled { get; set; } = true;

    [JsonPropertyName("canvaOutputPath")]
    public string CanvaOutputPath { get; set; } = PluginSettingsService.DefaultCanvaPath;

    // Figma
    [JsonPropertyName("figmaEnabled")]
    public bool FigmaEnabled { get; set; } = true;

    [JsonPropertyName("figmaOutputPath")]
    public string FigmaOutputPath { get; set; } = PluginSettingsService.DefaultFigmaPath;

    // Framer — credentials only, no output path (Framer is cloud-based)
    [JsonPropertyName("framerApiKey")]
    public string FramerApiKey { get; set; } = string.Empty;

    [JsonPropertyName("framerProjectUrl")]
    public string FramerProjectUrl { get; set; } = string.Empty;

    /// <summary>
    /// Ensures any field that was missing from an older persisted JSON
    /// (e.g. new plugins added in a future release) gets a sensible default.
    /// Also corrects the previous bug where plugin paths defaulted to the root
    /// output folder instead of their dedicated subdirectories.
    /// </summary>
    public void ApplyDefaults()
    {
        if (string.IsNullOrWhiteSpace(OutputRoot))
            OutputRoot = PluginSettingsService.DefaultOutputRoot;

        // If a plugin path is empty OR still points at the bare root (the old
        // wrong default), replace it with the correct subdirectory default.
        if (string.IsNullOrWhiteSpace(PenpotOutputPath)
            || PenpotOutputPath.TrimEnd('\\', '/') == OutputRoot.TrimEnd('\\', '/'))
            PenpotOutputPath = PluginSettingsService.DefaultPenpotPath;

        if (string.IsNullOrWhiteSpace(CanvaOutputPath)
            || CanvaOutputPath.TrimEnd('\\', '/') == OutputRoot.TrimEnd('\\', '/'))
            CanvaOutputPath = PluginSettingsService.DefaultCanvaPath;

        if (string.IsNullOrWhiteSpace(FigmaOutputPath)
            || FigmaOutputPath.TrimEnd('\\', '/') == OutputRoot.TrimEnd('\\', '/'))
            FigmaOutputPath = PluginSettingsService.DefaultFigmaPath;
    }

    public static PluginSettings Defaults() => new PluginSettings();
}