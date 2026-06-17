using System.Text;
using System.Text.RegularExpressions;
using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Svg;

namespace SnapStak.Wasm.Client.Engine.StructureAgent;

/// <summary>
/// Serialises the SVG node tree into SVG file content.
///
/// ConteX Law — both HTML (structure) and CSS (style) are sources of truth.
/// Every visual CSS property captured by content.js MUST be reflected in the SVG.
///
/// Fixes:
///   1. Correct relative coordinates  — child.relX = child.X - parent.X
///   2. <defs> written inline          — no broken string-replace at end
///   3. Box shadow → feDropShadow      — written directly into <defs> before node
///   4. Border → stroke on rect        — CSS border shorthand parsed correctly
///   5. borderRadius → rx on rect      — pill shapes capped at min(w,h)/2
///   6. Text wrapping → tspan lines    — based on container width and fontSize
///   7. Button text centering          — flex justifyContent:center → middle anchor
///   8. Sticky/fixed separation        — position:fixed/sticky extracted to own layer
///   9. Image clip-path for radius     — images respect card corner radius
///  10. Padding for text positioning   — paddingLeft/Top applied correctly
/// </summary>
internal static class SvgSerializer
{
    public static string SerializeTreeSVG(
        IReadOnlyList<SvgNode> tree,
        SvgTreeOptions options)
    {
        // We write defs inline as we encounter shadows.
        // Use a two-pass approach: collect all defs first, then write full SVG.
        var defsBuilder = new StringBuilder();
        var bodyBuilder = new StringBuilder();
        var fixedBuilder = new StringBuilder(); // fixed/sticky nodes on top
        int filterId = 0;

        foreach (var node in tree)
            SerializeNode(bodyBuilder, defsBuilder, fixedBuilder,
                          node, depth: 1,
                          parentOriginX: 0, parentOriginY: 0,
                          ref filterId);

        var sb = new StringBuilder(1024 * 64);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\"");
        sb.AppendLine($"     xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\"");
        sb.AppendLine($"     xmlns:snapstak=\"https://snapstak.ai/ns\"");
        sb.AppendLine($"     width=\"{options.Width}\" height=\"{options.Height}\"");
        sb.AppendLine($"     viewBox=\"0 0 {options.Width} {options.Height}\"");
        sb.AppendLine($"     data-snapstak-type=\"tree\"");
        sb.AppendLine($"     data-source-url=\"{EscapeXml(options.SourceUrl)}\"");
        sb.AppendLine($"     data-title=\"{EscapeXml(options.Title)}\">");

        sb.AppendLine("  <defs>");
        if (defsBuilder.Length > 0) sb.Append(defsBuilder);
        sb.AppendLine("  </defs>");

        sb.Append(bodyBuilder);

        if (fixedBuilder.Length > 0)
        {
            sb.AppendLine("  <!-- ═══ Fixed / sticky overlay layer ═══ -->");
            sb.Append(fixedBuilder);
        }

        WritePageMap(sb, options.PageMap);
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    public static string SerializeHiddenSVG(
        IReadOnlyList<HiddenComponent> components,
        int pageWidth)
    {
        var sb = new StringBuilder(1024 * 16);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\"");
        sb.AppendLine($"     xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\"");
        sb.AppendLine($"     xmlns:snapstak=\"https://snapstak.ai/ns\"");
        sb.AppendLine($"     width=\"{pageWidth}\"");
        sb.AppendLine($"     data-snapstak-type=\"hidden-catalogue\">");

        int yOffset = 0;
        foreach (var comp in components)
        {
            sb.AppendLine($"  <g inkscape:label=\"{EscapeXml(comp.ComponentType)}\"");
            sb.AppendLine($"     data-component-type=\"{EscapeXml(comp.ComponentType)}\"");
            sb.AppendLine($"     data-component-id=\"{EscapeXml(comp.ComponentId)}\"");
            sb.AppendLine($"     transform=\"translate(0,{yOffset})\">");

            int maxH = 0;
            foreach (var el in comp.Elements ?? new List<DomElement>())
            {
                int h = (int)Math.Ceiling(el.Rect.Height);
                if (h > maxH) maxH = h;
                SerializeHiddenElement(sb, el);
            }
            sb.AppendLine("  </g>");
            yOffset += maxH + 20;
        }
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    // ── Node serialisation ────────────────────────────────────────────────────

