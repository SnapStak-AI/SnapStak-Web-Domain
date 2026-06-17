using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using SnapStak.Wasm.Client.Engine.Plugins;
using ClientSvg = SnapStak.Wasm.Client.Models.Svg;
using ClientPlugins = SnapStak.Wasm.Client.Engine.Plugins;

// ─────────────────────────────────────────────────────────────────────────────
// SnapStakPipeline.cs — server-side deconstruction pipeline
//
// Runs in the CON10X Web Domain Server (ASP.NET Core Minimal API, :5174).
// Writes filesystem mirror of every component's pillar files, plus one file
// per registered translator plugin (Penpot .penpot, Canva .canva.pdf, etc.).
//
// Plugin integration (Approach A — shared client project):
//   • Adds a <ProjectReference> to SnapStak.Wasm.Client.csproj.
//   • Uses the same IConteXTranslatorPlugin interface and TranslatorPluginHost
//     as the WASM client, so every plugin runs in both processes.
//   • The server's local SvgNode class (defined below) is projected to the
//     client's Models.Svg.SvgNode (via MapToClientNode) before being handed
//     to the plugin contract.
//
// Translator outputs are written alongside each component's master SVG:
//   {componentDir}/{componentId}{plugin.FileExtension}
//   e.g. example_1234567.penpot          — Penpot binary archive
//        example_1234567.canva.pdf        — Canva Connect API import file
//
// Canva relay (CanvaRelayService.cs):
//   The .canva.pdf file is consumed by CanvaRelayService when the user clicks
//   "Send to Canva" in the UI. CanvaRelayService posts it to Canva's Connect
//   API on behalf of the authenticated user. Credentials are loaded from
//   environment variables — see canva.env for setup instructions.
// ─────────────────────────────────────────────────────────────────────────────

// ── Models ────────────────────────────────────────────────────────────────────

record DomRect(double X, double Y, double Width, double Height);

class DomElement
{
    [JsonProperty("internalId")] public string InternalId { get; set; } = string.Empty;
    [JsonProperty("parentId")] public string? ParentId { get; set; }
    [JsonProperty("tag")] public string Tag { get; set; } = "div";
    [JsonProperty("tagName")] public string? TagName { get; set; }
    [JsonProperty("textContent")] public string? TextContent { get; set; }
    [JsonProperty("className")] public string? ClassName { get; set; }
    [JsonProperty("ariaLabel")] public string? AriaLabel { get; set; }
    [JsonProperty("segmentId")] public string? SegmentId { get; set; }
    [JsonProperty("rect")] public JObject? RectObj { get; set; }
    [JsonProperty("cssProps")] public Dictionary<string, string>? CssProps { get; set; }
    // content.js has TWO emit paths for the image URL, on different channels that
    // both deserialise into this same DomElement class:
    //   • serialize() → domSnapshot.elements  ← emits "src:"        (main channel)
    //   • walkEl / walkHiddenEl → hiddenComponents.elements ← emits "imgSrc:" (hidden channel)
    // We accept both. Newtonsoft picks whichever field is present in the JSON for
    // the given element; they never both appear on the same element.
    [JsonProperty("src")] public string? ImgSrc { get; set; }
    [JsonProperty("imgSrc")]
    public string? ImgSrcAlt
    {
        get => null;                                    // never serialised back out
        set { if (!string.IsNullOrEmpty(value)) ImgSrc = value; }
    }
    [JsonProperty("dataSrc")] public string? DataSrc { get; set; }
    [JsonProperty("svgDataURI")] public string? SvgDataUri { get; set; }
    [JsonProperty("componentType")] public string? ComponentType { get; set; }
    [JsonProperty("label")] public string? Label { get; set; }
    [JsonProperty("borderRadiusPx")] public double BorderRadiusPx { get; set; }
    [JsonProperty("parentRect")] public JObject? ParentRect { get; set; }
    [JsonProperty("textWrapLines")] public List<string>? TextWrapLines { get; set; }
    [JsonProperty("textWrapLineWidths")] public List<double>? TextWrapLineWidths { get; set; }

    // innerWidth from parentRect — the browser-measured text column width (padding subtracted).
    // This is the source of truth for text wrapping. Falls back to element width if not present.
    public double TextColumnWidth
    {
        get
        {
            var iw = ParentRect?["innerWidth"]?.Value<double>();
            return (iw.HasValue && iw.Value > 4) ? iw.Value : 0;
        }
    }

    public double X => RectObj?["x"]?.Value<double>() ?? 0;
    public double Y => RectObj?["y"]?.Value<double>() ?? 0;
    public double Width => RectObj?["width"]?.Value<double>() ?? 0;
    public double Height => RectObj?["height"]?.Value<double>() ?? 0;

    public string? ResolvedImgSrc
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ImgSrc) && !ImgSrc.StartsWith("data:")) return ImgSrc;
            if (!string.IsNullOrWhiteSpace(DataSrc) && !DataSrc.StartsWith("data:")) return DataSrc;
            if (!string.IsNullOrWhiteSpace(ImgSrc)) return ImgSrc;
            return null;
        }
    }
}

class PageMapEntry
{
    [JsonProperty("tag")] public string Tag { get; set; } = string.Empty;
    [JsonProperty("segmentId")] public string? SegmentId { get; set; }
    [JsonProperty("label")] public string? Label { get; set; }
    [JsonProperty("y")] public int Y { get; set; }
    [JsonProperty("h")] public int H { get; set; }
    [JsonProperty("w")] public int W { get; set; }
    [JsonProperty("cssB64")] public string? CssB64 { get; set; }
    [JsonProperty("jsB64")] public string? JsB64 { get; set; }
    [JsonProperty("htmlB64")] public string? HtmlB64 { get; set; }
}

class DomSnapshot
{
    [JsonProperty("elements")] public List<DomElement> Elements { get; set; } = new();
    [JsonProperty("pageWidth")] public int PageWidth { get; set; } = 1440;
    [JsonProperty("pageHeight")] public int PageHeight { get; set; } = 900;
    [JsonProperty("pageMap")] public List<PageMapEntry> PageMap { get; set; } = new();
}

class HiddenComponent
{
    [JsonProperty("componentId")] public string ComponentId { get; set; } = string.Empty;
    [JsonProperty("segmentId")] public string? SegmentId { get; set; }
    [JsonProperty("componentType")] public string ComponentType { get; set; } = "HiddenPanel";
    [JsonProperty("label")] public string? Label { get; set; }
    [JsonProperty("cssB64")] public string? CssB64 { get; set; }
    [JsonProperty("jsB64")] public string? JsB64 { get; set; }
    [JsonProperty("elements")] public List<DomElement> Elements { get; set; } = new();
}

class TransformRequest
{
    [JsonProperty("componentId")] public string? ComponentId { get; set; }
    [JsonProperty("url")] public string? Url { get; set; }
    [JsonProperty("domSnapshot")] public DomSnapshot? DomSnapshot { get; set; }
    [JsonProperty("domSnapshotHidden")] public DomSnapshot? DomSnapshotHidden { get; set; }
    [JsonProperty("hiddenComponents")] public List<HiddenComponent> HiddenComponents { get; set; } = new();
    [JsonProperty("componentCSS")] public JObject? ComponentCss { get; set; }
    [JsonProperty("componentJS")] public JToken? ComponentJs { get; set; }
    [JsonProperty("influence")] public JObject? Influence { get; set; }
    [JsonProperty("objective")] public JObject? Objective { get; set; }
    [JsonProperty("client")] public string Client { get; set; } = string.Empty;
    [JsonProperty("viewportSnapshots")] public List<ViewportSnapshotData> ViewportSnapshots { get; set; } = new();
}

class ViewportSnapshotData
{
    [JsonProperty("viewportWidth")] public int ViewportWidth { get; set; }
    [JsonProperty("deviceType")] public string? DeviceType { get; set; }
    [JsonProperty("domSnapshot")] public DomSnapshot? DomSnapshot { get; set; }
    [JsonProperty("componentCSS")] public JObject? ComponentCss { get; set; }
    [JsonProperty("componentJS")] public JToken? ComponentJs { get; set; }
}

