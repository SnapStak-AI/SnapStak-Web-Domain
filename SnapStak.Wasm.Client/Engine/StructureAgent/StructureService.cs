using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Svg;

namespace SnapStak.Wasm.Client.Engine.StructureAgent;

/// <summary>
/// Converts a flat DOM element array into a parent-child SVG node tree.
/// Source-agnostic: works for WebView apps, React SPAs, and static websites.
/// All source-specific logic (component splitting, zone detection) lives in
/// content.js and runs in the browser — not here.
/// </summary>
internal static class StructureService
{
    public static List<SvgNode> BuildSVGTree(IReadOnlyList<DomElement> elements)
    {
        if (elements.Count == 0) return new List<SvgNode>();

        var nodeMap = new Dictionary<string, SvgNode>(elements.Count);

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
                AriaLabel = el.AriaLabel,
                ComponentType = el.ComponentType,
                Label = el.Label,
                SegmentId = el.SegmentId,
                ImgSrc = el.ResolvedImgSrc,
                SvgDataUri = el.SvgDataUri,
                X = el.Rect.X,
                Y = el.Rect.Y,
                Width = el.Rect.Width,
                Height = el.Rect.Height,
                CssProps = el.CssProps ?? new Dictionary<string, string>(),
                // Browser-measured text wrap lines — source of truth for tspan output.
                // Null when Range measurement was not possible (hidden elements, single
                // words, off-screen elements). SvgSerializer falls back to WrapText()
                // when this is null.
                TextLines = el.TextWrapLines,
                TextLineWidths = el.TextWrapLineWidths,
                TextWrapContainerW = el.TextWrapContainerW,
                TextWrapContainerH = el.TextWrapContainerH,
            };

            if (el.BorderRadiusPx > 0 && !node.CssProps.ContainsKey("borderRadius"))
                node.CssProps["borderRadius"] = $"{el.BorderRadiusPx}px";

            nodeMap[el.InternalId] = node;
        }

        var roots = new List<SvgNode>();
        foreach (var node in nodeMap.Values)
        {
            if (!string.IsNullOrWhiteSpace(node.ParentId) &&
                nodeMap.TryGetValue(node.ParentId, out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }

        SortChildren(roots);
        return roots;
    }

    private static void SortChildren(List<SvgNode> nodes)
    {
        nodes.Sort((a, b) =>
        {
            var yDiff = a.Y.CompareTo(b.Y);
            return yDiff != 0 ? yDiff : a.X.CompareTo(b.X);
        });
        foreach (var node in nodes)
            SortChildren(node.Children);
    }

    public static void ApplyCssProps(
        IReadOnlyList<SvgNode> nodes,
        IReadOnlyDictionary<string, Dictionary<string, string>> styleMap)
    {
        foreach (var node in nodes)
        {
            if (styleMap.TryGetValue(node.Id, out var props))
                foreach (var kv in props)
                    node.CssProps[kv.Key] = kv.Value;
            if (node.Children.Count > 0)
                ApplyCssProps(node.Children, styleMap);
        }
    }

    public static Dictionary<string, Dictionary<string, string>> BuildStyleMap(
        IReadOnlyList<DomElement> elements)
    {
        var map = new Dictionary<string, Dictionary<string, string>>();
        foreach (var el in elements)
            if (!string.IsNullOrWhiteSpace(el.InternalId) && el.CssProps != null)
                map[el.InternalId] = el.CssProps;
        return map;
    }
}