    private static void SerializeNode(
        StringBuilder body,
        StringBuilder defs,
        StringBuilder fixed_,
        SvgNode node,
        int depth,
        double parentOriginX,
        double parentOriginY,
        ref int filterId)
    {
        var css = node.CssProps;
        var position = css.GetValueOrDefault("position", "static");
        var display = css.GetValueOrDefault("display", "block");
        var visible = css.GetValueOrDefault("visibility", "visible");
        var opacity = css.GetValueOrDefault("opacity", "1");
        var overflow = css.GetValueOrDefault("overflow", "");

        bool isFixed = position == "fixed" || position == "sticky";

        // Fixed/sticky: write at absolute page coordinates into separate layer
        var target = isFixed ? fixed_ : body;
        var indent = new string(' ', depth * 2);

        // Compute position relative to parent (or absolute for fixed)
        var relX = isFixed ? Math.Round(node.X) : Math.Round(node.X - parentOriginX);
        var relY = isFixed ? Math.Round(node.Y) : Math.Round(node.Y - parentOriginY);
        var w = Math.Round(node.Width);
        var h = Math.Round(node.Height);

        // Skip invisible zero-size nodes with no content
        bool hasContent = !string.IsNullOrEmpty(node.TextContent) ||
                          !string.IsNullOrEmpty(node.ImgSrc) ||
                          !string.IsNullOrEmpty(node.SvgDataUri) ||
                          node.Children.Count > 0;
        if (w <= 0 && h <= 0 && !hasContent) return;

        var componentType = node.ComponentType ?? InferComponentType(node);
        var label = EscapeXml(node.Label ?? node.SegmentId ?? componentType);

        // ── Box shadow → SVG filter in <defs> ────────────────────────────────
        string? filterRef = null;
        var boxShadow = css.GetValueOrDefault("boxShadow", "");
        if (!string.IsNullOrEmpty(boxShadow) && boxShadow != "none")
        {
            var fid = $"f{filterId++}";
            var (sdx, sdy, sblur, scolor, sopacity) = ParseBoxShadow(boxShadow);
            defs.AppendLine($"    <filter id=\"{fid}\" x=\"-30%\" y=\"-30%\" width=\"160%\" height=\"160%\">");
            defs.AppendLine($"      <feDropShadow dx=\"{sdx:F1}\" dy=\"{sdy:F1}\" stdDeviation=\"{sblur / 2.0:F1}\"");
            defs.AppendLine($"                    flood-color=\"{EscapeXml(scolor)}\" flood-opacity=\"{sopacity:F2}\"/>");
            defs.AppendLine($"    </filter>");
            filterRef = fid;
        }

        // ── Border ────────────────────────────────────────────────────────────
        var (strokeColor, strokeWidth) = ParseBorder(css);

        // ── Border radius ─────────────────────────────────────────────────────
        var rx = ParseBorderRadius(css, w, h);

        // ── Open <g> ──────────────────────────────────────────────────────────
        target.Append($"{indent}<g id=\"{EscapeXml(node.Id)}\"");
        target.Append($" transform=\"translate({relX},{relY})\"");
        target.Append($" data-w=\"{w}\" data-h=\"{h}\"");
        target.Append($" inkscape:label=\"{label}\"");
        if (!string.IsNullOrEmpty(componentType))
            target.Append($" data-component-type=\"{EscapeXml(componentType)}\"");
        if (!string.IsNullOrEmpty(node.ClassName))
            target.Append($" data-classes=\"{EscapeXml(node.ClassName)}\"");
        if (!string.IsNullOrEmpty(node.SegmentId))
            target.Append($" data-segment-id=\"{EscapeXml(node.SegmentId)}\"");
        if (display is "flex" or "grid" or "inline-flex" or "inline-grid")
            target.Append($" data-display=\"{display}\"");
        if (isFixed) target.Append($" data-position=\"{position}\"");
        var gap = css.GetValueOrDefault("gap", "");
        if (!string.IsNullOrEmpty(gap)) target.Append($" data-gap=\"{EscapeXml(gap)}\"");
        if (display == "none" || visible == "hidden") target.Append(" data-hidden=\"true\"");
        if (filterRef != null) target.Append($" filter=\"url(#{filterRef})\"");
        if (opacity != "1" && !string.IsNullOrEmpty(opacity))
            target.Append($" opacity=\"{EscapeXml(opacity)}\"");
        target.AppendLine(">");

        // ── Background rect (with border) ─────────────────────────────────────
        var bgColor = css.GetValueOrDefault("backgroundColor", "");
        bool hasBg = !string.IsNullOrEmpty(bgColor) &&
                      bgColor != "rgba(0, 0, 0, 0)" &&
                      bgColor != "transparent" && w > 0 && h > 0;

        if (hasBg || strokeWidth > 0)
        {
            target.Append($"{indent}  <rect width=\"{w}\" height=\"{h}\"");
            target.Append($" fill=\"{(hasBg ? EscapeXml(bgColor) : "none")}\"");
            if (rx > 0) target.Append($" rx=\"{rx:F1}\"");
            if (strokeWidth > 0)
            {
                target.Append($" stroke=\"{EscapeXml(strokeColor)}\"");
                target.Append($" stroke-width=\"{strokeWidth:F1}\"");
            }
            target.AppendLine("/>");
        }

        // ── SVG icon (data URI) ───────────────────────────────────────────────
        if (!string.IsNullOrEmpty(node.SvgDataUri))
        {
            target.AppendLine(
                $"{indent}  <image href=\"{EscapeXml(node.SvgDataUri)}\"" +
                $" x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\"" +
                $" preserveAspectRatio=\"xMidYMid meet\"/>");
        }

        // ── Raster / CDN image ────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(node.ImgSrc))
        {
            var clipAttr = rx > 0 ? $" clip-path=\"inset(0 round {rx:F1}px)\"" : "";
            target.AppendLine(
                $"{indent}  <image href=\"{EscapeXml(node.ImgSrc)}\"" +
                $" x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\"" +
                $" preserveAspectRatio=\"xMidYMid slice\"{clipAttr}/>");
        }

        // ── Text (with wrapping and correct centering) ────────────────────────
        var text = node.TextContent?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            // Resolve the line list that drives both the SVG tspans AND translator
            // plugins. Single source of truth — stored on the node, passed into
            // RenderText so the two code paths are always identical.
            //
            // Priority:
            //   1. node.TextLines already set from browser Range measurement
            //      (content.js textWrapLines) — exact, pixel-perfect line breaks.
            //   2. WrapText() fallback — character-width estimate used only when
            //      Range data was unavailable (hidden elements, single-word text,
            //      off-screen captures).
            //
            // Available width for the fallback:
            //   Prefer TextWrapContainerW (browser-measured) over CSS-derived
            //   w minus padding — the browser value already accounts for padding,
            //   sub-pixel borders, and box-sizing variations.
            if (node.TextLines == null || node.TextLines.Count == 0)
            {
                var paddingL = ParsePx(css.GetValueOrDefault("paddingLeft", "0px"), 0);
                var paddingR = ParsePx(css.GetValueOrDefault("paddingRight", "0px"), 0);
                var availableW = node.TextWrapContainerW > 0
                    ? Math.Max(node.TextWrapContainerW, 20.0)
                    : Math.Max(w - paddingL - paddingR, 20.0);
                node.TextLines = WrapText(
                    text,
                    availableW,
                    ParsePx(css.GetValueOrDefault("fontSize", "14px"), 14));
            }

            RenderText(target, defs, indent, css, text, node.TextLines, node.TextLineWidths, w, h, ref filterId);
        }

