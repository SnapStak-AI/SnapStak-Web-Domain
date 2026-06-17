// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Penpot 2.12 type library for SnapStak ConteX Law.
// Ported from: github.com/betagouv/figpot (TypeScript, MPL-2.0)
// Cross-validated against: Penpot 2.12.0-RC3 export (Prototype_examples_v1_2.penpot)
//                          Penpot Plugin API TypeDoc (doc.plugins.penpot.app)
//                          Penpot-2_12-Format-Specification.docx (Rev 4, SnapStak)
//
// STRUCTURE
//   §1  Primitive / shared value types
//   §2  Fill, Stroke, Shadow, Blur
//   §3  Shape base + geometry
//   §4  Concrete shape types (Frame, Rect, Circle, Path, Text, Group, Bool)
//   §5  Layout (Flex, Grid, per-child)
//   §6  Component types
//   §7  Media / object (image chain)
//   §8  Archive / manifest types
//   §9  Design tokens
//   §10 Interaction / prototype

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapStak.Wasm.Client.Engine.Plugins.Penpot.Models;

// ═══════════════════════════════════════════════════════════════════════════════
// §1  Primitive / shared value types
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 6-character RGB hex color with leading '#'.
/// Example: "#ebe7e4"
/// </summary>
public record struct HexColor(string Value)
{
    public static implicit operator string(HexColor h) => h.Value;
    public static implicit operator HexColor(string s) => new(s);
}

/// <summary>
/// 2D affine transform matrix.
/// Identity: a=1, b=0, c=0, d=1, e=0, f=0
/// </summary>
public sealed class PenpotMatrix
{
    [JsonPropertyName("a")] public double A { get; set; } = 1;
    [JsonPropertyName("b")] public double B { get; set; } = 0;
    [JsonPropertyName("c")] public double C { get; set; } = 0;
    [JsonPropertyName("d")] public double D { get; set; } = 1;
    [JsonPropertyName("e")] public double E { get; set; } = 0;
    [JsonPropertyName("f")] public double F { get; set; } = 0;

    /// <summary>The identity matrix.</summary>
    public static PenpotMatrix Identity => new();

    /// <summary>Compute the mathematical inverse (used for transformInverse).</summary>
    public PenpotMatrix Inverse()
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return Identity;
        return new PenpotMatrix
        {
            A = D / det,
            B = -B / det,
            C = -C / det,
            D = A / det,
            E = (C * F - D * E) / det,
            F = (B * E - A * F) / det,
        };
    }
}

/// <summary>
/// Axis-aligned bounding rectangle. Used for selrect.
/// x2 = x + width, y2 = y + height (Penpot requirement — both representations
/// must be consistent).
/// </summary>
public sealed class PenpotSelrect
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    [JsonPropertyName("x1")] public double X1 { get; set; }
    [JsonPropertyName("y1")] public double Y1 { get; set; }
    [JsonPropertyName("x2")] public double X2 { get; set; }
    [JsonPropertyName("y2")] public double Y2 { get; set; }

    public static PenpotSelrect FromBox(double x, double y, double w, double h) => new()
    {
        X = x,
        Y = y,
        Width = w,
        Height = h,
        X1 = x,
        Y1 = y,
        X2 = x + w,
        Y2 = y + h,
    };
}

/// <summary>
/// 2D point. Used in the `points` array (four corners of the shape).
/// </summary>
public sealed class PenpotPoint
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
}

