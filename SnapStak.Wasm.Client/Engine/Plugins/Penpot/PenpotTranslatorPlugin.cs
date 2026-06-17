// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// ConteX Law — Penpot 2.12 binary translator plugin.
// Produces a valid .penpot ZIP archive (DEFLATE) from a TranslatorBundle.
//
// Target format: Penpot 2.12.0-RC3, data-model version 67.
// Spec source:   Penpot-2_12-Format-Specification.docx, Rev 4 (SnapStak/2026)
// Cross-ref:     github.com/betagouv/figpot (MPL-2.0)
//
// PAGE LAYOUT (spec §13.4)
//   index 0         : Desktop — Master (full-page desktop capture)
//   index 1..N      : Desktop — one page per carved segment
//   index N+1       : Mobile — Master (if bundle.Mobile != null)
//   index N+2..2N+1 : Mobile — one page per carved segment
//   index 2N+2..    : Hidden components
//
// IMAGE HANDLING (spec §2.1)
//   Remote images   : declared in Phase 1, bytes handed to Phase 2.
//   SVG data-URIs   : decoded inline, no network needed.
//   Missing fetches : shape gets an empty fills[] (no crash, no placeholder).
//
// KNOWN PLUGIN-V1 DEFERRALS (per spec §13)
//   • Components / variants not emitted — all shapes are plain page shapes.
//   • Page thumbnails not emitted — Penpot generates on first render.
//   • Design tokens — tokens.json is emitted as {}.
//   • positionData — omitted from text shapes (Penpot regenerates).
//   • Grid layout — deferred; all containers use flex or no layout.
//   • BLAKE2b hashing — implemented via Blake2Fast NuGet.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SnapStak.Wasm.Client.Engine.Plugins.Penpot.Models;
using SnapStak.Wasm.Client.Models.Svg;

namespace SnapStak.Wasm.Client.Engine.Plugins.Penpot;

public sealed class PenpotTranslatorPlugin : IConteXTranslatorPlugin
{
    // ── Plugin identity ───────────────────────────────────────────────────────

    public string Key => "penpot";
    public string DisplayName => "Penpot 2.12 (binary .penpot archive)";
    public string Version => "2.0.0";
    public string FileExtension => ".penpot";

    // ── JSON serialiser options ───────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── Phase 1 — declare remote URLs ────────────────────────────────────────

