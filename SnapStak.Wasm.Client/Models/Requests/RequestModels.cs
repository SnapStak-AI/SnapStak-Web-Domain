using Newtonsoft.Json;
using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Css;

namespace SnapStak.Wasm.Client.Models.Requests;

// ── Extraction Requests ───────────────────────────────────────────────────────

/// <summary>
/// POST /web-to-structure/transform — full page DOM path.
/// Port of req.body in webToSVG.js /transform.
/// </summary>
public sealed class TransformRequest
{
    [JsonProperty("domSnapshot")] public DomSnapshot? DomSnapshot { get; set; }
    [JsonProperty("domSnapshotHidden")] public DomSnapshot? DomSnapshotHidden { get; set; }
    [JsonProperty("hiddenComponents")] public List<HiddenComponent> HiddenComponents { get; set; } = new();
    [JsonProperty("url")] public string? Url { get; set; }
    [JsonProperty("viewport")] public ViewportInfo? Viewport { get; set; }
    [JsonProperty("componentId")] public string? ComponentId { get; set; }
    [JsonProperty("componentCSS")] public CssJson? ComponentCss { get; set; }
    [JsonProperty("componentJS")] public object? ComponentJs { get; set; }
    [JsonProperty("influence")] public InfluenceData? Influence { get; set; }
    [JsonProperty("objective")] public ObjectiveData? Objective { get; set; }
    [JsonProperty("segmentId")] public string? SegmentId { get; set; }
    [JsonProperty("pageComponentId")] public string? PageComponentId { get; set; }
    [JsonProperty("deconstructMode")] public string? DeconstructMode { get; set; }
    [JsonProperty("meta")] public RequestMeta? Meta { get; set; }
    [JsonProperty("viewportSnapshots")] public List<ViewportSnapshotRequest> ViewportSnapshots { get; set; } = new();
    // Resolved server-side from auth token
    public string UserUuid { get; set; } = "anonymous";
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    // Sent in the JSON body by the MAUI client — not a header.
    // "maui" = MAUI Android WebView client — zones are pre-split by content.js,
    // each POST is already a focused component. domSnapshot SVG must NOT be
    // produced; only behaviour files are written.
    [JsonProperty("client")] public string Client { get; set; } = string.Empty;
}

public sealed class ViewportInfo
{
    [JsonProperty("width")] public int Width { get; set; } = 1440;
    [JsonProperty("height")] public int Height { get; set; } = 900;
}

public sealed class RequestMeta
{
    [JsonProperty("url")] public string? Url { get; set; }
    [JsonProperty("width")] public int? Width { get; set; }
    [JsonProperty("height")] public int? Height { get; set; }
}

/// <summary>POST /web-to-structure/viewport-snapshot</summary>
public sealed class ViewportSnapshotRequest
{
    [JsonProperty("componentId")] public string ComponentId { get; set; } = string.Empty;
    [JsonProperty("viewportWidth")] public int ViewportWidth { get; set; }
    [JsonProperty("deviceType")] public string? DeviceType { get; set; }
    [JsonProperty("domSnapshot")] public DomSnapshot? DomSnapshot { get; set; }
    [JsonProperty("hiddenComponents")] public List<HiddenComponent> HiddenComponents { get; set; } = new();
    [JsonProperty("componentCSS")] public CssJson? ComponentCss { get; set; }
    [JsonProperty("componentJS")] public object? ComponentJs { get; set; }
    public string UserUuid { get; set; } = "anonymous";
}

/// <summary>POST /web-to-structure/segment — page segmentation</summary>
public sealed class SegmentPageRequest
{
    [JsonProperty("pageComponentId")] public string PageComponentId { get; set; } = string.Empty;
    public string UserUuid { get; set; } = "anonymous";
}

/// <summary>POST /web-to-structure/extract-segment</summary>
public sealed class ExtractSegmentRequest
{
    [JsonProperty("segmentId")] public string SegmentId { get; set; } = string.Empty;
    [JsonProperty("pageComponentId")] public string PageComponentId { get; set; } = string.Empty;
    [JsonProperty("name")] public string? Name { get; set; }
    public string UserUuid { get; set; } = "anonymous";
}

// ── Generation Requests ───────────────────────────────────────────────────────

/// <summary>
/// POST /structure-to-code/generate — full four-pillar generation pipeline.
/// Port of req.body in svgToCode.js /generate.
/// </summary>
public sealed class GenerateRequest
{
    [JsonProperty("componentId")] public string? ComponentId { get; set; }
    [JsonProperty("uuid")] public string? Uuid { get; set; }
    [JsonProperty("framework")] public string Framework { get; set; } = "react";
    [JsonProperty("screenWidthTarget")] public int? ScreenWidthTarget { get; set; }
    [JsonProperty("deviceType")] public string? DeviceType { get; set; }
    [JsonProperty("styleOutput")] public string StyleOutput { get; set; } = "css";
    [JsonProperty("language")] public string Language { get; set; } = "js";
    [JsonProperty("segmentId")] public string? SegmentId { get; set; }
    [JsonProperty("pageComponentId")] public string? PageComponentId { get; set; }
    [JsonProperty("segmentName")] public string? SegmentName { get; set; }
    [JsonProperty("componentCSS")] public CssJson? ComponentCss { get; set; }
    [JsonProperty("componentJS")] public object? ComponentJs { get; set; }
    [JsonProperty("hiddenComponents")] public List<HiddenComponent> HiddenComponents { get; set; } = new();
    [JsonProperty("influence")] public InfluenceData? Influence { get; set; }
    [JsonProperty("unifiedMode")] public bool UnifiedMode { get; set; } = true;
    [JsonProperty("capturedBreakpoints")] public int[]? CapturedBreakpoints { get; set; }
    // Resolved server-side
    public string UserUuid { get; set; } = "anonymous";
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string PassiveModel { get; set; } = string.Empty;
}

/// <summary>POST /structure-to-code/convert — Stage 3 framework conversion</summary>
public sealed class ConvertRequest
{
    [JsonProperty("html")] public string Html { get; set; } = string.Empty;
    [JsonProperty("css")] public string Css { get; set; } = string.Empty;
    [JsonProperty("framework")] public string Framework { get; set; } = "react";
    [JsonProperty("uuid")] public string Uuid { get; set; } = string.Empty;
    [JsonProperty("componentId")] public string ComponentId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
}

/// <summary>POST /structure-to-code/assemble — combine all component zips</summary>
public sealed class AssembleRequest
{
    [JsonProperty("pageComponentId")] public string PageComponentId { get; set; } = string.Empty;
    [JsonProperty("framework")] public string Framework { get; set; } = "react";
    [JsonProperty("styleOutput")] public string StyleOutput { get; set; } = "css";
    [JsonProperty("language")] public string Language { get; set; } = "js";
    public string UserUuid { get; set; } = "anonymous";
}