// ── Enumerations (all values verified against Penpot Plugin API TypeDoc) ──────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PenpotShapeType
{
    [JsonStringEnumMemberName("frame")] Frame,
    [JsonStringEnumMemberName("rect")] Rect,
    [JsonStringEnumMemberName("circle")] Circle,
    [JsonStringEnumMemberName("path")] Path,
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("group")] Group,
    [JsonStringEnumMemberName("bool")] Bool,
    [JsonStringEnumMemberName("svg-raw")] SvgRaw,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PenpotBlendMode
{
    [JsonStringEnumMemberName("normal")] Normal,
    [JsonStringEnumMemberName("darken")] Darken,
    [JsonStringEnumMemberName("multiply")] Multiply,
    [JsonStringEnumMemberName("color-burn")] ColorBurn,
    [JsonStringEnumMemberName("lighten")] Lighten,
    [JsonStringEnumMemberName("screen")] Screen,
    [JsonStringEnumMemberName("color-dodge")] ColorDodge,
    [JsonStringEnumMemberName("overlay")] Overlay,
    [JsonStringEnumMemberName("soft-light")] SoftLight,
    [JsonStringEnumMemberName("hard-light")] HardLight,
    [JsonStringEnumMemberName("difference")] Difference,
    [JsonStringEnumMemberName("exclusion")] Exclusion,
    [JsonStringEnumMemberName("hue")] Hue,
    [JsonStringEnumMemberName("saturation")] Saturation,
    [JsonStringEnumMemberName("color")] Color,
    [JsonStringEnumMemberName("luminosity")] Luminosity,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PenpotConstraintsH
{
    [JsonStringEnumMemberName("left")] Left,
    [JsonStringEnumMemberName("right")] Right,
    [JsonStringEnumMemberName("leftright")] LeftRight,
    [JsonStringEnumMemberName("center")] Center,
    [JsonStringEnumMemberName("scale")] Scale,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PenpotConstraintsV
{
    [JsonStringEnumMemberName("top")] Top,
    [JsonStringEnumMemberName("bottom")] Bottom,
    [JsonStringEnumMemberName("topbottom")] TopBottom,
    [JsonStringEnumMemberName("center")] Center,
    [JsonStringEnumMemberName("scale")] Scale,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PenpotGrowType
{
    [JsonStringEnumMemberName("fixed")] Fixed,
    [JsonStringEnumMemberName("auto-width")] AutoWidth,
    [JsonStringEnumMemberName("auto-height")] AutoHeight,
}

// ═══════════════════════════════════════════════════════════════════════════════
// §2  Fill, Stroke, Shadow, Blur
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One fill layer. A shape's fills[] array may carry multiple; they paint
/// bottom-to-top (fills[0] is painted first).
/// Exactly one of fillColor / fillColorGradient / fillImage should be set.
/// </summary>
public sealed class PenpotFill
{
    // Solid fill
    [JsonPropertyName("fillColor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FillColor { get; set; }

    // Plants-app's rule: fillOpacity is emitted only when it's not 1.0.
    // 340/601 of Plants' solid fills are `{fillColor}` alone; the rest include
    // an explicit fillOpacity when it differs from 1. Image fills never emit it
    // at all. Use a nullable backing field so we can distinguish "not set"
    // from "set to 1.0".
    [JsonPropertyName("fillOpacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FillOpacity { get; set; }

    // Library color refs (optional)
    [JsonPropertyName("fillColorRefId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FillColorRefId { get; set; }

    [JsonPropertyName("fillColorRefFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FillColorRefFile { get; set; }

    // Gradient fill
    [JsonPropertyName("fillColorGradient")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotGradient? FillColorGradient { get; set; }

    // Image fill
    [JsonPropertyName("fillImage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotFillImage? FillImage { get; set; }

    // Factories
    public static PenpotFill Solid(string hexColor, double opacity = 1.0)
    {
        var f = new PenpotFill { FillColor = hexColor };
        // Only emit fillOpacity when it meaningfully differs from 1. This
        // matches Plants-app's schema: 340/601 of its solid fills are
        // {fillColor} alone, opacity only appears when non-1.
        if (Math.Abs(opacity - 1.0) > 1e-9) f.FillOpacity = opacity;
        return f;
    }

    public static PenpotFill FromImage(PenpotFillImage img) =>
        new() { FillImage = img };   // image fills never carry fillOpacity (per Plants)

    public static PenpotFill FromGradient(PenpotGradient grad) =>
        new() { FillColorGradient = grad };
}

/// <summary>
/// Linear or radial gradient fill.
/// Coordinates are normalised 0..1 relative to the shape bounding box.
/// Verified against Penpot Plugin API TypeDoc.
/// </summary>
public sealed class PenpotGradient
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "linear"; // "linear" | "radial"

    [JsonPropertyName("startX")] public double StartX { get; set; } = 0.5;
    [JsonPropertyName("startY")] public double StartY { get; set; } = 0;
    [JsonPropertyName("endX")] public double EndX { get; set; } = 0.5;
    [JsonPropertyName("endY")] public double EndY { get; set; } = 1;
    [JsonPropertyName("width")] public double Width { get; set; } = 1;

    [JsonPropertyName("stops")]
    public List<PenpotGradientStop> Stops { get; set; } = new();
}

public sealed class PenpotGradientStop
{
    [JsonPropertyName("color")] public string Color { get; set; } = "#000000";
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1;
    [JsonPropertyName("offset")] public double Offset { get; set; }
}

/// <summary>
/// Image fill reference. The id points to
/// files/{file}/media/{id}.json → objects/{mediaId}.{ext}.
/// See spec §2.1 and §10.1.2.
/// </summary>
public sealed class PenpotFillImage
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    // Plants-app's fillImage blocks never carry a `name` field. Keeping the
    // property on the class for internal use, but marking with JsonIgnore so
    // it's never serialised into the final JSON — otherwise Penpot's importer
    // may reject unexpected fields.
    [JsonIgnore] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("mtype")] public string Mtype { get; set; } = "image/png";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("keepAspectRatio")] public bool KeepAspectRatio { get; set; } = true;
}

/// <summary>
/// One stroke definition.
/// All enum values verified against Penpot Plugin API TypeDoc.
/// </summary>
public sealed class PenpotStroke
{
    [JsonPropertyName("strokeStyle")]
    public string StrokeStyle { get; set; } = "solid";
    // "svg" | "none" | "mixed" | "solid" | "dotted" | "dashed"

    [JsonPropertyName("strokeColor")]
    public string StrokeColor { get; set; } = "#000000";

    [JsonPropertyName("strokeOpacity")]
    public double StrokeOpacity { get; set; } = 1.0;

    [JsonPropertyName("strokeWidth")]
    public double StrokeWidth { get; set; } = 1.0;

    [JsonPropertyName("strokeAlignment")]
    public string StrokeAlignment { get; set; } = "center";
    // "center" | "inner" | "outer"

    [JsonPropertyName("strokeCapStart")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StrokeCapStart { get; set; }
    // "round"|"square"|"line-arrow"|"triangle-arrow"|"square-marker"|"circle-marker"|"diamond-marker"

    [JsonPropertyName("strokeCapEnd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StrokeCapEnd { get; set; }

    [JsonPropertyName("strokeColorRefId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StrokeColorRefId { get; set; }

    [JsonPropertyName("strokeColorRefFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StrokeColorRefFile { get; set; }

    [JsonPropertyName("strokeColorGradient")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotGradient? StrokeColorGradient { get; set; }
}

/// <summary>
/// Drop shadow or inner shadow.
/// Note: the field on a shape is called "shadow" (singular), not "shadows".
/// </summary>
public sealed class PenpotShadow
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("style")] public string Style { get; set; } = "drop-shadow"; // | "inner-shadow"
    [JsonPropertyName("color")] public PenpotShadowColor Color { get; set; } = new();
    [JsonPropertyName("offsetX")] public double OffsetX { get; set; }
    [JsonPropertyName("offsetY")] public double OffsetY { get; set; }
    [JsonPropertyName("blur")] public double Blur { get; set; } = 4;
    [JsonPropertyName("spread")] public double Spread { get; set; }
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }
}

public sealed class PenpotShadowColor
{
    [JsonPropertyName("color")] public string Color { get; set; } = "#000000";
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.2;
}

/// <summary>
/// Layer blur. The simple numeric form is also accepted by Penpot — both
/// "blur": 10 and the object form are valid on the wire.
/// The plugin emits the object form for explicitness.
/// </summary>
public sealed class PenpotBlur
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "layer-blur"; // only valid value

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════
// §3  Shape base model
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Base attributes shared by all nine shape types.
/// Serialised directly to {page-uuid}/{shape-uuid}.json.
///
/// The plugin always sets:
///   - type, id, name, parentId, frameId, pageId
///   - x, y, width, height (null for path/bool)
///   - rotation (0.0 for axis-aligned shapes)
///   - selrect (derived from x/y/w/h)
///   - points  (four corners, derived from selrect for identity-transform shapes)
///   - transform / transformInverse (identity for un-rotated shapes)
///   - fills, strokes (always arrays, never null)
/// All other fields are optional and should be omitted when not needed.
/// </summary>
public class PenpotShape
{
    // ── Identity ──────────────────────────────────────────────────────────────
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "rect";
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("parentId")] public string ParentId { get; set; } = string.Empty;
    [JsonPropertyName("frameId")] public string FrameId { get; set; } = string.Empty;

    [JsonPropertyName("pageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PageId { get; set; }

    // ── Geometry ──────────────────────────────────────────────────────────────
    // x/y/width/height keys MUST ALWAYS be emitted, even when null — Penpot's
    // malli schema for shapes uses [:maybe :double] which accepts null but
    // still requires the key to be present. Verified across Plants-app and the
    // Penpot 2.12-RC3 reference export: every shape of every type has these
    // four keys, with null values only on paths and bools (100%). Omitting
    // them (the old WhenWritingNull behaviour) causes import validation to
    // fail silently with "Not all files have been imported".
    [JsonPropertyName("x")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Y { get; set; }

    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Width { get; set; }

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Height { get; set; }

    [JsonPropertyName("rotation")]
    public double Rotation { get; set; }

    [JsonPropertyName("selrect")]
    public PenpotSelrect Selrect { get; set; } = new();

    [JsonPropertyName("points")]
    public List<PenpotPoint> Points { get; set; } = new();

    [JsonPropertyName("transform")]
    public PenpotMatrix Transform { get; set; } = PenpotMatrix.Identity;

    [JsonPropertyName("transformInverse")]
    public PenpotMatrix TransformInverse { get; set; } = PenpotMatrix.Identity;

    // ── Appearance ────────────────────────────────────────────────────────────
    [JsonPropertyName("fills")]
    public List<PenpotFill> Fills { get; set; } = new();

    [JsonPropertyName("strokes")]
    public List<PenpotStroke> Strokes { get; set; } = new();

    [JsonPropertyName("shadow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PenpotShadow>? Shadow { get; set; }

    [JsonPropertyName("blur")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotBlur? Blur { get; set; }

    // Nullable so that unset opacity (the 99%+ case of fully-opaque shapes) is
    // dropped entirely by the global WhenWritingNull rule, matching Plants-app
    // where only 11 of 532 shapes carried an opacity field. Setting a value via
    // ApplyOpacity → emits normally.
    [JsonPropertyName("opacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Opacity { get; set; }

    [JsonPropertyName("blendMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlendMode { get; set; }

    // ── Per-corner radii ───────────────────────────────────────────────────────
    // Nullable + JsonIgnoreCondition.WhenWritingNull so they only serialise on
    // shapes that have SetRadius(All)* called. Plants-app only emits r1-r4 on
    // rects (and their subclasses); emitting them on paths, groups, text, and
    // frames makes Penpot's import schema reject the file. Default-valued
    // doubles would serialise as 0, tripping the same rejection, so we use
    // nullable doubles and only assign when a radius is set.
    [JsonPropertyName("r1")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? R1 { get; set; }

    [JsonPropertyName("r2")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? R2 { get; set; }

    [JsonPropertyName("r3")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? R3 { get; set; }

    [JsonPropertyName("r4")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? R4 { get; set; }

    // ── Visibility / state ────────────────────────────────────────────────────
    [JsonPropertyName("hidden")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hidden { get; set; }

    [JsonPropertyName("blocked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Blocked { get; set; }

    // The global JsonSerializerOptions uses DefaultIgnoreCondition = WhenWritingNull,
    // which would drop these. Plants-app's verified export has them as explicit
    // nulls ("flipX": null, "flipY": null) on every shape. Override with
    // JsonIgnoreCondition.Never so they always serialize.
    [JsonPropertyName("flipX")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? FlipX { get; set; } = null;

    [JsonPropertyName("flipY")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool? FlipY { get; set; } = null;

    // ── Proportions ───────────────────────────────────────────────────────────
    // Emitted on every shape type EXCEPT text, per Plants-app's schema
    // (145/145 of its text shapes carry neither field; all other shape types
    // emit both on every shape). Text shapes manage their own width/height
    // from content metrics, not a stored aspect ratio. Nullable + WhenWritingNull
    // so text can suppress these by leaving them null, while non-text shapes
    // get them set in SetGeometry().
    [JsonPropertyName("proportion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Proportion { get; set; }

    [JsonPropertyName("proportionLock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProportionLock { get; set; }

    // ── Responsive constraints ────────────────────────────────────────────────
    [JsonPropertyName("constraintsH")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConstraintsH { get; set; }
    // "left" | "right" | "leftright" | "center" | "scale"

    [JsonPropertyName("constraintsV")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConstraintsV { get; set; }
    // "top" | "bottom" | "topbottom" | "center" | "scale"

    // ── Viewer ────────────────────────────────────────────────────────────────
    [JsonPropertyName("hideInViewer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideInViewer { get; set; }

    [JsonPropertyName("fixedScroll")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FixedScroll { get; set; }

    // ── Interactions (prototype) ───────────────────────────────────────────────
    [JsonPropertyName("interactions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PenpotInteraction>? Interactions { get; set; }

    // ── Component sync ────────────────────────────────────────────────────────
    [JsonPropertyName("touched")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Touched { get; set; }

    [JsonPropertyName("componentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComponentId { get; set; }

    [JsonPropertyName("componentFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComponentFile { get; set; }

    [JsonPropertyName("componentRoot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ComponentRoot { get; set; }

    [JsonPropertyName("mainInstance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MainInstance { get; set; }

    [JsonPropertyName("shapeRef")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShapeRef { get; set; }

    // ── Design grids (visual overlay, not layout) ─────────────────────────────
    [JsonPropertyName("grids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PenpotDesignGrid>? Grids { get; set; }

    // ── Exports ───────────────────────────────────────────────────────────────
    [JsonPropertyName("exports")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PenpotExportPreset>? Exports { get; set; }

    // ── Layout children (set when this shape is inside a flex/grid frame) ─────
    [JsonPropertyName("layoutItemHSizing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutItemHSizing { get; set; }     // "fill" | "fix" | "auto"

    [JsonPropertyName("layoutItemVSizing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutItemVSizing { get; set; }

    [JsonPropertyName("layoutItemMinW")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LayoutItemMinW { get; set; }

    [JsonPropertyName("layoutItemMaxW")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LayoutItemMaxW { get; set; }

    [JsonPropertyName("layoutItemMinH")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LayoutItemMinH { get; set; }

    [JsonPropertyName("layoutItemMaxH")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LayoutItemMaxH { get; set; }

    [JsonPropertyName("layoutItemAlignSelf")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutItemAlignSelf { get; set; }   // "start"|"end"|"center"|"stretch"

    [JsonPropertyName("layoutItemAbsolute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LayoutItemAbsolute { get; set; }

    [JsonPropertyName("layoutItemZIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LayoutItemZIndex { get; set; }

    [JsonPropertyName("layoutItemMargin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotLayoutPadding? LayoutItemMargin { get; set; }

    [JsonPropertyName("layoutItemMarginType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutItemMarginType { get; set; }  // "simple" | "multiple"

    // ── Helper: set geometry consistently ────────────────────────────────────
    public virtual void SetGeometry(double x, double y, double w, double h)
    {
        X = x; Y = y; Width = w; Height = h;
        Selrect = PenpotSelrect.FromBox(x, y, w, h);
        Points = new List<PenpotPoint>
        {
            new() { X = x,     Y = y     }, // top-left
            new() { X = x + w, Y = y     }, // top-right
            new() { X = x + w, Y = y + h }, // bottom-right
            new() { X = x,     Y = y + h }, // bottom-left
        };
        Transform = PenpotMatrix.Identity;
        TransformInverse = PenpotMatrix.Identity;
        // Penpot's `proportion` field is the aspect-ratio-LOCK reference — not the
        // current w/h ratio. It only matters when proportionLock=true, and on new
        // shapes the convention (verified in Plants-app export) is 1.0 everywhere.
        // Emitted on every non-text shape (Plants has 0/145 text shapes carry it,
        // 100% of other types do). PenpotText's constructor nulls these out.
        Proportion = 1.0;
        ProportionLock = false;
    }

    /// <summary>
    /// Set uniform corner radius (mirrors to all four r fields and rx/ry for
    /// types that carry those).
    /// </summary>
    public void SetRadius(double r)
    {
        R1 = R2 = R3 = R4 = r;
    }

    /// <summary>Set independent per-corner radii.</summary>
    public void SetRadiusAll(double r1, double r2, double r3, double r4)
    {
        R1 = r1; R2 = r2; R3 = r3; R4 = r4;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// §4  Concrete shape types
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Frame shape. Container with optional flex/grid auto-layout.
/// Top-level frames on a page are what Penpot users call "boards".
/// </summary>
public sealed class PenpotFrame : PenpotShape
{
    public PenpotFrame() { Type = "frame"; }

    /// <summary>
    /// Child shape UUIDs in z-order (first = bottom).
    /// REQUIRED on every frame — even frames with no children must emit shapes: [].
    /// Omitting this field causes Penpot's import worker to abort layer-tree assembly.
    /// </summary>
    [JsonPropertyName("shapes")]
    public List<string> Shapes { get; set; } = new();

    /// <summary>
    /// Plants-app behaviour: hideFillOnExport appears ONLY on the root frame
    /// (1/1 root frames, 0/45 non-root frames). Nullable with WhenWritingNull
    /// → BuildRootFrame sets it explicitly to false and it emits; BuildFrame
    /// leaves it null and it's omitted.
    /// </summary>
    [JsonPropertyName("hideFillOnExport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HideFillOnExport { get; set; }

    [JsonPropertyName("showContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShowContent { get; set; }

    [JsonPropertyName("hideInViewer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public new bool HideInViewer { get; set; }

    [JsonPropertyName("useForThumbnail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseForThumbnail { get; set; }

    /// <summary>
    /// Uniform X-axis corner radius — legacy field, also present on frames.
    /// Verified Plants-app behaviour:
    ///   - Root frame (00000000-…): rx/ry entirely absent.
    ///   - Non-root frames: ALL 45/45 sampled frames carry "rx": 0 / "ry": 0 even
    ///     when there's no radius. Penpot's 0003-fix-root-shape migration treats
    ///     missing rx on a non-root frame as invalid.
    /// Nullable → the root frame (where this stays unset) omits the field, while
    /// all non-root frames (where BuildFrame → SetGeometry path initialises to 0)
    /// emit it with value 0, matching Plants-app.
    /// </summary>
    [JsonPropertyName("rx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Rx { get; set; }

    /// <summary>Uniform Y-axis corner radius — legacy field. See Rx for emission rules.</summary>
    [JsonPropertyName("ry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Ry { get; set; }

    /// <summary>Set uniform radius on all corners and legacy rx/ry fields.</summary>
    public new void SetRadius(double r)
    {
        base.SetRadius(r);
        Rx = Ry = r;
    }

    public new void SetRadiusAll(double r1, double r2, double r3, double r4)
    {
        base.SetRadiusAll(r1, r2, r3, r4);
        Rx = Ry = r1;
    }

    // ── Flex layout fields ────────────────────────────────────────────────────
    [JsonPropertyName("layout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Layout { get; set; }                 // "flex" | "grid"

    [JsonPropertyName("layoutFlexDir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutFlexDir { get; set; }          // "row"|"row-reverse"|"column"|"column-reverse"

    [JsonPropertyName("layoutAlignItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutAlignItems { get; set; }       // "start"|"end"|"center"|"stretch"

    [JsonPropertyName("layoutAlignContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutAlignContent { get; set; }

    [JsonPropertyName("layoutJustifyContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutJustifyContent { get; set; }

    [JsonPropertyName("layoutJustifyItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutJustifyItems { get; set; }

    [JsonPropertyName("layoutWrapType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutWrapType { get; set; }         // "nowrap" | "wrap"

    [JsonPropertyName("layoutGap")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotLayoutGap? LayoutGap { get; set; }

    [JsonPropertyName("layoutGapType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutGapType { get; set; }          // "simple" | "multiple"

    [JsonPropertyName("layoutPadding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotLayoutPadding? LayoutPadding { get; set; }

    [JsonPropertyName("layoutPaddingType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutPaddingType { get; set; }      // "simple" | "multiple"

    // ── Grid layout fields (requires feature flag "layout/grid") ─────────────
    [JsonPropertyName("layoutGridDir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LayoutGridDir { get; set; }          // "row" | "column"

    [JsonPropertyName("layoutGridColumns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PenpotGridTrack>? LayoutGridColumns { get; set; }

    [JsonPropertyName("layoutGridRows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PenpotGridTrack>? LayoutGridRows { get; set; }

    [JsonPropertyName("layoutGridCells")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, PenpotGridCell>? LayoutGridCells { get; set; }
}

/// <summary>
/// Rectangle shape. Workhorse for backgrounds, buttons, and image holders.
/// Images are represented as rect + fillImage fill (see spec §7.3).
/// </summary>
public sealed class PenpotRect : PenpotShape
{
    public PenpotRect() { Type = "rect"; }

    /// <summary>
    /// Uniform X-axis corner radius (legacy field; emit alongside r1-r4).
    /// Plants-app emits rx/ry on 98% of rects even when the radius is zero.
    /// Nullable with WhenWritingNull: BuildRect always calls SetGeometry which
    /// does not touch Rx, but ApplyRadii or explicit init can set it (and 0 is
    /// valid emission). To force rx:0 on every rect, initialize to 0 in the
    /// BuildRect factory (plugin code does this via SetRadius defaults in C#
    /// property initialisers — here we leave it nullable so an explicit
    /// assignment drives emission).
    /// </summary>
    [JsonPropertyName("rx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Rx { get; set; }

    /// <summary>Uniform Y-axis corner radius (legacy field). See Rx.</summary>
    [JsonPropertyName("ry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Ry { get; set; }

    /// <summary>Set uniform radius on all corners and legacy rx/ry fields.</summary>
    public new void SetRadius(double r)
    {
        base.SetRadius(r);
        Rx = Ry = r;
    }

    public new void SetRadiusAll(double r1, double r2, double r3, double r4)
    {
        base.SetRadiusAll(r1, r2, r3, r4);
        // Legacy fields: use the first corner value for rx/ry
        Rx = Ry = r1;
    }
}

/// <summary>
/// Circle / ellipse. Defined entirely by its bounding box — no extra fields.
/// Width == Height → circle; otherwise ellipse.
/// </summary>
public sealed class PenpotCircle : PenpotShape
{
    public PenpotCircle() { Type = "circle"; }
}

/// <summary>
/// Vector path. Geometry is stored as an SVG path string in Content.
/// x, y, width, height are null — bounding box lives in selrect.
/// </summary>
public sealed class PenpotPath : PenpotShape
{
    public PenpotPath()
    {
        Type = "path";
        // x/y/width/height must be present as null on every path — malli
        // schema = [:maybe :double] — key required, value null. Plants-app
        // paths (693/693) and Penpot-ref paths (151/151) all emit these as
        // explicit null. The base class's Never condition forces them to
        // serialize even when null; here we only need to ensure the values
        // start null because paths derive their box from Content, not x/y/w/h.
        X = null;
        Y = null;
        Width = null;
        Height = null;
        // proportion / proportionLock must be present on every path too —
        // Plants (693/693) and Penpot-ref (151/151) both have them set.
        // The base SetGeometry assigns these, but paths don't call
        // SetGeometry (they get their geometry from the path data string),
        // so we set them here in the constructor instead.
        Proportion = 1.0;
        ProportionLock = false;
    }

    /// <summary>
    /// SVG-style path data string.
    /// Commands used: M (moveto), L (lineto), C (curveto), Z (closepath).
    /// Example: "M913,289L913,313C913,321.28,906.28,328,898,328Z"
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("svgAttrs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? SvgAttrs { get; set; }

    [JsonPropertyName("svgViewbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotSelrect? SvgViewbox { get; set; }

    [JsonPropertyName("svgTransform")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotMatrix? SvgTransform { get; set; }

    [JsonPropertyName("svgDefs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? SvgDefs { get; set; }
}

/// <summary>
/// Group shape. Plain container with no layout or background.
/// </summary>
public sealed class PenpotGroup : PenpotShape
{
    public PenpotGroup() { Type = "group"; }

    [JsonPropertyName("shapes")]
    public List<string> Shapes { get; set; } = new();

    [JsonPropertyName("maskedGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MaskedGroup { get; set; }
}

/// <summary>
/// Boolean operation shape.
/// For plugin v1: emit content: [] and let Penpot recompute from child shapes.
/// </summary>
public sealed class PenpotBool : PenpotShape
{
    public PenpotBool() { Type = "bool"; }

    [JsonPropertyName("shapes")]
    public List<string> Shapes { get; set; } = new();

    [JsonPropertyName("boolType")]
    public string BoolType { get; set; } = "union";
    // "union" | "difference" | "intersection" | "exclude"

    /// <summary>
    /// Computed boolean path segments. Emit empty for v1 — Penpot recomputes.
    /// NOTE: bool content uses the command-vector form, NOT the SVG string.
    /// </summary>
    [JsonPropertyName("content")]
    public List<PenpotBoolSegment> Content { get; set; } = new();
}

public sealed class PenpotBoolSegment
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "move-to";
    // "move-to" | "line-to" | "curve-to" | "close-path"

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotBoolSegmentParams? Params { get; set; }
}

public sealed class PenpotBoolSegmentParams
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("c1x")] public double C1X { get; set; }
    [JsonPropertyName("c1y")] public double C1Y { get; set; }
    [JsonPropertyName("c2x")] public double C2X { get; set; }
    [JsonPropertyName("c2y")] public double C2Y { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Text shape — most structurally complex type
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Text shape. Content is a four-level tree:
///   Root → ParagraphSet → Paragraph → Leaf
/// Styling is inheritable; leaf-level overrides paragraph-level.
/// </summary>
public sealed class PenpotText : PenpotShape
{
    public PenpotText()
    {
        Type = "text";
        // Text shapes do NOT carry proportion / proportionLock in Plants-app's
        // exports (145/145). Text width/height is derived from content metrics
        // at render time, not a stored aspect ratio. Leaving these null means
        // JsonIgnore(WhenWritingNull) suppresses both fields.
        Proportion = null;
        ProportionLock = null;
    }

    /// <summary>
    /// Override to prevent the base SetGeometry from populating Proportion /
    /// ProportionLock — Plants-app text shapes never carry these. All other
    /// geometry setup (selrect, points, transform) is identical to the base.
    /// </summary>
    public override void SetGeometry(double x, double y, double w, double h)
    {
        base.SetGeometry(x, y, w, h);
        Proportion = null;
        ProportionLock = null;
    }

    [JsonPropertyName("growType")]
    public string GrowType { get; set; } = "auto-height";
    // "fixed" | "auto-width" | "auto-height"

    [JsonPropertyName("content")]
    public PenpotTextRoot Content { get; set; } = new();

    /// <summary>
    /// Cached render metrics. Optional — Penpot regenerates on load.
    /// Safe to emit as [] or omit entirely.
    /// </summary>
    [JsonPropertyName("positionData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object>? PositionData { get; set; }
}

/// <summary>Level 1 — root node of the text content tree.</summary>
public sealed class PenpotTextRoot
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "root";

    [JsonPropertyName("verticalAlign")]
    public string VerticalAlign { get; set; } = "top"; // "top" | "center" | "bottom"

    [JsonPropertyName("fills")]
    public List<PenpotFill> Fills { get; set; } = new();

    [JsonPropertyName("children")]
    public List<PenpotParagraphSet> Children { get; set; } = new();
}

/// <summary>Level 2 — paragraph-set.</summary>
public sealed class PenpotParagraphSet
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "paragraph-set";

    [JsonPropertyName("children")]
    public List<PenpotParagraph> Children { get; set; } = new();
}

/// <summary>Level 3 — paragraph. Carries shared styling for its leaves.</summary>
public sealed class PenpotParagraph
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "paragraph";

    /// <summary>
    /// DraftJS-derived key. 5-char alphanumeric string.
    /// Generate with: RandomAlphaNum(5)
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("fontFamily")] public string FontFamily { get; set; } = "sourcesanspro";
    [JsonPropertyName("fontSize")] public string FontSize { get; set; } = "16";   // string, no "px"
    [JsonPropertyName("fontWeight")] public string FontWeight { get; set; } = "400";
    [JsonPropertyName("fontStyle")] public string FontStyle { get; set; } = "normal";
    [JsonPropertyName("textAlign")] public string TextAlign { get; set; } = "left";
    [JsonPropertyName("textTransform")] public string TextTransform { get; set; } = "none";
    [JsonPropertyName("textDecoration")] public string TextDecoration { get; set; } = "none";
    [JsonPropertyName("letterSpacing")] public string LetterSpacing { get; set; } = "0";
    [JsonPropertyName("lineHeight")] public string LineHeight { get; set; } = "1.2";

    /// <summary>Penpot font registry key. "gfont-karla" for Google Fonts Karla.</summary>
    [JsonPropertyName("fontId")] public string FontId { get; set; } = "sourcesanspro";

    /// <summary>Variant identifier: "regular", "500", "700italic".</summary>
    [JsonPropertyName("fontVariantId")] public string FontVariantId { get; set; } = "regular";

    [JsonPropertyName("fills")]
    public List<PenpotFill> Fills { get; set; } = new();

    [JsonPropertyName("children")]
    public List<PenpotTextLeaf> Children { get; set; } = new();
}

/// <summary>
/// Level 4 — leaf. Carries the actual text. May override paragraph styling.
/// No "type" field — absence of type distinguishes leaves from paragraphs.
/// </summary>
public sealed class PenpotTextLeaf
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("direction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction { get; set; } = "ltr"; // "ltr" | "rtl"

    // Style overrides — null means "inherit from paragraph"
    [JsonPropertyName("fontFamily")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontFamily { get; set; }

    [JsonPropertyName("fontSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontSize { get; set; }

    [JsonPropertyName("fontWeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontWeight { get; set; }

    [JsonPropertyName("fontStyle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontStyle { get; set; }

    [JsonPropertyName("textTransform")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextTransform { get; set; }

    [JsonPropertyName("textDecoration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextDecoration { get; set; }

    [JsonPropertyName("letterSpacing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LetterSpacing { get; set; }

    [JsonPropertyName("lineHeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LineHeight { get; set; }

    [JsonPropertyName("fontId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontId { get; set; }

    [JsonPropertyName("fontVariantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontVariantId { get; set; }

    [JsonPropertyName("fills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PenpotFill>? Fills { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════════
// §5  Layout support types
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PenpotLayoutGap
{
    [JsonPropertyName("rowGap")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RowGap { get; set; }

    [JsonPropertyName("columnGap")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ColumnGap { get; set; }
}

/// <summary>Padding / margin. p1=top, p2=right, p3=bottom, p4=left.</summary>
public sealed class PenpotLayoutPadding
{
    [JsonPropertyName("p1")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? P1 { get; set; }
    [JsonPropertyName("p2")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? P2 { get; set; }
    [JsonPropertyName("p3")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? P3 { get; set; }
    [JsonPropertyName("p4")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? P4 { get; set; }

    public static PenpotLayoutPadding Uniform(double v) =>
        new() { P1 = v, P2 = v, P3 = v, P4 = v };
}

/// <summary>
/// Grid track definition — column or row in a CSS-grid layout.
/// Verified against figpot's GridTrack type.
/// </summary>
public sealed class PenpotGridTrack
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "percent"; // "percent"|"flex"|"auto"|"fixed"

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Value { get; set; }
}

/// <summary>Grid cell — explicit cell placement in a grid layout.</summary>
public sealed class PenpotGridCell
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("row")] public int Row { get; set; } = 1;
    [JsonPropertyName("rowSpan")] public int RowSpan { get; set; } = 1;
    [JsonPropertyName("column")] public int Column { get; set; } = 1;
    [JsonPropertyName("columnSpan")] public int ColumnSpan { get; set; } = 1;
    [JsonPropertyName("position")] public string Position { get; set; } = "auto";
    // "auto" | "manual" | "area"

    [JsonPropertyName("alignSelf")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AlignSelf { get; set; }    // "auto"|"start"|"end"|"center"|"stretch"

    [JsonPropertyName("justifySelf")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JustifySelf { get; set; }

    [JsonPropertyName("areaName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AreaName { get; set; }

    [JsonPropertyName("shapes")]
    public List<string> Shapes { get; set; } = new();
}

/// <summary>Design-grid overlay on a shape (not auto-layout).</summary>
public sealed class PenpotDesignGrid
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "column"; // "column" | "row" | "square"

    [JsonPropertyName("display")]
    public bool Display { get; set; } = true;

    [JsonPropertyName("params")]
    public PenpotDesignGridParams Params { get; set; } = new();
}

public sealed class PenpotDesignGridParams
{
    [JsonPropertyName("color")]
    public PenpotShadowColor Color { get; set; } = new() { Color = "#0000FF", Opacity = 0.1 };

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Size { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }      // "stretch"|"left"|"center"|"right"

    [JsonPropertyName("margin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Margin { get; set; }

    [JsonPropertyName("itemLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ItemLength { get; set; }

    [JsonPropertyName("gutter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Gutter { get; set; }
}

public sealed class PenpotExportPreset
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "png";

    [JsonPropertyName("scale")]
    public double Scale { get; set; } = 1.0;

    [JsonPropertyName("suffix")]
    public string Suffix { get; set; } = string.Empty;
}

// ═══════════════════════════════════════════════════════════════════════════════
// §6  Component types
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Component definition. Stored at files/{file}/components/{component-uuid}.json.
/// For plugin v1 — not emitted; all content is plain page shapes.
/// </summary>
public sealed class PenpotComponent
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }
    [JsonPropertyName("modifiedAt")] public string ModifiedAt { get; set; } = DateTime.UtcNow.ToString("O");
    [JsonPropertyName("mainInstanceId")] public string MainInstanceId { get; set; } = string.Empty;
    [JsonPropertyName("mainInstancePage")] public string MainInstancePage { get; set; } = string.Empty;

    [JsonPropertyName("objects")]
    public Dictionary<string, PenpotShape> Objects { get; set; } = new();
}

// ═══════════════════════════════════════════════════════════════════════════════
// §7  Media / object chain
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Media metadata record at files/{file}/media/{media-uuid}.json.
/// The id field is what fillImage.id references.
/// </summary>
public sealed class PenpotMediaRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("mtype")] public string Mtype { get; set; } = "image/png";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("mediaId")] public string MediaId { get; set; } = string.Empty;
    [JsonPropertyName("thumbnailId")] public string ThumbnailId { get; set; } = string.Empty;
    [JsonPropertyName("isLocal")] public bool IsLocal { get; set; } = true;
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");
}

/// <summary>
/// Blob metadata at objects/{blob-uuid}.json.
/// Accompanies every binary file in the objects/ folder.
/// </summary>
public sealed class PenpotBlobMetadata
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>BLAKE2b hash in "blake2b:{hex}" format. Used for deduplication.</summary>
    [JsonPropertyName("hash")] public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("bucket")] public string Bucket { get; set; } = "file-media-object";
    // "file-media-object" for full images, "file-object-thumbnail" for thumbnails

    [JsonPropertyName("contentType")] public string ContentType { get; set; } = "image/png";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>
/// Frame thumbnail record at files/{file}/thumbnails/frame/{page}/{frame}.json.
/// For plugin v1 — omit entirely. Penpot generates thumbnails on first render.
/// </summary>
public sealed class PenpotFrameThumbnail
{
    [JsonPropertyName("fileId")] public string FileId { get; set; } = string.Empty;
    [JsonPropertyName("pageId")] public string PageId { get; set; } = string.Empty;
    [JsonPropertyName("frameId")] public string FrameId { get; set; } = string.Empty;
    [JsonPropertyName("tag")] public string Tag { get; set; } = "frame";
    [JsonPropertyName("mediaId")] public string MediaId { get; set; } = string.Empty;
}

// ═══════════════════════════════════════════════════════════════════════════════
// §8  Archive / manifest types
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Root manifest.json at the archive root.
/// Identifies the archive type and lists contained files.
/// </summary>
public sealed class PenpotManifest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "penpot/export-files";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("generatedBy")]
    public string GeneratedBy { get; set; } = "contex-law/1.0.0";

    [JsonPropertyName("refer")]
    public string Refer { get; set; } = "penpot";

    [JsonPropertyName("files")]
    public List<PenpotManifestFileEntry> Files { get; set; } = new();

    [JsonPropertyName("relations")]
    public List<object> Relations { get; set; } = new();
}

public sealed class PenpotManifestFileEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("features")] public List<string> Features { get; set; } = PenpotFeatureFlags.Default;
}

/// <summary>
/// Known feature flag strings for Penpot 2.12.
/// Always declare every flag whose data you emit. Emitting undeclared features
/// may cause Penpot to reject or silently degrade the file.
/// </summary>
public static class PenpotFeatureFlags
{
    public const string PathData = "fdata/path-data";
    public const string DesignTokens = "design-tokens/v1";
    public const string VariantsV1 = "variants/v1";
    public const string LayoutGrid = "layout/grid";
    public const string ComponentsV2 = "components/v2";
    public const string ShapeDataType = "fdata/shape-data-type";

    /// <summary>
    /// Minimum safe flag set for a single-file import without grid layout.
    /// Matches the 6-flag set from the verified SnapStak 2.12.0-RC3 export.
    /// </summary>
    public static readonly List<string> Default = new()
    {
        PathData, DesignTokens, VariantsV1, ComponentsV2, ShapeDataType,
        // LayoutGrid omitted — add it only when emitting grid-layout frames
    };

    /// <summary>Full set including grid layout.</summary>
    public static readonly List<string> WithGrid = new()
    {
        PathData, DesignTokens, VariantsV1, LayoutGrid, ComponentsV2, ShapeDataType,
    };

    /// <summary>
    /// Current Penpot data-model version. Verified against the Prototype_examples_v1_2
    /// reference export (Penpot 2.12.0-RC3). Declaring this in file-meta tells the
    /// import worker that the file is at the latest schema and does NOT need any
    /// of the legacy migrations re-run against it. Required, along with the full
    /// AppliedMigrations list below.
    /// </summary>
    public const int DataModelVersion = 67;

    /// <summary>
    /// Canonical migration-ID sequence for data-model version 67, in apply-order.
    /// Copied verbatim from the file-meta of Prototype_examples_v1_2.penpot (a
    /// known-good Penpot 2.12-RC3 export). Emitting this list tells Penpot's
    /// importer that every migration has already been run, so it skips the
    /// migration chain and proceeds directly to schema validation.
    ///
    /// This list is frozen per data-model release — update it only when Penpot
    /// bumps DataModelVersion. Any mismatch between Version and the length/content
    /// of Migrations causes "check error on validating file" during import.
    /// </summary>
    public static readonly List<string> AppliedMigrations = new()
    {
        "legacy-2",  "legacy-3",  "legacy-5",  "legacy-6",  "legacy-7",
        "legacy-8",  "legacy-9",  "legacy-10", "legacy-11", "legacy-12",
        "legacy-13", "legacy-14", "legacy-16", "legacy-17", "legacy-18",
        "legacy-19", "legacy-25", "legacy-26", "legacy-27", "legacy-28",
        "legacy-29", "legacy-31", "legacy-32", "legacy-33", "legacy-34",
        "legacy-36", "legacy-37", "legacy-38", "legacy-39", "legacy-40",
        "legacy-41", "legacy-42", "legacy-43", "legacy-44", "legacy-45",
        "legacy-46", "legacy-47", "legacy-48", "legacy-49", "legacy-50",
        "legacy-51", "legacy-52", "legacy-53", "legacy-54", "legacy-55",
        "legacy-56", "legacy-57", "legacy-59", "legacy-62", "legacy-65",
        "legacy-66", "legacy-67",
        "0001-remove-tokens-from-groups",
        "0002-normalize-bool-content-v2",
        "0002-clean-shape-interactions",
        "0003-fix-root-shape",
        "0003-convert-path-content-v2",
        "0005-deprecate-image-type",
        "0006-fix-old-texts-fills",
        "0008-fix-library-colors-v4",
        "0009-clean-library-colors",
        "0009-add-partial-text-touched-flags",
        "0010-fix-swap-slots-pointing-non-existent-shapes",
        "0011-fix-invalid-text-touched-flags",
        "0012-fix-position-data",
        "0013-fix-component-path",
        "0013-clear-invalid-strokes-and-fills",
        "0014-fix-tokens-lib-duplicate-ids",
        "0014-clear-components-nil-objects",
        "0015-clean-shadow-color",
        "0015-fix-text-attrs-blank-strings",
        "0016-copy-fills-from-position-data-to-text-node",
    };
}

/// <summary>
/// File-level metadata at files/{file-uuid}.json (outside the file folder).
/// Tells Penpot which data-model version the file is at.
///
/// IMPORTANT — Version / Migrations contract:
///   Penpot's import worker treats the file-meta `version` as the data-model
///   version it was last saved at. If version=0 (or missing), the importer
///   assumes the file is pre-migration and attempts to run EVERY migration in
///   sequence against it — but our emitted shapes already use the current
///   post-migration schema, so legacy migrations fail or silently corrupt
///   fields. Empirically this manifests as "Not all files have been imported"
///   with no useful client-side error.
///
///   The correct approach (verified against Prototype_examples_v1_2.penpot —
///   a known-good Penpot 2.12-RC3 export): declare version=67 and include the
///   full 72-item migration ID list so the importer knows those migrations
///   have already been applied. The list is frozen per data-model release;
///   PenpotFeatureFlags.AppliedMigrations holds the canonical sequence for
///   data-model v67.
/// </summary>
public sealed class PenpotFileMeta
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("features")] public List<string> Features { get; set; } = PenpotFeatureFlags.Default;
    [JsonPropertyName("version")] public int Version { get; set; } = PenpotFeatureFlags.DataModelVersion;
    [JsonPropertyName("migrations")] public List<string> Migrations { get; set; } = new(PenpotFeatureFlags.AppliedMigrations);
    [JsonPropertyName("options")] public PenpotFileOptions Options { get; set; } = new();
    [JsonPropertyName("isShared")] public bool IsShared { get; set; }
    [JsonPropertyName("hasMediaTrimmed")] public bool HasMediaTrimmed { get; set; }
    [JsonPropertyName("revn")] public int Revn { get; set; }
    [JsonPropertyName("vern")] public int Vern { get; set; }
    [JsonPropertyName("teamId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TeamId { get; set; }

    [JsonPropertyName("projectId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectId { get; set; }

    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");
    [JsonPropertyName("modifiedAt")] public string ModifiedAt { get; set; } = DateTime.UtcNow.ToString("O");
}

public sealed class PenpotFileOptions
{
    [JsonPropertyName("componentsV2")]
    public bool ComponentsV2 { get; set; } = true;
}

/// <summary>
/// Page metadata at files/{file}/pages/{page-uuid}.json.
/// </summary>
public sealed class PenpotPageMeta
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("background")] public string Background { get; set; } = "#FFFFFF";

    [JsonPropertyName("options")]
    public PenpotPageOptions Options { get; set; } = new();
}

public sealed class PenpotPageOptions
{
    [JsonPropertyName("background")]
    public string Background { get; set; } = "#FFFFFF";
}

/// <summary>
/// The fixed root-frame UUID present on every page.
/// Its shapes[] lists all top-level frame UUIDs on the page.
/// </summary>
public static class PenpotRootFrameId
{
    public const string Value = "00000000-0000-0000-0000-000000000000";
}

// ═══════════════════════════════════════════════════════════════════════════════
// §9  Design tokens
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// tokens.json — W3C DTCG format.
/// For plugin v1: emit an empty dictionary.
/// Full token generation deferred to a later version.
/// </summary>
public sealed class PenpotTokenColor
{
    [JsonPropertyName("$value")] public string Value { get; set; } = string.Empty;
    [JsonPropertyName("$type")] public string Type { get; set; } = "color";
    [JsonPropertyName("$description")] public string Description { get; set; } = string.Empty;
}

// ═══════════════════════════════════════════════════════════════════════════════
// §10  Interaction / prototype types
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Prototype interaction on a shape. Emit interactions: [] for plugin v1.
/// Structure verified from figpot's Interaction trait port.
/// </summary>
public sealed class PenpotInteraction
{
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "click";
    // "click"|"mouse-press"|"mouse-over"|"mouse-enter"|"mouse-leave"|"after-delay"

    [JsonPropertyName("actionType")]
    public string ActionType { get; set; } = "navigate";
    // "navigate"|"open-overlay"|"toggle-overlay"|"close-overlay"|"prev-screen"|"open-url"

    [JsonPropertyName("destination")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Destination { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("preserveScroll")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreserveScroll { get; set; }

    [JsonPropertyName("animation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PenpotInteractionAnimation? Animation { get; set; }
}

public sealed class PenpotInteractionAnimation
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "dissolve"; // "dissolve"|"slide"|"push"

    [JsonPropertyName("duration")]
    public int Duration { get; set; } = 300;

    [JsonPropertyName("easing")]
    public string Easing { get; set; } = "ease"; // "linear"|"ease"|"ease-in"|"ease-out"|"ease-in-out"
}