using System.Text.RegularExpressions;
using SnapStak.Wasm.Client.Models.Dom;

namespace SnapStak.Wasm.Client.Engine.Constructor;

/// <summary>
/// Reads the SVG file back into canvas objects for the Constructor.
/// Port of deserializeSVG() and deserializeHiddenSVG() in svgDeserializer.js.
///
/// deserializeSVG: reads data-snapstak-type="tree" SVGs produced by SvgSerializer.
///   Returns a flat list of canvas objects with _parentId links
///   matching the format consumed by generateComponentCode() in canvasToCodeService.js.
///
/// deserializeHiddenSVG: reads hidden component catalogues.
///   Returns HiddenComponent list used by BehaviourAgent and Constructor.
/// </summary>
internal static class SvgDeserializer
{
    // ── DeserializeSVG ────────────────────────────────────────────────────────

    public sealed class CanvasObject
    {
        public string  Id            { get; set; } = string.Empty;
        public string? ParentId      { get; set; }  // _parentId in Node.js
        public string  Type          { get; set; } = "Container";
        public string? Label         { get; set; }
        public double  Left          { get; set; }
        public double  Top           { get; set; }
        public double  Width         { get; set; }
        public double  Height        { get; set; }
        public string? Fill          { get; set; }
        public string? Text          { get; set; }
        public string? FontSize      { get; set; }
        public string? FontWeight    { get; set; }
        public string? FontFamily    { get; set; }
        public string? TextAnchor    { get; set; }
        public string? TextColor     { get; set; }
        public string? SegmentId     { get; set; }
        public string? ClassName     { get; set; }
        public bool    IsHidden      { get; set; }
        public string? Display       { get; set; }
        public string? Gap           { get; set; }
        public string? Margin        { get; set; }
        public string? Position      { get; set; }
        public string? ImgSrc        { get; set; }
    }

    public sealed class DeserializeResult
    {
        public List<CanvasObject>             Objects            { get; set; } = new();
        public Dictionary<string, string>     ComponentMappings  { get; set; } = new();
        public Dictionary<string, string>     IconMap            { get; set; } = new();
        public Dictionary<string, string>     ImageMap           { get; set; } = new();
    }

    public static DeserializeResult DeserializeSVG(string svgContent)
    {
        var result = new DeserializeResult();
        if (string.IsNullOrWhiteSpace(svgContent)) return result;

        // Stack-based parser: track depth to assign _parentId
        var stack   = new Stack<string>(); // id stack
        var gOpenRe = new Regex(@"<g\b([^>]*)>", RegexOptions.None, TimeSpan.FromSeconds(5));
        var gCloseRe = "</g>";

        var lines = svgContent.Split('\n');

        foreach (var line in lines)
        {
            // Process <g> opens
            foreach (Match gm in gOpenRe.Matches(line))
            {
                var attrs = gm.Groups[1].Value;
                var id    = GetAttr(attrs, "id");
                if (string.IsNullOrEmpty(id))
                {
                    stack.Push("__anon__");
                    continue;
                }

                var obj = new CanvasObject
                {
                    Id       = id,
                    ParentId = stack.Count > 0 ? stack.Peek() : null,
                    Type     = GetAttr(attrs, "inkscape:label") ?? GetAttr(attrs, "data-component-type") ?? "Container",
                    Label    = GetAttr(attrs, "inkscape:label"),
                    SegmentId = GetAttr(attrs, "data-segment-id"),
                    ClassName = GetAttr(attrs, "data-classes"),
                    Display   = GetAttr(attrs, "data-display"),
                    Gap       = GetAttr(attrs, "data-gap"),
                    Margin    = GetAttr(attrs, "data-margin"),
                    Position  = GetAttr(attrs, "data-position"),
                    IsHidden  = GetAttr(attrs, "data-hidden") == "true",
                };

                // Parse translate(X,Y)
                var translateMatch = Regex.Match(attrs,
                    @"transform=""translate\(\s*([\d.-]+)\s*,\s*([\d.-]+)\s*\)""");
                if (translateMatch.Success)
                {
                    obj.Left = double.Parse(translateMatch.Groups[1].Value);
                    obj.Top  = double.Parse(translateMatch.Groups[2].Value);
                }

                // data-w / data-h
                var wMatch = Regex.Match(attrs, @"data-w=""(\d+)""");
                var hMatch = Regex.Match(attrs, @"data-h=""(\d+)""");
                obj.Width  = wMatch.Success ? double.Parse(wMatch.Groups[1].Value) : 0;
                obj.Height = hMatch.Success ? double.Parse(hMatch.Groups[1].Value) : 0;

                result.Objects.Add(obj);
                stack.Push(id);
            }

            // Process <rect> — fill colour
            if (line.TrimStart().StartsWith("<rect") && result.Objects.Count > 0)
            {
                var fillMatch = Regex.Match(line, @"fill=""([^""]+)""");
                if (fillMatch.Success)
                    result.Objects[^1].Fill = fillMatch.Groups[1].Value;
            }

            // Process <text>
            if (line.TrimStart().StartsWith("<text") && result.Objects.Count > 0)
            {
                var last      = result.Objects[^1];
                var fillMatch = Regex.Match(line, @"fill=""([^""]+)""");
                var fsMatch   = Regex.Match(line, @"font-size=""([^""]+)""");
                var fwMatch   = Regex.Match(line, @"font-weight=""([^""]+)""");
                var ffMatch   = Regex.Match(line, @"font-family=""([^""]+)""");
                var taMatch   = Regex.Match(line, @"text-anchor=""([^""]+)""");
                var textMatch = Regex.Match(line, @">([^<]+)</text>");

                if (fillMatch.Success)  last.TextColor  = fillMatch.Groups[1].Value;
                if (fsMatch.Success)    last.FontSize   = fsMatch.Groups[1].Value;
                if (fwMatch.Success)    last.FontWeight = fwMatch.Groups[1].Value;
                if (ffMatch.Success)    last.FontFamily = ffMatch.Groups[1].Value;
                if (taMatch.Success)    last.TextAnchor = taMatch.Groups[1].Value;
                if (textMatch.Success)  last.Text       = UnescapeXml(textMatch.Groups[1].Value);
            }

            // Process <image>
            if (line.TrimStart().StartsWith("<image") && result.Objects.Count > 0)
            {
                var hrefMatch = Regex.Match(line, @"href=""([^""]+)""");
                if (hrefMatch.Success)
                    result.Objects[^1].ImgSrc = hrefMatch.Groups[1].Value;
            }

            // Process </g>
            var closeCount = CountOccurrences(line, gCloseRe);
            for (int c = 0; c < closeCount; c++)
                if (stack.Count > 0) stack.Pop();
        }

        return result;
    }