class SvgNode
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string Tag { get; set; } = "div";
    public string? TextContent { get; set; }
    public string? ClassName { get; set; }
    public string? ComponentType { get; set; }
    public string? Label { get; set; }
    public string? SegmentId { get; set; }
    public string? ImgSrc { get; set; }
    public string? SvgDataUri { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double TextColumnWidth { get; set; }
    public Dictionary<string, string> CssProps { get; set; } = new();
    public List<SvgNode> Children { get; set; } = new();
    public List<string>? TextLines { get; set; }
    public List<double>? TextLineWidths { get; set; }
}

// ── SVG Builder (ported from StructureService + SvgSerializer) ────────────────

static class SvgPipeline
{
    // ── Build tree ────────────────────────────────────────────────────────────

    public static List<SvgNode> BuildTree(List<DomElement> elements)
    {
        var map = new Dictionary<string, SvgNode>(elements.Count);
        foreach (var el in elements)
        {
            if (string.IsNullOrWhiteSpace(el.InternalId)) continue;
            var node = new SvgNode
            {
                Id = el.InternalId,
                ParentId = el.ParentId,
                Tag = (el.Tag ?? el.TagName ?? "div").ToLowerInvariant(),
                TextContent = el.TextContent,
                ClassName = el.ClassName,
                ComponentType = el.ComponentType,
                Label = el.Label,
                SegmentId = el.SegmentId,
                ImgSrc = el.ResolvedImgSrc,
                SvgDataUri = el.SvgDataUri,
                X = el.X,
                Y = el.Y,
                Width = el.Width,
                Height = el.Height,
                CssProps = el.CssProps ?? new(),
                TextColumnWidth = el.TextColumnWidth,
                TextLines = el.TextWrapLines,
                TextLineWidths = el.TextWrapLineWidths,
            };
            if (el.BorderRadiusPx > 0 && !node.CssProps.ContainsKey("borderRadius"))
                node.CssProps["borderRadius"] = $"{el.BorderRadiusPx}px";
            map[el.InternalId] = node;
        }

        var roots = new List<SvgNode>();
        foreach (var node in map.Values)
        {
            if (!string.IsNullOrWhiteSpace(node.ParentId) && map.TryGetValue(node.ParentId, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }

        SortChildren(roots);
        return roots;
    }

    private static void SortChildren(List<SvgNode> nodes)
    {
        nodes.Sort((a, b) => { var d = a.Y.CompareTo(b.Y); return d != 0 ? d : a.X.CompareTo(b.X); });
        foreach (var n in nodes) SortChildren(n.Children);
    }

    // ── Serialize SVG ─────────────────────────────────────────────────────────

    public static string Serialize(List<SvgNode> tree, int width, int height,
        string sourceUrl, string title, List<PageMapEntry> pageMap)
    {
        var defs = new StringBuilder();
        var body = new StringBuilder();
        var fixed_ = new StringBuilder();
        int fid = 0;

        foreach (var node in tree)
            SerializeNode(body, defs, fixed_, node, 1, 0, 0, ref fid);

        var sb = new StringBuilder(1024 * 64);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\"");
        sb.AppendLine("     xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\"");
        sb.AppendLine("     xmlns:snapstak=\"https://snapstak.ai/ns\"");
        sb.AppendLine($"     width=\"{width}\" height=\"{height}\"");
        sb.AppendLine($"     viewBox=\"0 0 {width} {height}\"");
        sb.AppendLine("     data-snapstak-type=\"tree\"");
        sb.AppendLine($"     data-source-url=\"{EscXml(sourceUrl)}\"");
        sb.AppendLine($"     data-title=\"{EscXml(title)}\">");
        sb.AppendLine("  <defs>");
        if (defs.Length > 0) sb.Append(defs);
        sb.AppendLine("  </defs>");
        sb.Append(body);
        if (fixed_.Length > 0) { sb.AppendLine("  <!-- fixed/sticky -->"); sb.Append(fixed_); }
        WritePageMap(sb, pageMap);
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void SerializeNode(StringBuilder body, StringBuilder defs, StringBuilder fixed_,
        SvgNode node, int depth, double pX, double pY, ref int filterId)
    {
        var css = node.CssProps;
        var position = css.GetValueOrDefault("position", "static");
        var display = css.GetValueOrDefault("display", "block");
        var visible = css.GetValueOrDefault("visibility", "visible");
        var opacity = css.GetValueOrDefault("opacity", "1");
        bool isFixed = position == "fixed"; // sticky stays in normal flow — carving requires children to remain inside parent <g>
        var target = isFixed ? fixed_ : body;
        var indent = new string(' ', depth * 2);

        var relX = isFixed ? Math.Round(node.X) : Math.Round(node.X - pX);
        var relY = isFixed ? Math.Round(node.Y) : Math.Round(node.Y - pY);
        var w = Math.Round(node.Width);
        var h = Math.Round(node.Height);

        bool hasContent = !string.IsNullOrEmpty(node.TextContent) ||
                          !string.IsNullOrEmpty(node.ImgSrc) ||
                          !string.IsNullOrEmpty(node.SvgDataUri) ||
                          node.Children.Count > 0;
        if (w <= 0 && h <= 0 && !hasContent) return;

        var componentType = node.ComponentType ?? InferType(node);
        var label = EscXml(node.Label ?? node.SegmentId ?? componentType);

        // Box shadow filter
        string? filterRef = null;
        var boxShadow = css.GetValueOrDefault("boxShadow", "");
        if (!string.IsNullOrEmpty(boxShadow) && boxShadow != "none")
        {
            var fid2 = $"f{filterId++}";
            var (sdx, sdy, sblur, scolor, sopacity) = ParseBoxShadow(boxShadow);
            defs.AppendLine($"    <filter id=\"{fid2}\" x=\"-30%\" y=\"-30%\" width=\"160%\" height=\"160%\">");
            defs.AppendLine($"      <feDropShadow dx=\"{sdx:F1}\" dy=\"{sdy:F1}\" stdDeviation=\"{sblur / 2.0:F1}\"");
            defs.AppendLine($"                    flood-color=\"{EscXml(scolor)}\" flood-opacity=\"{sopacity:F2}\"/>");
            defs.AppendLine($"    </filter>");
            filterRef = fid2;
        }

        var (strokeColor, strokeWidth) = ParseBorder(css);
        var rx = ParseBorderRadius(css, w, h);

        target.Append($"{indent}<g id=\"{EscXml(node.Id)}\"");
        target.Append($" transform=\"translate({relX},{relY})\"");
        target.Append($" data-w=\"{w}\" data-h=\"{h}\"");
        target.Append($" inkscape:label=\"{label}\"");
        if (!string.IsNullOrEmpty(componentType)) target.Append($" data-component-type=\"{EscXml(componentType)}\"");
        if (!string.IsNullOrEmpty(node.ClassName)) target.Append($" data-classes=\"{EscXml(node.ClassName)}\"");
        if (!string.IsNullOrEmpty(node.SegmentId)) target.Append($" data-segment-id=\"{EscXml(node.SegmentId)}\"");
        if (display is "flex" or "grid" or "inline-flex" or "inline-grid") target.Append($" data-display=\"{display}\"");
        if (isFixed) target.Append($" data-position=\"{position}\"");
        var gap = css.GetValueOrDefault("gap", "");
        if (!string.IsNullOrEmpty(gap)) target.Append($" data-gap=\"{EscXml(gap)}\"");
        if (display == "none" || visible == "hidden") target.Append(" data-hidden=\"true\"");
        if (filterRef != null) target.Append($" filter=\"url(#{filterRef})\"");
        if (opacity != "1" && !string.IsNullOrEmpty(opacity)) target.Append($" opacity=\"{EscXml(opacity)}\"");
        target.AppendLine(">");

        var bgColor = css.GetValueOrDefault("backgroundColor", "");
        bool hasBg = !string.IsNullOrEmpty(bgColor) && bgColor != "rgba(0, 0, 0, 0)" && bgColor != "transparent" && w > 0 && h > 0;
        if (hasBg || strokeWidth > 0)
        {
            target.Append($"{indent}  <rect width=\"{w}\" height=\"{h}\"");
            target.Append($" fill=\"{(hasBg ? EscXml(bgColor) : "none")}\"");
            if (rx > 0) target.Append($" rx=\"{rx:F1}\"");
            if (strokeWidth > 0) { target.Append($" stroke=\"{EscXml(strokeColor)}\""); target.Append($" stroke-width=\"{strokeWidth:F1}\""); }
            target.AppendLine("/>");
        }

        if (!string.IsNullOrEmpty(node.SvgDataUri))
            target.AppendLine($"{indent}  <image href=\"{EscXml(node.SvgDataUri)}\" x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" preserveAspectRatio=\"xMidYMid meet\"/>");

        if (!string.IsNullOrEmpty(node.ImgSrc))
        {
            var clip = rx > 0 ? $" clip-path=\"inset(0 round {rx:F1}px)\"" : "";
            target.AppendLine($"{indent}  <image href=\"{EscXml(node.ImgSrc)}\" x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" preserveAspectRatio=\"xMidYMid slice\"{clip}/>");
        }

        var text = node.TextContent?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            // Use browser-measured lines from content.js Range API when available.
            // Fall back to ComputeTextLines() estimator only when not present.
            if (node.TextLines == null || node.TextLines.Count == 0)
                node.TextLines = ComputeTextLines(css, text, w, h);
            RenderText(target, defs, indent, css, text, node.TextLines, node.TextLineWidths, w, h, ref filterId);
        }

        foreach (var child in node.Children)
            SerializeNode(body, defs, fixed_, child, depth + 1, node.X, node.Y, ref filterId);

        target.AppendLine($"{indent}</g>");
    }

    private static List<string> ComputeTextLines(
        Dictionary<string, string> css, string text, double w, double h)
    {
        var fontSize = ParsePx(css.GetValueOrDefault("fontSize", "14px"), 14);
        var fontWeight = css.GetValueOrDefault("fontWeight", "400");
        var fontFamily = css.GetValueOrDefault("fontFamily", "sans-serif");
        var lhRaw = css.GetValueOrDefault("lineHeight", "");
        var whiteSpace = css.GetValueOrDefault("whiteSpace", "");
        var textTx = css.GetValueOrDefault("textTransform", "");
        double lhPx = string.IsNullOrEmpty(lhRaw) ? 0 : ParsePx(lhRaw, 0);
        double naturalLh = Math.Round(fontSize * 1.4);
        double lineHeightPx = (lhPx > 0 && lhPx <= fontSize * 2) ? lhPx : naturalLh;
        bool isMono = fontFamily.Contains("monospace");
        bool isUpper = textTx == "uppercase";
        bool isBold = fontWeight == "700" || fontWeight == "bold" ||
            (double.TryParse(fontWeight, out var fw) && fw >= 600);
        double charRatio = isMono ? 0.60 : isUpper ? 0.58 : isBold ? 0.56 : 0.52;
        bool noWrap = whiteSpace == "nowrap" || whiteSpace == "pre";
        int maxLines = (h > 0 && lineHeightPx > 0)
            ? Math.Max(1, (int)Math.Round(h / lineHeightPx)) : 99;
        int maxChars = (!noWrap && w > 0)
            ? Math.Max(1, (int)Math.Floor(w / (fontSize * charRatio))) : 9999;
        return WrapText(text, maxChars, maxLines);
    }

    private static void RenderText(StringBuilder t, StringBuilder defs, string indent, Dictionary<string, string> css,
        string text, List<string> lines, List<double>? lineWidths, double w, double h, ref int filterId)
    {
        var color = css.GetValueOrDefault("color", "#000000");
        var fontSize = ParsePx(css.GetValueOrDefault("fontSize", "14px"), 14);
        var fontWeight = css.GetValueOrDefault("fontWeight", "400");
        var fontFamily = css.GetValueOrDefault("fontFamily", "sans-serif");
        var lhRaw = css.GetValueOrDefault("lineHeight", "");
        var textTx = css.GetValueOrDefault("textTransform", "");
        var textDec = css.GetValueOrDefault("textDecoration", "");
        var padL = ParsePx(css.GetValueOrDefault("paddingLeft", "0px"), 0);
        var padR = ParsePx(css.GetValueOrDefault("paddingRight", "0px"), 0);
        var justify = css.GetValueOrDefault("justifyContent", "");
        var textAlign = css.GetValueOrDefault("textAlign", "start");
        var display = css.GetValueOrDefault("display", "block");
        var alignItems = css.GetValueOrDefault("alignItems", "");
        var vertAlign = css.GetValueOrDefault("verticalAlign", "");

        double lhPx = string.IsNullOrEmpty(lhRaw) ? 0 : ParsePx(lhRaw, 0);
        double naturalLh = Math.Round(fontSize * 1.4);
        double lineHeightPx = (lhPx > 0 && lhPx <= fontSize * 2) ? lhPx : naturalLh;

        bool isCentered = alignItems == "center" || vertAlign == "middle" ||
                          (lhPx > 0 && h > 0 && Math.Abs(lhPx - h) < 4);
        bool isEnd = alignItems == "flex-end" || alignItems == "end" || vertAlign == "bottom";

        string dominantBaseline = isCentered ? "central" : "auto";
        double textY = isCentered ? Math.Round(h / 2.0)
                     : isEnd ? Math.Round(h - fontSize * 0.2)
                     : Math.Round(fontSize);

        bool flexCenter = (display == "flex" || display == "inline-flex") && justify == "center";
        string anchor; double textX;
        if (flexCenter || textAlign == "center") { anchor = "middle"; textX = w / 2.0; }
        else if (textAlign == "right" || textAlign == "end") { anchor = "end"; textX = w - padR; }
        else { anchor = "start"; textX = padL; }

        if (textTx == "uppercase") lines = lines.Select(l => l.ToUpperInvariant()).ToList();
        else if (textTx == "lowercase") lines = lines.Select(l => l.ToLowerInvariant()).ToList();

        // ── clipPath to contain font-metric overflow ──────────────────────────
        var clipId = $"tc{filterId++}";
        defs.AppendLine($"    <clipPath id=\"{clipId}\">");
        defs.AppendLine($"      <rect width=\"{w:F0}\" height=\"{h:F0}\"/>");
        defs.AppendLine($"    </clipPath>");

        t.AppendLine($"{indent}  <g clip-path=\"url(#{clipId})\">");
        t.Append($"{indent}    <text");
        t.Append($" x=\"{textX:F0}\"");
        t.Append($" y=\"{textY:F0}\"");
        t.Append($" font-family=\"{EscXml(fontFamily)}\"");
        t.Append($" font-size=\"{fontSize:F0}px\"");
        t.Append($" font-weight=\"{EscXml(fontWeight)}\"");
        t.Append($" fill=\"{EscXml(color)}\"");
        t.Append($" text-anchor=\"{anchor}\"");
        t.Append($" dominant-baseline=\"{dominantBaseline}\"");
        if (!string.IsNullOrEmpty(textDec) && textDec != "none")
            t.Append($" text-decoration=\"{EscXml(textDec)}\"");
        t.AppendLine(">");

        if (lines.Count <= 1)
        {
            t.AppendLine($"{indent}      <tspan>{EscXml(text)}</tspan>");
        }
        else
        {
            for (int li = 0; li < lines.Count; li++)
            {
                string dy = li == 0 ? "0" : $"{lineHeightPx:F0}";
                t.Append($"{indent}      <tspan x=\"{textX:F0}\" dy=\"{dy}\"");
                if (lineWidths != null && li < lineWidths.Count && lineWidths[li] > 0)
                    t.Append($" textLength=\"{lineWidths[li]:F0}\" lengthAdjust=\"spacingAndGlyphs\"");
                t.AppendLine($">{EscXml(lines[li])}</tspan>");
            }
        }
        t.AppendLine($"{indent}    </text>");
        t.AppendLine($"{indent}  </g>");
    }

    // Port of wrapWords() from svgSerializer.js
    private static List<string> WrapText(string text, int maxChars, int maxLines)
    {
        if (maxChars >= 9999) return new() { text };

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var cur = new StringBuilder();

        foreach (var word in words)
        {
            var test = cur.Length > 0 ? cur + " " + word : word;
            if (test.Length <= maxChars)
                cur = new StringBuilder(test);
            else
            {
                if (cur.Length > 0) lines.Add(cur.ToString());
                cur = new StringBuilder(word);
            }
        }
        if (cur.Length > 0) lines.Add(cur.ToString());

        var result = lines.Count > 0 ? lines : new List<string> { text };
        // When lines exceed maxLines, append overflow words onto the last line
        // rather than discarding them. Matches browser overflow behavior where
        // all words are visible, with the last line extending beyond the box.
        if (result.Count > maxLines)
        {
            var kept = result.Take(maxLines - 1).ToList();
            var overflow = string.Join(" ", result.Skip(maxLines - 1));
            kept.Add(overflow);
            return kept;
        }
        return result;
    }


    private static void WritePageMap(StringBuilder sb, List<PageMapEntry> pageMap)
    {
        if (pageMap.Count == 0) return;
        sb.AppendLine("  <snapstak:pagemap xmlns:snapstak=\"https://snapstak.ai/ns\">");
        foreach (var s in pageMap)
        {
            sb.Append($"    <snapstak:component tag=\"{EscXml(s.Tag)}\"");
            sb.Append($" segmentId=\"{EscXml(s.SegmentId ?? "")}\"");
            sb.Append($" label=\"{EscXml(s.Label ?? s.Tag)}\"");
            sb.AppendLine($" y=\"{s.Y}\" h=\"{s.H}\" w=\"{s.W}\">");
            if (!string.IsNullOrEmpty(s.CssB64)) sb.AppendLine($"      <snapstak:css>{s.CssB64}</snapstak:css>");
            if (!string.IsNullOrEmpty(s.JsB64)) sb.AppendLine($"      <snapstak:js>{s.JsB64}</snapstak:js>");
            sb.AppendLine("    </snapstak:component>");
        }
        sb.AppendLine("  </snapstak:pagemap>");
    }

    // ── Segment carver ────────────────────────────────────────────────────────

    public static string? CarveSegment(string svgContent, string segmentId, string pageComponentId)
    {
        var re = new Regex($"<g\\s[^>]*data-segment-id=\"{Regex.Escape(segmentId)}\"[^>]*>");
        var match = re.Match(svgContent);
        if (!match.Success) return null;

        int depth = 0, i = match.Index, len = svgContent.Length;
        while (i < len)
        {
            if (svgContent[i] == '<' && i + 1 < len && svgContent[i + 1] == 'g' && i + 2 < len && (svgContent[i + 2] == ' ' || svgContent[i + 2] == '>')) { depth++; i += 2; }
            else if (svgContent[i] == '<' && svgContent.AsSpan(i).StartsWith("</g>")) { depth--; if (depth == 0) { i += 4; break; } i += 4; }
            else i++;
        }

        var gContent = svgContent[match.Index..i];
        var wM = Regex.Match(match.Value, "data-w=\"(\\d+)\"");
        var hM = Regex.Match(match.Value, "data-h=\"(\\d+)\"");
        var w = wM.Success ? int.Parse(wM.Groups[1].Value) : 0;
        var h = hM.Success ? int.Parse(hM.Groups[1].Value) : 0;
        var norm = new Regex("transform=\"translate\\([\\d.-]+\\s*,\\s*[\\d.-]+\\)\"").Replace(gContent, "transform=\"translate(0,0)\"", 1);
        var defsM = Regex.Match(svgContent, "<defs>([\\s\\S]*?)</defs>");
        var defs = defsM.Success ? $"\n  <defs>{defsM.Groups[1].Value}</defs>" : string.Empty;

        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg"
     xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape"
     width="{w}" height="{h}"
     viewBox="0 0 {w} {h}"
     data-snapstak-type="tree"
     data-segment-id="{segmentId}"
     data-page-component-id="{pageComponentId}">{defs}
{norm}
</svg>
""";
    }

    // ── Carve the tree for a single segment ───────────────────────────────────
    //
    // Walks the master tree and returns the subtree rooted at the node matching
    // the given segmentId, with coordinates renormalised to (0,0). Used as the
    // input for translator plugins when processing carved segments — plugins
    // need the actual tree, not the stringified SVG.
    public static List<SvgNode>? CarveSegmentTree(List<SvgNode> tree, string segmentId)
    {
        foreach (var root in tree)
        {
            var found = FindSegment(root, segmentId);
            if (found != null) return new List<SvgNode> { Renormalise(found, found.X, found.Y) };
        }
        return null;

        static SvgNode? FindSegment(SvgNode n, string sid)
        {
            if (n.SegmentId == sid) return n;
            foreach (var c in n.Children)
            {
                var r = FindSegment(c, sid);
                if (r != null) return r;
            }
            return null;
        }

        static SvgNode Renormalise(SvgNode src, double offX, double offY) => new()
        {
            Id = src.Id,
            ParentId = src.ParentId,
            Tag = src.Tag,
            TextContent = src.TextContent,
            ClassName = src.ClassName,
            ComponentType = src.ComponentType,
            Label = src.Label,
            SegmentId = src.SegmentId,
            ImgSrc = src.ImgSrc,
            SvgDataUri = src.SvgDataUri,
            X = src.X - offX,
            Y = src.Y - offY,
            Width = src.Width,
            Height = src.Height,
            TextColumnWidth = src.TextColumnWidth,
            CssProps = src.CssProps,
            TextLines = src.TextLines,
            Children = src.Children.Select(c => Renormalise(c, offX, offY)).ToList(),
        };
    }

    // ── CSS parsers ───────────────────────────────────────────────────────────

    static (string color, double width) ParseBorder(Dictionary<string, string> css)
    {
        var b = css.GetValueOrDefault("border", "");
        if (string.IsNullOrEmpty(b) || b.StartsWith("0px") || b == "none" || b == "medium" || b == "initial") return ("none", 0);
        var wm = Regex.Match(b, @"([\d.]+)px"); var cm = Regex.Match(b, @"(rgb\a?\([^)]+\)|#[0-9a-fA-F]{3,8})");
        double sw = wm.Success ? double.Parse(wm.Groups[1].Value) : 0;
        string sc = cm.Success ? cm.Groups[1].Value : "#000000";
        if (b.Contains("none") || sw < 0.1) return ("none", 0);
        return (sc, sw);
    }

    static double ParseBorderRadius(Dictionary<string, string> css, double w, double h)
    {
        var br = css.GetValueOrDefault("borderRadius", "");
        if (string.IsNullOrEmpty(br) || br == "0px" || br == "0") return 0;
        var m = Regex.Match(br, @"([\d.]+)(px|%)"); if (!m.Success) return 0;
        var val = double.Parse(m.Groups[1].Value);
        var px = m.Groups[2].Value == "%" ? val / 100.0 * Math.Min(w, h) : val;
        return Math.Min(px, Math.Min(w, h) / 2.0);
    }

    static (double dx, double dy, double blur, string color, double opacity) ParseBoxShadow(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "none") return (0, 2, 4, "#000000", 0.1);
        var nums = Regex.Matches(s, @"-?[\d.]+px").Cast<Match>().Select(m => double.Parse(m.Value.Replace("px", ""))).ToList();
        var cm = Regex.Match(s, @"rgba?\([^)]+\)|#[0-9a-fA-F]{3,8}");
        var color = cm.Success ? cm.Value : "#000000";
        double opacity = 0.2;
        var opM = Regex.Match(color, @"rgba\([^,]+,[^,]+,[^,]+,\s*([\d.]+)\)");
        if (opM.Success) opacity = double.Parse(opM.Groups[1].Value);
        var rgbColor = Regex.Replace(color, @",\s*[\d.]+\)", ")").Replace("rgba(", "rgb(");
        return (nums.Count > 0 ? nums[0] : 0, nums.Count > 1 ? nums[1] : 2, nums.Count > 2 ? Math.Abs(nums[2]) : 4, rgbColor, opacity);
    }

    static double ParsePx(string v, double fallback)
    {
        if (string.IsNullOrEmpty(v)) return fallback;
        var m = Regex.Match(v, @"([\d.]+)"); return m.Success ? double.Parse(m.Groups[1].Value) : fallback;
    }

    static string InferType(SvgNode n) => n.Tag switch
    {
        "button" => "Button",
        "a" => "Link",
        "img" => "Image",
        "input" => "Input",
        "nav" => "Navigation",
        "header" => "Header",
        "footer" => "Footer",
        "main" => "Main",
        "svg" => "Icon",
        _ => "Container"
    };

    static string EscXml(string? v)
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        return v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    static string? DecodeSvgDataUri(string uri)
    {
        if (uri.StartsWith("data:image/svg+xml;charset=utf-8,", StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(uri["data:image/svg+xml;charset=utf-8,".Length..]);
        if (uri.StartsWith("data:image/svg+xml;base64,", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8.GetString(Convert.FromBase64String(uri["data:image/svg+xml;base64,".Length..]));
        return null;
    }
}

// ── Storage ───────────────────────────────────────────────────────────────────

static class Storage
{
    static string Root = string.Empty;
    static readonly ConcurrentDictionary<string, object> _locks = new();
    static object Lock(string u) => _locks.GetOrAdd(u, _ => new object());

    public static void Configure(string root) => Root = root;

    public static string ComponentDir(string user, string componentId)
    {
        var dir = $"{Root}/{user}/{componentId}";
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string IconsDir(string componentDir)
    {
        var dir = $"{componentDir}/icons";
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Write(string path, string content)
        => File.WriteAllText(path, content, Encoding.UTF8);

    public static void WriteBytes(string path, byte[] bytes)
        => File.WriteAllBytes(path, bytes);

    public static void WriteJson(string path, object obj)
        => Write(path, JsonConvert.SerializeObject(obj, Formatting.Indented));

    // ── Manifest ──────────────────────────────────────────────────────────────

    public static void RegisterComponent(string user, string componentId, string componentDir,
        string zone, string label, bool isMaster = false)
    {
        lock (Lock(user))
        {
            var path = $"{Root}/{user}/manifest.json";
            var manifest = File.Exists(path)
                ? JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path)) ?? new JObject()
                : new JObject { ["userUuid"] = "local", ["createdAt"] = DateTime.UtcNow.ToString("O"), ["components"] = new JArray() };

            manifest["updatedAt"] = DateTime.UtcNow.ToString("O");
            var components = (JArray)(manifest["components"] ??= new JArray());
            var existing = components.FirstOrDefault(c => c["componentId"]?.ToString() == componentId);
            if (existing != null) components.Remove(existing);

            string F(string s) => $"{componentDir}/{componentId}{s}";
            bool E(string s) => File.Exists(F(s));

            components.Add(new JObject
            {
                ["componentId"] = componentId,
                ["label"] = label,
                ["zone"] = zone,
                ["isMaster"] = isMaster,
                ["folder"] = componentDir,
                ["status"] = "Pending",
                ["registeredAt"] = DateTime.UtcNow.ToString("O"),
                ["pillarStatus"] = new JObject
                {
                    ["structure"] = E(".svg"),
                    ["behaviour"] = E("_css.md") && E("_js.md"),
                    ["influence"] = E("_influence.json"),
                    ["objective"] = E("_objective.json"),
                },
                ["files"] = new JObject
                {
                    ["structure"] = E(".svg") ? $"{componentId}.svg" : null,
                    ["cssJson"] = E("_css.json") ? $"{componentId}_css.json" : null,
                    ["jsJson"] = E("_js.json") ? $"{componentId}_js.json" : null,
                    ["influence"] = E("_influence.json") ? $"{componentId}_influence.json" : null,
                    ["objective"] = E("_objective.json") ? $"{componentId}_objective.json" : null,
                },
            });

            File.WriteAllText(path, manifest.ToString(Formatting.Indented), Encoding.UTF8);
            Console.WriteLine($"[Storage] ✅ Manifest: {componentId} ({zone}) registered");
        }
    }

    public static string? InferSectionTag(string componentId)
    {
        var lower = componentId.ToLowerInvariant();
        return new[] { "header", "footer", "main", "nav", "aside", "section", "article", "form" }
            .FirstOrDefault(t => lower.Contains(t));
    }

    public static (string Zone, string Label) InferZoneLabel(string componentId)
    {
        var l = componentId.ToLowerInvariant();
        if (l.StartsWith("header_")) return ("header", "Header");
        if (l.StartsWith("navbar_")) return ("navbar", "Navbar");
        if (l.StartsWith("footer_")) return ("footer", "Footer");
        if (l.StartsWith("nav_")) return ("nav", "Navigation");
        if (l.StartsWith("main_")) return ("main", "Main Content");
        if (l.StartsWith("section_")) return ("section", "Section");
        if (l.StartsWith("article_")) return ("article", "Article");
        if (l.StartsWith("aside_")) return ("aside", "Aside");
        if (l.StartsWith("form_")) return ("form", "Form");
        if (l.StartsWith("div_ss_hc_")) return ("div", "Hidden Component");
        if (l.StartsWith("div_")) return ("div", "Component");
        var first = componentId.Split('_')[0];
        var lbl = first.Length > 0 ? char.ToUpperInvariant(first[0]) + first[1..].ToLowerInvariant() : "Component";
        return ("main", lbl);
    }
}

// ── Transform pipeline (ported from StructureAgentCom.TransformAsync) ─────────

/// <summary>
/// HttpClient handler that converts any WebP image response into a JPEG before
/// returning it to the caller. Penpot accepts PNG and JPEG as fillImage sources
/// but rejects WebP during import schema validation — Autosport and many other
/// CDN-hosted sites serve WebP by default. Converting at the fetch boundary
/// means every downstream plugin sees only PNG/JPEG bytes; plugin code
/// (ResolveImage, RegisterImage) stays WebP-unaware.
///
/// Detection is content-based (RIFF/WEBP magic bytes) rather than relying on
/// Content-Type headers or URL extensions, since CDNs often serve WebP with
/// generic Content-Type: image/jpeg or no extension at all.
/// </summary>
sealed class WebPToJpegConversionHandler : System.Net.Http.DelegatingHandler
{
    public WebPToJpegConversionHandler(System.Net.Http.HttpMessageHandler inner) : base(inner) { }

    protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
        System.Net.Http.HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content == null) return response;

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!IsWebP(bytes))
        {
            // Re-wrap untouched bytes so subsequent ReadAsByteArrayAsync calls still work.
            // (The original Content stream was consumed by the read above.)
            var passthrough = new System.Net.Http.ByteArrayContent(bytes);
            foreach (var h in response.Content.Headers)
                passthrough.Headers.TryAddWithoutValidation(h.Key, h.Value);
            response.Content = passthrough;
            return response;
        }

        // Convert WebP → JPEG via SkiaSharp. SkiaSharp 2.88+ decodes WebP via
        // its bundled libwebp; JPEG encoding at quality 90 is visually lossless
        // for typical web imagery at display sizes.
        byte[] jpegBytes;
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap == null)
            {
                Console.WriteLine($"[WebPToJpeg] ⚠️ SKBitmap.Decode returned null for {request.RequestUri}; passing WebP through unchanged");
                var fallback = new System.Net.Http.ByteArrayContent(bytes);
                foreach (var h in response.Content.Headers)
                    fallback.Headers.TryAddWithoutValidation(h.Key, h.Value);
                response.Content = fallback;
                return response;
            }

            // JPEG cannot represent an alpha channel. Composite over opaque white
            // so transparent pixels read as white rather than random RGB values
            // bleeding through. Matches what browsers do when downloading a
            // transparent image as JPEG.
            using var flat = new SKBitmap(bitmap.Width, bitmap.Height, isOpaque: true);
            using (var canvas = new SKCanvas(flat))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(bitmap, 0, 0);
            }

            using var image = SKImage.FromBitmap(flat);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality: 90);
            jpegBytes = data.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebPToJpeg] ⚠️ Conversion failed for {request.RequestUri}: {ex.Message}; passing WebP through unchanged");
            var fallback = new System.Net.Http.ByteArrayContent(bytes);
            foreach (var h in response.Content.Headers)
                fallback.Headers.TryAddWithoutValidation(h.Key, h.Value);
            response.Content = fallback;
            return response;
        }

        var jpegContent = new System.Net.Http.ByteArrayContent(jpegBytes);
        // Preserve non-content-type headers from the original response, override
        // Content-Type so downstream GuessMime picks up "image/jpeg".
        foreach (var h in response.Content.Headers)
        {
            if (string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            jpegContent.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        jpegContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        response.Content = jpegContent;

        Console.WriteLine($"[WebPToJpeg] ✅ {request.RequestUri}: {bytes.Length} bytes WebP → {jpegBytes.Length} bytes JPEG");
        return response;
    }

    /// <summary>
    /// Sniff the leading bytes for the WebP magic signature:
    /// 'R' 'I' 'F' 'F' [4 bytes size] 'W' 'E' 'B' 'P'.
    /// </summary>
    static bool IsWebP(byte[] bytes)
    {
        if (bytes.Length < 12) return false;
        return bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46   // "RIFF"
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50;  // "WEBP"
    }
}

static class Pipeline
{
    const string UserUuid = "local";

    // Static — reflection discovery runs once per process lifetime. Safe because
    // TranslatorPluginHost is stateless after the initial scan.
    static readonly TranslatorPluginHost _translators = new();

    // Shared HttpClient for Phase 1 resource fetches (image downloads).
    // Wrapped with WebPToJpegConversionHandler so downstream plugins never see
    // WebP bytes — Penpot rejects WebP during import validation, and converting
    // once at the fetch boundary keeps every plugin's ResolveImage path simple.
    // Lazily initialised — only allocated when a plugin actually declares URLs.
    static readonly System.Net.Http.HttpClient _http = new(
        new WebPToJpegConversionHandler(new System.Net.Http.HttpClientHandler()))
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    // Plugin settings — configured by Program.cs after loading snapstak_settings.json.
    // All plugins enabled by default, output paths empty = write to component dir.
    static ServerSettings _settings = new ServerSettings();
    public static void LogPlugins()
    {
        var count = _translators.Plugins.Count;
        if (count == 0)
        {
            Console.WriteLine("[Pipeline] ❌ No translator plugins discovered — check that SnapStak.Wasm.Client assembly is loaded.");
            return;
        }
        foreach (var p in _translators.Plugins)
            Console.WriteLine($"[Pipeline] ✅ Plugin ready: '{p.Key}' ({p.DisplayName} v{p.Version}) → {p.FileExtension}");
    }

    public static void Configure(ServerSettings settings) => _settings = settings;

    // Returns true if the plugin should run for this capture.
    static bool IsPluginEnabled(string pluginKey) => pluginKey switch
    {
        "penpot" => _settings.PenpotEnabled,
        "canva" => _settings.CanvaEnabled,
        "figma" => _settings.FigmaEnabled,
        _ => true,
    };

    // Resolves the output directory for a plugin file.
    // Uses the plugin-specific path if configured; otherwise the component dir.
    static string PluginOutputDir(string pluginKey, string defaultDir)
    {
        var custom = pluginKey switch
        {
            "penpot" => _settings.PenpotOutputPath,
            "canva" => _settings.CanvaOutputPath,
            "figma" => _settings.FigmaOutputPath,
            _ => string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(custom))
        {
            Directory.CreateDirectory(custom);
            return custom;
        }
        return defaultDir;
    }

    public static async System.Threading.Tasks.Task TransformAsync(TransformRequest req)
    {
        if (req.DomSnapshot == null || string.IsNullOrEmpty(req.ComponentId)) return;

        var componentDir = Storage.ComponentDir(UserUuid, req.ComponentId);
        var uri = Uri.TryCreate(req.Url, UriKind.Absolute, out var parsed)
            ? parsed.Host.Replace("www.", "") : (req.Url ?? "unknown");

        // ── Build master SVG ──────────────────────────────────────────────────
        var tree = SvgPipeline.BuildTree(req.DomSnapshot.Elements);
        var svgString = SvgPipeline.Serialize(tree,
            req.DomSnapshot.PageWidth, req.DomSnapshot.PageHeight,
            req.Url ?? "", uri, req.DomSnapshot.PageMap);

        Storage.Write($"{componentDir}/{req.ComponentId}.svg", svgString);
        Console.WriteLine($"[Pipeline] ✅ SVG written → {req.ComponentId}.svg");

        // ── Run translator plugins against the master tree ────────────────────
        // Bundle is fully populated: desktop tree + all carved segment trees +
        // all hidden component trees + mobile viewport tree (if present).
        // Segments and hidden components are built here so plugins receive the
        // complete picture in a single pass — Penpot, Canva, and Figma all
        // use this to produce multi-page outputs.

        // Build segment trees from the pageMap
        var masterSegments = (req.DomSnapshot.PageMap ?? new())
            .Where(s => !string.IsNullOrEmpty(s.SegmentId))
            .Select(s =>
            {
                var segTree = SvgPipeline.CarveSegmentTree(tree, s.SegmentId!) ?? new List<SvgNode>();
                return new ClientPlugins.TranslatorSegment
                {
                    SegmentId = s.SegmentId!,
                    Label = s.Label ?? s.Tag ?? s.SegmentId!,
                    Zone = s.Tag ?? "section",
                    Tree = segTree.Select(MapToClientNode).ToList(),
                    Width = s.W,
                    Height = s.H,
                };
            })
            .Where(s => s.Tree.Count > 0)
            .ToArray();

        // Build hidden component trees from HiddenComponents
        var masterHidden = req.HiddenComponents
            .Select(hc =>
            {
                var hcTree = SvgPipeline.BuildTree(hc.Elements ?? new());
                var hcW = hcTree.Count > 0
                    ? (int)hcTree.Max(n => n.X + n.Width) : 390;
                var hcH = hcTree.Count > 0
                    ? (int)hcTree.Max(n => n.Y + n.Height) : 800;
                if (hcW < 1) hcW = 390;
                if (hcH < 1) hcH = 800;
                return new ClientPlugins.TranslatorHiddenComponent
                {
                    ComponentId = hc.ComponentId ?? hc.SegmentId ?? hcTree.GetHashCode().ToString(),
                    Label = hc.ComponentType ?? "HiddenComponent",
                    Tree = hcTree.Select(MapToClientNode).ToList(),
                    Width = hcW,
                    Height = hcH,
                };
            })
            .ToArray();

        // Build mobile viewport from the first viewport snapshot (if present)
        List<ClientSvg.SvgNode>? mobileClientTree = null;
        ClientSvg.SvgTreeOptions? mobileOpts = null;
        var primaryViewport = req.ViewportSnapshots.FirstOrDefault();
        if (primaryViewport?.DomSnapshot != null)
        {
            var mobTree = SvgPipeline.BuildTree(primaryViewport.DomSnapshot.Elements);
            mobileClientTree = mobTree.Select(MapToClientNode).ToList();
            mobileOpts = new ClientSvg.SvgTreeOptions
            {
                Width = primaryViewport.DomSnapshot.PageWidth > 0 ? primaryViewport.DomSnapshot.PageWidth : primaryViewport.ViewportWidth,
                Height = primaryViewport.DomSnapshot.PageHeight > 0 ? primaryViewport.DomSnapshot.PageHeight : primaryViewport.ViewportWidth * 2,
                SourceUrl = req.Url ?? "",
                Title = $"{uri}_mobile",
            };
        }

        await RunTranslatorsAsync(
            tree,
            new ClientSvg.SvgTreeOptions
            {
                Width = req.DomSnapshot.PageWidth,
                Height = req.DomSnapshot.PageHeight,
                SourceUrl = req.Url ?? "",
                Title = uri,
                PageMap = new(),
            },
            req.ComponentId!,
            componentDir,
            segments: masterSegments,
            hiddenComponents: masterHidden,
            mobileTree: mobileClientTree,
            mobileOptions: mobileOpts);

        // ── Write CSS/JS JSON from pageMap ────────────────────────────────────
        WritePageMapCssJs(req.DomSnapshot.PageMap ?? new(), componentDir, req.ComponentId);

        // ── Write component JS ────────────────────────────────────────────────
        if (req.ComponentJs != null)
            Storage.WriteJson($"{componentDir}/{req.ComponentId}_js.json", req.ComponentJs);

        // ── Write source HTML per landmark ────────────────────────────────────
        WriteSourceHtml(req.DomSnapshot.PageMap ?? new(), componentDir, req.ComponentId);

        // ── Write hidden elements ─────────────────────────────────────────────
        if (req.DomSnapshotHidden?.Elements.Count > 0)
            Storage.WriteJson($"{componentDir}/{req.ComponentId}_hidden.json", req.DomSnapshotHidden.Elements);

        // ── Write Influence + Objective ───────────────────────────────────────
        if (req.Influence != null)
            Storage.WriteJson($"{componentDir}/{req.ComponentId}_influence.json", req.Influence);
        if (req.Objective != null)
            Storage.WriteJson($"{componentDir}/{req.ComponentId}_objective.json", req.Objective);

        // ── Register master in manifest ───────────────────────────────────────
        var (mZone, mLabel) = Storage.InferZoneLabel(req.ComponentId);
        Storage.RegisterComponent(UserUuid, req.ComponentId, componentDir, mZone, mLabel, isMaster: true);

        // ── Carve segments from pageMap (also runs translators per segment) ──
        if ((req.DomSnapshot.PageMap?.Count ?? 0) > 0)
            await CarveSegments(req, tree, svgString, componentDir, uri);

        // ── Write hidden components (also runs translators per component) ───
        if (req.HiddenComponents.Count > 0)
            await WriteHiddenComponents(req);

        // ── Process mobile viewport snapshots (non-fatal) ─────────────────────
        if (req.ViewportSnapshots.Count > 0)
        {
            foreach (var vp in req.ViewportSnapshots)
            {
                try { await SaveViewportSnapshot(req.ComponentId!, componentDir, vp); }
                catch (Exception vpEx)
                { Console.WriteLine($"[Pipeline] ⚠️ Viewport snapshot failed (non-fatal): {vpEx.Message}\n{vpEx.StackTrace}"); }
            }
        }

        Console.WriteLine($"[Pipeline] ✅ All files written for {req.ComponentId}");
    }

    // ── Translator plugin invocation ──────────────────────────────────────────
    //
    // Shared by every place that writes a SnapStak SVG — master transform,
    // viewport snapshots, carved segments, and hidden components.
    //
    // Projects the server's local SvgNode tree to the client's SvgNode type
    // (which is what IConteXTranslatorPlugin expects), runs every plugin, and
    // writes each byte[] to disk using the plugin's declared extension.
    static async System.Threading.Tasks.Task RunTranslatorsAsync(
        List<SvgNode> serverTree,
        ClientSvg.SvgTreeOptions options,
        string componentId,
        string componentDir,
        ClientPlugins.TranslatorSegment[]? segments = null,
        ClientPlugins.TranslatorHiddenComponent[]? hiddenComponents = null,
        List<ClientSvg.SvgNode>? mobileTree = null,
        ClientSvg.SvgTreeOptions? mobileOptions = null)
    {
        Console.WriteLine($"[Pipeline] 🔄 RunTranslatorsAsync: componentId={componentId} plugins={_translators.Plugins.Count} tree={serverTree.Count} nodes");

        if (_translators.Plugins.Count == 0)
        {
            Console.WriteLine("[Pipeline] ⚠️ No plugins registered — skipping translation.");
            return;
        }

        var clientTree = serverTree.Select(MapToClientNode).ToList();
        var bundle = TranslatorPluginHost.BuildBundle(
            clientTree, options, componentId,
            segments: segments,
            mobileTree: mobileTree,
            mobileOptions: mobileOptions,
            builtHiddenComponents: hiddenComponents);

        foreach (var output in await _translators.TranslateAllAsync(bundle, _http).ConfigureAwait(false))
        {
            if (output.Bytes.Length == 0)
            {
                if (output.Error != null)
                    Console.WriteLine(
                        $"[Pipeline] ❌ Translator '{output.PluginKey}' threw: "
                        + $"{output.Error.Message}\n{output.Error.StackTrace}");
                else
                    Console.WriteLine($"[Pipeline] ⚠️ Translator '{output.PluginKey}' returned 0 bytes (no output for this bundle).");
                continue;
            }

            // Skip disabled plugins
            if (!IsPluginEnabled(output.PluginKey))
            {
                Console.WriteLine($"[Pipeline] ⏭  Translator '{output.PluginKey}' skipped (disabled in settings)");
                continue;
            }

            // Route to plugin-specific output folder if configured, otherwise
            // write alongside other component files in componentDir.
            var outDir = PluginOutputDir(output.PluginKey, componentDir);
            var path = Path.Combine(outDir, $"{componentId}{output.FileExtension}");
            Console.WriteLine($"[Pipeline] 📝 Writing '{output.PluginKey}' output → {path}");
            try
            {
                Storage.WriteBytes(path, output.Bytes);
                Console.WriteLine(
                    $"[Pipeline] ✅ Translator '{output.PluginKey}' → "
                    + $"{Path.GetFileName(path)} ({output.Bytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Pipeline] ❌ Failed to persist output from '{output.PluginKey}': "
                    + $"{ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    // Server SvgNode → client SvgNode projection. Fields are identical; this is
    // a mechanical copy. If the server and client tree shapes diverge in future,
    // this is the single place to reconcile them.
    static ClientSvg.SvgNode MapToClientNode(SvgNode s) => new()
    {
        Id = s.Id,
        ParentId = s.ParentId,
        Tag = s.Tag,
        TextContent = s.TextContent,
        ClassName = s.ClassName,
        ComponentType = s.ComponentType,
        Label = s.Label,
        SegmentId = s.SegmentId,
        ImgSrc = s.ImgSrc,
        SvgDataUri = s.SvgDataUri,
        X = s.X,
        Y = s.Y,
        Width = s.Width,
        Height = s.Height,
        CssProps = s.CssProps,
        TextLines = s.TextLines,
        Children = s.Children.Select(MapToClientNode).ToList(),
    };

    static async System.Threading.Tasks.Task SaveViewportSnapshot(string componentId, string componentDir, ViewportSnapshotData vp)
    {
        if (vp.DomSnapshot == null) return;

        // JSON snapshot
        Storage.WriteJson($"{componentDir}/{componentId}_viewport_{vp.ViewportWidth}px.json", new
        {
            viewportWidth = vp.ViewportWidth,
            deviceType = vp.DeviceType,
            domSnapshot = vp.DomSnapshot,
            componentCSS = vp.ComponentCss,
            componentJS = vp.ComponentJs,
        });

        // Mobile Structure SVG
        var mobileTree = SvgPipeline.BuildTree(vp.DomSnapshot.Elements);
        var mobileW = vp.DomSnapshot.PageWidth > 0 ? vp.DomSnapshot.PageWidth : vp.ViewportWidth;
        var mobileH = vp.DomSnapshot.PageHeight > 0 ? vp.DomSnapshot.PageHeight : vp.ViewportWidth * 2;
        var mobileSvg = SvgPipeline.Serialize(mobileTree,
            mobileW, mobileH,
            string.Empty, $"{componentId}_viewport_{vp.ViewportWidth}px",
            vp.DomSnapshot.PageMap);

        Storage.Write($"{componentDir}/{componentId}_viewport_{vp.ViewportWidth}px.svg", mobileSvg);
        Console.WriteLine($"[Pipeline] ✅ Mobile SVG → {componentId}_viewport_{vp.ViewportWidth}px.svg");

        // Translator plugins — produce per-viewport translator outputs too.
        // Filename pattern e.g. {componentId}_viewport_390px.penpot.svg.
        var viewportComponentId = $"{componentId}_viewport_{vp.ViewportWidth}px";
        await RunTranslatorsAsync(
            mobileTree,
            new ClientSvg.SvgTreeOptions
            {
                Width = mobileW,
                Height = mobileH,
                Title = viewportComponentId,
            },
            viewportComponentId,
            componentDir);

        // Filtered viewport CSS — mobile Behaviour pillar
        if (vp.ComponentCss != null)
        {
            try { WriteFilteredViewportCss(componentDir, componentId, vp.ViewportWidth, mobileSvg, vp.ComponentCss); }
            catch (Exception ex) { Console.WriteLine($"[Pipeline] ⚠️ Viewport CSS failed (non-fatal): {ex.Message}"); }
        }
    }

    static void WriteFilteredViewportCss(string componentDir, string componentId, int viewportWidth,
        string mobileSvg, JObject cssObj)
    {
        var svgClasses = new HashSet<string>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(mobileSvg, @"data-classes=""([^""]*)"""))
            foreach (var cls in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                svgClasses.Add(cls);

        bool Relevant(string? sel)
        {
            if (string.IsNullOrEmpty(sel)) return false;
            var tokens = System.Text.RegularExpressions.Regex
                .Matches(sel, @"\.(-?[_a-zA-Z][^\s.#:\[\],+~>{}]*)")
                .Select(m => m.Groups[1].Value).ToList();
            return tokens.Count == 0 || tokens.Any(c => svgClasses.Contains(c));
        }

        var matched = cssObj["matched"] as JArray ?? new JArray();
        var behavior = cssObj["behavior"] as JArray ?? new JArray();
        var media = cssObj["media"] as JArray ?? new JArray();
        var keyframes = cssObj["keyframes"] as JArray ?? new JArray();

        var filtered = new JObject
        {
            ["matched"] = new JArray(matched.Where(r => Relevant(r["selector"]?.Value<string>()))),
            ["behavior"] = new JArray(behavior.Where(r => Relevant(r["selector"]?.Value<string>()))),
            ["media"] = new JArray(media.Select(b => new JObject
            {
                ["media"] = b["media"],
                ["rules"] = new JArray((b["rules"] as JArray ?? new JArray())
                    .Where(r => Relevant(r["selector"]?.Value<string>()))),
            }).Where(b => (b["rules"] as JArray)!.Count > 0)),
            ["keyframes"] = keyframes,
        };

        if (((JArray)filtered["matched"]!).Count > 0 || ((JArray)filtered["behavior"]!).Count > 0
            || ((JArray)filtered["media"]!).Count > 0)
        {
            Storage.WriteJson($"{componentDir}/{componentId}_viewport_{viewportWidth}px_css.json", filtered);
            Console.WriteLine($"[Pipeline] ✅ Viewport CSS → {componentId}_viewport_{viewportWidth}px_css.json");
        }
    }

