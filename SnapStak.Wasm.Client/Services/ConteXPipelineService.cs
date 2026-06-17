using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Requests;
using SnapStak.Wasm.Client.Storage;

namespace SnapStak.Wasm.Client.Services;

/// <summary>
/// Top-level orchestrator for the CON10X pipeline.
/// Called by Blazor pages/components. Coordinates all four agents.
///
/// Stage 1 — Deconstruct:  DOM snapshot → SVG (StructureAgentService)
/// Stage 2 — Behaviour AI: CSS/JS → .md descriptions (BehaviourAgentService)
/// Stage 3 — Generate:     SVG + 4 pillars → framework code (ConstructorService)
/// Stage 4 — Download:     zip bytes → browser download
///
/// Progress is reported via IProgress<PipelineProgress> so the UI can
/// update a progress bar without polling.
/// </summary>
public sealed class ConteXPipelineService
{
    private readonly StructureAgentService _structure;
    private readonly BehaviourAgentService _behaviour;
    private readonly ConstructorService _constructor;
    private readonly LicenceService _licence;
    private readonly ModelService _models;
    private readonly IPillarStorage _storage;

    public ConteXPipelineService(
        StructureAgentService s,
        BehaviourAgentService b,
        ConstructorService c,
        LicenceService l,
        ModelService m,
        IPillarStorage st)
    {
        _structure = s;
        _behaviour = b;
        _constructor = c;
        _licence = l;
        _models = m;
        _storage = st;
    }

    // ── Stage 1: Deconstruct ──────────────────────────────────────────────────

    /// <summary>
    /// Receives a DOM snapshot from the browser extension and transforms it
    /// into the Structure SVG + all available pillar files.
    /// </summary>
    public async Task<TransformResult> TransformAsync(
        TransformRequest request,
        IProgress<PipelineProgress>? progress = null)
    {
        progress?.Report(new PipelineProgress("Building Structure pillar…", 10));

        var userUuid = await _licence.GetUserUuidAsync();
        request.UserUuid = userUuid;

        var result = _structure.Transform(request);
        if (!result.Success)
            return new TransformResult { Success = false, Error = result.Error };

        progress?.Report(new PipelineProgress("Structure (S) pillar complete", 40));

        // Register in session manifest so Sessions page shows this component
        var componentDir = _storage.ResolveComponentDir(userUuid, request.ComponentId!);
        var zone = IPillarStorage.InferSectionTag(request.ComponentId!) ?? "page";
        var label = Uri.TryCreate(request.Url, UriKind.Absolute, out var parsed)
            ? parsed.Host : request.Url ?? request.ComponentId!;
        _storage.RegisterComponentInManifest(userUuid, request.ComponentId!, componentDir, zone, label);

        return new TransformResult
        {
            Success = true,
            ComponentId = request.ComponentId!,
            ObjectCount = result.ObjectCount,
            Width = result.Width,
            Height = result.Height,
        };
    }

    /// <summary>
    /// Saves a mobile viewport snapshot for a previously deconstructed component.
    /// </summary>
    public async Task SaveViewportSnapshotAsync(ViewportSnapshotRequest request)
    {
        request.UserUuid = await _licence.GetUserUuidAsync();
        _structure.SaveViewportSnapshot(request);
    }

    // ── Stage 2 + 3: Generate ─────────────────────────────────────────────────

    /// <summary>
    /// Runs Behaviour AI (if needed) then the Constructor to produce
    /// production-ready framework code as a zip byte array.
    /// The active model is fetched from models.snapstak.ai — never stored
    /// locally and never exposed to the user.
    /// </summary>
    public async Task<WasmGenerateResult> GenerateAsync(
        string componentId,
        string framework,
        string styleOutput = "css",
        string language = "js",
        bool unifiedMode = true,
        IProgress<PipelineProgress>? progress = null)
    {
        var userUuid = await _licence.GetUserUuidAsync();
        var apiKey = await _licence.GetApiKeyAsync();

        if (string.IsNullOrWhiteSpace(apiKey))
            return new WasmGenerateResult { Success = false, Error = "No API key configured. Please add your OpenRouter key in Settings." };

        // Fetch the active model list from models.snapstak.ai.
        // This increments the usage counter and ensures models are always current.
        // Model IDs are never surfaced to the user.
        var modelList = await _models.FetchModelsAsync();
        var behaviourModel = modelList.Models.FirstOrDefault(m => m.Tag == "Behaviour")?.Id
                             ?? "openai/gpt-4.1-mini";
        var constructorModel = modelList.Models.FirstOrDefault(m => m.Tag == "Constructor")?.Id
                               ?? "anthropic/claude-sonnet-4-6";

        progress?.Report(new PipelineProgress("Building Behaviour pillar…", 50));

        var request = new GenerateRequest
        {
            ComponentId = componentId,
            UserUuid = userUuid,
            Framework = framework,
            StyleOutput = styleOutput,
            Language = language,
            UnifiedMode = unifiedMode,
            ApiKey = apiKey,
            ModelId = constructorModel,
            PassiveModel = behaviourModel,
        };

        progress?.Report(new PipelineProgress("AI Gateway generating framework code…", 70));
        var result = await _constructor.GenerateAsync(request);

        if (result.Success)
            progress?.Report(new PipelineProgress("Framework code ready — preparing download…", 95));

        return result;
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    public async Task<string> GetUserUuidAsync() => await _licence.GetUserUuidAsync();
}

// ── Supporting types ──────────────────────────────────────────────────────────

public sealed class TransformResult
{
    public bool Success { get; set; }
    public string ComponentId { get; set; } = string.Empty;
    public int ObjectCount { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Error { get; set; }
}

public sealed class PipelineProgress
{
    public string Message { get; }
    public int Percentage { get; }
    public PipelineProgress(string message, int percentage)
    { Message = message; Percentage = percentage; }
}