        // ── Children ──────────────────────────────────────────────────────────
        foreach (var child in node.Children)
            SerializeNode(body, defs, fixed_, child,
                          depth + 1, node.X, node.Y, ref filterId);

        target.AppendLine($"{indent}</g>");
    }

    private static void RenderText(
        StringBuilder target,
        StringBuilder defs,
        string indent,
        Dictionary<string, string> css,
        string text,
        List<string> lines,
        List<double>? lineWidths,
        double w, double h,
        ref int filterId)
    {
        var color = css.GetValueOrDefault("color", "#000000");
        var fontSize = ParsePx(css.GetValueOrDefault("fontSize", "14px"), 14);
        var fontWeight = css.GetValueOrDefault("fontWeight", "400");
        var fontFamily = css.GetValueOrDefault("fontFamily", "sans-serif");
        var lineHeightRaw = css.GetValueOrDefault("lineHeight", "");
        var lineHeight = string.IsNullOrEmpty(lineHeightRaw)
            ? fontSize * 1.3
            : ParsePx(lineHeightRaw, fontSize * 1.3);
        var textTransform = css.GetValueOrDefault("textTransform", "");
        var textDecoration = css.GetValueOrDefault("textDecoration", "");
        var paddingLeft = ParsePx(css.GetValueOrDefault("paddingLeft", "0px"), 0);
        var paddingRight = ParsePx(css.GetValueOrDefault("paddingRight", "0px"), 0);
        var paddingTop = ParsePx(css.GetValueOrDefault("paddingTop", "0px"), 0);

        // ── Determine horizontal alignment ────────────────────────────────────
        // Priority: justifyContent (flex centering) > textAlign > default left
        var justifyContent = css.GetValueOrDefault("justifyContent", "");
        var textAlign = css.GetValueOrDefault("textAlign", "start");
        var display = css.GetValueOrDefault("display", "block");

        bool isFlexCenter = (display == "flex" || display == "inline-flex") &&
                            (justifyContent == "center");

        string anchor;
        double baseX;
        if (isFlexCenter || textAlign == "center")
        {
            anchor = "middle";
            baseX = w / 2.0;
        }
        else if (textAlign == "right" || textAlign == "end")
        {
            anchor = "end";
            baseX = w - paddingRight;
        }
        else
        {
            anchor = "start";
            baseX = paddingLeft;
        }

        // Apply text-transform to every line independently so casing is consistent
        // regardless of whether lines came from Range measurement or WrapText().
        if (textTransform == "uppercase")
            lines = lines.Select(l => l.ToUpperInvariant()).ToList();
        else if (textTransform == "lowercase")
            lines = lines.Select(l => l.ToLowerInvariant()).ToList();

        // ── Vertical positioning ──────────────────────────────────────────────
        double startY;
        if (lines.Count == 1 && h > 0)
        {
            startY = paddingTop > 0
                ? paddingTop + fontSize * 0.85
                : (h + fontSize * 0.75) / 2.0;
        }
        else
        {
            startY = paddingTop > 0 ? paddingTop + fontSize * 0.85 : lineHeight;
        }

        // ── Clip text to container bounds ─────────────────────────────────────
        // clip-path="inset(0)" on a <text> element does not work — SVG inset()
        // clips relative to the element's own content box which has no defined
        // width/height for text. Instead write a <clipPath> rect to <defs> sized
        // to the container dimensions, and apply it to a <g> wrapping the text.
        // This hard-clips any line that overflows due to font metric differences
        // between Chrome and the SVG renderer.
        var clipId = $"tc{filterId++}";
        defs.AppendLine($"    <clipPath id=\"{clipId}\">");
        defs.AppendLine($"      <rect width=\"{w:F0}\" height=\"{h:F0}\"/>");
        defs.AppendLine($"    </clipPath>");

        target.AppendLine($"{indent}  <g clip-path=\"url(#{clipId})\">");
        target.Append($"{indent}    <text");
        target.Append($" fill=\"{EscapeXml(color)}\"");
        target.Append($" font-size=\"{fontSize:F0}px\"");
        target.Append($" font-weight=\"{EscapeXml(fontWeight)}\"");
        target.Append($" font-family=\"{EscapeXml(fontFamily)}\"");
        target.Append($" text-anchor=\"{anchor}\"");
        if (!string.IsNullOrEmpty(textDecoration) && textDecoration != "none")
            target.Append($" text-decoration=\"{EscapeXml(textDecoration)}\"");
        target.AppendLine(">");

        for (int li = 0; li < lines.Count; li++)
        {
            var ly = startY + li * lineHeight;
            target.Append($"{indent}      <tspan x=\"{baseX:F1}\" y=\"{ly:F1}\"");
            // Use browser-measured pixel width so the SVG renderer scales glyph
            // spacing to exactly match Chrome — prevents overflow from font metric
            // differences. clipPath is retained as a hard-clip safety net.
            if (lineWidths != null && li < lineWidths.Count && lineWidths[li] > 0)
                target.Append($" textLength=\"{lineWidths[li]:F0}\" lengthAdjust=\"spacingAndGlyphs\"");
            target.AppendLine($">{EscapeXml(lines[li])}</tspan>");
        }

        target.AppendLine($"{indent}    </text>");
        target.AppendLine($"{indent}  </g>");
    }

    // ── Text wrapping ─────────────────────────────────────────────────────────

    private static List<string> WrapText(string text, double maxWidth, double fontSize)
    {
        if (maxWidth <= 0 || fontSize <= 0) return new List<string> { text };

        // Average character width: roughly 0.52x fontSize for typical Latin fonts
        double charW = fontSize * 0.52;
        int charsPerLine = Math.Max(1, (int)(maxWidth / charW));

        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();

        foreach (var word in words)
        {
            if (current.Length == 0)
            {
                current.Append(word);
            }
            else if ((current.Length + 1 + word.Length) <= charsPerLine)
            {
                current.Append(' ');
                current.Append(word);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines.Count > 0 ? lines : new List<string> { text };
    }

    // ── CSS parsers ───────────────────────────────────────────────────────────

    /// <summary>Parses CSS border shorthand → (strokeColor, strokeWidth px)</summary>
    private static (string color, double width) ParseBorder(Dictionary<string, string> css)
    {
        var border = css.GetValueOrDefault("border", "");
        if (string.IsNullOrEmpty(border) || border.StartsWith("0px") ||
            border == "none" || border == "medium" || border == "initial")
            return ("none", 0);

        var wm = Regex.Match(border, @"([\d.]+)px");
        var cm = Regex.Match(border, @"(rgb\a?\([^)]+\)|#[0-9a-fA-F]{3,8})");

        double strokeW = wm.Success ? double.Parse(wm.Groups[1].Value) : 0;
        string strokeC = cm.Success ? cm.Groups[1].Value : "#000000";

        if (border.Contains("none") || strokeW < 0.1) return ("none", 0);
        return (strokeC, strokeW);
    }

    /// <summary>Parses borderRadius CSS → SVG rx in px, capped at min(w,h)/2</summary>
    private static double ParseBorderRadius(Dictionary<string, string> css, double w, double h)
    {
        var br = css.GetValueOrDefault("borderRadius", "");
        if (string.IsNullOrEmpty(br) || br == "0px" || br == "0") return 0;

        var m = Regex.Match(br, @"([\d.]+)(px|%)");
        if (!m.Success) return 0;

        var val = double.Parse(m.Groups[1].Value);
        var px = m.Groups[2].Value == "%" ? val / 100.0 * Math.Min(w, h) : val;
        return Math.Min(px, Math.Min(w, h) / 2.0);
    }

    /// <summary>Parses boxShadow → (dx, dy, blur, color, opacity)</summary>
    private static (double dx, double dy, double blur, string color, double opacity) ParseBoxShadow(string shadow)
    {
        if (string.IsNullOrEmpty(shadow) || shadow == "none")
            return (0, 2, 4, "#000000", 0.1);

        var nums = Regex.Matches(shadow, @"-?[\d.]+px")
                         .Cast<Match>()
                         .Select(m => double.Parse(m.Value.Replace("px", "")))
                         .ToList();
        var cm = Regex.Match(shadow, @"rgba?\([^)]+\)|#[0-9a-fA-F]{3,8}");
        var color = cm.Success ? cm.Value : "#000000";

        // Try to extract opacity from rgba()
        double opacity = 0.2;
        var opM = Regex.Match(color, @"rgba\([^,]+,[^,]+,[^,]+,\s*([\d.]+)\)");
        if (opM.Success) opacity = double.Parse(opM.Groups[1].Value);

        // Normalise color to rgb() for SVG flood-color compatibility
        var rgbColor = Regex.Replace(color, @",\s*[\d.]+\)", ")").Replace("rgba(", "rgb(");

        return (
            nums.Count > 0 ? nums[0] : 0,
            nums.Count > 1 ? nums[1] : 2,
            nums.Count > 2 ? Math.Abs(nums[2]) : 4,
            rgbColor,
            opacity
        );
    }

    /// <summary>Parses a CSS px/em/rem value to double</summary>
    private static double ParsePx(string value, double fallback)
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        var m = Regex.Match(value, @"([\d.]+)");
        return m.Success ? double.Parse(m.Groups[1].Value) : fallback;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string InferComponentType(SvgNode node) =>
        node.Tag switch
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
            _ => "Container",
        };

    private static void SerializeHiddenElement(StringBuilder sb, DomElement el)
    {
        var x = Math.Round(el.Rect.X);
        var y = Math.Round(el.Rect.Y);
        var w = Math.Round(el.Rect.Width);
        var h = Math.Round(el.Rect.Height);

        sb.Append($"    <g transform=\"translate({x},{y})\" data-w=\"{w}\" data-h=\"{h}\"");
        if (!string.IsNullOrEmpty(el.ClassName))
            sb.Append($" data-classes=\"{EscapeXml(el.ClassName)}\"");
        if (!string.IsNullOrEmpty(el.AriaLabel))
            sb.Append($" aria-label=\"{EscapeXml(el.AriaLabel)}\"");
        sb.AppendLine(">");

        if (!string.IsNullOrEmpty(el.ResolvedImgSrc))
            sb.AppendLine($"      <image href=\"{EscapeXml(el.ResolvedImgSrc)}\" width=\"{w}\" height=\"{h}\"/>");
        else if (!string.IsNullOrEmpty(el.TextContent?.Trim()))
            sb.AppendLine($"      <text>{EscapeXml(el.TextContent.Trim())}</text>");

        sb.AppendLine("    </g>");
    }

    private static void WritePageMap(StringBuilder sb, IReadOnlyList<PageMapEntry> pageMap)
    {
        if (pageMap.Count == 0) return;
        sb.AppendLine("  <snapstak:pagemap xmlns:snapstak=\"https://snapstak.ai/ns\">");
        foreach (var s in pageMap)
        {
            sb.Append($"    <snapstak:component tag=\"{EscapeXml(s.Tag)}\"");
            sb.Append($" segmentId=\"{EscapeXml(s.SegmentId ?? "")}\"");
            sb.Append($" label=\"{EscapeXml(s.Label ?? s.Tag)}\"");
            sb.Append($" y=\"{s.Y}\" h=\"{s.H}\" w=\"{s.W}\">");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(s.CssB64)) sb.AppendLine($"      <snapstak:css>{s.CssB64}</snapstak:css>");
            if (!string.IsNullOrEmpty(s.JsB64)) sb.AppendLine($"      <snapstak:js>{s.JsB64}</snapstak:js>");
            sb.AppendLine("    </snapstak:component>");
        }
        sb.AppendLine("  </snapstak:pagemap>");
    }

    private static string EscapeXml(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}