    static async System.Threading.Tasks.Task CarveSegments(TransformRequest req, List<SvgNode> masterTree, string masterSvg, string masterDir, string siteName)
    {
        foreach (var section in req.DomSnapshot!.PageMap)
        {
            var segmentId = section.SegmentId;
            if (string.IsNullOrEmpty(segmentId)) continue;

            var sectionTag = (section.Tag ?? "section").ToLowerInvariant();
            var segId = $"{sectionTag}_{segmentId}";
            var segDir = Storage.ComponentDir(UserUuid, segId);

            // ── Carve segment SVG from master ─────────────────────────────────
            var segSvg = SvgPipeline.CarveSegment(masterSvg, segmentId, req.ComponentId!);
            if (segSvg != null)
            {
                Storage.Write($"{segDir}/{segId}.svg", segSvg);
                Console.WriteLine($"[Pipeline] ✅ Segment SVG → {segId}.svg");
            }

            // ── Run translator plugins against the carved segment tree ───────
            // Carved segments are SnapStak SVGs — they get their own translated
            // output file alongside the .svg.
            var segTree = SvgPipeline.CarveSegmentTree(masterTree, segmentId);
            if (segTree != null && segTree.Count > 0)
            {
                await RunTranslatorsAsync(
                    segTree,
                    new ClientSvg.SvgTreeOptions
                    {
                        Width = section.W,
                        Height = section.H,
                        Title = segId,
                    },
                    segId,
                    segDir);
            }

            // ── HTML ──────────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(section.HtmlB64))
            {
                try
                {
                    var html = Encoding.UTF8.GetString(Convert.FromBase64String(section.HtmlB64));
                    Storage.Write($"{segDir}/{segId}_{sectionTag}_source.html", html);
                }
                catch { }
            }

            // ── CSS ───────────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(section.CssB64))
            {
                try { Storage.Write($"{segDir}/{segId}_css.json", Encoding.UTF8.GetString(Convert.FromBase64String(section.CssB64))); }
                catch { }
            }

            // ── JS ────────────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(section.JsB64))
            {
                try { Storage.Write($"{segDir}/{segId}_js.json", Encoding.UTF8.GetString(Convert.FromBase64String(section.JsB64))); }
                catch { }
            }

            // ── Influence + Objective (copied from master) ────────────────────
            if (req.Influence != null)
                Storage.WriteJson($"{segDir}/{segId}_influence.json", req.Influence);
            if (req.Objective != null)
                Storage.WriteJson($"{segDir}/{segId}_objective.json", req.Objective);

            // ── Register segment in manifest ──────────────────────────────────
            var (zone, label) = Storage.InferZoneLabel(segId);
            Storage.RegisterComponent(UserUuid, segId, segDir, zone, label);
        }
    }

