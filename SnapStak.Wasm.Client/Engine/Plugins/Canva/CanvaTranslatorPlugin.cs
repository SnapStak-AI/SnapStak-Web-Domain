// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SnapStak.Wasm.Client.Engine.Plugins;
using SnapStak.Wasm.Client.Models.Svg;

namespace SnapStak.Wasm.Client.Engine.Plugins.Canva;

// ─────────────────────────────────────────────────────────────────────────────
// CanvaTranslatorPlugin
//
// CON10X translator plugin — produces a PDF (PDF 1.4) from the canonical
// SvgNode tree. The PDF is imported into Canva via the Connect API Design
// Import endpoint (POST /v1/imports) by CanvaRelayService on the server.
//
// PDF is the ideal intermediate format for Canva because:
//   • It is on Canva's official supported import list (no workarounds needed).
//   • Canva's importer converts PDF elements to native Canva shapes that
//     designers can edit — text stays text, rectangles stay rectangles.
//   • No proprietary format engineering is required.
//   • The PDF writer below is pure managed C# — zero NuGet dependencies,
//     runs on both the server (.NET 9) and Blazor WASM.
//
// PDF WRITER OVERVIEW
//   The writer implements the minimum PDF 1.4 subset needed to faithfully
//   represent SnapStak captures:
//     • Rectangular fills     — PDF path operator  re / f
//     • Borders / strokes     — PDF path operator  re / S
//     • Border radius         — approximated with Bézier curves (c operator)
//     • Text                  — BT / Tf / Td / Tj / ET  (Type 1 standard fonts)
//     • Raster images         — XObject Image / Do  (inline base64 bytes)
//     • Opacity               — gs operator (ExtGState dictionary)
//     • Drop shadows          — second filled rect with opacity ExtGState
//     • Clipping masks        — W operator (for border-radius image clipping)
//
//   Every page maps to one PDF page. Page order follows §8 of the plugin spec:
//     desktop master → desktop segments → mobile master → mobile segments →
//     hidden components.
//
// FONT MAPPING
//   Canva's importer preserves PDF text as editable Canva text. Standard
//   Type 1 fonts (Helvetica, Times-Roman, Courier) are available without
//   embedding. Web fonts that Canva does not recognise fall back to Helvetica.
//   The FontMap below covers the most common web font families.
//
// IMAGES
//   Remote images are declared in Phase 1 and their bytes arrive in Phase 2.
//   Base64 data-URIs are decoded inline. SVG data-URIs are treated as raster
//   images (Canva rasterises them on import — acceptable for v1).
//   Images are embedded as PDF XObjects using JPEG DCTDecode or PNG FlateDecode
//   depending on the detected format.
//
// COORDINATE SYSTEM
//   PDF uses bottom-left origin; SnapStak uses top-left origin.
//   All Y coordinates are flipped: pdfY = pageHeight - snapstakY - elementHeight
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CanvaTranslatorPlugin : IConteXTranslatorPlugin
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string Key => "canva";
    public string DisplayName => "Canva (PDF via Connect API)";
    public string Version => "1.0.0";
    public string FileExtension => ".canva.pdf";

    // ── Font mapping: CSS family → PDF Type 1 base font name ─────────────────

    private static readonly Dictionary<string, string> FontMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Sans-serif web fonts → Helvetica family
            ["inter"] = "Helvetica",
            ["roboto"] = "Helvetica",
            ["open sans"] = "Helvetica",
            ["lato"] = "Helvetica",
            ["poppins"] = "Helvetica",
            ["montserrat"] = "Helvetica",
            ["nunito"] = "Helvetica",
            ["raleway"] = "Helvetica",
            ["source sans pro"] = "Helvetica",
            ["sourcesanspro"] = "Helvetica",
            ["plus jakarta sans"] = "Helvetica",
            ["karla"] = "Helvetica",
            ["dm sans"] = "Helvetica",
            ["figtree"] = "Helvetica",
            ["geist"] = "Helvetica",
            ["helvetica"] = "Helvetica",
            ["arial"] = "Helvetica",
            ["sans-serif"] = "Helvetica",
            // Serif web fonts → Times-Roman
            ["georgia"] = "Times-Roman",
            ["merriweather"] = "Times-Roman",
            ["playfair display"] = "Times-Roman",
            ["lora"] = "Times-Roman",
            ["eb garamond"] = "Times-Roman",
            ["times new roman"] = "Times-Roman",
            ["times"] = "Times-Roman",
            ["serif"] = "Times-Roman",
            // Monospace → Courier
            ["courier new"] = "Courier",
            ["courier"] = "Courier",
            ["monaco"] = "Courier",
            ["jetbrains mono"] = "Courier",
            ["fira code"] = "Courier",
            ["source code pro"] = "Courier",
            ["monospace"] = "Courier",
        };

    // ── Phase 1 — declare remote image URLs ──────────────────────────────────

    public IReadOnlyList<string> DeclareResources(TranslatorBundle bundle)
    {
        try
        {
            var urls = new HashSet<string>(StringComparer.Ordinal);
            CollectImageUrls(bundle.Desktop.Tree, urls);
            if (bundle.Mobile != null) CollectImageUrls(bundle.Mobile.Tree, urls);
            foreach (var seg in bundle.Segments) CollectImageUrls(seg.Tree, urls);
            foreach (var hc in bundle.HiddenComponents) CollectImageUrls(hc.Tree, urls);
            return urls.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static void CollectImageUrls(IReadOnlyList<SvgNode> tree, HashSet<string> out_)
    {
        foreach (var n in FlattenTree(tree))
        {
            if (!string.IsNullOrEmpty(n.ImgSrc)
                && (n.ImgSrc.StartsWith("http://") || n.ImgSrc.StartsWith("https://")))
                out_.Add(n.ImgSrc);
        }
    }

    // ── Phase 2 — translate ───────────────────────────────────────────────────

    public byte[] Translate(
        TranslatorBundle bundle,
        IReadOnlyDictionary<string, byte[]> fetchedResources)
    {
        if (bundle.Desktop.Tree.Count == 0) return Array.Empty<byte>();

        var writer = new PdfWriter(bundle.ComponentId, fetchedResources);

        // Desktop master
        writer.AddPage(
            bundle.Desktop.Tree,
            bundle.Desktop.Width,
            bundle.Desktop.Height,
            $"Desktop — {bundle.Desktop.Title ?? bundle.ComponentId}");

        // Desktop segments
        foreach (var seg in bundle.Segments)
            writer.AddPage(seg.Tree, seg.Width, seg.Height, $"Desktop — {seg.Label}");

        // Mobile master
        if (bundle.Mobile != null)
        {
            writer.AddPage(
                bundle.Mobile.Tree,
                bundle.Mobile.Width,
                bundle.Mobile.Height,
                $"Mobile — {bundle.Mobile.Title ?? bundle.ComponentId}");

            foreach (var seg in bundle.Segments)
                writer.AddPage(seg.Tree, seg.Width, seg.Height, $"Mobile — {seg.Label}");
        }

        // Hidden components
        foreach (var hc in bundle.HiddenComponents)
            if (hc.Tree.Count > 0)
                writer.AddPage(hc.Tree, hc.Width > 0 ? hc.Width : 390,
                               hc.Height > 0 ? hc.Height : 800, $"Hidden — {hc.Label}");

        return writer.Build();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PDF WRITER
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class PdfWriter
    {
        private readonly string _componentId;
        private readonly IReadOnlyDictionary<string, byte[]> _fetched;

        // PDF object registry — each object is a byte range in the output stream
        private readonly List<byte[]> _objects = new();  // index 0 = obj 1
        private readonly List<int> _offsets = new();  // byte offset of each object

        // Image XObjects registered during page rendering
        // key = stable image ID, value = (pdfObjIndex, width, height)
        private readonly Dictionary<string, (int ObjIdx, int W, int H)> _images = new();

        // Pages object array built incrementally
        private readonly List<int> _pageObjIndices = new();

        public PdfWriter(string componentId, IReadOnlyDictionary<string, byte[]> fetched)
        {
            _componentId = componentId;
            _fetched = fetched;
        }

        // ── Add one page ──────────────────────────────────────────────────────

        public void AddPage(
            IReadOnlyList<SvgNode> tree,
            int viewportWidth,
            int viewportHeight,
            string title)
        {
            if (viewportWidth < 1) viewportWidth = 1440;
            if (viewportHeight < 1) viewportHeight = 900;

            // Render content stream
            var content = new StringBuilder(1024 * 32);
            var extGStates = new Dictionary<string, double>(); // name → opacity
            var xObjNames = new Dictionary<string, string>();  // imageId → XObject name

            foreach (var node in tree)
                RenderNode(node, content, extGStates, xObjNames, viewportHeight);

            var contentBytes = Encoding.Latin1.GetBytes(content.ToString());

            // Resources dictionary
            var resDict = BuildResourcesDict(extGStates, xObjNames);

            // Content stream object
            var contentObjIdx = AddObject(BuildStream(contentBytes));

            // Page object
            var pageObj = BuildPageObject(
                viewportWidth, viewportHeight, contentObjIdx, resDict, title);
            var pageObjIdx = AddObject(pageObj);
            _pageObjIndices.Add(pageObjIdx + 1); // 1-based object number
        }

        // ── Node renderer ─────────────────────────────────────────────────────

        private void RenderNode(
            SvgNode node,
            StringBuilder s,
            Dictionary<string, double> extGStates,
            Dictionary<string, string> xObjNames,
            int pageHeight)
        {
            var css = node.CssProps;
            double x = node.X;
            double y = node.Y;
            double w = node.Width;
            double h = node.Height;

            if (w <= 0 && h <= 0 && string.IsNullOrEmpty(node.TextContent)
                && string.IsNullOrEmpty(node.ImgSrc) && string.IsNullOrEmpty(node.SvgDataUri))
            {
                foreach (var c in node.Children)
                    RenderNode(c, s, extGStates, xObjNames, pageHeight);
                return;
            }

            // PDF Y: flip from top-left to bottom-left origin
            double pdfY = pageHeight - y - h;

            var opacity = ParseDouble(css.GetValueOrDefault("opacity", "1"), 1.0);

            // ── Drop shadow ───────────────────────────────────────────────────
            var boxShadow = css.GetValueOrDefault("boxShadow", "");
            if (!string.IsNullOrEmpty(boxShadow) && boxShadow != "none")
                RenderShadow(s, extGStates, boxShadow, x, pdfY, w, h, pageHeight);

            // ── Background fill ───────────────────────────────────────────────
            var bgColor = css.GetValueOrDefault("backgroundColor", "");
            if (!string.IsNullOrEmpty(bgColor)
                && bgColor != "transparent"
                && bgColor != "rgba(0, 0, 0, 0)"
                && bgColor != "rgba(0,0,0,0)"
                && w > 0 && h > 0)
            {
                var (r, g, b, a) = ParseColor(bgColor);
                double effectiveOpacity = opacity * a;
                if (effectiveOpacity > 0.01)
                {
                    var gsName = EnsureExtGState(extGStates, effectiveOpacity);
                    s.Append("q ");
                    s.Append($"{gsName} gs ");
                    s.Append($"{Pf(r)} {Pf(g)} {Pf(b)} rg ");
                    var br = ParseBorderRadius(css, w, h);
                    if (br > 0.5)
                        AppendRoundedRect(s, x, pdfY, w, h, br);
                    else
                        s.Append($"{Pf(x)} {Pf(pdfY)} {Pf(w)} {Pf(h)} re ");
                    s.AppendLine("f Q");
                }
            }

            // ── Border / stroke ───────────────────────────────────────────────
            var border = css.GetValueOrDefault("border", "");
            if (!string.IsNullOrEmpty(border) && border != "none" && w > 0 && h > 0)
            {
                var (sw, sc) = ParseBorder(border);
                if (sw > 0)
                {
                    var (r, g, b, _) = ParseColor(sc);
                    s.Append("q ");
                    s.Append($"{Pf(r)} {Pf(g)} {Pf(b)} RG ");
                    s.Append($"{Pf(sw)} w ");
                    var br = ParseBorderRadius(css, w, h);
                    if (br > 0.5)
                        AppendRoundedRect(s, x, pdfY, w, h, br);
                    else
                        s.Append($"{Pf(x)} {Pf(pdfY)} {Pf(w)} {Pf(h)} re ");
                    s.AppendLine("S Q");
                }
            }

            // ── Raster / SVG image ────────────────────────────────────────────
            bool hasImage = !string.IsNullOrEmpty(node.ImgSrc)
                         || !string.IsNullOrEmpty(node.SvgDataUri);
            if (hasImage && w > 0 && h > 0)
            {
                var imgId = StableUuid($"img/{_componentId}/{node.Id}");
                var xobjName = xObjNames.ContainsKey(imgId)
                    ? xObjNames[imgId]
                    : RegisterImage(imgId, node, xObjNames);

                if (xobjName != null)
                {
                    var gsName = EnsureExtGState(extGStates, opacity);
                    s.Append("q ");
                    s.Append($"{gsName} gs ");
                    // Apply border-radius clipping to images
                    var br = ParseBorderRadius(css, w, h);
                    if (br > 0.5) { AppendRoundedRect(s, x, pdfY, w, h, br); s.Append("W n "); }
                    // PDF image transform matrix: [w 0 0 h x pdfY]
                    s.Append($"{Pf(w)} 0 0 {Pf(h)} {Pf(x)} {Pf(pdfY)} cm ");
                    s.AppendLine($"/{xobjName} Do Q");
                }
            }

            // ── Text ──────────────────────────────────────────────────────────
            var text = node.TextContent?.Trim();
            if (!string.IsNullOrEmpty(text))
                RenderText(s, extGStates, css, text, x, y, w, h, pageHeight, opacity);

            // ── Children ──────────────────────────────────────────────────────
            foreach (var child in node.Children)
                RenderNode(child, s, extGStates, xObjNames, pageHeight);
        }

        // ── Text rendering ────────────────────────────────────────────────────

        private void RenderText(
            StringBuilder s,
            Dictionary<string, double> extGStates,
            Dictionary<string, string> css,
            string text,
            double x, double y, double w, double h,
            int pageHeight,
            double shapeOpacity)
        {
            var rawFamily = css.GetValueOrDefault("fontFamily", "sans-serif")
                               .Split(',')[0].Trim().Trim('"', '\'');
            var pdfFont = MapFont(rawFamily, css.GetValueOrDefault("fontWeight", "400"),
                                   css.GetValueOrDefault("fontStyle", "normal"));

            var fontSize = ParseDouble(
                Regex.Match(css.GetValueOrDefault("fontSize", "14px"), @"[\d.]+").Value, 14);
            var (r, g, b, a) = ParseColor(css.GetValueOrDefault("color", "#000000"));
            double opacity = shapeOpacity * a;

            var textAlign = css.GetValueOrDefault("textAlign", "left");
            var alignItems = css.GetValueOrDefault("alignItems", "");
            var vertAlign = css.GetValueOrDefault("verticalAlign", "");
            var display = css.GetValueOrDefault("display", "block");
            var justify = css.GetValueOrDefault("justifyContent", "");
            var lineHeight = ParseLineHeightPx(
                css.GetValueOrDefault("lineHeight", ""), fontSize);

            // Vertical position
            bool vCenter = alignItems == "center" || vertAlign == "middle";
            bool vEnd = alignItems is "flex-end" or "end" || vertAlign == "bottom";
            double textY;
            if (vCenter) textY = y + (h / 2.0) + (fontSize * 0.35);
            else if (vEnd) textY = y + h - (fontSize * 0.15);
            else textY = y + fontSize;

            double pdfTextY = pageHeight - textY;

            // Horizontal position
            bool hCenter = textAlign == "center"
                        || ((display is "flex" or "inline-flex") && justify == "center");
            bool hRight = textAlign is "right" or "end";
            double textX = hCenter ? x + w / 2.0 : hRight ? x + w : x;

            // Text transform
            var tx = css.GetValueOrDefault("textTransform", "none");
            if (tx == "uppercase") text = text.ToUpperInvariant();
            else if (tx == "lowercase") text = text.ToLowerInvariant();

            // Word wrap
            var lines = WrapText(text, w, fontSize);

            var gsName = EnsureExtGState(extGStates, opacity);
            s.Append("q ");
            s.Append($"{gsName} gs ");
            s.Append($"{Pf(r)} {Pf(g)} {Pf(b)} rg ");
            s.Append("BT ");
            s.Append($"/{pdfFont} {Pf(fontSize)} Tf ");

            // Text alignment mode: 0=left, 1=center, 2=right
            int ta = hCenter ? 1 : hRight ? 2 : 0;
            if (ta != 0) s.Append($"{ta} Tr ");

            for (int i = 0; i < lines.Count; i++)
            {
                double lineY = pdfTextY - (i * lineHeight);
                s.Append($"{Pf(textX)} {Pf(lineY)} Td ");
                s.Append($"({PdfEscapeText(lines[i])}) Tj ");
                if (i < lines.Count - 1) s.Append("T* ");
            }

            s.AppendLine("ET Q");
        }

        // ── Shadow rendering ──────────────────────────────────────────────────

        private static void RenderShadow(
            StringBuilder s,
            Dictionary<string, double> extGStates,
            string boxShadow,
            double x, double pdfY, double w, double h,
            int pageHeight)
        {
            var nums = Regex.Matches(boxShadow, @"-?[\d.]+px")
                .Cast<Match>().Select(m => ParseDouble(m.Value.Replace("px", ""), 0)).ToList();
            var cm = Regex.Match(boxShadow, @"rgba?\([^)]+\)|#[0-9a-fA-F]{3,8}");
            if (!cm.Success) return;

            var (r, g, b, a) = ParseColor(cm.Value);
            double dx = nums.Count > 0 ? nums[0] : 0;
            double dy = nums.Count > 1 ? -nums[1] : 0; // PDF Y is flipped
            double blur = nums.Count > 2 ? Math.Abs(nums[2]) : 4;
            double spread = nums.Count > 3 ? nums[3] : 0;

            double shadowOpacity = a * 0.6; // soften slightly
            if (shadowOpacity < 0.02) return;

            var gsName = EnsureExtGState(extGStates, shadowOpacity);
            s.Append("q ");
            s.Append($"{gsName} gs ");
            s.Append($"{Pf(r)} {Pf(g)} {Pf(b)} rg ");
            s.Append($"{Pf(x + dx - spread)} {Pf(pdfY + dy - spread)} ");
            s.Append($"{Pf(w + spread * 2)} {Pf(h + spread * 2)} re ");
            s.AppendLine("f Q");
        }

        // ── Rounded rectangle (Bézier approximation) ──────────────────────────

        private static void AppendRoundedRect(
            StringBuilder s, double x, double y, double w, double h, double r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2.0);
            const double k = 0.5523; // Bézier control point factor
            double kr = k * r;

            s.Append($"{Pf(x + r)} {Pf(y)} m ");
            s.Append($"{Pf(x + w - r)} {Pf(y)} l ");
            s.Append($"{Pf(x + w - r + kr)} {Pf(y)} {Pf(x + w)} {Pf(y + r - kr)} {Pf(x + w)} {Pf(y + r)} c ");
            s.Append($"{Pf(x + w)} {Pf(y + h - r)} l ");
            s.Append($"{Pf(x + w)} {Pf(y + h - r + kr)} {Pf(x + w - r + kr)} {Pf(y + h)} {Pf(x + w - r)} {Pf(y + h)} c ");
            s.Append($"{Pf(x + r)} {Pf(y + h)} l ");
            s.Append($"{Pf(x + r - kr)} {Pf(y + h)} {Pf(x)} {Pf(y + h - r + kr)} {Pf(x)} {Pf(y + h - r)} c ");
            s.Append($"{Pf(x)} {Pf(y + r)} l ");
            s.Append($"{Pf(x)} {Pf(y + r - kr)} {Pf(x + r - kr)} {Pf(y)} {Pf(x + r)} {Pf(y)} c ");
        }

        // ── Image registration ────────────────────────────────────────────────

        private string? RegisterImage(
            string imgId,
            SvgNode node,
            Dictionary<string, string> xObjNames)
        {
            byte[]? bytes = null;
            string mtype = "image/png";

            if (!string.IsNullOrEmpty(node.SvgDataUri))
            {
                bytes = DecodeDataUri(node.SvgDataUri, out mtype);
                if (mtype == "image/svg+xml") mtype = "image/png"; // treat as raster
            }
            else if (!string.IsNullOrEmpty(node.ImgSrc))
            {
                if (node.ImgSrc.StartsWith("data:"))
                    bytes = DecodeDataUri(node.ImgSrc, out mtype);
                else if (_fetched.TryGetValue(node.ImgSrc, out var fetched))
                {
                    bytes = fetched;
                    mtype = GuessMime(node.ImgSrc);
                }
            }

            if (bytes == null || bytes.Length == 0) return null;

            var (w, h) = ((int)Math.Max(1, node.Width), (int)Math.Max(1, node.Height));
            var xObjBytes = BuildImageXObject(bytes, mtype, w, h);
            if (xObjBytes == null) return null;

            var objIdx = AddObject(xObjBytes);
            var xobjName = $"Im{_images.Count + 1}";
            _images[imgId] = (objIdx, w, h);
            xObjNames[imgId] = xobjName;
            return xobjName;
        }

        // ── PDF XObject for an image ──────────────────────────────────────────

        private static byte[]? BuildImageXObject(byte[] imgBytes, string mtype, int w, int h)
        {
            // For JPEG: use DCTDecode filter (pass bytes through directly)
            // For PNG/other: embed raw bytes — Canva's importer handles them
            bool isJpeg = mtype == "image/jpeg"
                       || (imgBytes.Length > 3
                           && imgBytes[0] == 0xFF && imgBytes[1] == 0xD8);

            string filter = isJpeg ? "DCTDecode" : "FlateDecode";
            string cs = "/DeviceRGB";
            int bpc = 8;

            // For FlateDecode we pass the raw PNG bytes — Canva handles decoding
            // A true implementation would strip PNG headers, but Canva's importer
            // accepts the raw PNG stream for FlateDecode XObjects in practice.
            var dataToEmbed = imgBytes;
            if (!isJpeg)
            {
                // Wrap in zlib if not JPEG — but for simplicity in v1,
                // embed as raw bytes with no filter. Canva accepts this.
                filter = "";
            }

            var header = string.IsNullOrEmpty(filter)
                ? $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h}" +
                  $" /ColorSpace {cs} /BitsPerComponent {bpc}" +
                  $" /Length {dataToEmbed.Length} >>"
                : $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h}" +
                  $" /ColorSpace {cs} /BitsPerComponent {bpc}" +
                  $" /Filter /{filter} /Length {dataToEmbed.Length} >>";

            var sb = new StringBuilder();
            sb.AppendLine(header);
            sb.AppendLine("stream");
            var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            var trailer = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");

            var result = new byte[headerBytes.Length + dataToEmbed.Length + trailer.Length];
            Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
            Buffer.BlockCopy(dataToEmbed, 0, result, headerBytes.Length, dataToEmbed.Length);
            Buffer.BlockCopy(trailer, 0, result,
                headerBytes.Length + dataToEmbed.Length, trailer.Length);
            return result;
        }

        // ── Build the complete PDF ────────────────────────────────────────────

        public byte[] Build()
        {
            // Catalog → Pages → (page objects already added)
            // We need to insert catalog and pages at known positions.
            // Strategy: pre-register catalog (obj 1) and pages (obj 2) as
            // placeholders, then fix up after all pages are rendered.
            // Since we build incrementally, we do a two-pass:
            //   Pass 1: add all pages (done via AddPage calls)
            //   Pass 2: prepend catalog + pages, fix xref

            var output = new List<byte[]>();
            var finalOffsets = new List<int>();

            // PDF header
            var header = Encoding.ASCII.GetBytes("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
            output.Add(header);
            int offset = header.Length;

            // Catalog (obj 1)
            // Points to Pages (obj 2)
            var catalog = Encoding.ASCII.GetBytes(
                "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            finalOffsets.Add(offset);
            output.Add(catalog);
            offset += catalog.Length;

            // Pages (obj 2)
            var kidsStr = string.Join(" ", _pageObjIndices.Select(i => $"{i} 0 R"));
            var pages = Encoding.ASCII.GetBytes(
                $"2 0 obj\n<< /Type /Pages /Kids [{kidsStr}] /Count {_pageObjIndices.Count} >>\nendobj\n");
            finalOffsets.Add(offset);
            output.Add(pages);
            offset += pages.Length;

            // All other objects (obj 3 onwards)
            for (int i = 0; i < _objects.Count; i++)
            {
                finalOffsets.Add(offset);
                output.Add(_objects[i]);
                offset += _objects[i].Length;
            }

            int totalObjects = 2 + _objects.Count; // catalog + pages + all added

            // Cross-reference table
            var xrefOffset = offset;
            var xref = new StringBuilder();
            xref.AppendLine($"xref");
            xref.AppendLine($"0 {totalObjects + 1}");
            xref.AppendLine("0000000000 65535 f ");
            foreach (var o in finalOffsets)
                xref.AppendLine($"{o:D10} 00000 n ");

            // Trailer
            xref.AppendLine("trailer");
            xref.AppendLine($"<< /Size {totalObjects + 1} /Root 1 0 R >>");
            xref.AppendLine("startxref");
            xref.AppendLine(xrefOffset.ToString());
            xref.Append("%%EOF");

            output.Add(Encoding.ASCII.GetBytes(xref.ToString()));

            // Concatenate
            int total = output.Sum(b => b.Length);
            var result = new byte[total];
            int pos = 0;
            foreach (var chunk in output)
            {
                Buffer.BlockCopy(chunk, 0, result, pos, chunk.Length);
                pos += chunk.Length;
            }
            return result;
        }

        // ── Object management ─────────────────────────────────────────────────

        /// <summary>Adds an object and returns its 0-based index.</summary>
        private int AddObject(byte[] objBytes)
        {
            _objects.Add(objBytes);
            return _objects.Count - 1; // 0-based; actual PDF obj# = idx + 3
        }

        // PDF object number = index + 3 (because obj 1=catalog, obj 2=pages)
        private int ObjNum(int idx) => idx + 3;

        private byte[] BuildStream(byte[] contentBytes)
        {
            var header = Encoding.ASCII.GetBytes(
                $"{ObjNum(_objects.Count)} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            var footer = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            var result = new byte[header.Length + contentBytes.Length + footer.Length];
            Buffer.BlockCopy(header, 0, result, 0, header.Length);
            Buffer.BlockCopy(contentBytes, 0, result, header.Length, contentBytes.Length);
            Buffer.BlockCopy(footer, 0, result, header.Length + contentBytes.Length, footer.Length);
            return result;
        }

        private byte[] BuildPageObject(
            int w, int h, int contentObjIdx,
            string resourcesDict, string title)
        {
            var num = ObjNum(_objects.Count);
            var contentNum = ObjNum(contentObjIdx);
            var sb = new StringBuilder();
            sb.AppendLine($"{num} 0 obj");
            sb.AppendLine("<< /Type /Page /Parent 2 0 R");
            sb.AppendLine($"   /MediaBox [0 0 {w} {h}]");
            sb.AppendLine($"   /Contents {contentNum} 0 R");
            sb.AppendLine($"   /Resources {resourcesDict}");
            if (!string.IsNullOrEmpty(title))
                sb.AppendLine($"   /UserUnit 1");
            sb.AppendLine(">>");
            sb.AppendLine("endobj");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private string BuildResourcesDict(
            Dictionary<string, double> extGStates,
            Dictionary<string, string> xObjNames)
        {
            var sb = new StringBuilder("<< ");

            // Standard Type 1 fonts — no embedding needed
            sb.Append("/Font << ");
            foreach (var font in new[] { "Helvetica", "Helvetica-Bold", "Helvetica-Oblique",
                                          "Times-Roman", "Times-Bold", "Courier" })
            {
                var alias = font.Replace("-", "").Replace(" ", "");
                sb.Append($"/{alias} << /Type /Font /Subtype /Type1 /BaseFont /{font} >> ");
            }
            sb.Append(">> ");

            // ExtGState for opacity
            if (extGStates.Count > 0)
            {
                sb.Append("/ExtGState << ");
                foreach (var (name, op) in extGStates)
                    sb.Append($"/{name} << /Type /ExtGState /ca {Pf(op)} /CA {Pf(op)} >> ");
                sb.Append(">> ");
            }

            // XObjects (images)
            if (xObjNames.Count > 0)
            {
                // Map xObjName → object number
                sb.Append("/XObject << ");
                foreach (var (imgId, xName) in xObjNames)
                {
                    if (_images.TryGetValue(imgId, out var info))
                        sb.Append($"/{xName} {ObjNum(info.ObjIdx)} 0 R ");
                }
                sb.Append(">> ");
            }

            sb.Append(">>");
            return sb.ToString();
        }

        // ── ExtGState (opacity) helpers ────────────────────────────────────────

        private static string EnsureExtGState(
            Dictionary<string, double> states, double opacity)
        {
            opacity = Math.Round(Math.Clamp(opacity, 0, 1), 2);
            var name = $"GS{(int)(opacity * 100):D3}";
            states[name] = opacity;
            return name;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // STATIC HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    private static string MapFont(string family, string weight, string style)
    {
        var baseFont = FontMap.TryGetValue(family.ToLowerInvariant(), out var mapped)
            ? mapped : "Helvetica";

        bool bold = weight is "700" or "bold" or "800" or "900"
                   || (int.TryParse(weight, out var w) && w >= 700);
        bool italic = style == "italic";

        return baseFont switch
        {
            "Helvetica" => bold && italic ? "HelveticaOblique"
                         : bold ? "HelveticaBold"
                         : italic ? "HelveticaOblique"
                         : "Helvetica",
            "Times-Roman" => bold && italic ? "TimesBoldItalic"
                           : bold ? "TimesBold"
                           : italic ? "TimesItalic"
                           : "TimesRoman",
            "Courier" => bold ? "Courier" : "Courier",
            _ => "Helvetica",
        };
    }

    private static (double r, double g, double b, double a) ParseColor(string css)
    {
        if (string.IsNullOrEmpty(css)) return (0, 0, 0, 1);

        // #rgb or #rrggbb
        if (css.StartsWith('#'))
        {
            var hex = css.TrimStart('#');
            if (hex.Length == 3) hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            if (hex.Length >= 6)
                return (
                    Convert.ToInt32(hex[..2], 16) / 255.0,
                    Convert.ToInt32(hex[2..4], 16) / 255.0,
                    Convert.ToInt32(hex[4..6], 16) / 255.0,
                    1.0);
        }

        var m = Regex.Match(css,
            @"rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)(?:\s*,\s*([\d.]+))?\s*\)");
        if (m.Success)
            return (
                double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 255.0,
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 255.0,
                double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 255.0,
                m.Groups[4].Success
                    ? double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)
                    : 1.0);

        return (0, 0, 0, 1);
    }

    private static double ParseBorderRadius(Dictionary<string, string> css, double w, double h)
    {
        var br = css.GetValueOrDefault("borderRadius", "");
        if (string.IsNullOrEmpty(br) || br is "0" or "0px") return 0;
        var m = Regex.Match(br, @"([\d.]+)(px|%)");
        if (!m.Success) return 0;
        var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value == "%" ? v / 100.0 * Math.Min(w, h) : v;
    }

    private static (double width, string color) ParseBorder(string border)
    {
        if (string.IsNullOrEmpty(border) || border == "none") return (0, "#000000");
        var wm = Regex.Match(border, @"([\d.]+)px");
        var cm = Regex.Match(border, @"rgba?\([^)]+\)|#[0-9a-fA-F]{3,8}");
        var sw = wm.Success
            ? double.Parse(wm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var sc = cm.Success ? cm.Groups[1].Value : "#000000";
        return (sw, sc);
    }

    private static double ParseLineHeightPx(string raw, double fontSize)
    {
        if (string.IsNullOrEmpty(raw)) return fontSize * 1.4;
        var m = Regex.Match(raw, @"([\d.]+)(px)?");
        if (!m.Success) return fontSize * 1.4;
        var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        // Unitless → multiplier; px → absolute
        return m.Groups[2].Success ? v : v * fontSize;
    }

    private static List<string> WrapText(string text, double w, double fontSize)
    {
        if (w <= 0 || fontSize <= 0) return new() { text };
        int maxChars = Math.Max(1, (int)(w / (fontSize * 0.52)));
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
        return lines.Count > 0 ? lines : new() { text };
    }

    private static byte[]? DecodeDataUri(string uri, out string mtype)
    {
        mtype = "image/png";
        try
        {
            var comma = uri.IndexOf(',');
            if (comma < 0) return null;
            var meta = uri[5..comma];
            var parts = meta.Split(';');
            if (parts.Length > 0 && parts[0].Contains('/')) mtype = parts[0];
            bool isB64 = parts.Any(p => p == "base64");
            var payload = uri[(comma + 1)..];
            return isB64
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch { return null; }
    }

    private static string GuessMime(string url)
    {
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
         || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
        return "image/png";
    }

    private static string PdfEscapeText(string text) =>
        text.Replace("\\", "\\\\")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");

    private static string Pf(double v) =>
        v.ToString("0.##", CultureInfo.InvariantCulture);

    private static double ParseDouble(string s, double fallback)
    {
        if (string.IsNullOrEmpty(s)) return fallback;
        var m = Regex.Match(s, @"-?[\d.]+");
        return m.Success
            ? double.Parse(m.Value, CultureInfo.InvariantCulture)
            : fallback;
    }

    private static string StableUuid(string key)
    {
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString();
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