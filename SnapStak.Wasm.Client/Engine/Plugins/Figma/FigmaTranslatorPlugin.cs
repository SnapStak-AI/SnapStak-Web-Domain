// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SnapStak.Wasm.Client.Engine.Plugins;
using SnapStak.Wasm.Client.Models.Svg;

namespace SnapStak.Wasm.Client.Engine.Plugins.Figma;

// ─────────────────────────────────────────────────────────────────────────────
// FigmaTranslatorPlugin
//
// Produces a clean .figma.svg from the SvgNode tree.
// Built entirely from the in-memory tree — never reads the master .svg file.
//
// OUTPUT SVG contains ONLY:
//   Root <svg>  : xmlns, xmlns:xlink, width, height, viewBox, id
//   <defs>      : <filter>/<feDropShadow> for box-shadow effects
//   Per node    : <g id transform opacity filter>
//                   <rect fill stroke rx>
//                   <image href(base64) width height preserveAspectRatio clip-path>
//                   <text fill font-* text-anchor> / <tspan x y>
//
// OUTPUT SVG NEVER contains:
//   xmlns:snapstak, xmlns:inkscape          — SnapStak namespaces
//   data-snapstak-type, data-source-url,
//   data-title                              — root metadata
//   inkscape:label, data-w, data-h,
//   data-component-type, data-classes,
//   data-segment-id, data-display,
//   data-position, data-gap, data-hidden    — SnapStak node metadata
//   <snapstak:pagemap>                      — base64 CSS/JS pillar data
//   <!-- comments -->                       — build annotations
// ─────────────────────────────────────────────────────────────────────────────

public sealed class FigmaTranslatorPlugin : IConteXTranslatorPlugin
{
    public string Key => "figma";
    public string DisplayName => "Figma (SVG via Plugin API)";
    public string Version => "1.0.0";
    public string FileExtension => ".figma.svg";

    // ── Phase 1 ───────────────────────────────────────────────────────────────