    static async System.Threading.Tasks.Task WriteHiddenComponents(TransformRequest req)
    {
        foreach (var comp in req.HiddenComponents)
        {
            var hcTag = (comp.ComponentType ?? "div").ToLowerInvariant();
            var hcSegId = comp.SegmentId ?? comp.ComponentId;
            var hcId = $"{hcTag}_{hcSegId}";
            var hcDir = Storage.ComponentDir(UserUuid, hcId);
            var iconsDir = Storage.IconsDir(hcDir);

            // Extract SVG icons
            foreach (var el in comp.Elements)
            {
                if (!string.IsNullOrEmpty(el.SvgDataUri) && !string.IsNullOrEmpty(el.InternalId))
                {
                    try
                    {
                        var svgContent = DecodeSvgDataUri(el.SvgDataUri);
                        if (!string.IsNullOrEmpty(svgContent))
                        {
                            Storage.Write($"{iconsDir}/{el.InternalId}.svg", svgContent);
                            el.ImgSrc = $"./icons/{el.InternalId}.svg";
                            el.SvgDataUri = null;
                        }
                    }
                    catch { el.SvgDataUri = null; }
                }
            }

            // Build SVG for hidden component
            var hcTree = SvgPipeline.BuildTree(comp.Elements);
            var hcWidth = comp.Elements.Count > 0 ? (int)comp.Elements.Max(e => e.X + e.Width) : 390;
            var hcHeight = comp.Elements.Count > 0 ? (int)comp.Elements.Max(e => e.Y + e.Height) : 800;
            if (hcWidth < 1) hcWidth = 390; if (hcHeight < 1) hcHeight = 800;

            var compSvg = SvgPipeline.Serialize(hcTree, hcWidth, hcHeight, "", hcId, new());
            Storage.Write($"{hcDir}/{hcId}.svg", compSvg);

            // ── Run translator plugins against the hidden component tree ──
            await RunTranslatorsAsync(
                hcTree,
                new ClientSvg.SvgTreeOptions
                {
                    Width = hcWidth,
                    Height = hcHeight,
                    Title = hcId,
                },
                hcId,
                hcDir);

            // CSS
            if (!string.IsNullOrEmpty(comp.CssB64))
                try { Storage.Write($"{hcDir}/{hcId}_css.json", Encoding.UTF8.GetString(Convert.FromBase64String(comp.CssB64))); } catch { }

            // JS
            if (!string.IsNullOrEmpty(comp.JsB64))
                try { Storage.Write($"{hcDir}/{hcId}_js.json", Encoding.UTF8.GetString(Convert.FromBase64String(comp.JsB64))); } catch { }

            // Influence + Objective
            if (req.Influence != null) Storage.WriteJson($"{hcDir}/{hcId}_influence.json", req.Influence);
            if (req.Objective != null) Storage.WriteJson($"{hcDir}/{hcId}_objective.json", req.Objective);

            var (hZone, hLabel) = Storage.InferZoneLabel(hcId);
            Storage.RegisterComponent(UserUuid, hcId, hcDir, hZone, hLabel);
            Console.WriteLine($"[Pipeline] ✅ Hidden component → {hcId}");
        }
    }