    // ── DeserializeHiddenSVG ──────────────────────────────────────────────────

    public static List<HiddenComponent> DeserializeHiddenSVG(string svgContent)
    {
        var components = new List<HiddenComponent>();
        if (string.IsNullOrWhiteSpace(svgContent)) return components;

        var groupRe = new Regex(
            @"<g\b([^>]*)>([\s\S]*?)</g>",
            RegexOptions.None,
            TimeSpan.FromSeconds(10));

        foreach (Match gm in groupRe.Matches(svgContent))
        {
            var attrs = gm.Groups[1].Value;
            var inner = gm.Groups[2].Value;

            var compType = GetAttr(attrs, "data-component-type");
            var compId   = GetAttr(attrs, "data-component-id");
            if (string.IsNullOrEmpty(compType) && string.IsNullOrEmpty(compId)) continue;

            var comp = new HiddenComponent
            {
                ComponentId   = compId ?? string.Empty,
                ComponentType = compType ?? "HiddenPanel",
                Label         = GetAttr(attrs, "inkscape:label"),
            };

            // Parse child elements
            var childRe = new Regex(@"<g\b([^>]*)>([\s\S]*?)</g>", RegexOptions.None, TimeSpan.FromSeconds(5));
            foreach (Match cm in childRe.Matches(inner))
            {
                var cAttrs = cm.Groups[1].Value;
                var cInner = cm.Groups[2].Value;

                var el = new DomElement
                {
                    InternalId = GetAttr(cAttrs, "id") ?? Guid.NewGuid().ToString("N")[..8],
                    ClassName  = GetAttr(cAttrs, "data-classes"),
                    AriaLabel  = GetAttr(cAttrs, "aria-label"),
                };

                var wMatch = Regex.Match(cAttrs, @"data-w=""(\d+)""");
                var hMatch = Regex.Match(cAttrs, @"data-h=""(\d+)""");
                el.Rect = new DomRect
                {
                    Width  = wMatch.Success ? double.Parse(wMatch.Groups[1].Value) : 0,
                    Height = hMatch.Success ? double.Parse(hMatch.Groups[1].Value) : 0,
                };

                var imgMatch = Regex.Match(cInner, @"href=""([^""]+)""");
                if (imgMatch.Success) el.ImgSrc = imgMatch.Groups[1].Value;

                var textMatch = Regex.Match(cInner, @"<text>([^<]+)</text>");
                if (textMatch.Success) el.TextContent = UnescapeXml(textMatch.Groups[1].Value);

                comp.Elements.Add(el);
            }

            components.Add(comp);
        }

        return components;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetAttr(string attrs, string name)
    {
        var m = Regex.Match(attrs, $@"{Regex.Escape(name)}=""([^""]*)""");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string UnescapeXml(string value) =>
        value.Replace("&amp;", "&")
             .Replace("&lt;",  "<")
             .Replace("&gt;",  ">")
             .Replace("&quot;","\"")
             .Replace("&apos;","'");

    private static int CountOccurrences(string source, string target)
    {
        int count = 0, idx = 0;
        while ((idx = source.IndexOf(target, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += target.Length;
        }
        return count;
    }
}