    public IReadOnlyList<string> DeclareResources(TranslatorBundle bundle)
    {
        try
        {
            var urls = new HashSet<string>(StringComparer.Ordinal);
            CollectUrls(bundle.Desktop.Tree, urls);
            if (bundle.Mobile != null) CollectUrls(bundle.Mobile.Tree, urls);
            foreach (var seg in bundle.Segments) CollectUrls(seg.Tree, urls);
            foreach (var hc in bundle.HiddenComponents) CollectUrls(hc.Tree, urls);
            return urls.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static void CollectUrls(IReadOnlyList<SvgNode> tree, HashSet<string> out_)
    {
        foreach (var n in Flatten(tree))
            if (!string.IsNullOrEmpty(n.ImgSrc)
                && (n.ImgSrc.StartsWith("http://", StringComparison.Ordinal)
                 || n.ImgSrc.StartsWith("https://", StringComparison.Ordinal)))
                out_.Add(n.ImgSrc);
    }

    // ── Phase 2 ───────────────────────────────────────────────────────────────

    public byte[] Translate(
        TranslatorBundle bundle,
        IReadOnlyDictionary<string, byte[]> fetched)
    {
        if (bundle.Desktop.Tree.Count == 0) return Array.Empty<byte>();
        return Encoding.UTF8.GetBytes(
            Build(bundle.Desktop.Tree, bundle.Desktop.Width, bundle.Desktop.Height,
                  bundle.Desktop.Title ?? bundle.ComponentId, fetched));
    }

    // ── Builder ───────────────────────────────────────────────────────────────

    private static string Build(
        IReadOnlyList<SvgNode> tree,
        int w, int h, string title,
        IReadOnlyDictionary<string, byte[]> fetched)
    {
        var defs = new StringBuilder();
        var body = new StringBuilder();
        int fid = 0;

        foreach (var node in tree)
            WriteNode(node, body, defs, fetched, 1, 0, 0, ref fid);

        var sb = new StringBuilder(1024 * 64);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\"");
        sb.AppendLine("     xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
        sb.AppendLine($"     width=\"{w}\" height=\"{h}\"");
        sb.AppendLine($"     viewBox=\"0 0 {w} {h}\"");
        sb.AppendLine($"     id=\"{E(Slug(title))}\">");
        if (defs.Length > 0) { sb.AppendLine("  <defs>"); sb.Append(defs); sb.AppendLine("  </defs>"); }
        sb.Append(body);
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void WriteNode(
        SvgNode node, StringBuilder body, StringBuilder defs,
        IReadOnlyDictionary<string, byte[]> fetched,
        int depth, double px, double py, ref int fid)
    {
        var css = node.CssProps;
        bool fixed_ = css.GetValueOrDefault("position", "") is "fixed" or "sticky";
        double rx_ = Math.Round(fixed_ ? node.X : node.X - px);
        double ry_ = Math.Round(fixed_ ? node.Y : node.Y - py);
        double w = Math.Round(node.Width);
        double h = Math.Round(node.Height);

        bool has = !string.IsNullOrEmpty(node.TextContent)
                || !string.IsNullOrEmpty(node.ImgSrc)
                || !string.IsNullOrEmpty(node.SvgDataUri)
                || node.Children.Count > 0;
        if (w <= 0 && h <= 0 && !has) return;

        var ind = new string(' ', depth * 2);
        var op = css.GetValueOrDefault("opacity", "1");

        // Shadow filter
        string? fref = null;
        var bs = css.GetValueOrDefault("boxShadow", "");
        if (!string.IsNullOrEmpty(bs) && bs != "none")
        {
            var fi = $"f{fid++}";
            var (dx, dy, blur, col, fop) = ParseShadow(bs);
            defs.AppendLine($"    <filter id=\"{fi}\" x=\"-30%\" y=\"-30%\" width=\"160%\" height=\"160%\">");
            defs.AppendLine($"      <feDropShadow dx=\"{dx:F1}\" dy=\"{dy:F1}\" stdDeviation=\"{blur / 2:F1}\"");
            defs.AppendLine($"                    flood-color=\"{E(col)}\" flood-opacity=\"{fop:F2}\"/>");
            defs.AppendLine("    </filter>");
            fref = fi;
        }

        var (sc, sw) = ParseBorder(css);
        double rx = ParseRadius(css, w, h);

        // <g> — id, transform, opacity, filter ONLY
        var label = node.Label ?? node.SegmentId ?? node.ComponentType ?? node.Tag ?? "layer";
        var lid = $"{Slug(label)}-{Hash6(node.Id)}";
        body.Append($"{ind}<g id=\"{E(lid)}\" transform=\"translate({rx_},{ry_})\"");
        if (op != "1" && !string.IsNullOrEmpty(op)) body.Append($" opacity=\"{E(op)}\"");
        if (fref != null) body.Append($" filter=\"url(#{fref})\"");
        body.AppendLine(">");

        // Background rect
        var bg = css.GetValueOrDefault("backgroundColor", "");
        bool hbg = !string.IsNullOrEmpty(bg) && bg != "transparent"
                && bg != "rgba(0, 0, 0, 0)" && bg != "rgba(0,0,0,0)" && w > 0 && h > 0;
        if (hbg || sw > 0)
        {
            body.Append($"{ind}  <rect width=\"{w}\" height=\"{h}\" fill=\"{E(hbg ? bg : "none")}\"");
            if (rx > 0) body.Append($" rx=\"{rx:F1}\"");
            if (sw > 0) body.Append($" stroke=\"{E(sc)}\" stroke-width=\"{sw:F1}\"");
            body.AppendLine("/>");
        }

        // SVG icon
        if (!string.IsNullOrEmpty(node.SvgDataUri))
            body.AppendLine($"{ind}  <image href=\"{E(node.SvgDataUri)}\" x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" preserveAspectRatio=\"xMidYMid meet\"/>");

        // Raster image — remote URLs inlined as base64; broken hrefs are omitted
        else if (!string.IsNullOrEmpty(node.ImgSrc))
        {
            var href = InlineImg(node.ImgSrc, fetched);
            if (!string.IsNullOrEmpty(href))
            {
                var clip = rx > 0 ? $" clip-path=\"inset(0 round {rx:F1}px)\"" : "";
                body.AppendLine($"{ind}  <image href=\"{E(href)}\" x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" preserveAspectRatio=\"xMidYMid slice\"{clip}/>");
            }
        }

        // Text
        var txt = node.TextContent?.Trim();
        if (!string.IsNullOrEmpty(txt)) WriteText(body, css, txt, ind, w, h);

        // Children
        foreach (var c in node.Children)
            WriteNode(c, body, defs, fetched, depth + 1, node.X, node.Y, ref fid);

        body.AppendLine($"{ind}</g>");
    }

    // ── Text (matches SvgSerializer.RenderText exactly) ───────────────────────

    private static void WriteText(
        StringBuilder sb, Dictionary<string, string> css,
        string text, string ind, double w, double h)
    {
        var color = css.GetValueOrDefault("color", "#000000");
        var fs = ParsePx(css.GetValueOrDefault("fontSize", "14px"), 14);
        var fw = css.GetValueOrDefault("fontWeight", "400");
        var ff = css.GetValueOrDefault("fontFamily", "sans-serif");
        var ta = css.GetValueOrDefault("textAlign", "start");
        var tx = css.GetValueOrDefault("textTransform", "");
        var td = css.GetValueOrDefault("textDecoration", "");
        var pl = ParsePx(css.GetValueOrDefault("paddingLeft", "0px"), 0);
        var pr = ParsePx(css.GetValueOrDefault("paddingRight", "0px"), 0);
        var pt = ParsePx(css.GetValueOrDefault("paddingTop", "0px"), 0);
        var jc = css.GetValueOrDefault("justifyContent", "");
        var disp = css.GetValueOrDefault("display", "block");
        var lhRaw = css.GetValueOrDefault("lineHeight", "");
        double lh = string.IsNullOrEmpty(lhRaw) ? fs * 1.3 : ParsePx(lhRaw, fs * 1.3);

        if (tx == "uppercase") text = text.ToUpperInvariant();
        else if (tx == "lowercase") text = text.ToLowerInvariant();

        bool fxc = (disp is "flex" or "inline-flex") && jc == "center";
        string anc; double bx;
        if (fxc || ta == "center") { anc = "middle"; bx = w / 2.0; }
        else if (ta is "right" or "end") { anc = "end"; bx = w - pr; }
        else { anc = "start"; bx = pl; }

        var lines = Wrap(text, Math.Max(w - pl - pr, 20), fs);
        double sy = lines.Count == 1 && h > 0
            ? (pt > 0 ? pt + fs * 0.85 : (h + fs * 0.75) / 2.0)
            : (pt > 0 ? pt + fs * 0.85 : lh);

        sb.Append($"{ind}  <text fill=\"{E(color)}\" font-size=\"{fs:F0}px\" font-weight=\"{E(fw)}\" font-family=\"{E(ff)}\" text-anchor=\"{anc}\"");
        if (!string.IsNullOrEmpty(td) && td != "none") sb.Append($" text-decoration=\"{E(td)}\"");
        sb.AppendLine(">");
        for (int i = 0; i < lines.Count; i++)
            sb.AppendLine($"{ind}    <tspan x=\"{bx:F1}\" y=\"{(sy + i * lh):F1}\">{E(lines[i])}</tspan>");
        sb.AppendLine($"{ind}  </text>");
    }

    // ── CSS parsers ───────────────────────────────────────────────────────────

    private static (string c, double w) ParseBorder(Dictionary<string, string> css)
    {
        var b = css.GetValueOrDefault("border", "");
        if (string.IsNullOrEmpty(b) || b.StartsWith("0px") || b is "none" or "medium" or "initial") return ("none", 0);
        var wm = Regex.Match(b, @"([\d.]+)px");
        var cm = Regex.Match(b, @"(rgba?\([^)]+\)|#[0-9a-fA-F]{3,8})");
        double sw = wm.Success ? double.Parse(wm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return (b.Contains("none") || sw < 0.1) ? ("none", 0) : (cm.Success ? cm.Groups[1].Value : "#000000", sw);
    }

    private static double ParseRadius(Dictionary<string, string> css, double w, double h)
    {
        var br = css.GetValueOrDefault("borderRadius", "");
        if (string.IsNullOrEmpty(br) || br is "0" or "0px") return 0;
        var m = Regex.Match(br, @"([\d.]+)(px|%)");
        if (!m.Success) return 0;
        var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return Math.Min(m.Groups[2].Value == "%" ? v / 100.0 * Math.Min(w, h) : v, Math.Min(w, h) / 2.0);
    }

    private static (double dx, double dy, double blur, string color, double op) ParseShadow(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "none") return (0, 2, 4, "#000000", 0.1);
        var nums = Regex.Matches(s, @"-?[\d.]+px").Cast<Match>()
            .Select(m => double.Parse(m.Value.Replace("px", ""), CultureInfo.InvariantCulture)).ToList();
        var cm = Regex.Match(s, @"rgba?\([^)]+\)|#[0-9a-fA-F]{3,8}");
        var raw = cm.Success ? cm.Value : "rgba(0,0,0,0.2)";
        double op = 0.2;
        var om = Regex.Match(raw, @"rgba\([^,]+,[^,]+,[^,]+,\s*([\d.]+)\)");
        if (om.Success) op = double.Parse(om.Groups[1].Value, CultureInfo.InvariantCulture);
        var rgb = Regex.Replace(raw, @",\s*[\d.]+\)", ")").Replace("rgba(", "rgb(");
        return (nums.Count > 0 ? nums[0] : 0, nums.Count > 1 ? nums[1] : 2,
                nums.Count > 2 ? Math.Abs(nums[2]) : 4, rgb, op);
    }

    private static double ParsePx(string v, double fb)
    {
        var m = Regex.Match(v ?? "", @"[\d.]+");
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : fb;
    }

    private static List<string> Wrap(string text, double maxW, double fs)
    {
        if (maxW <= 0 || fs <= 0) return new() { text };
        int max = Math.Max(1, (int)(maxW / (fs * 0.52)));
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>(); var cur = new StringBuilder();
        foreach (var word in words)
        {
            var t = cur.Length > 0 ? cur + " " + word : word;
            if (t.Length <= max) cur = new StringBuilder(t);
            else { if (cur.Length > 0) lines.Add(cur.ToString()); cur = new StringBuilder(word); }
        }
        if (cur.Length > 0) lines.Add(cur.ToString());
        return lines.Count > 0 ? lines : new() { text };
    }

    // ── Image ─────────────────────────────────────────────────────────────────

    private static string InlineImg(string src, IReadOnlyDictionary<string, byte[]> f)
    {
        if (src.StartsWith("data:", StringComparison.Ordinal)) return src;
        if (f.TryGetValue(src, out var b) && b.Length > 0)
            return $"data:{Mime(src)};base64,{Convert.ToBase64String(b)}";
        return string.Empty;
    }

    private static string Mime(string url)
    {
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
         || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
        return "image/png";
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string Slug(string s) =>
        Regex.Replace(s.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');

    private static string Hash6(string s)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)))[..6].ToLowerInvariant();
    }

    private static string E(string? v)
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        return v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static IEnumerable<SvgNode> Flatten(IReadOnlyList<SvgNode> tree)
    {
        var s = new Stack<SvgNode>();
        foreach (var n in tree) s.Push(n);
        while (s.Count > 0) { var n = s.Pop(); yield return n; foreach (var c in n.Children) s.Push(c); }
    }
}