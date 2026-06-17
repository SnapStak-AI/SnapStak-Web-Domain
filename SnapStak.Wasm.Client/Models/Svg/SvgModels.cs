namespace SnapStak.Wasm.Client.Models.Svg;

public sealed class SvgNode
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string Tag { get; set; } = "div";
    public string? TextContent { get; set; }
    public string? ClassName { get; set; }
    public string? AriaLabel { get; set; }
    public string? ComponentType { get; set; }
    public string? Label { get; set; }
    public string? SegmentId { get; set; }
    public string? ImgSrc { get; set; }  // resolved CDN URL or base64
    public string? SvgDataUri { get; set; }  // inline SVG icon as data URI

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public Dictionary<string, string> CssProps { get; set; } = new();
    public List<SvgNode> Children { get; set; } = new();
    // Pre-computed wrap lines from SvgSerializer - exact tspan source of truth.
    // When TextWrapLines is non-null it was measured by the browser Range API in
    // content.js and reflects the actual rendered line breaks. Translator plugins
    // must use these directly instead of re-wrapping with a character-width estimate.
    // TextWrapContainerW / H are the exact rendered container dimensions from the
    // live browser — use these for layout width calculations, not CSS-derived values.
    public List<string>? TextLines { get; set; }
    public List<double>? TextLineWidths { get; set; }
    public double TextWrapContainerW { get; set; }
    public double TextWrapContainerH { get; set; }
}

public sealed class SvgTreeOptions
{
    public int Width { get; set; } = 1440;
    public int Height { get; set; } = 900;
    public string SourceUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<Models.Dom.PageMapEntry> PageMap { get; set; } = new();
}