    public IReadOnlyList<string> DeclareResources(TranslatorBundle bundle)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);

        CollectImgUrls(bundle.Desktop.Tree, urls);
        if (bundle.Mobile != null) CollectImgUrls(bundle.Mobile.Tree, urls);
        foreach (var seg in bundle.Segments) CollectImgUrls(seg.Tree, urls);
        foreach (var hc in bundle.HiddenComponents) CollectImgUrls(hc.Tree, urls);

        return urls.ToArray();
    }

    private static void CollectImgUrls(IReadOnlyList<SvgNode> tree, HashSet<string> out_)
    {
        foreach (var node in FlattenTree(tree))
        {
            // Remote image src — needs fetch. We now include remote *.svg URLs
            // (e.g. country flag sprites) because their bytes are decoded and
            // rendered as native Penpot path/rect/circle shapes in Phase 2,
            // just like inline data:image/svg+xml URIs. They do not become
            // fillImage records; they become groups of parsed shapes.
            if (!string.IsNullOrEmpty(node.ImgSrc)
                && (node.ImgSrc.StartsWith("http://") || node.ImgSrc.StartsWith("https://")))
            {
                out_.Add(node.ImgSrc);
            }
            // SvgDataUri is local (data:image/svg+xml;…) — not used in Penpot fillImage
        }
    }

    // ── Phase 2 — translate ───────────────────────────────────────────────────

    public byte[] Translate(
        TranslatorBundle bundle,
        IReadOnlyDictionary<string, byte[]> fetchedResources)
    {
        if (bundle.Desktop.Tree.Count == 0) return Array.Empty<byte>();

        var ctx = new BuildContext(bundle, fetchedResources);
        BuildPages(ctx);

        using var ms = new MemoryStream();
        using var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true);

        WriteManifest(zip, ctx);
        WriteFileMeta(zip, ctx);
        // WriteTokens intentionally NOT called — the verified Plants-app export
        // has no tokens.json at all when there are no design tokens. Emitting an
        // empty {} at files/<file>/tokens.json is non-standard and can trip the
        // binfile-v3 decoder's "resolve pointers to other data fragments" step.
        WritePages(zip, ctx);
        WriteMedia(zip, ctx);
        WriteObjects(zip, ctx);

        zip.Dispose();
        return ms.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Build context — accumulated state while walking the bundle
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class BuildContext
    {
        public readonly TranslatorBundle Bundle;
        public readonly IReadOnlyDictionary<string, byte[]> Fetched;

        // Stable file UUID derived from the component ID
        public readonly string FileId;

        // Pages built during BuildPages()
        public readonly List<PageEntry> Pages = new();

        // Image registrations — populated as shapes are built
        public readonly List<MediaRegistration> Media = new();
        public readonly List<BlobRegistration> Blobs = new();

        // Seen-URL cache so the same image URL → same media UUID
        private readonly Dictionary<string, string> _urlToMediaId =
            new(StringComparer.Ordinal);

        // Horizontal-scroll containers are labelled "Carousel 1", "Carousel 2",
        // etc. on the canvas. Populated at the start of each page build by
        // scanning the SVG tree for overflow-x: auto|scroll nodes. NodeName
        // consults this to override the normal name chain, and MapType
        // consults it to prevent demotion to "group" (a carousel must be a
        // frame so the clip boundary + canvas label apply).
        public readonly Dictionary<string, string> CarouselNames =
            new(StringComparer.Ordinal);

        // Full scrollable width for each detected carousel, so BuildFrame can
        // size the board to include off-viewport cards instead of clipping
        // at the visible viewport slice.
        public readonly Dictionary<string, double> CarouselWidths =
            new(StringComparer.Ordinal);

        public BuildContext(TranslatorBundle bundle, IReadOnlyDictionary<string, byte[]> fetched)
        {
            Bundle = bundle;
            Fetched = fetched;
            FileId = StableUuid($"file/{bundle.ComponentId}");
        }

        /// <summary>
        /// Register an image (remote URL bytes or inline SVG bytes) and return
        /// the media UUID to put in fillImage.id. Idempotent for the same URL.
        /// Returns null if bytes are unavailable (URL fetch failed).
        /// </summary>
        public string? RegisterImage(
            string sourceKey,  // URL or data-URI key
            byte[] bytes,
            string mtype,
            string name,
            int origW, int origH)
        {
            if (_urlToMediaId.TryGetValue(sourceKey, out var existing)) return existing;

            var mediaId = StableUuid($"media/{sourceKey}");
            var blobId = StableUuid($"blob/full/{sourceKey}");
            var thumbId = StableUuid($"blob/thumb/{sourceKey}");

            // Thumbnail — downscale to 256px max dimension
            var (thumbBytes, thumbW, thumbH) = MakeThumbnail(bytes, mtype, origW, origH);

            var ext = MimeToExt(mtype);
            var fullHash = Blake2bHex(bytes);
            var thumbHash = Blake2bHex(thumbBytes);

            Media.Add(new MediaRegistration
            {
                Id = mediaId,
                Name = name,
                Mtype = mtype,
                Width = origW,
                Height = origH,
                MediaId = blobId,
                ThumbnailId = thumbId,
            });

            Blobs.Add(new BlobRegistration
            {
                Id = blobId,
                Ext = ext,
                Hash = fullHash,
                ContentType = mtype,
                Bytes = bytes,
                Bucket = "file-media-object",
            });

            Blobs.Add(new BlobRegistration
            {
                Id = thumbId,
                Ext = ext,
                Hash = thumbHash,
                ContentType = mtype,
                Bytes = thumbBytes,
                Bucket = "file-object-thumbnail",
            });

            _urlToMediaId[sourceKey] = mediaId;
            return mediaId;
        }
    }

    private sealed class PageEntry
    {
        public required string PageId { get; init; }
        public required string PageName { get; init; }
        public required int Index { get; init; }
        public required string Background { get; init; }
        // shape uuid → shape json (root frame + all shapes)
        public readonly Dictionary<string, PenpotShape> Shapes = new();
        // ordered top-level frame UUIDs (root frame's shapes[])
        public readonly List<string> TopLevelFrames = new();
    }

    private sealed class MediaRegistration
    {
        public required string Id, Name, Mtype, MediaId, ThumbnailId;
        public required int Width, Height;
    }

    private sealed class BlobRegistration
    {
        public required string Id, Ext, Hash, ContentType, Bucket;
        public required byte[] Bytes;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Page construction
    // ─────────────────────────────────────────────────────────────────────────

    private static void BuildPages(BuildContext ctx)
    {
        var bundle = ctx.Bundle;
        int idx = 0;

        // Page names are the component title verbatim. The filename convention
        // (presence of "viewport" in the path) is how upstream identifies
        // mobile vs desktop; the page name itself does not need to repeat it,
        // and prefixing with "Desktop — " adds noise to the Penpot canvas
        // without adding information.

        // Desktop master
        var dPage = MakePage(ctx, bundle.Desktop.Title ?? bundle.ComponentId,
                             idx++, bundle.Desktop, "#FFFFFF");
        ctx.Pages.Add(dPage);

        // Desktop segments
        foreach (var seg in bundle.Segments)
        {
            var sp = MakeSegmentPage(ctx, seg.Label, idx++,
                                     seg.Tree, seg.Width, seg.Height);
            ctx.Pages.Add(sp);
        }

        // Mobile master
        if (bundle.Mobile != null)
        {
            var mPage = MakePage(ctx, bundle.Mobile.Title ?? bundle.ComponentId,
                                 idx++, bundle.Mobile, "#FFFFFF");
            ctx.Pages.Add(mPage);

            // Mobile segments (share labels with desktop)
            foreach (var seg in bundle.Segments)
            {
                var sp = MakeSegmentPage(ctx, seg.Label, idx++,
                                         seg.Tree, seg.Width, seg.Height);
                ctx.Pages.Add(sp);
            }
        }

        // Hidden components
        foreach (var hc in bundle.HiddenComponents)
        {
            var hp = MakeSegmentPage(ctx, $"Hidden — {hc.Label}", idx++,
                                     hc.Tree, hc.Width, hc.Height);
            ctx.Pages.Add(hp);
        }
    }

    private static PageEntry MakePage(
        BuildContext ctx, string name, int index,
        TranslatorViewport vp, string background)
    {
        var pageId = StableUuid($"page/{ctx.Bundle.ComponentId}/{index}");
        var entry = new PageEntry
        {
            PageId = pageId,
            PageName = name,
            Index = index,
            Background = background,
        };

        // Root frame (fixed UUID per spec §13.3) — the invisible 0.01×0.01
        // wrapper whose direct children render as top-level boards on the
        // canvas. Penpot labels every direct child of Root Frame.
        var rootFrame = BuildRootFrame(pageId, vp.Width, vp.Height);
        entry.Shapes[PenpotRootFrameId.Value] = rootFrame;

        // Pre-walk: find every horizontal-scroll container and assign it a
        // "Carousel N" label plus its full scrollable width. The numbering
        // is document-order (depth-first), starting at 1. Stored in the
        // build context so NodeName / MapType / BuildFrame can consult it
        // during the main walk. Reset per-page so numbering is page-local.
        ctx.CarouselNames.Clear();
        ctx.CarouselWidths.Clear();
        int carouselCounter = 1;
        foreach (var root in vp.Tree)
            TagCarousels(root, ctx, ref carouselCounter);

        // Page-content board — the main top-level board containing the entire
        // translated SVG tree. Matches the Plants-app pattern where each named
        // screen ("Splash", "Home", "Checkout") is one board with all its
        // content nested inside. Without this wrapper, every top-level
        // SvgNode becomes a direct child of Root Frame and Penpot renders a
        // floating label for each.
        //
        // Page-content board is sized to the full content width when any
        // carousel extends past the viewport, so the horizontal overflow is
        // not clipped by the page board itself.
        double pageBoardWidth = vp.Width;
        foreach (var w in ctx.CarouselWidths.Values)
            if (w > pageBoardWidth) pageBoardWidth = w;

        var pageBoardId = StableUuid($"page-board/{pageId}");
        var pageBoard = BuildPageContentBoard(
            pageId, pageBoardId, name,
            (int)Math.Ceiling(pageBoardWidth), vp.Height, background);
        entry.Shapes[pageBoardId] = pageBoard;

        // Build the SVG tree as children of the page-content board. Each
        // horizontal-scroll container encountered during the walk will emit
        // a frame named "Carousel N" sized to its full scrollable width — in
        // its original tree position, so the page reads correctly as a
        // web-page mockup with the carousel content visible inline.
        var svgTreeIds = new List<string>();
        foreach (var node in vp.Tree)
        {
            var childId = BuildShapeTree(
                ctx, entry, node,
                parentId: pageBoardId,
                enclosingFrameId: pageBoardId,
                pageId: pageId);
            svgTreeIds.Add(childId);
        }
        pageBoard.Shapes = svgTreeIds;

        // Root Frame's only direct child is the page-content board.
        entry.TopLevelFrames.Add(pageBoardId);
        ((PenpotFrame)rootFrame).Shapes = entry.TopLevelFrames;
        return entry;
    }

    // Walk the SVG tree, assign "Carousel N" labels and record the full
    // scrollable width for each horizontal-scroll container. Matches the
    // detection rule used by content.js at line 498: overflowX ∈ {auto,
    // scroll}. Children of a carousel are NOT scanned for inner carousels —
    // a carousel is treated as a self-contained unit.
    private static void TagCarousels(SvgNode node, BuildContext ctx, ref int counter)
    {
        if (IsHorizontalScroller(node))
        {
            ctx.CarouselNames[node.Id] = $"Carousel {counter}";
            ctx.CarouselWidths[node.Id] = MeasureContentWidth(node);
            counter++;
            return;  // don't recurse into a carousel — nested scrollers rare
        }
        foreach (var child in node.Children)
            TagCarousels(child, ctx, ref counter);
    }

    private static bool IsHorizontalScroller(SvgNode node)
    {
        var css = node.CssProps;
        if (css == null || css.Count == 0) return false;

        var ox = css.GetValueOrDefault("overflowX", "");
        if (ox is "auto" or "scroll") return true;

        // overflow shorthand — only treat as horizontal scroll when content
        // actually extends past the node's box width. Vertical-only
        // scrollers (long article text) are excluded.
        var o = css.GetValueOrDefault("overflow", "");
        if (o is "auto" or "scroll")
        {
            double contentRight = 0;
            foreach (var c in node.Children)
                contentRight = Math.Max(contentRight, c.X + c.Width);
            if (contentRight > node.X + node.Width + 1) return true;
        }

        return false;
    }

    // Full scrollable width of a carousel — the rightmost descendant edge
    // relative to the scroller's own left edge. Floor is the scroller's own
    // width (so if content.js did not stamp off-viewport children the frame
    // is at least as wide as the visible clip). Ceiling at 20000 guards
    // against pathological trees.
    private static double MeasureContentWidth(SvgNode scroller)
    {
        double rightmost = scroller.X + scroller.Width;
        WalkForRight(scroller, ref rightmost);
        double width = rightmost - scroller.X;
        if (width < scroller.Width) width = scroller.Width;
        if (width > 20000) width = 20000;
        return width;

        static void WalkForRight(SvgNode n, ref double r)
        {
            foreach (var c in n.Children)
            {
                var edge = c.X + c.Width;
                if (edge > r) r = edge;
                if (c.Children.Count > 0) WalkForRight(c, ref r);
            }
        }
    }

    // Page-content board — the one and only top-level board on the canvas.
    // Sized to the viewport (extended to full content width if any carousel
    // overflows), filled with the page background, named after the page.
    private static PenpotFrame BuildPageContentBoard(
        string pageId, string id, string name,
        int width, int height, string background)
    {
        var f = new PenpotFrame
        {
            Id = id,
            Name = SanitiseShapeName(name),
            PageId = pageId,
            ParentId = PenpotRootFrameId.Value,
            FrameId = PenpotRootFrameId.Value,
            Rx = 0,
            Ry = 0,
            HideFillOnExport = false,
        };
        f.SetGeometry(0, 0, width, height);
        f.SetRadius(0);
        var (hex, opacity) = ParseCssColor(background);
        f.Fills = new List<PenpotFill> { PenpotFill.Solid(hex ?? "#FFFFFF", opacity) };
        return f;
    }

    private static PageEntry MakeSegmentPage(
        BuildContext ctx, string name, int index,
        IReadOnlyList<SvgNode> tree, int w, int h)
    {
        var vp = new TranslatorViewport
        {
            Tree = tree,
            Width = w,
            Height = h,
            SourceUrl = ctx.Bundle.Desktop.SourceUrl,
            Title = name,
        };
        return MakePage(ctx, name, index, vp, "#FFFFFF");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shape tree builder
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recursively build the Penpot shape tree from one SvgNode.
    /// Returns the UUID of the shape created for <paramref name="node"/>.
    /// </summary>
    private static string BuildShapeTree(
        BuildContext ctx,
        PageEntry page,
        SvgNode node,
        string parentId,
        string enclosingFrameId,
        string pageId,
        double parentX = 0,
        double parentY = 0)
    {
        var css = node.CssProps;

        // Translate absolute page coordinates to parent-frame-relative coordinates.
        // SvgNode X/Y are absolute page positions from getBoundingClientRect().
        // Penpot expects child coordinates relative to the enclosing frame origin.
        var relNode = parentX == 0 && parentY == 0 ? node : new SvgNode
        {
            Id = node.Id,
            ParentId = node.ParentId,
            Tag = node.Tag,
            TextContent = node.TextContent,
            ClassName = node.ClassName,
            ComponentType = node.ComponentType,
            Label = node.Label,
            SegmentId = node.SegmentId,
            ImgSrc = node.ImgSrc,
            SvgDataUri = node.SvgDataUri,
            X = node.X - parentX,
            Y = node.Y - parentY,
            Width = node.Width,
            Height = node.Height,
            CssProps = node.CssProps,
            TextLines = node.TextLines,
            TextLineWidths = node.TextLineWidths,
            Children = node.Children,
        };

        // Skip invisible zero-size leaf nodes
        bool hasVisibleContent = node.Width > 0 || node.Height > 0
            || !string.IsNullOrEmpty(node.TextContent)
            || !string.IsNullOrEmpty(node.ImgSrc)
            || !string.IsNullOrEmpty(node.SvgDataUri)
            || node.Children.Count > 0;
        if (!hasVisibleContent)
        {
            // Still need a placeholder — return a transparent zero-size rect
        }

        var shapeId = StableUuid($"shape/{pageId}/{node.Id}");

        // Inline SVG data URIs are converted into native Penpot path/rect/circle
        // shapes so they render in the canvas instead of appearing as empty rects.
        // The node itself becomes a group; children come from parsing the decoded SVG.
        if (!string.IsNullOrEmpty(node.SvgDataUri))
        {
            var group = BuildInlineSvgGroup(
                ctx, page, node, shapeId, parentId, enclosingFrameId, pageId,
                node.SvgDataUri!, isDataUri: true);
            if (group != null)
            {
                page.Shapes[shapeId] = group;
                return shapeId;
            }
            // Parse failed — fall through to the normal rect fallback below so we
            // at least get a named placeholder shape at the right position.
        }

        // Remote .svg URLs (e.g. country flag sprites from a CDN). Their fetched
        // bytes are raw SVG XML, not a raster, so Penpot's fillImage pipeline
        // cannot use them. Route them through the same inline-SVG shape builder
        // the data-URI path uses — decode the bytes as UTF-8 SVG and emit one
        // Penpot path/rect/circle per element.
        if (!string.IsNullOrEmpty(node.ImgSrc)
            && IsRemoteSvgUrl(node.ImgSrc!)
            && ctx.Fetched.TryGetValue(node.ImgSrc!, out var svgBytes)
            && svgBytes != null
            && svgBytes.Length > 0)
        {
            var svgXml = DecodeSvgBytesToXml(svgBytes);
            if (!string.IsNullOrEmpty(svgXml))
            {
                var group = BuildInlineSvgGroup(
                    ctx, page, node, shapeId, parentId, enclosingFrameId, pageId,
                    svgXml!, isDataUri: false);
                if (group != null)
                {
                    page.Shapes[shapeId] = group;
                    return shapeId;
                }
            }
            // Parse failed — fall through to the rect fallback below.
        }

        var penpotType = MapType(ctx, node);

        PenpotShape shape = penpotType switch
        {
            "frame" => BuildFrame(ctx, page, node, shapeId, parentId, enclosingFrameId, pageId, css),
            "text" => BuildText(node, shapeId, parentId, enclosingFrameId, pageId, css),
            "rect" => BuildRect(ctx, node, shapeId, parentId, enclosingFrameId, pageId, css),
            "group" => BuildGroup(ctx, page, node, shapeId, parentId, enclosingFrameId, pageId),
            _ => BuildRect(ctx, node, shapeId, parentId, enclosingFrameId, pageId, css),
        };

        page.Shapes[shapeId] = shape;
        return shapeId;
    }

    // ── Frame ─────────────────────────────────────────────────────────────────

    private static PenpotFrame BuildFrame(
        BuildContext ctx, PageEntry page, SvgNode node,
        string id, string parentId, string enclosingFrameId, string pageId,
        Dictionary<string, string> css)
    {
        // Carousel override: a horizontal-scroll container was tagged in the
        // pre-walk. Use the "Carousel N" name and the full scrollable width
        // so all items (including off-viewport ones stamped by content.js)
        // are visible inside the frame instead of clipped at the viewport
        // edge.
        bool isCarousel = ctx.CarouselNames.TryGetValue(node.Id, out var carouselName);
        double frameWidth = node.Width;
        if (isCarousel && ctx.CarouselWidths.TryGetValue(node.Id, out var fullWidth))
            frameWidth = fullWidth;

        var frame = new PenpotFrame
        {
            Id = id,
            Name = isCarousel ? carouselName! : NodeName(node),
            PageId = pageId,
            ParentId = parentId,
            FrameId = enclosingFrameId,
            Rx = 0,
            Ry = 0,
            HideFillOnExport = false,
            // ShowContent=true whenever this frame or any descendant has content
            // that extends beyond the frame's own width — carousels are the most
            // common case but any horizontally-overflowing container needs this.
            ShowContent = isCarousel || HasOverflowingDescendant(node, ctx),
        };
        frame.SetGeometry(node.X, node.Y, frameWidth, node.Height);
        ApplyRadii(frame, css, frameWidth, node.Height);
        ApplyBackground(frame, css);
        ApplyBorder(frame, css);
        ApplyShadow(frame, css);
        ApplyOpacity(frame, css);

        // Children — note: for a carousel, children's world coordinates
        // already place them correctly within the widened frame because
        // content.js stamps absolute positions during horizontal scroll
        // capture. No coordinate translation needed.
        var childIds = new List<string>();
        foreach (var child in node.Children)
        {
            var childId = BuildShapeTree(ctx, page, child, id, id, pageId);
            childIds.Add(childId);
        }

        frame.Shapes = childIds;
        return frame;
    }

    // ── Rect (also used for images) ───────────────────────────────────────────

    private static PenpotRect BuildRect(
        BuildContext ctx, SvgNode node,
        string id, string parentId, string enclosingFrameId, string pageId,
        Dictionary<string, string> css)
    {
        var rect = new PenpotRect
        {
            Id = id,
            Name = NodeName(node),
            PageId = pageId,
            ParentId = parentId,
            FrameId = enclosingFrameId,
            // Rects emit rx/ry and r1-r4 universally in Plants-app exports, even
            // when the radius is zero. The base-class r1-r4 are nullable so they
            // only serialise on shape types that explicitly set them; paths,
            // groups, text, and frames omit them (Penpot rejects files where
            // non-rect shapes carry per-corner radii). Init to 0 here so rects
            // still emit all four corner radii. ApplyRadii may override.
            Rx = 0,
            Ry = 0,
            R1 = 0,
            R2 = 0,
            R3 = 0,
            R4 = 0,
        };
        rect.SetGeometry(node.X, node.Y, node.Width, node.Height);
        ApplyRadii(rect, css, node.Width, node.Height);
        ApplyBackground(rect, css);
        ApplyBorder(rect, css);
        ApplyShadow(rect, css);
        ApplyOpacity(rect, css);

        // Image fill — remote/base64 raster. Inline SVG data URIs are handled
        // upstream in BuildShapeTree by BuildInlineSvgGroup, which emits native
        // Penpot path/rect/circle shapes instead of the empty rect that a failed
        // image fill would leave behind.
        if (!string.IsNullOrEmpty(node.ImgSrc))
        {
            var imgFill = ResolveImage(ctx, node);
            if (imgFill != null) rect.Fills = new List<PenpotFill> { imgFill };
        }

        return rect;
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    private static PenpotText BuildText(
        SvgNode node,
        string id, string parentId, string enclosingFrameId, string pageId,
        Dictionary<string, string> css)
    {
        // Single-line text nodes use "auto-width" so Penpot does not wrap them.
        // All other nodes use "fixed" so the shape stays within its browser-measured
        // bounds and does not expand into the gap between this shape and the next.
        // "auto-height" was tried but caused headings to expand downward into the
        // gap below them when Penpot's font metrics differ slightly from Chrome's.
        var fontSize = ParsePx(css.GetValueOrDefault("fontSize", "14px"), 14);
        // Source of truth: content.js only populates TextLines when the browser
        // detected 2 or more lines via Range measurement. If TextLines is null,
        // the text is a single line — regardless of container height.
        // Centred single-line text uses "fixed" so Penpot centres within the
        // container width. Left-aligned single-line uses "auto-width".
        bool isSingleLine = node.TextLines == null || node.TextLines.Count <= 1;
        var textAlignForGrow = css.GetValueOrDefault("textAlign", "");
        bool isCentred = textAlignForGrow == "center";
        string growType = !isSingleLine ? "fixed" : isCentred ? "fixed" : "auto-width";
        var text = new PenpotText
        {
            Id = id,
            Name = TextShapeName(node),
            PageId = pageId,
            ParentId = parentId,
            FrameId = enclosingFrameId,
            GrowType = growType,
        };
        text.SetGeometry(node.X, node.Y,
            node.Width > 0 ? node.Width : 200,
            node.Height > 0 ? node.Height : 24);

        ApplyOpacity(text, css);

        var textValue = node.TextContent?.Trim() ?? string.Empty;
        var textTransform = css.GetValueOrDefault("textTransform", "none");
        if (textTransform == "uppercase") textValue = textValue.ToUpperInvariant();
        else if (textTransform == "lowercase") textValue = textValue.ToLowerInvariant();

        var color = CssColorToHex(css.GetValueOrDefault("color", "#000000"));
        var fontWeight = css.GetValueOrDefault("fontWeight", "400");
        var fontFamily = SanitiseFontFamily(css.GetValueOrDefault("fontFamily", "sourcesanspro"));
        var fontId = FontId(fontFamily);
        var fontVariant = FontVariantId(fontWeight, css.GetValueOrDefault("fontStyle", "normal"));
        var textAlign = css.GetValueOrDefault("textAlign", "left");
        var lineHeight = ParseLineHeight(css.GetValueOrDefault("lineHeight", ""), fontSize);
        var letterSp = ParseLetterSpacing(css.GetValueOrDefault("letterSpacing", "0"));
        var vAlign = css.GetValueOrDefault("verticalAlign", "");
        var alignItems = css.GetValueOrDefault("alignItems", "");
        var penpotVAlign = (vAlign == "middle" || alignItems == "center") ? "center"
                         : (vAlign == "bottom" || alignItems == "flex-end") ? "bottom"
                         : "top";

        var fill = PenpotFill.Solid(color);
        var fontStyle = css.GetValueOrDefault("fontStyle", "normal");
        var textDec = CssTextDecoration(css);

        // Use the full text as a single paragraph with a single leaf.
        // One-paragraph-per-line causes Penpot to add extra inter-paragraph
        // spacing on top of the line height, producing gaps that do not match
        // the SVG layout. A single paragraph with the full text and fixed
        // geometry matches the SVG spacing exactly.
        var paragraph = new PenpotParagraph
        {
            Key = RandomKey(),
            FontFamily = fontFamily,
            FontSize = fontSize.ToString("0"),
            FontWeight = fontWeight,
            FontStyle = fontStyle,
            TextAlign = textAlign,
            TextTransform = "none",
            TextDecoration = textDec,
            LetterSpacing = letterSp,
            LineHeight = lineHeight,
            FontId = fontId,
            FontVariantId = fontVariant,
            Fills = new List<PenpotFill> { fill },
            Children = new List<PenpotTextLeaf>
            {
                new PenpotTextLeaf
                {
                    Text = textValue,
                    Direction = "ltr",
                    FontFamily = fontFamily,
                    FontSize = fontSize.ToString("0"),
                    FontWeight = fontWeight,
                    FontStyle = fontStyle,
                    FontId = fontId,
                    FontVariantId = fontVariant,
                    Fills = new List<PenpotFill> { fill },
                }
            },
        };

        text.Content = new PenpotTextRoot
        {
            VerticalAlign = penpotVAlign,
            Fills = new List<PenpotFill>(),
            Children = new List<PenpotParagraphSet>
            {
                new() { Children = new List<PenpotParagraph> { paragraph } },
            },
        };
        return text;
    }

    // ── Group ─────────────────────────────────────────────────────────────────

    private static PenpotGroup BuildGroup(
        BuildContext ctx, PageEntry page, SvgNode node,
        string id, string parentId, string enclosingFrameId, string pageId)
    {
        var group = new PenpotGroup
        {
            Id = id,
            Name = NodeName(node),
            PageId = pageId,
            ParentId = parentId,
            FrameId = enclosingFrameId,
        };
        group.SetGeometry(node.X, node.Y, node.Width, node.Height);

        var childIds = new List<string>();
        foreach (var child in node.Children)
        {
            var childId = BuildShapeTree(ctx, page, child, id, enclosingFrameId, pageId);
            childIds.Add(childId);
        }
        group.Shapes = childIds;
        return group;
    }

    // ── Root frame (fixed UUID, 0.01×0.01 per spec §13.3) ────────────────────

    private static PenpotFrame BuildRootFrame(string pageId, int vpWidth, int vpHeight)
    {
        var f = new PenpotFrame
        {
            Id = PenpotRootFrameId.Value,
            Name = "Root Frame",
            PageId = pageId,
            ParentId = PenpotRootFrameId.Value,
            FrameId = PenpotRootFrameId.Value,
            HideFillOnExport = false,
            // FlipX / FlipY are left at their default null and emitted as
            // "flipX": null / "flipY": null — see PenpotShape (model forces
            // their serialization with JsonIgnoreCondition.Never).
        };
        // Convention: width 0.01, height 0.01 per verified export.
        f.SetGeometry(0, 0, 0.01, 0.01);
        // Root frame must carry r1=r2=r3=r4=0 explicitly. Both Plants-app and
        // the Penpot reference export (Prototype_examples_v1_2) emit these on
        // the 00000000-... shape. Without them, Penpot's import validator
        // rejects the file with "Not all files have been imported". Non-root
        // frames optionally carry them; rects always do; other types never do.
        f.SetRadius(0);
        f.Fills = new List<PenpotFill> { PenpotFill.Solid("#FFFFFF") };
        return f;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Image resolution helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static PenpotFill? ResolveImage(BuildContext ctx, SvgNode node)
    {
        var url = node.ImgSrc!;
        byte[]? bytes;
        if (url.StartsWith("data:"))
        {
            bytes = DecodeDataUri(url, out var mtype);
            if (bytes == null) return null;
            // Prefer byte sniffing over the data URI's declared mtype — the
            // header may lie, and Penpot rejects image/webp either way.
            var sniffed = SniffMime(bytes);
            if (!string.IsNullOrEmpty(sniffed)) mtype = sniffed;
            if (mtype == "image/webp") return null;   // reject — Penpot unsupported
            var (w, h) = EstimateImageDimensions(node);
            var mediaId = ctx.RegisterImage(url, bytes, mtype, node.Label ?? "image", w, h);
            if (mediaId == null) return null;
            return PenpotFill.FromImage(new PenpotFillImage
            {
                Id = mediaId,
                Name = node.Label ?? "image",
                Mtype = mtype,
                Width = w,
                Height = h,
                KeepAspectRatio = true,
            });
        }
        else
        {
            if (!ctx.Fetched.TryGetValue(url, out bytes)) return null;
            // Sniff actual bytes first — the server's WebP→JPEG conversion handler
            // rewrites response bytes without touching URLs, so the URL may end in
            // ".webp" while bytes are JPEG. Trusting the URL would produce a media
            // record whose mtype lies about the content, and Penpot rejects that
            // on import. Fall back to URL-based guessing only if sniff returns empty.
            var mtype = SniffMime(bytes);
            if (string.IsNullOrEmpty(mtype)) mtype = GuessMime(url);
            // SVG not supported as Penpot fillImage — skip, fall back to background colour
            if (string.IsNullOrEmpty(mtype)) return null;
            // Penpot also rejects image/webp as a fillImage source type. If WebP
            // bytes somehow reached us unconverted (handler bypassed, decode failed),
            // refuse to register them so the shape falls back to its background.
            if (mtype == "image/webp") return null;
            var (w, h) = EstimateImageDimensions(node);
            var mediaId = ctx.RegisterImage(url, bytes, mtype, node.Label ?? "image", w, h);
            if (mediaId == null) return null;
            return PenpotFill.FromImage(new PenpotFillImage
            {
                Id = mediaId,
                Name = node.Label ?? "image",
                Mtype = mtype,
                Width = w,
                Height = h,
                KeepAspectRatio = false,
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inline SVG → native Penpot shapes
    // ─────────────────────────────────────────────────────────────────────────
    //
    // The CON10X engine emits inline <image href="data:image/svg+xml,…"> URIs
    // for icons and logos. Penpot does NOT accept image/svg+xml as a raster
    // fillImage (only PNG/JPEG/WebP work). So instead of trying to embed the
    // SVG as an image, we DECODE the data URI, parse the inner <svg>, and emit
    // one Penpot shape per <path>/<rect>/<circle>/<g>, wrapped in a PenpotGroup.
    //
    // This is what Penpot's own SVG-import does — every shape in the Plants-app
    // reference export that originated from an SVG is a native path/circle/
    // rect/group with its geometry in the .content / selrect fields.

    /// <summary>
    /// Build a PenpotGroup whose children are the parsed path/rect/circle
    /// shapes from raw SVG XML. All children are registered in
    /// <paramref name="page"/>.Shapes and the group's shapes[] is populated
    /// with their UUIDs. Returns null on parse failure.
    ///
    /// The SVG source can come from either an inline data:image/svg+xml URI
    /// (logos/icons emitted by the CON10X engine) or from remote .svg bytes
    /// (country flag sprites, CDN-hosted vector assets). Both paths behave
    /// identically here — Penpot only accepts raster fillImage sources, so
    /// every SVG must be flattened to native shape primitives regardless
    /// of origin.
    /// </summary>
    private static PenpotGroup? BuildInlineSvgGroup(
        BuildContext ctx,
        PageEntry page,
        SvgNode node,
        string groupId,
        string parentId,
        string enclosingFrameId,
        string pageId,
        string source,
        bool isDataUri)
    {
        string? innerSvg;
        if (isDataUri)
        {
            var decoded = DecodeInlineSvg(source);
            innerSvg = decoded.xml;
        }
        else
        {
            innerSvg = source;
        }
        if (string.IsNullOrEmpty(innerSvg)) return null;

        var (vbX, vbY, vbW, vbH) = ParseSvgViewBox(innerSvg, node.Width, node.Height);
        if (vbW <= 0 || vbH <= 0) return null;

        double scaleX = node.Width > 0 ? node.Width / vbW : 1;
        double scaleY = node.Height > 0 ? node.Height / vbH : 1;

        // Mapping from SVG-local (vbX, vbY, vbW, vbH) → canvas (node.X … node.X + node.Width)
        // canvas_x = node.X + (svg_x - vbX) * scaleX
        // canvas_y = node.Y + (svg_y - vbY) * scaleY
        //
        // We apply this via Penpot's svgTransform matrix on each emitted shape,
        // so the raw path "d" strings stay readable and we don't have to
        // re-emit every path's coordinates.
        var svgTransform = new PenpotMatrix
        {
            A = scaleX,
            B = 0,
            C = 0,
            D = scaleY,
            E = node.X - vbX * scaleX,
            F = node.Y - vbY * scaleY,
        };

        var group = new PenpotGroup
        {
            Id = groupId,
            Name = node.Label ?? "svg",
            PageId = pageId,
            ParentId = parentId,
            FrameId = enclosingFrameId,
            Shapes = new List<string>(),
        };
        group.SetGeometry(node.X, node.Y,
            node.Width > 0 ? node.Width : 1,
            node.Height > 0 ? node.Height : 1);

        // Extract CSS class → fill colour map BEFORE stripping <defs>.
        // The CON10X engine's race-center logo (and many CDN-hosted flag SVGs)
        // paint their <path> elements through class selectors declared in a
        // <style> block inside <defs>, not through inline fill="…" attributes.
        // Without this map, every path would fall through to the SVG default
        // fill (black) in ApplyInheritedPaint, which on a dark Penpot canvas
        // renders as invisible silhouettes — matching the empty-logo bug we saw
        // in the Penpot import of autosport.com.
        var classFills = ExtractCssClassFills(innerSvg);

        // Parse SVG content body — strip outer <svg>, <defs>, <filter> tags.
        var body = StripSvgWrapper(innerSvg);

        int childIndex = 0;
        foreach (var el in EnumerateSvgElements(body, inheritedFill: null, inheritedStroke: null,
                                                inheritedOpacity: 1.0, classFills))
        {
            var childId = StableUuid($"shape/{pageId}/{node.Id}/svgchild/{childIndex++}");
            PenpotShape? child = el.Tag switch
            {
                "path" => BuildSvgPath(el, childId, groupId, enclosingFrameId, pageId, svgTransform, node, vbX, vbY, vbW, vbH, scaleX, scaleY),
                "rect" => BuildSvgRect(el, childId, groupId, enclosingFrameId, pageId, svgTransform, node, scaleX, scaleY),
                "circle" => BuildSvgCircle(el, childId, groupId, enclosingFrameId, pageId, svgTransform, node, scaleX, scaleY),
                _ => null,
            };
            if (child == null) continue;
            page.Shapes[childId] = child;
            group.Shapes.Add(childId);
        }

        // If nothing parsed, fall back to null so the caller uses a placeholder rect.
        if (group.Shapes.Count == 0) return null;
        return group;
    }

    /// <summary>
    /// True if the URL points at a remote .svg asset that should be fetched
    /// and decoded into native Penpot shapes instead of embedded as a raster
    /// fillImage. Query strings and fragments are tolerated.
    /// </summary>
    private static bool IsRemoteSvgUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        var path = url;
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        var h = path.IndexOf('#');
        if (h >= 0) path = path[..h];
        return path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decode fetched SVG bytes into a UTF-8 XML string, stripping a UTF-8 BOM
    /// if present. Returns null if the bytes are empty or look binary (e.g. the
    /// CDN returned a PNG under an .svg URL, which does happen).
    /// </summary>
    private static string? DecodeSvgBytesToXml(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        // Simple sanity check: real SVG content contains the string "<svg"
        // within the first kilobyte. Anything else is not usable here.
        int start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            start = 3;
        string xml;
        try
        {
            xml = Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }
        catch
        {
            return null;
        }
        var probe = xml.Length > 1024 ? xml[..1024] : xml;
        if (probe.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) < 0) return null;
        return xml;
    }

    /// <summary>
    /// Extract a best-effort map of CSS class name → fill colour from any
    /// &lt;style&gt; blocks in the SVG (typically nested inside &lt;defs&gt;).
    /// Only the "fill:" declaration is captured; other properties are ignored
    /// because Penpot's path shape model carries fill as its sole paint
    /// property. Class names are stored without the leading dot. Works on both
    /// single-class rules (".cls-1 { fill: #ed1a4c; }") and comma-separated
    /// selector lists (".a, .b { fill: red; }"). CDATA wrappers are tolerated.
    /// </summary>
    private static Dictionary<string, string> ExtractCssClassFills(string svgXml)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(svgXml)) return map;

        // Pull every <style …>…</style> block. Content may be wrapped in
        // <![CDATA[ … ]]> or plain text — handle both.
        foreach (Match styleMatch in Regex.Matches(svgXml,
            @"<style\b[^>]*>(?<body>.*?)</style\s*>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var body = styleMatch.Groups["body"].Value;
            // Drop CDATA wrappers if present.
            body = Regex.Replace(body, @"<!\[CDATA\[", "", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, @"\]\]>", "", RegexOptions.IgnoreCase);

            // Match one or more class selectors followed by a declaration block.
            // Example matches:
            //   .cls-1 { fill: #ed1a4c; }
            //   .cls-1, .cls-2 { fill: rgb(240, 240, 240); }
            foreach (Match ruleMatch in Regex.Matches(body,
                @"(?<sel>\.[A-Za-z_][-\w]*(?:\s*,\s*\.[A-Za-z_][-\w]*)*)\s*\{(?<decl>[^}]*)\}",
                RegexOptions.Singleline))
            {
                var decl = ruleMatch.Groups["decl"].Value;
                var fillMatch = Regex.Match(decl,
                    @"fill\s*:\s*(?<val>[^;]+?)\s*(?:;|$)",
                    RegexOptions.IgnoreCase);
                if (!fillMatch.Success) continue;
                var fillValue = fillMatch.Groups["val"].Value.Trim();
                if (string.IsNullOrEmpty(fillValue)) continue;

                // Split selector list, strip leading dot, store each class.
                foreach (var rawSel in ruleMatch.Groups["sel"].Value.Split(','))
                {
                    var sel = rawSel.Trim();
                    if (sel.Length < 2 || sel[0] != '.') continue;
                    var cls = sel[1..];
                    // Last rule wins on duplicate — matches CSS cascade for
                    // equal-specificity rules in source order.
                    map[cls] = fillValue;
                }
            }
        }
        return map;
    }

    /// <summary>
    /// Decode a data:image/svg+xml[;base64],… URI into its raw SVG XML string.
    /// Returns (xml, mtype) or (null, null) on failure.
    /// </summary>
    private static (string? xml, string? mtype) DecodeInlineSvg(string dataUri)
    {
        try
        {
            if (!dataUri.StartsWith("data:", StringComparison.Ordinal))
                return (null, null);
            var comma = dataUri.IndexOf(',');
            if (comma < 0) return (null, null);
            var meta = dataUri[5..comma];
            var parts = meta.Split(';');
            var mtype = parts.Length > 0 ? parts[0] : "image/svg+xml";
            var isBase64 = parts.Any(p => string.Equals(p, "base64", StringComparison.Ordinal));
            var payload = dataUri[(comma + 1)..];
            var xml = isBase64
                ? Encoding.UTF8.GetString(Convert.FromBase64String(payload))
                : Uri.UnescapeDataString(payload);
            return (xml, mtype);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Parse viewBox (or width/height) from the outer <svg> element. Falls back
    /// to the node's canvas dimensions if no viewBox is declared.
    /// </summary>
    private static (double x, double y, double w, double h) ParseSvgViewBox(
        string svgXml, double fallbackW, double fallbackH)
    {
        var vb = Regex.Match(svgXml,
            @"<svg\b[^>]*\bviewBox\s*=\s*""\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*""",
            RegexOptions.IgnoreCase);
        if (vb.Success)
        {
            return (
                double.Parse(vb.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(vb.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(vb.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(vb.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        // Fall back to outer width/height attrs (percentages ignored — assume full)
        var w = ParseAttrPx(svgXml, "width", fallbackW > 0 ? fallbackW : 24);
        var h = ParseAttrPx(svgXml, "height", fallbackH > 0 ? fallbackH : 24);
        return (0, 0, w, h);
    }

    /// <summary>Strip the outer <svg> wrapper and any <defs>/<filter> blocks
    /// so the remaining string is just a sequence of child elements.</summary>
    private static string StripSvgWrapper(string svgXml)
    {
        var s = svgXml;
        s = Regex.Replace(s, @"<\?xml[^>]*\?>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<!DOCTYPE[^>]*>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<defs\b[^>]*>.*?</defs\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<filter\b[^>]*>.*?</filter\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Remove outer <svg …> opener and matching </svg>.
        s = Regex.Replace(s, @"^\s*<svg\b[^>]*>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</svg\s*>\s*$", "", RegexOptions.IgnoreCase);
        return s;
    }

    /// <summary>
    /// One SVG element extracted from the tree. Flattens <g> wrappers so each
    /// leaf carries the inherited fill/stroke/opacity from its ancestors.
    /// </summary>
    private sealed class SvgElement
    {
        public string Tag = "";
        public string Raw = "";                                       // full opening tag
        public Dictionary<string, string> Attrs = new(StringComparer.OrdinalIgnoreCase);
        public string? InheritedFill;
        public string? InheritedStroke;
        public double InheritedOpacity = 1.0;
    }

    /// <summary>
    /// Walk the SVG body, yielding every &lt;path&gt;/&lt;rect&gt;/&lt;circle&gt; element with its
    /// effective (inherited) fill/stroke/opacity from ancestor &lt;g&gt; wrappers.
    /// <paramref name="classFills"/> maps CSS class names (without the leading
    /// dot) to the raw CSS fill value declared in a &lt;style&gt; block. When an
    /// element carries class="…", its effective fill is resolved by consulting
    /// this map; an explicit fill="…" attribute on the element still wins.
    /// </summary>
    private static IEnumerable<SvgElement> EnumerateSvgElements(
        string body, string? inheritedFill, string? inheritedStroke, double inheritedOpacity,
        IReadOnlyDictionary<string, string> classFills)
    {
        // Walk tokens in order. For each <g …> open, push its attrs onto the
        // inheritance stack; for </g>, pop. For leaf <path|rect|circle …/>,
        // emit a merged SvgElement.
        var tokenRe = new Regex(
            @"<(?<tag>g|path|rect|circle|ellipse|line|polyline|polygon|use|image|text|tspan)\b(?<attrs>[^>]*?)(?<self>/)?>|</g\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var fillStack = new Stack<string?>(); fillStack.Push(inheritedFill);
        var strokeStack = new Stack<string?>(); strokeStack.Push(inheritedStroke);
        var opacityStack = new Stack<double>(); opacityStack.Push(inheritedOpacity);

        // Resolve any CSS classes on an element/group into a concrete fill
        // value. Returns null if no class matches. The first class whose
        // name is present in classFills wins — matches the single-fill
        // semantics we model here.
        string? ResolveClassFill(Dictionary<string, string> attrs)
        {
            if (classFills.Count == 0) return null;
            var classAttr = attrs.GetValueOrDefault("class", "");
            if (string.IsNullOrWhiteSpace(classAttr)) return null;
            foreach (var cls in classAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (classFills.TryGetValue(cls, out var fill) && !string.IsNullOrEmpty(fill))
                    return fill;
            }
            return null;
        }

        foreach (Match m in tokenRe.Matches(body))
        {
            if (m.Value.StartsWith("</g", StringComparison.OrdinalIgnoreCase))
            {
                if (fillStack.Count > 1) fillStack.Pop();
                if (strokeStack.Count > 1) strokeStack.Pop();
                if (opacityStack.Count > 1) opacityStack.Pop();
                continue;
            }

            var tag = m.Groups["tag"].Value.ToLowerInvariant();
            var attrs = ParseXmlAttrs(m.Groups["attrs"].Value);
            var self = m.Groups["self"].Success;

            if (tag == "g")
            {
                // Push inheritance — a <g fill="..." stroke="..." opacity="...">
                // applies those values to descendants unless they override.
                // A <g class="cls-2"> where .cls-2 { fill: … } is equivalent
                // to <g fill="…"> for inheritance purposes.
                var inlineFill = attrs.GetValueOrDefault("fill", "");
                var resolvedFill = !string.IsNullOrEmpty(inlineFill)
                    ? inlineFill
                    : ResolveClassFill(attrs) ?? fillStack.Peek();
                fillStack.Push(resolvedFill);
                strokeStack.Push(attrs.GetValueOrDefault("stroke", strokeStack.Peek()!));
                var opStr = attrs.GetValueOrDefault("opacity", "");
                var op = opacityStack.Peek();
                if (double.TryParse(opStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var opParsed))
                    op *= Math.Clamp(opParsed, 0, 1);
                opacityStack.Push(op);
                // Self-closing <g/> is odd but handle it gracefully.
                if (self)
                {
                    fillStack.Pop(); strokeStack.Pop(); opacityStack.Pop();
                }
                continue;
            }

            // Leaf shape-emitting tags we care about.
            if (tag == "path" || tag == "rect" || tag == "circle")
            {
                // If the element has no inline fill= but does carry a class
                // with a fill rule, promote that fill into the element's
                // attribute dictionary so ApplyInheritedPaint picks it up
                // without needing to re-parse the class attribute itself.
                if (!attrs.ContainsKey("fill"))
                {
                    var classFill = ResolveClassFill(attrs);
                    if (!string.IsNullOrEmpty(classFill))
                        attrs["fill"] = classFill!;
                }
                yield return new SvgElement
                {
                    Tag = tag,
                    Raw = m.Value,
                    Attrs = attrs,
                    InheritedFill = fillStack.Peek(),
                    InheritedStroke = strokeStack.Peek(),
                    InheritedOpacity = opacityStack.Peek(),
                };
            }
            // Other tags (ellipse, line, poly*, use, image, text, tspan) skipped
            // for v1 — rare in CON10X icons and logos.
        }
    }

    private static Dictionary<string, string> ParseXmlAttrs(string attrBlock)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(attrBlock, @"([a-zA-Z_:][-\w:.]*)\s*=\s*""([^""]*)""|([a-zA-Z_:][-\w:.]*)\s*=\s*'([^']*)'"))
        {
            var k = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
            var v = m.Groups[1].Success ? m.Groups[2].Value : m.Groups[4].Value;
            dict[k] = v;
        }
        return dict;
    }

    // ── Leaf shape builders — SVG element → Penpot shape ──────────────────────

    private static PenpotPath? BuildSvgPath(
        SvgElement el, string id, string parentId, string frameId, string pageId,
        PenpotMatrix svgTransform, SvgNode parentNode,
        double vbX, double vbY, double vbW, double vbH,
        double scaleX, double scaleY)
    {
        var dOrig = el.Attrs.GetValueOrDefault("d", "").Trim();
        if (string.IsNullOrEmpty(dOrig)) return null;

        // Transform the path's d string from SVG-local coords → canvas absolute coords.
        // Plants-app's exported paths carry their content IN CANVAS SPACE with svgTransform
        // = identity. Penpot's renderer draws `content` directly; it does NOT composite
        // svgTransform into the draw. Emitting local coords + a non-identity svgTransform
        // causes paths to render at the wrong position (typically clustered at the canvas
        // origin, invisible because the root frame is 0.01×0.01).
        double tX = parentNode.X - vbX * scaleX;
        double tY = parentNode.Y - vbY * scaleY;
        var dCanvas = TransformSvgPath(dOrig, tX, tY, scaleX, scaleY);

        var path = new PenpotPath
        {
            Id = id,
            Name = "svg-path",
            PageId = pageId,
            ParentId = parentId,
            FrameId = frameId,
            Content = dCanvas,
            SvgAttrs = new Dictionary<string, string>(),
            // Identity matrix — path content is already baked in canvas space.
            SvgTransform = PenpotMatrix.Identity,
        };

        // Bounding box of the transformed path (canvas coords).
        var (cbX, cbY, cbW, cbH) = ComputePathBoundsLocal(dCanvas);
        if (cbW <= 0 || cbH <= 0)
        {
            // Unreadable path — bound it to the parent node's box.
            cbX = parentNode.X; cbY = parentNode.Y;
            cbW = parentNode.Width > 0 ? parentNode.Width : 1;
            cbH = parentNode.Height > 0 ? parentNode.Height : 1;
        }
        path.Selrect = PenpotSelrect.FromBox(cbX, cbY, cbW, cbH);
        path.Points = new List<PenpotPoint>
        {
            new() { X = cbX,       Y = cbY },
            new() { X = cbX + cbW, Y = cbY },
            new() { X = cbX + cbW, Y = cbY + cbH },
            new() { X = cbX,       Y = cbY + cbH },
        };

        // svgViewbox records the SVG-local source bounds (not canvas) — used as a
        // reference by Penpot's SVG export/re-import path. Not used for rendering.
        var (pbX, pbY, pbW, pbH) = ComputePathBoundsLocal(dOrig);
        if (pbW <= 0 || pbH <= 0) { pbX = vbX; pbY = vbY; pbW = vbW; pbH = vbH; }
        path.SvgViewbox = PenpotSelrect.FromBox(pbX, pbY, pbW, pbH);

        ApplyInheritedPaint(path, el);
        return path;
    }

    /// <summary>
    /// Rewrite an SVG path "d" string with a translate + per-axis scale baked in.
    /// Every coordinate is converted to absolute canvas space; relative commands
    /// are expanded into their absolute forms so the output does not depend on
    /// hidden prior-point state.
    ///
    /// Handles M/L/H/V/C/S/Q/T/A/Z in both absolute and relative forms.
    /// Preserves arc flags (large-arc, sweep) unmodified. Arc radii are scaled
    /// proportionally when scaleX == scaleY; non-uniform scale of arcs is not
    /// exact but visually negligible for icon content.
    /// </summary>
    private static string TransformSvgPath(
        string d, double tx, double ty, double sx, double sy)
    {
        var tokens = Regex.Matches(d,
            @"([MmLlHhVvCcSsQqTtAaZz])|(-?\d*\.?\d+(?:[eE][-+]?\d+)?)");
        var sb = new StringBuilder();
        double cx = 0, cy = 0, startX = 0, startY = 0;
        bool firstM = true;

        // Format a coordinate compactly — InvariantCulture to avoid locale comma decimals.
        string F(double v) => v.ToString("0.######",
            System.Globalization.CultureInfo.InvariantCulture);

        char cmd = 'M';
        int i = 0;
        double NextNum()
        {
            while (i < tokens.Count && !tokens[i].Groups[2].Success) i++;
            if (i >= tokens.Count) return 0;
            var v = double.Parse(tokens[i].Groups[2].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            i++;
            return v;
        }

        // Map local → canvas.
        double MapX(double x) => x * sx + tx;
        double MapY(double y) => y * sy + ty;

        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Groups[1].Success)
            {
                cmd = t.Groups[1].Value[0];
                i++;
                if (char.ToUpperInvariant(cmd) == 'Z')
                {
                    sb.Append('Z');
                    cx = startX; cy = startY;
                    continue;
                }
            }

            bool rel = char.IsLower(cmd);
            char up = char.ToUpperInvariant(cmd);

            switch (up)
            {
                case 'M':
                case 'L':
                case 'T':
                    {
                        double x = NextNum(), y = NextNum();
                        if (rel && !firstM) { x += cx; y += cy; }
                        cx = x; cy = y;
                        if (up == 'M') { startX = x; startY = y; firstM = false; }

                        sb.Append(up);
                        sb.Append(F(MapX(x))).Append(',').Append(F(MapY(y)));

                        // After an M, subsequent coord pairs are implicit L (SVG spec).
                        if (up == 'M') cmd = rel ? 'l' : 'L';
                        break;
                    }
                case 'H':
                    {
                        double x = NextNum();
                        if (rel) x += cx;
                        cx = x;
                        // Emit as absolute L so we don't have to track axis-specific scaling
                        // expectations from downstream renderers.
                        sb.Append('L').Append(F(MapX(x))).Append(',').Append(F(MapY(cy)));
                        break;
                    }
                case 'V':
                    {
                        double y = NextNum();
                        if (rel) y += cy;
                        cy = y;
                        sb.Append('L').Append(F(MapX(cx))).Append(',').Append(F(MapY(y)));
                        break;
                    }
                case 'C':
                    {
                        double x1 = NextNum(), y1 = NextNum();
                        double x2 = NextNum(), y2 = NextNum();
                        double x = NextNum(), y = NextNum();
                        if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                        cx = x; cy = y;
                        sb.Append('C')
                          .Append(F(MapX(x1))).Append(',').Append(F(MapY(y1))).Append(' ')
                          .Append(F(MapX(x2))).Append(',').Append(F(MapY(y2))).Append(' ')
                          .Append(F(MapX(x))).Append(',').Append(F(MapY(y)));
                        break;
                    }
                case 'S':
                    {
                        double x2 = NextNum(), y2 = NextNum();
                        double x = NextNum(), y = NextNum();
                        if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                        cx = x; cy = y;
                        sb.Append('S')
                          .Append(F(MapX(x2))).Append(',').Append(F(MapY(y2))).Append(' ')
                          .Append(F(MapX(x))).Append(',').Append(F(MapY(y)));
                        break;
                    }
                case 'Q':
                    {
                        double x1 = NextNum(), y1 = NextNum();
                        double x = NextNum(), y = NextNum();
                        if (rel) { x1 += cx; y1 += cy; x += cx; y += cy; }
                        cx = x; cy = y;
                        sb.Append('Q')
                          .Append(F(MapX(x1))).Append(',').Append(F(MapY(y1))).Append(' ')
                          .Append(F(MapX(x))).Append(',').Append(F(MapY(y)));
                        break;
                    }
                case 'A':
                    {
                        double rx = NextNum(), ry = NextNum();
                        double xRot = NextNum();
                        double large = NextNum(), sweep = NextNum();
                        double x = NextNum(), y = NextNum();
                        if (rel) { x += cx; y += cy; }
                        cx = x; cy = y;
                        // Scale the radii. Exact transformation of arcs under non-uniform
                        // scale requires re-computing the ellipse — for icons, sx==sy
                        // virtually always, and the scaled radii approximation is fine.
                        double rxS = rx * Math.Abs(sx);
                        double ryS = ry * Math.Abs(sy);
                        sb.Append('A')
                          .Append(F(rxS)).Append(',').Append(F(ryS)).Append(' ')
                          .Append(F(xRot)).Append(' ')
                          .Append((int)large).Append(',').Append((int)sweep).Append(' ')
                          .Append(F(MapX(x))).Append(',').Append(F(MapY(y)));
                        break;
                    }
                default:
                    i++;
                    break;
            }
        }

        return sb.ToString();
    }

    private static PenpotRect? BuildSvgRect(
        SvgElement el, string id, string parentId, string frameId, string pageId,
        PenpotMatrix svgTransform, SvgNode parentNode,
        double scaleX, double scaleY)
    {
        double x = ParseSvgNumber(el.Attrs.GetValueOrDefault("x", "0"));
        double y = ParseSvgNumber(el.Attrs.GetValueOrDefault("y", "0"));
        double w = ParseSvgNumber(el.Attrs.GetValueOrDefault("width", "0"));
        double h = ParseSvgNumber(el.Attrs.GetValueOrDefault("height", "0"));
        if (w <= 0 || h <= 0) return null;

        double canvasX = parentNode.X + x * scaleX;
        double canvasY = parentNode.Y + y * scaleY;
        double canvasW = w * scaleX;
        double canvasH = h * scaleY;

        var rect = new PenpotRect
        {
            Id = id,
            Name = "svg-rect",
            PageId = pageId,
            ParentId = parentId,
            FrameId = frameId,
            Rx = 0,
            Ry = 0,
            R1 = 0,
            R2 = 0,
            R3 = 0,
            R4 = 0,
        };
        rect.SetGeometry(canvasX, canvasY, canvasW, canvasH);
        ApplyInheritedPaint(rect, el);
        return rect;
    }

    private static PenpotCircle? BuildSvgCircle(
        SvgElement el, string id, string parentId, string frameId, string pageId,
        PenpotMatrix svgTransform, SvgNode parentNode,
        double scaleX, double scaleY)
    {
        double cx = ParseSvgNumber(el.Attrs.GetValueOrDefault("cx", "0"));
        double cy = ParseSvgNumber(el.Attrs.GetValueOrDefault("cy", "0"));
        double r = ParseSvgNumber(el.Attrs.GetValueOrDefault("r", "0"));
        if (r <= 0) return null;

        double canvasX = parentNode.X + (cx - r) * scaleX;
        double canvasY = parentNode.Y + (cy - r) * scaleY;
        double canvasW = 2 * r * scaleX;
        double canvasH = 2 * r * scaleY;

        var circle = new PenpotCircle
        {
            Id = id,
            Name = "svg-circle",
            PageId = pageId,
            ParentId = parentId,
            FrameId = frameId,
        };
        circle.SetGeometry(canvasX, canvasY, canvasW, canvasH);
        ApplyInheritedPaint(circle, el);
        return circle;
    }

    // ── SVG paint / opacity → Penpot fills & strokes ──────────────────────────

    private static void ApplyInheritedPaint(PenpotShape shape, SvgElement el)
    {
        var fillRaw = el.Attrs.GetValueOrDefault("fill", el.InheritedFill ?? "");
        var strokeRaw = el.Attrs.GetValueOrDefault("stroke", el.InheritedStroke ?? "");
        var fillOpacityRaw = el.Attrs.GetValueOrDefault("fill-opacity", "");
        var strokeOpacityRaw = el.Attrs.GetValueOrDefault("stroke-opacity", "");
        var strokeWidthRaw = el.Attrs.GetValueOrDefault("stroke-width", "1");
        var elemOpacityRaw = el.Attrs.GetValueOrDefault("opacity", "");

        double elemOpacity = el.InheritedOpacity;
        if (double.TryParse(elemOpacityRaw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var eop))
            elemOpacity *= Math.Clamp(eop, 0, 1);

        // Fill
        var fills = new List<PenpotFill>();
        if (!string.IsNullOrEmpty(fillRaw) && !string.Equals(fillRaw, "none", StringComparison.OrdinalIgnoreCase))
        {
            var (hex, a) = ParseCssColor(fillRaw);
            if (hex != null)
            {
                if (double.TryParse(fillOpacityRaw, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var fop))
                    a *= Math.Clamp(fop, 0, 1);
                fills.Add(PenpotFill.Solid(hex, a));
            }
        }
        else if (string.IsNullOrEmpty(fillRaw))
        {
            // SVG default fill is black if no fill attr (anywhere in the ancestor chain).
            fills.Add(PenpotFill.Solid("#000000", 1.0));
        }
        shape.Fills = fills;

        // Stroke
        if (!string.IsNullOrEmpty(strokeRaw) && !string.Equals(strokeRaw, "none", StringComparison.OrdinalIgnoreCase))
        {
            var (hex, a) = ParseCssColor(strokeRaw);
            if (hex != null)
            {
                double sw = ParseSvgNumber(strokeWidthRaw);
                if (sw < 0.1) sw = 1.0;
                if (double.TryParse(strokeOpacityRaw, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var sop))
                    a *= Math.Clamp(sop, 0, 1);
                shape.Strokes = new List<PenpotStroke>
                {
                    new() {
                        StrokeStyle     = "solid",
                        StrokeColor     = hex,
                        StrokeOpacity   = a,
                        StrokeWidth     = sw,
                        StrokeAlignment = "center",
                    },
                };
            }
        }

        if (elemOpacity < 0.999)
            shape.Opacity = elemOpacity;
    }

    // ── Path "d" → approximate bounding box (SVG-local coords) ────────────────
    //
    // Sufficient for selrect. Scans all numeric tokens in the path data,
    // pairs them as (x, y) coordinates based on command context, and tracks
    // the bounding box. This is an approximation — curve handles and arc
    // parameters can extend outside the control-point bounds — but for icon
    // and logo SVGs it's visually indistinguishable from the exact bbox.

    private static (double x, double y, double w, double h) ComputePathBoundsLocal(string d)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        double cx = 0, cy = 0;

        var tokens = Regex.Matches(d, @"([MmLlHhVvCcSsQqTtAaZz])|(-?\d*\.?\d+(?:[eE][-+]?\d+)?)");
        int i = 0;
        char cmd = 'M'; bool firstPoint = true;
        double startX = 0, startY = 0;

        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Groups[1].Success)
            {
                cmd = t.Groups[1].Value[0];
                i++;
                if (cmd == 'Z' || cmd == 'z')
                {
                    cx = startX; cy = startY;
                    continue;
                }
            }

            double ParseNext()
            {
                while (i < tokens.Count && !tokens[i].Groups[2].Success) i++;
                if (i >= tokens.Count) return 0;
                var v = double.Parse(tokens[i].Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                i++;
                return v;
            }

            bool rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                case 'L':
                case 'T':
                    {
                        double x = ParseNext(), y = ParseNext();
                        if (rel && !firstPoint) { x += cx; y += cy; }
                        cx = x; cy = y;
                        if (char.ToUpperInvariant(cmd) == 'M' && firstPoint) { startX = x; startY = y; }
                        UpdateBounds(x, y);
                        firstPoint = false;
                        if (char.ToUpperInvariant(cmd) == 'M') cmd = rel ? 'l' : 'L';
                        break;
                    }
                case 'H':
                    {
                        double x = ParseNext();
                        if (rel) x += cx;
                        cx = x;
                        UpdateBounds(x, cy);
                        break;
                    }
                case 'V':
                    {
                        double y = ParseNext();
                        if (rel) y += cy;
                        cy = y;
                        UpdateBounds(cx, y);
                        break;
                    }
                case 'C':
                    {
                        double x1 = ParseNext(), y1 = ParseNext();
                        double x2 = ParseNext(), y2 = ParseNext();
                        double x = ParseNext(), y = ParseNext();
                        if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                        UpdateBounds(x1, y1); UpdateBounds(x2, y2); UpdateBounds(x, y);
                        cx = x; cy = y;
                        break;
                    }
                case 'S':
                case 'Q':
                    {
                        double x1 = ParseNext(), y1 = ParseNext();
                        double x = ParseNext(), y = ParseNext();
                        if (rel) { x1 += cx; y1 += cy; x += cx; y += cy; }
                        UpdateBounds(x1, y1); UpdateBounds(x, y);
                        cx = x; cy = y;
                        break;
                    }
                case 'A':
                    {
                        // rx ry xrot large sweep x y
                        double rx = ParseNext(), ry = ParseNext();
                        ParseNext(); ParseNext(); ParseNext();
                        double x = ParseNext(), y = ParseNext();
                        if (rel) { x += cx; y += cy; }
                        // Approximation: include rx/ry envelope around cx,cy and endpoint
                        UpdateBounds(cx - rx, cy - ry); UpdateBounds(cx + rx, cy + ry);
                        UpdateBounds(x, y);
                        cx = x; cy = y;
                        break;
                    }
                default:
                    i++; // unknown — skip token
                    break;
            }
        }

        void UpdateBounds(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        if (minX == double.MaxValue) return (0, 0, 0, 0);
        return (minX, minY, maxX - minX, maxY - minY);
    }

    // ── Small utilities ───────────────────────────────────────────────────────

    private static double ParseAttrPx(string xml, string attr, double fallback)
    {
        var m = Regex.Match(xml, $@"<svg\b[^>]*\b{attr}\s*=\s*""\s*([\d.]+)(?:px)?",
            RegexOptions.IgnoreCase);
        return m.Success
            ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;
    }

    private static double ParseSvgNumber(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var m = Regex.Match(s, @"-?\d*\.?\d+(?:[eE][-+]?\d+)?");
        return m.Success
            ? double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
    }

    private static (int w, int h) EstimateImageDimensions(SvgNode node)
    {
        var w = node.Width > 0 ? (int)Math.Round(node.Width) : 100;
        var h = node.Height > 0 ? (int)Math.Round(node.Height) : 100;
        return (w, h);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CSS → Penpot attribute converters
    // ─────────────────────────────────────────────────────────────────────────

    private static void ApplyBackground(PenpotShape shape, Dictionary<string, string> css)
    {
        var bg = css.GetValueOrDefault("backgroundColor", "");
        if (string.IsNullOrEmpty(bg) || bg == "transparent"
            || bg is "rgba(0, 0, 0, 0)" or "rgba(0,0,0,0)") return;

        var (hex, opacity) = ParseCssColor(bg);
        if (hex == null) return;
        shape.Fills = new List<PenpotFill> { PenpotFill.Solid(hex, opacity) };
    }

    private static void ApplyBorder(PenpotShape shape, Dictionary<string, string> css)
    {
        var border = css.GetValueOrDefault("border", "");
        if (string.IsNullOrEmpty(border) || border is "none" or "0px none") return;

        var wm = Regex.Match(border, @"([\d.]+)px");
        var cm = Regex.Match(border, @"(rgba?\([^)]+\)|#[0-9a-fA-F]{3,8})");
        if (!wm.Success) return;
        var sw = double.Parse(wm.Groups[1].Value);
        if (sw < 0.1) return;

        var (hex, _) = cm.Success ? ParseCssColor(cm.Groups[1].Value) : ("#000000", 1.0);
        shape.Strokes = new List<PenpotStroke>
        {
            new() {
                StrokeStyle = "solid",
                StrokeColor = hex ?? "#000000",
                StrokeWidth = sw,
                StrokeAlignment = "inner",
            },
        };
    }

    private static void ApplyRadii(PenpotShape shape, Dictionary<string, string> css,
                                   double w, double h)
    {
        var br = css.GetValueOrDefault("borderRadius", "");
        if (string.IsNullOrEmpty(br) || br is "0px" or "0") return;

        // "8px 8px 0px 0px" — four values
        var parts = br.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4)
        {
            var r1 = ParseRadiusPart(parts[0], w, h);
            var r2 = ParseRadiusPart(parts[1], w, h);
            var r3 = ParseRadiusPart(parts[2], w, h);
            var r4 = ParseRadiusPart(parts[3], w, h);
            shape.SetRadiusAll(r1, r2, r3, r4);
        }
        else
        {
            var r = ParseRadiusPart(parts[0], w, h);
            shape.SetRadius(r);
        }
        // Mirror to rx/ry on rect and frame (both carry these legacy fields)
        if (shape is PenpotRect rect) { rect.Rx = shape.R1; rect.Ry = shape.R1; }
        else if (shape is PenpotFrame frame) { frame.Rx = shape.R1; frame.Ry = shape.R1; }
    }

    private static double ParseRadiusPart(string part, double w, double h)
    {
        var m = Regex.Match(part, @"([\d.]+)(px|%)");
        if (!m.Success) return 0;
        var v = double.Parse(m.Groups[1].Value);
        return m.Groups[2].Value == "%" ? v / 100.0 * Math.Min(w, h) : v;
    }

    private static void ApplyShadow(PenpotShape shape, Dictionary<string, string> css)
    {
        var bs = css.GetValueOrDefault("boxShadow", "");
        if (string.IsNullOrEmpty(bs) || bs == "none") return;

        var nums = Regex.Matches(bs, @"-?[\d.]+px")
            .Cast<Match>().Select(m => double.Parse(m.Value.Replace("px", ""))).ToList();
        var cm = Regex.Match(bs, @"rgba?\([^)]+\)|#[0-9a-fA-F]{3,8}");
        var rawColor = cm.Success ? cm.Value : "rgba(0,0,0,0.2)";
        var (hex, op) = ParseCssColor(rawColor);

        shape.Shadow = new List<PenpotShadow>
        {
            new()
            {
                Style   = bs.Contains("inset") ? "inner-shadow" : "drop-shadow",
                Color   = new PenpotShadowColor { Color = hex ?? "#000000", Opacity = op },
                OffsetX = nums.Count > 0 ? nums[0] : 0,
                OffsetY = nums.Count > 1 ? nums[1] : 2,
                Blur    = nums.Count > 2 ? Math.Abs(nums[2]) : 4,
                Spread  = nums.Count > 3 ? nums[3] : 0,
            },
        };
    }

    private static void ApplyOpacity(PenpotShape shape, Dictionary<string, string> css)
    {
        var op = css.GetValueOrDefault("opacity", "");
        if (string.IsNullOrEmpty(op)) return;
        if (double.TryParse(op, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v))
            shape.Opacity = Math.Clamp(v, 0, 1);
    }

    private static void ApplyFlexLayout(PenpotFrame frame, Dictionary<string, string> css)
    {
        var display = css.GetValueOrDefault("display", "");
        if (display is not ("flex" or "inline-flex")) return;

        frame.Layout = "flex";
        var dir = css.GetValueOrDefault("flexDirection", "row");
        frame.LayoutFlexDir = dir switch
        {
            "row-reverse" => "row-reverse",
            "column" => "column",
            "column-reverse" => "column-reverse",
            _ => "row",
        };

        frame.LayoutAlignItems = MapAlign(css.GetValueOrDefault("alignItems", ""));
        frame.LayoutJustifyContent = MapJustify(css.GetValueOrDefault("justifyContent", ""));
        frame.LayoutWrapType = css.GetValueOrDefault("flexWrap", "") == "wrap" ? "wrap" : "nowrap";

        // Required by Penpot's import schema — every flex frame in the reference
        // Penpot export carries this field (192/192 in Prototype_examples). Default
        // "stretch" matches the dominant Penpot UI default and the reference mode
        // distribution (183/192 "stretch", 9/192 "start").
        frame.LayoutAlignContent = MapAlignContent(css.GetValueOrDefault("alignContent", ""));

        // Per-frame sizing hints — how THIS frame sizes within its parent. Present
        // on ~88% of reference flex frames. "auto" is the neutral default.
        frame.LayoutItemHSizing = "auto";
        frame.LayoutItemVSizing = "auto";

        var gapRaw = css.GetValueOrDefault("gap", "");
        if (!string.IsNullOrEmpty(gapRaw))
        {
            var gapParts = gapRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var rowGap = ParsePx(gapParts[0], 0);
            var colGap = gapParts.Length > 1 ? ParsePx(gapParts[1], rowGap) : rowGap;
            frame.LayoutGap = new PenpotLayoutGap { RowGap = rowGap, ColumnGap = colGap };
            frame.LayoutGapType = Math.Abs(rowGap - colGap) < 0.01 ? "simple" : "multiple";
        }
        else
        {
            // Penpot's reference export has layoutGap on every flex frame even
            // when zero (192/192 coverage). Default to zero-gap "simple" so the
            // field is present.
            frame.LayoutGap = new PenpotLayoutGap { RowGap = 0, ColumnGap = 0 };
            frame.LayoutGapType = "simple";
        }

        var pad = ParseFourValueCss(css.GetValueOrDefault("padding", ""));
        if (pad != null)
        {
            frame.LayoutPadding = pad;
            // "simple" when all four sides are equal (matches Penpot's UI default
            // for uniform padding), "multiple" otherwise. Reference distribution
            // leans "simple" for zero-padding and uniform cases.
            double p1 = pad.P1 ?? 0, p2 = pad.P2 ?? 0, p3 = pad.P3 ?? 0, p4 = pad.P4 ?? 0;
            bool uniform = Math.Abs(p1 - p2) < 0.01
                        && Math.Abs(p2 - p3) < 0.01
                        && Math.Abs(p3 - p4) < 0.01;
            frame.LayoutPaddingType = uniform ? "simple" : "multiple";
        }
        else
        {
            // Ensure the layoutPadding field is present — reference has it on
            // every flex frame. Zero-pad "simple" matches the default.
            frame.LayoutPadding = new PenpotLayoutPadding { P1 = 0, P2 = 0, P3 = 0, P4 = 0 };
            frame.LayoutPaddingType = "simple";
        }
    }

    /// <summary>
    /// Map CSS align-content to Penpot's set. Default "stretch" matches
    /// Penpot UI convention and reference-export distribution (95% stretch).
    /// </summary>
    private static string MapAlignContent(string css) => css switch
    {
        "flex-start" or "start" => "start",
        "flex-end" or "end" => "end",
        "center" => "center",
        "space-between" => "space-between",
        "space-around" => "space-around",
        "space-evenly" => "space-evenly",
        _ => "stretch",
    };

    // ─────────────────────────────────────────────────────────────────────────
    // ZIP archive writers
    // ─────────────────────────────────────────────────────────────────────────

    private static void WriteManifest(ZipArchive zip, BuildContext ctx)
    {
        var manifest = new PenpotManifest
        {
            GeneratedBy = "contex-law/1.0.0",
            Files = new List<PenpotManifestFileEntry>
            {
                new()
                {
                    Id       = ctx.FileId,
                    Name     = ctx.Bundle.Desktop.Title ?? ctx.Bundle.ComponentId,
                    Features = PenpotFeatureFlags.Default,
                },
            },
        };
        WriteJson(zip, "manifest.json", manifest);
    }

    private static void WriteFileMeta(ZipArchive zip, BuildContext ctx)
    {
        var now = DateTime.UtcNow.ToString("O");

        // Declare the file at data-model version 67 (current Penpot 2.12-RC3
        // schema) with the full canonical migrations list — both defaults now
        // come from PenpotFeatureFlags. Verified against Prototype_examples_v1_2.
        // Emitting version=0 with migrations=[] tells Penpot the file is
        // pre-migration and triggers the legacy migration chain, which then
        // fails against our already-current shape schemas.
        //
        // TeamId / ProjectId are required fields from the importer's perspective
        // (it remaps them onto the destination workspace regardless of value).
        // Stable UUIDs keyed off the component ID give idempotent re-exports.
        var meta = new PenpotFileMeta
        {
            Id = ctx.FileId,
            Name = ctx.Bundle.Desktop.Title ?? ctx.Bundle.ComponentId,
            TeamId = StableUuid($"team/{ctx.Bundle.ComponentId}"),
            ProjectId = StableUuid($"project/{ctx.Bundle.ComponentId}"),
            CreatedAt = now,
            ModifiedAt = now,
        };
        WriteJson(zip, $"files/{ctx.FileId}.json", meta);
    }

    private static void WritePages(ZipArchive zip, BuildContext ctx)
    {
        foreach (var page in ctx.Pages)
        {
            // Page metadata
            var meta = new PenpotPageMeta
            {
                Id = page.PageId,
                Name = page.PageName,
                Index = page.Index,
                Background = page.Background,
                Options = new PenpotPageOptions { Background = page.Background },
            };
            WriteJson(zip,
                $"files/{ctx.FileId}/pages/{page.PageId}.json",
                meta);

            // Shape files
            foreach (var (shapeId, shape) in page.Shapes)
            {
                WriteJson(zip,
                    $"files/{ctx.FileId}/pages/{page.PageId}/{shapeId}.json",
                    shape);
            }
        }
    }

    private static void WriteMedia(ZipArchive zip, BuildContext ctx)
    {
        foreach (var m in ctx.Media)
        {
            var rec = new PenpotMediaRecord
            {
                Id = m.Id,
                Name = m.Name,
                Mtype = m.Mtype,
                Width = m.Width,
                Height = m.Height,
                MediaId = m.MediaId,
                ThumbnailId = m.ThumbnailId,
                IsLocal = true,
            };
            WriteJson(zip, $"files/{ctx.FileId}/media/{m.Id}.json", rec);
        }
    }

    private static void WriteObjects(ZipArchive zip, BuildContext ctx)
    {
        foreach (var blob in ctx.Blobs)
        {
            var bmeta = new PenpotBlobMetadata
            {
                Id = blob.Id,
                Hash = blob.Hash,
                Bucket = blob.Bucket,
                ContentType = blob.ContentType,
                Size = blob.Bytes.LongLength,
            };
            WriteJson(zip, $"objects/{blob.Id}.json", bmeta);
            WriteBinary(zip, $"objects/{blob.Id}.{blob.Ext}", blob.Bytes);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ZIP helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void WriteJson<T>(ZipArchive zip, string entryPath, T value)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = entry.Open();
        // Serialize using the *runtime* type so subclass fields (PenpotFrame.Shapes,
        // PenpotText.Content, PenpotRect.Rx/Ry, etc.) are included.
        // Without this, System.Text.Json uses the compile-time generic parameter T
        // (which is PenpotShape for all shapes) and silently drops every derived field.
        var json = JsonSerializer.SerializeToUtf8Bytes(value, value!.GetType(), JsonOpts);
        stream.Write(json, 0, json.Length);
    }

    private static void WriteRaw(ZipArchive zip, string entryPath, string content)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteBinary(ZipArchive zip, string entryPath, byte[] bytes)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Type mapping
    // ─────────────────────────────────────────────────────────────────────────

    private static string MapType(BuildContext ctx, SvgNode node)
    {
        if (!string.IsNullOrEmpty(node.TextContent)) return "text";
        if (!string.IsNullOrEmpty(node.ImgSrc) || !string.IsNullOrEmpty(node.SvgDataUri))
            return "rect"; // images are rects with fillImage

        // Carousel override: a horizontal-scroll container tagged in the
        // pre-walk must always be a frame. The frame provides the "Carousel N"
        // canvas label and the clip boundary that contains the cards.
        // Skipping this check risks a structural demotion to group, which
        // would strip the label and the clip.
        if (ctx.CarouselNames.ContainsKey(node.Id)) return "frame";

        var t = (node.ComponentType ?? node.Tag ?? "div").ToLowerInvariant();
        var mapped = t switch
        {
            "header" or "footer" or "main" or "section" or "article"
                or "nav" or "aside" or "form" or "div" => HasLayoutChildren(node) ? "frame" : "rect",
            "button" or "a" => "frame",   // may contain label + icon
            "group" => "group",
            "svg" => "rect",    // svg-raw is vestigial; use rect+fillImage
            _ => "rect",
        };

        // Rescue case: the mapping above wants "rect" but the node has
        // descendants with real paint content (text, image, inline SVG) further
        // down the tree. Emitting a rect would silently discard that subtree
        // because BuildRect does not recurse. Promote to frame so BuildShapeTree
        // keeps walking. Concrete example: Autosport's search button where the
        // SvgDataUri sits 2–3 levels below a <div> whose direct children have
        // no text or image of their own, failing HasLayoutChildren's shallow
        // check but carrying a valid icon deeper down.
        if (mapped == "rect" && HasMeaningfulDescendants(node)) return "frame";

        // Demote purely-structural frames to group. A frame with no visual
        // properties of its own (no background, no border, no shadow, no
        // rounded corners, no flex/grid layout) is just a wrapper. Penpot
        // renders such frames as grey boxes with a floating label on the
        // canvas, which is noise on a translated page. Groups render
        // silently, matching the Plants-app pattern where wrappers between
        // top-level boards and their visible content are groups, not frames.
        if (mapped == "frame" && !HasVisualProperties(node))
            return "group";

        return mapped;
    }

    /// <summary>
    /// Returns true if any descendant either is a carousel (explicit horizontal
    /// scroll container) or has a width that exceeds the current node's width —
    /// i.e. any content that would overflow horizontally and be clipped.
    /// </summary>
    private static bool HasOverflowingDescendant(SvgNode node, BuildContext ctx)
    {
        foreach (var child in node.Children)
        {
            if (ctx.CarouselNames.ContainsKey(child.Id)) return true;
            if (child.Width > node.Width + 1) return true;
            if (HasOverflowingDescendant(child, ctx)) return true;
        }
        return false;
    }

    private static bool HasLayoutChildren(SvgNode node) =>
        node.Children.Count > 0 &&
        node.Children.Any(c => c.Width > 0 || c.Height > 0 || !string.IsNullOrEmpty(c.TextContent));

    private static bool HasVisualProperties(SvgNode node)
    {
        var css = node.CssProps;
        if (css == null || css.Count == 0) return false;

        var bg = css.GetValueOrDefault("backgroundColor", "");
        if (!string.IsNullOrEmpty(bg)
            && bg != "transparent"
            && bg != "rgba(0, 0, 0, 0)"
            && bg != "rgba(0,0,0,0)")
            return true;

        var bgImg = css.GetValueOrDefault("backgroundImage", "");
        if (!string.IsNullOrEmpty(bgImg) && bgImg != "none") return true;

        var border = css.GetValueOrDefault("border", "");
        if (!string.IsNullOrEmpty(border) && border != "none" && border != "0px none"
            && !border.StartsWith("0px "))
            return true;

        var shadow = css.GetValueOrDefault("boxShadow", "");
        if (!string.IsNullOrEmpty(shadow) && shadow != "none") return true;

        var radius = css.GetValueOrDefault("borderRadius", "");
        if (!string.IsNullOrEmpty(radius) && radius != "0" && radius != "0px")
            return true;

        var display = css.GetValueOrDefault("display", "");
        if (display is "flex" or "inline-flex" or "grid" or "inline-grid") return true;

        var overflow = css.GetValueOrDefault("overflow", "");
        if (overflow is "hidden" or "clip" or "scroll" or "auto") return true;

        var overflowX = css.GetValueOrDefault("overflowX", "");
        if (overflowX is "hidden" or "clip" or "scroll" or "auto") return true;

        var overflowY = css.GetValueOrDefault("overflowY", "");
        if (overflowY is "hidden" or "clip" or "scroll" or "auto") return true;

        return false;
    }

    /// <summary>
    /// True if this node has any descendant (at any depth) carrying paint
    /// content Penpot can render — text, a raster/remote image URL, or an
    /// inline SVG data URI. Recursion is depth-bounded to prevent runaway
    /// traversal on pathological trees but covers realistic DOM nesting.
    /// </summary>
    private static bool HasMeaningfulDescendants(SvgNode node)
    {
        return Walk(node, 0);

        static bool Walk(SvgNode n, int depth)
        {
            if (depth > 32) return false;
            foreach (var c in n.Children)
            {
                if (!string.IsNullOrEmpty(c.TextContent)) return true;
                if (!string.IsNullOrEmpty(c.ImgSrc)) return true;
                if (!string.IsNullOrEmpty(c.SvgDataUri)) return true;
                if (c.Children.Count > 0 && Walk(c, depth + 1)) return true;
            }
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CSS parsing helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static (string? hex, double opacity) ParseCssColor(string css)
    {
        if (string.IsNullOrEmpty(css)) return (null, 1.0);
        // #rrggbb or #rgb
        if (css.StartsWith('#'))
        {
            if (css.Length == 4)
                css = $"#{css[1]}{css[1]}{css[2]}{css[2]}{css[3]}{css[3]}";
            return (css.ToLowerInvariant(), 1.0);
        }
        // rgba(r,g,b,a)
        var rgba = Regex.Match(css,
            @"rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)(?:\s*,\s*([\d.]+))?\s*\)");
        if (rgba.Success)
        {
            int r = (int)double.Parse(rgba.Groups[1].Value);
            int g = (int)double.Parse(rgba.Groups[2].Value);
            int b = (int)double.Parse(rgba.Groups[3].Value);
            double a = rgba.Groups[4].Success ? double.Parse(rgba.Groups[4].Value) : 1.0;
            return ($"#{r:x2}{g:x2}{b:x2}", a);
        }
        return ("#000000", 1.0);
    }

    private static string CssColorToHex(string css)
    {
        var (hex, _) = ParseCssColor(css);
        return hex ?? "#000000";
    }

    private static double ParsePx(string v, double fallback)
    {
        if (string.IsNullOrEmpty(v)) return fallback;
        var m = Regex.Match(v, @"([\d.]+)");
        return m.Success
            ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;
    }

    private static string ParseLineHeight(string v, double fontSize)
    {
        if (string.IsNullOrEmpty(v) || v == "normal") return "1.2";
        // unitless multiplier
        var mu = Regex.Match(v, @"^([\d.]+)$");
        if (mu.Success) return mu.Groups[1].Value;
        // px → divide by font size for multiplier
        var px = Regex.Match(v, @"([\d.]+)px");
        if (px.Success && fontSize > 0)
        {
            var lh = double.Parse(px.Groups[1].Value) / fontSize;
            return lh.ToString("0.##");
        }
        return "1.2";
    }

    private static string ParseLetterSpacing(string v)
    {
        if (string.IsNullOrEmpty(v)) return "0";
        var m = Regex.Match(v, @"-?[\d.]+");
        return m.Success ? m.Value : "0";
    }

    private static string CssTextDecoration(Dictionary<string, string> css)
    {
        var td = css.GetValueOrDefault("textDecoration", "none");
        if (td.Contains("underline")) return "underline";
        if (td.Contains("line-through")) return "line-through";
        return "none";
    }

    private static PenpotLayoutPadding? ParseFourValueCss(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        return parts.Length switch
        {
            1 => PenpotLayoutPadding.Uniform(ParsePx(parts[0], 0)),
            2 => new()
            {
                P1 = ParsePx(parts[0], 0),
                P2 = ParsePx(parts[1], 0),
                P3 = ParsePx(parts[0], 0),
                P4 = ParsePx(parts[1], 0)
            },
            4 => new()
            {
                P1 = ParsePx(parts[0], 0),
                P2 = ParsePx(parts[1], 0),
                P3 = ParsePx(parts[2], 0),
                P4 = ParsePx(parts[3], 0)
            },
            _ => null,
        };
    }

    private static string MapAlign(string v) => v switch
    {
        "flex-start" or "start" => "start",
        "flex-end" or "end" => "end",
        "center" => "center",
        "stretch" => "stretch",
        _ => "start",
    };

    private static string MapJustify(string v) => v switch
    {
        "flex-start" or "start" => "start",
        "flex-end" or "end" => "end",
        "center" => "center",
        "space-between" => "space-between",
        "space-around" => "space-around",
        "space-evenly" => "space-evenly",
        _ => "start",
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Font helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string SanitiseFontFamily(string raw)
    {
        // Take first family, strip quotes
        var first = raw.Split(',')[0].Trim().Trim('"', '\'').Trim();
        if (string.IsNullOrEmpty(first)) return "sourcesanspro";
        return first;
    }

    private static string FontId(string family)
    {
        // Penpot's default system fonts don't use a "gfont-" prefix.
        // Known Google Fonts exported from real Penpot files use "gfont-<slug>".
        // Map a selection of common web fonts; everything else gets sourcesanspro.
        var slug = Regex.Replace(family.ToLowerInvariant(), @"\s+", "");
        return slug switch
        {
            "karla" => "gfont-karla",
            "inter" => "gfont-inter",
            "roboto" => "gfont-roboto",
            "opensans" => "gfont-open-sans",
            "lato" => "gfont-lato",
            "poppins" => "gfont-poppins",
            "montserrat" => "gfont-montserrat",
            "nunito" => "gfont-nunito",
            "raleway" => "gfont-raleway",
            "plusjakartasans" => "gfont-plus-jakarta-sans",
            "bitter" => "gfont-bitter",
            "sourcesanspro"
                or "source sans pro" => "sourcesanspro",
            _ => "sourcesanspro",
        };
    }

    private static string FontVariantId(string weight, string style)
    {
        var w = weight switch
        {
            "100" => "100",
            "200" => "200",
            "300" => "300",
            "400" or "normal" => "regular",
            "500" => "500",
            "600" => "600",
            "700" or "bold" => "700",
            "800" => "800",
            "900" => "900",
            _ => "regular",
        };
        return style == "italic"
            ? w == "regular" ? "italic" : $"{w}italic"
            : w;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UUID helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string StableUuid(string key)
    {
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant RFC 4122
        return new Guid(bytes).ToString();
    }

    // 5-char alphanumeric key for paragraph.key (DraftJS heritage)
    private static readonly char[] _keyChars =
        "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();
    private static readonly Random _rng = new();
    private static string RandomKey()
    {
        Span<char> buf = stackalloc char[5];
        for (int i = 0; i < 5; i++) buf[i] = _keyChars[_rng.Next(_keyChars.Length)];
        return new string(buf);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Image / binary helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static byte[]? DecodeDataUri(string dataUri, out string mtype)
    {
        mtype = "image/png";
        try
        {
            // data:[<mtype>][;base64],<data>
            var comma = dataUri.IndexOf(',');
            if (comma < 0) return null;
            var meta = dataUri[5..comma];
            var parts = meta.Split(';');
            if (parts.Length > 0 && parts[0].Contains('/'))
                mtype = parts[0];
            bool isBase64 = parts.Any(p => p == "base64");
            var payload = dataUri[(comma + 1)..];
            if (isBase64) return Convert.FromBase64String(payload);
            // URL-encoded SVG
            var decoded = Uri.UnescapeDataString(payload);
            return Encoding.UTF8.GetBytes(decoded);
        }
        catch { return null; }
    }

    /// <summary>
    /// Determine the real image MIME type from the file's magic bytes. Always
    /// prefer this over URL-based guessing when the bytes are in hand: the
    /// server's WebP→JPEG conversion handler rewrites response bytes but not
    /// URLs, so a URL like "..._foo.webp" may actually carry JPEG bytes. Using
    /// the URL's extension in that case produces a media record whose mtype
    /// lies about the content, which Penpot rejects on import.
    /// </summary>
    private static string SniffMime(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 12) return "";
        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return "image/png";
        // WebP: "RIFF" ....... "WEBP"
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";
        // GIF: "GIF8"
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return "image/gif";
        return "";
    }

    private static string GuessMime(string url)
    {
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return "image/jpeg";
        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        // SVG is not supported as a Penpot fillImage type — skip by returning empty string.
        // BuildRect checks for null/empty fill and falls back to background colour.
        if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return "image/png";
    }

    private static string MimeToExt(string mtype) => mtype switch
    {
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        "image/svg+xml" => "svg",
        _ => "png",
    };

    /// <summary>
    /// Naive thumbnail — returns the original bytes when no image library is
    /// available in the Blazor WASM sandbox. In a server-side context, swap
    /// this for a proper ImageSharp resize call.
    /// </summary>
    private static (byte[] bytes, int w, int h) MakeThumbnail(byte[] src, string mtype, int origW, int origH)
    {
        // TODO: replace with ImageSharp resize when running server-side.
        const int MaxDim = 256;
        var scale = origW > 0 && origH > 0
            ? Math.Min(1.0, MaxDim / (double)Math.Max(origW, origH))
            : 1.0;
        return (src, (int)(origW * scale), (int)(origH * scale));
    }

    /// <summary>
    /// Content hash emitted into objects/{blob}.json.
    /// Penpot's import worker computes BLAKE2b-256 (32-byte digest) of the
    /// stored object bytes and compares against this string. If they don't
    /// match the import is rejected with :inconsistent-penpot-file /
    /// "found corrupted storage object: hash does not match".
    ///
    /// Deterministically verified: BLAKE2b-256 of an arbitrary object binary
    /// from a Penpot-exported reference file matches the declared hash in
    /// that object's metadata JSON. Same for a failing export — Penpot's
    /// computed hash matches hashlib.blake2b(bytes, digest_size=32).
    ///
    /// Requires the SauceControl.Blake2Fast NuGet package — install via
    /// Visual Studio's NuGet Package Manager UI (right-click Client project →
    /// Manage NuGet Packages → Browse → "SauceControl.Blake2Fast" → Install).
    /// The package is pure managed and works across all target frameworks.
    /// Note: NuGet package is "SauceControl.Blake2Fast" but the .NET namespace
    /// is just "Blake2Fast" — use `global::Blake2Fast.Blake2b.ComputeHash(...)`
    /// or add `using Blake2Fast;` at the top of the file.
    /// </summary>
    private static string Blake2bHex(byte[] data)
    {
        // Package: SauceControl.Blake2Fast (install via VS NuGet Package Manager UI)
        // Namespace: Blake2Fast (note — package name and namespace differ).
        // Blake2Fast.Blake2b.ComputeHash(int digestLength, ReadOnlySpan<byte> input)
        // → returns byte[] of length `digestLength`.
        var hash = global::Blake2Fast.Blake2b.ComputeHash(32, data);
        return "blake2b:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Misc helpers
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // Shape naming
    // ─────────────────────────────────────────────────────────────────────────
    //
    // Penpot displays shape `name` as a floating canvas label on top-level
    // boards (direct children of Root Frame) and on the selected shape.
    // Nested frames can also display labels. The single biggest reduction in
    // label noise comes from ensuring only ONE board is a direct child of
    // Root Frame — the page-content board — see MakePage. This helper still
    // matters for frames that remain labelled (like carousels) and for the
    // Layers panel, which uses `name` regardless of canvas rendering.
    //
    // Chain mirrors SvgSerializer.InferComponentType exactly when falling
    // back on Tag, so the .penpot shape name and the SVG's data-component-type
    // attribute stay in sync for the same node.

    private static string NodeName(SvgNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.ComponentType))
            return SanitiseShapeName(node.ComponentType!);

        if (!string.IsNullOrWhiteSpace(node.AriaLabel))
            return SanitiseShapeName(node.AriaLabel!);

        if (!string.IsNullOrWhiteSpace(node.Label) && !IsOpaqueIdentifier(node.Label!))
            return SanitiseShapeName(node.Label!);

        return InferComponentType(node);
    }

    private static string TextShapeName(SvgNode node)
    {
        var textValue = node.TextContent?.Trim();
        if (!string.IsNullOrEmpty(textValue))
        {
            textValue = Regex.Replace(textValue, @"\s+", " ");
            if (textValue.Length > 40)
                textValue = textValue.Substring(0, 39).TrimEnd() + "…";
            return SanitiseShapeName(textValue);
        }

        if (!string.IsNullOrWhiteSpace(node.ComponentType))
            return SanitiseShapeName(node.ComponentType!);
        if (!string.IsNullOrWhiteSpace(node.AriaLabel))
            return SanitiseShapeName(node.AriaLabel!);
        if (!string.IsNullOrWhiteSpace(node.Label) && !IsOpaqueIdentifier(node.Label!))
            return SanitiseShapeName(node.Label!);
        return "Text";
    }

    // Mirrors SvgSerializer.InferComponentType. Update both in lockstep.
    private static string InferComponentType(SvgNode node)
    {
        if (string.IsNullOrWhiteSpace(node.Tag)) return "Container";
        return node.Tag.ToLowerInvariant() switch
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
    }

    private static readonly Regex _ssHashRegex =
        new(@"^ss_[0-9a-f]{6,}_\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _hexBlobRegex =
        new(@"^[0-9a-f]{24,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _uuidLikeRegex =
        new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsOpaqueIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        if (_ssHashRegex.IsMatch(s)) return true;
        if (_uuidLikeRegex.IsMatch(s)) return true;
        if (_hexBlobRegex.IsMatch(s)) return true;
        return false;
    }

    private static string SanitiseShapeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Container";
        var cleaned = Regex.Replace(raw, @"[\u0000-\u001f\u007f]", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Length == 0) return "Container";
        if (cleaned.Length > 60)
            cleaned = cleaned.Substring(0, 59).TrimEnd() + "…";
        return cleaned;
    }

    private static IEnumerable<SvgNode> FlattenTree(IReadOnlyList<SvgNode> tree)
    {
        var stack = new Stack<SvgNode>();
        foreach (var n in tree) stack.Push(n);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.Children) stack.Push(c);
        }
    }
}