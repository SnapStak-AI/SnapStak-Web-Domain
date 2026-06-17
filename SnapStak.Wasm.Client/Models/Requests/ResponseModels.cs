using SnapStak.Wasm.Client.Models.Dom;

namespace SnapStak.Wasm.Client.Models.Requests;

// ── Extraction Responses ──────────────────────────────────────────────────────

public sealed class StructureResult
{
    public bool    Success       { get; set; }
    public string  SvgString     { get; set; } = string.Empty;
    public int     ObjectCount   { get; set; }
    public int     Width         { get; set; }
    public int     Height        { get; set; }
    public string  SourceUrl     { get; set; } = string.Empty;
    public int     HiddenCount   { get; set; }
    public int     HiddenKb      { get; set; }
    public int     ViewportWidth { get; set; }
    public string? Error         { get; set; }
}

public sealed class ViewportSnapshotResult
{
    public bool Success       { get; set; }
    public int  ViewportWidth { get; set; }
    public string? Error      { get; set; }
}

public sealed class SegmentManifest
{
    public bool   Success         { get; set; }
    public string PageComponentId { get; set; } = string.Empty;
    public List<SegmentComponent> Components { get; set; } = new();
    public string CreatedAt       { get; set; } = DateTime.UtcNow.ToString("O");
    public string? Error          { get; set; }
}

public sealed class SegmentComponent
{
    public string SegmentId   { get; set; } = string.Empty;
    public string ComponentId { get; set; } = string.Empty;
    public string Name        { get; set; } = string.Empty;
    public string Tag         { get; set; } = string.Empty;
    public string Label       { get; set; } = string.Empty;
    public int    W           { get; set; }
    public int    H           { get; set; }
    public string SvgFile     { get; set; } = string.Empty;
}

// ── Generation Responses ──────────────────────────────────────────────────────

public sealed class GenerateResult
{
    public bool   Success       { get; set; }
    public string DownloadToken { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public string Framework     { get; set; } = string.Empty;
    public string StyleOutput   { get; set; } = string.Empty;
    public string Language      { get; set; } = string.Empty;
    public GenerateStats Stats  { get; set; } = new();
    public string? Error        { get; set; }
}

public sealed class GenerateStats
{
    public long   DurationMs   { get; set; }
    public string Model        { get; set; } = string.Empty;
    public int    PromptChars  { get; set; }
    public int    ObjectCount  { get; set; }
    public int    FileCount    { get; set; }
}

public sealed class ConvertResult
{
    public bool   Success   { get; set; }
    public string Code      { get; set; } = string.Empty;
    public string Framework { get; set; } = string.Empty;
    public long   DurationMs{ get; set; }
    public string? Error    { get; set; }
}

public sealed class AssembleResult
{
    public bool   Success       { get; set; }
    public string DownloadToken { get; set; } = string.Empty;
    public string ProjectName   { get; set; } = string.Empty;
    public string? Error        { get; set; }
}

/// <summary>
/// Parameters for IRulesEngine.BuildConteXCodePrompt().
/// Carries all four pillars plus rendering options.
/// </summary>
public sealed class ConteXCodePromptParams
{
    public required string ComponentId   { get; set; }
    public required string Framework     { get; set; }
    public          string StyleOutput   { get; set; } = "css";
    public          string Language      { get; set; } = "js";
    public          string SvgSkeleton   { get; set; } = string.Empty;
    public          string Css           { get; set; } = string.Empty;
    public          object? RawCss       { get; set; }
    public          string BehaviourCss  { get; set; } = string.Empty;
    public          string BehaviourJs   { get; set; } = string.Empty;
    public          Models.Pillars.InfluenceData?  Influence        { get; set; }
    public          Models.Pillars.ObjectiveData?  Objective        { get; set; }
    public          List<DomElement>?    HiddenElements   { get; set; }
    public          List<HiddenComponent>? HiddenComponents { get; set; }
    public          string? SourceHtml   { get; set; }
}
