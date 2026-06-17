using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Models.Dom;

/// <summary>
/// A single element from content.js DOM serialisation.
/// JsonProperty names MUST match exactly what content.js sends in the entry object.
/// </summary>
public sealed class DomElement
{
    [JsonProperty("internalId")] public string InternalId { get; set; } = string.Empty;
    [JsonProperty("parentId")] public string? ParentId { get; set; }
    [JsonProperty("tag")] public string Tag { get; set; } = "div";
    [JsonProperty("tagName")] public string? TagName { get; set; }
    [JsonProperty("textContent")] public string? TextContent { get; set; }
    [JsonProperty("className")] public string? ClassName { get; set; }
    [JsonProperty("ariaLabel")] public string? AriaLabel { get; set; }
    [JsonProperty("role")] public string? Role { get; set; }
    [JsonProperty("segmentId")] public string? SegmentId { get; set; }
    [JsonProperty("rect")] public DomRect Rect { get; set; } = new();
    [JsonProperty("cssProps")] public Dictionary<string, string>? CssProps { get; set; }

    // Image — content.js has TWO emit paths that both deserialise into this class:
    //   • serialize() → domSnapshot.elements  ← emits "src:"        (main channel, line ~2605)
    //   • walkEl / walkHiddenEl → hiddenComponents.elements ← emits "imgSrc:" (lines 1382, 1901)
    // We accept both names. The alt property forwards its value into ImgSrc on
    // deserialisation; one of the two will be present per element, never both.
    [JsonProperty("src")] public string? ImgSrc { get; set; }
    [JsonProperty("imgSrc")]
    public string? ImgSrcAlt
    {
        get => null;                                    // never serialised back out
        set { if (!string.IsNullOrEmpty(value)) ImgSrc = value; }
    }
    [JsonProperty("srcset")] public string? Srcset { get; set; }
    [JsonProperty("dataSrc")] public string? DataSrc { get; set; }
    [JsonProperty("alt")] public string? Alt { get; set; }

    // picture element sources — each has srcset, sizes, media, type
    [JsonProperty("pictureSources")] public List<PictureSource>? PictureSources { get; set; }

    // SVG icon — content.js sends field as "svgDataURI" (capital URI)
    [JsonProperty("svgDataURI")] public string? SvgDataUri { get; set; }

    [JsonProperty("componentType")] public string? ComponentType { get; set; }
    [JsonProperty("label")] public string? Label { get; set; }
    [JsonProperty("href")] public string? Href { get; set; }
    [JsonProperty("borderRadiusPx")] public double BorderRadiusPx { get; set; }
    [JsonProperty("hidden")] public bool Hidden { get; set; }

    // ── Browser-measured text wrap lines (content.js Range API) ──────────────
    // When present these are the authoritative line breaks for SVG <tspan> output.
    // Populated only for visible, multi-word text elements where Range measurement
    // succeeded. SvgSerializer uses these directly instead of calling WrapText().
    // TextWrapContainerW / H are the exact rendered container dimensions at capture
    // time — the serializer uses them in place of CSS-derived estimates.
    [JsonProperty("textWrapLines")] public List<string>? TextWrapLines { get; set; }
    [JsonProperty("textWrapLineWidths")] public List<double>? TextWrapLineWidths { get; set; }
    [JsonProperty("textWrapContainerW")] public double TextWrapContainerW { get; set; }
    [JsonProperty("textWrapContainerH")] public double TextWrapContainerH { get; set; }

    // Resolved inline — not from JSON
    public List<DomElement> Children { get; set; } = new();

    /// <summary>
    /// Returns the best available image URL from all possible sources.
    /// Priority: src > dataSrc > first pictureSources srcset entry.
    /// </summary>
    public string? ResolvedImgSrc
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ImgSrc) && !ImgSrc.StartsWith("data:")) return ImgSrc;
            if (!string.IsNullOrWhiteSpace(DataSrc) && !DataSrc.StartsWith("data:")) return DataSrc;
            if (PictureSources != null)
            {
                foreach (var ps in PictureSources)
                {
                    var url = PickFirstFromSrcset(ps.Srcset);
                    if (!string.IsNullOrWhiteSpace(url) && !url.StartsWith("data:")) return url;
                }
            }
            // Fall back to base64 LQIP if nothing else
            if (!string.IsNullOrWhiteSpace(ImgSrc)) return ImgSrc;
            return null;
        }
    }

    private static string? PickFirstFromSrcset(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset)) return null;
        foreach (var part in srcset.Split(','))
        {
            var url = part.Trim().Split(' ')[0];
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }
        return null;
    }
}

public sealed class PictureSource
{
    [JsonProperty("srcset")] public string? Srcset { get; set; }
    [JsonProperty("sizes")] public string? Sizes { get; set; }
    [JsonProperty("media")] public string? Media { get; set; }
    [JsonProperty("type")] public string? Type { get; set; }
}

public sealed class DomRect
{
    [JsonProperty("x")] public double X { get; set; }
    [JsonProperty("y")] public double Y { get; set; }
    [JsonProperty("width")] public double Width { get; set; }
    [JsonProperty("height")] public double Height { get; set; }
    [JsonProperty("top")] public double Top { get; set; }
    [JsonProperty("left")] public double Left { get; set; }
}