    static void WritePageMapCssJs(List<PageMapEntry> pageMap, string componentDir, string componentId)
    {
        var merged = new JObject { ["matched"] = new JArray(), ["behavior"] = new JArray(), ["media"] = new JArray(), ["keyframes"] = new JArray() };
        foreach (var s in pageMap)
        {
            if (string.IsNullOrEmpty(s.CssB64)) continue;
            try
            {
                var css = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(s.CssB64)));
                if (css["matched"] is JArray m) ((JArray)merged["matched"]!).Merge(m);
                if (css["behavior"] is JArray b) ((JArray)merged["behavior"]!).Merge(b);
                if (css["media"] is JArray me) ((JArray)merged["media"]!).Merge(me);
                if (css["keyframes"] is JArray k) ((JArray)merged["keyframes"]!).Merge(k);
            }
            catch { }
        }
        if (merged["matched"] is JArray ma && ma.Count > 0)
            Storage.WriteJson($"{componentDir}/{componentId}_css.json", merged);
    }

    static void WriteSourceHtml(List<PageMapEntry> pageMap, string componentDir, string componentId)
    {
        foreach (var s in pageMap)
        {
            if (string.IsNullOrEmpty(s.Tag) || string.IsNullOrEmpty(s.HtmlB64)) continue;
            try
            {
                var html = Encoding.UTF8.GetString(Convert.FromBase64String(s.HtmlB64));
                if (html.Length > 0)
                    Storage.Write($"{componentDir}/{componentId}_{s.Tag}_source.html", html);
            }
            catch { }
        }
    }

    static string? DecodeSvgDataUri(string uri)
    {
        if (uri.StartsWith("data:image/svg+xml;charset=utf-8,", StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(uri["data:image/svg+xml;charset=utf-8,".Length..]);
        if (uri.StartsWith("data:image/svg+xml;base64,", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8.GetString(Convert.FromBase64String(uri["data:image/svg+xml;base64,".Length..]));
        return null;
    }
}