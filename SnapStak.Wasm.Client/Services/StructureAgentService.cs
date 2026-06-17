using SnapStak.Wasm.Client.Engine.Plugins;
using SnapStak.Wasm.Client.Engine.StructureAgent;
using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Requests;
using SnapStak.Wasm.Client.Models.Svg;
using SnapStak.Wasm.Client.Storage;
using Newtonsoft.Json;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;

namespace SnapStak.Wasm.Client.Services;

/// <summary>
/// WASM port of StructureAgentCom.
/// Receives DOM snapshots from the browser extension via JS interop,
/// builds SVG trees, and writes all pillar files to IndexedDB.
///
/// Also runs every registered translator plugin after the master SVG is
/// written — one additional file per plugin, using the plugin's self-declared
/// extension. Plugins are discovered by reflection via TranslatorPluginHost.
/// </summary>
public sealed class StructureAgentService
{
    private readonly IPillarStorage _storage;
    private readonly TranslatorPluginHost _translators;

    // HttpClient used by TranslateAllAsync Phase 1 to fetch remote image URLs
    // declared by translator plugins (e.g. CanvaTranslatorPlugin embeds images
    // directly in the PDF; PenpotTranslatorPlugin embeds them in the archive).
    // Injected from Program.cs — see the AddScoped<StructureAgentService> call.
    // Requires CANVA_CLIENT_ID / CANVA_CLIENT_SECRET / CANVA_REDIRECT_URI to be
    // set in the environment before the server starts (see canva.env).
    private readonly System.Net.Http.HttpClient? _http;

    public StructureAgentService(
        IPillarStorage storage,
        TranslatorPluginHost translators,
        System.Net.Http.HttpClient? http = null)
    {
        _storage = storage;
        _translators = translators;
        _http = http;
    }

    public StructureResult Transform(TransformRequest request)
    {
        try
        {
            if (request.DomSnapshot == null)
                return Error("domSnapshot is required.");
            if (request.DomSnapshot.Elements.Count == 0)
                return Error("domSnapshot.elements is required.");
            if (string.IsNullOrEmpty(request.Url))
                return Error("url is required.");
            if (string.IsNullOrEmpty(request.ComponentId))
                return Error("componentId is required.");

            var componentDir = _storage.ResolveComponentDir(request.UserUuid, request.ComponentId);
            var styleMap = StructureService.BuildStyleMap(request.DomSnapshot.Elements);
            var tree = StructureService.BuildSVGTree(request.DomSnapshot.Elements);
            StructureService.ApplyCssProps(tree, styleMap);

            var uri = Uri.TryCreate(request.Url, UriKind.Absolute, out var parsed)
                ? parsed.Host : request.Url;

            var svgOptions = new SvgTreeOptions
            {
                Width = request.DomSnapshot.PageWidth,
                Height = request.DomSnapshot.PageHeight,
                SourceUrl = request.Url,
                Title = uri,
                PageMap = request.DomSnapshot.PageMap,
            };

            var svgString = SvgSerializer.SerializeTreeSVG(tree, svgOptions);
            _storage.WriteSvg(componentDir, request.ComponentId, svgString);

            // ── Run every translator plugin against the canonical tree ───────
            // One extra file per plugin, written alongside the master SVG.
            // Isolated failures are logged — the main pipeline is never blocked.
            _ = RunTranslatorsAsync(
                tree,
                svgOptions,
                request.ComponentId!,
                componentDir,
                request.Influence,
                request.Objective,
                request.HiddenComponents);

            // Write behaviour source data
            WritePageMapCssJs(request.DomSnapshot.PageMap, componentDir, request.ComponentId);
            if (request.ComponentJs != null)
                _storage.WriteJsJson(componentDir, request.ComponentId, request.ComponentJs);
            WriteSourceHtml(request.DomSnapshot.PageMap, componentDir, request.ComponentId);

            // Write hidden elements and components
            if (request.DomSnapshotHidden != null && request.DomSnapshotHidden.Elements.Count > 0)
                _storage.WriteHiddenElements(componentDir, request.ComponentId, request.DomSnapshotHidden.Elements);
            if (request.HiddenComponents.Count > 0)
                _storage.WriteHiddenComponents(componentDir, request.ComponentId, request.HiddenComponents);

            // Write Influence and Objective if provided
            if (request.Influence != null)
                _storage.WriteInfluence(componentDir, request.ComponentId, request.Influence);
            if (request.Objective != null)
                _storage.WriteObjective(componentDir, request.ComponentId, request.Objective);

            // ── Process mobile viewport snapshots (non-fatal) ─────────────────
            // background.js embeds the 390px mobile snapshot in viewportSnapshots[].
            // Process here so _viewport_390px.svg and _viewport_390px_css.json are
            // written alongside the desktop files. Fully isolated — never breaks main flow.
            try
            {
                foreach (var vp in request.ViewportSnapshots)
                {
                    if (vp.DomSnapshot == null) continue;
                    vp.UserUuid = request.UserUuid;
                    vp.ComponentId = request.ComponentId!;
                    var vpResult = SaveViewportSnapshot(vp);
                    if (!vpResult.Success)
                        Console.WriteLine($"[StructureAgent] ⚠️ Viewport snapshot failed: {vpResult.Error}");
                }
            }
            catch (Exception vpEx)
            {
                Console.WriteLine($"[StructureAgent] ⚠️ Viewport processing failed (non-fatal): {vpEx.Message}");
            }

            return new StructureResult
            {
                Success = true,
                SvgString = svgString,
                ObjectCount = request.DomSnapshot.Elements.Count,
                Width = request.DomSnapshot.PageWidth,
                Height = request.DomSnapshot.PageHeight,
                SourceUrl = request.Url,
            };
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    public StructureResult SaveViewportSnapshot(ViewportSnapshotRequest request)
    {
        try
        {
            if (request.DomSnapshot == null) return Error("domSnapshot is required.");
            var componentDir = _storage.ResolveComponentDir(request.UserUuid, request.ComponentId);

            // Save full JSON snapshot — includes CSS/JS/hidden components so Convert
            // time has all pillar data for this viewport width.
            _storage.WriteViewportSnapshot(componentDir, request.ComponentId,
                request.ViewportWidth, new
                {
                    viewportWidth = request.ViewportWidth,
                    deviceType = request.DeviceType,
                    domSnapshot = request.DomSnapshot,
                    hiddenComponents = request.HiddenComponents,
                    componentCSS = request.ComponentCss,
                    componentJS = request.ComponentJs,
                });

            // ── Build mobile Structure SVG ─────────────────────────────────────
            // JSON is data transport. The SVG is the Structure pillar source of truth.
            var styleMap = StructureService.BuildStyleMap(request.DomSnapshot.Elements);
            var mobileTree = StructureService.BuildSVGTree(request.DomSnapshot.Elements);
            StructureService.ApplyCssProps(mobileTree, styleMap);

            var mobileOptions = new SvgTreeOptions
            {
                Width = request.DomSnapshot.PageWidth > 0 ? request.DomSnapshot.PageWidth : request.ViewportWidth,
                Height = request.DomSnapshot.PageHeight > 0 ? request.DomSnapshot.PageHeight : request.ViewportWidth * 2,
                Title = $"{request.ComponentId}_viewport_{request.ViewportWidth}px",
                PageMap = request.DomSnapshot.PageMap,
            };

            var mobileSvg = SvgSerializer.SerializeTreeSVG(mobileTree, mobileOptions);
            _storage.WriteViewportSvg(componentDir, request.ComponentId, request.ViewportWidth, mobileSvg);

            // ── Run translator plugins against the mobile tree ────────────────
            // Viewport outputs are tagged with the width so they don't collide
            // with the master transform output. Example filename on disk:
            //   <comp>_viewport_390px.penpot.svg
            var viewportComponentId = $"{request.ComponentId}_viewport_{request.ViewportWidth}px";
            _ = RunTranslatorsAsync(
                mobileTree,
                mobileOptions,
                viewportComponentId,
                componentDir,
                influence: null,
                objective: null,
                hiddenComponents: null);

            // ── Write filtered viewport CSS JSON — mobile Behaviour pillar ─────
            // Filter to only rules whose selectors match classes present in the
            // mobile SVG. Without this file the mobile Behaviour pillar is empty.
            if (request.ComponentCss != null)
                WriteFilteredViewportCss(componentDir, request.ComponentId,
                    request.ViewportWidth, mobileSvg, request.ComponentCss);

            Console.WriteLine($"[StructureAgent] ✅ Viewport snapshot saved — " +
                $"{request.ViewportWidth}px ({request.DomSnapshot.Elements.Count} elements)");

            return new StructureResult { Success = true, ViewportWidth = request.ViewportWidth };
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    // ── Translator plugin invocation ──────────────────────────────────────────
    //
    // Calls every discovered translator plugin via the host. Each plugin returns
    // bytes; the storage layer writes them under {componentId}{FileExtension}.
    // Plugin failures are isolated — one broken plugin never blocks the others
    // or the main pipeline.
    private async Task RunTranslatorsAsync(
        List<SvgNode> tree,
        SvgTreeOptions options,
        string componentId,
        string componentDir,
        InfluenceData? influence,
        ObjectiveData? objective,
        IReadOnlyList<HiddenComponent>? hiddenComponents)
    {
        if (_translators.Plugins.Count == 0) return;

        var bundle = TranslatorPluginHost.BuildBundle(
            tree, options, componentId, influence, objective, hiddenComponents);

        foreach (var output in await _translators.TranslateAllAsync(bundle, _http).ConfigureAwait(false))
        {
            if (output.Bytes.Length == 0)
            {
                if (output.Error != null)
                    Console.WriteLine(
                        $"[StructureAgent] ⚠️ Translator '{output.PluginKey}' failed: "
                        + output.Error.Message);
                // Empty bytes with no Error == plugin declined for this tree. Silent skip.
                continue;
            }

            try
            {
                _storage.WriteTranslatorOutput(
                    componentDir, componentId, output.FileExtension, output.Bytes);
                Console.WriteLine(
                    $"[StructureAgent] ✅ Translator '{output.PluginKey}' → "
                    + $"{componentId}{output.FileExtension} ({output.Bytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[StructureAgent] ❌ Failed to persist output from '{output.PluginKey}': "
                    + ex.Message);
            }
        }
    }

    private void WriteFilteredViewportCss(
        string componentDir, string componentId, int viewportWidth,
        string mobileSvg, Models.Css.CssJson css)
    {
        try
        {
            // Extract all class tokens from data-classes="..." attributes in the SVG
            var svgClasses = new HashSet<string>();
            foreach (Match m in Regex.Matches(mobileSvg, @"data-classes=""([^""]*)"""))
                foreach (var cls in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    svgClasses.Add(cls);

            // A selector is relevant if it has no class tokens (tag/id/universal)
            // or at least one of its class tokens is present in the SVG.
            bool SelectorRelevant(string? sel)
            {
                if (string.IsNullOrEmpty(sel)) return false;
                var classTokens = Regex.Matches(sel, @"\.(-?[_a-zA-Z][^\s.#:\[\],+~>{}]*)")
                                       .Select(m => m.Groups[1].Value)
                                       .ToList();
                return classTokens.Count == 0 || classTokens.Any(c => svgClasses.Contains(c));
            }

            var filtered = new Models.Css.CssJson();

            if (css.Matched.Count > 0)
                filtered.Matched.AddRange(css.Matched.Where(r => SelectorRelevant(r.Selector)));
            if (css.Behavior.Count > 0)
                filtered.Behavior.AddRange(css.Behavior.Where(r => SelectorRelevant(r.Selector)));
            if (css.Media.Count > 0)
            {
                foreach (var block in css.Media)
                {
                    var rules = block.Rules.Where(r => SelectorRelevant(r.Selector)).ToList();
                    if (rules.Count > 0)
                        filtered.Media.Add(new Models.Css.MediaBlock
                        {
                            Media = block.ResolvedMediaQuery,
                            Rules = rules,
                        });
                }
            }
            if (css.Keyframes.Count > 0)
                filtered.Keyframes.AddRange(css.Keyframes);

            if (!filtered.IsEmpty)
            {
                _storage.WriteViewportCssJson(componentDir, componentId, viewportWidth, filtered);
                Console.WriteLine($"[StructureAgent] ✅ Viewport CSS JSON saved — " +
                    $"{componentId}_viewport_{viewportWidth}px_css.json " +
                    $"({svgClasses.Count} classes, {filtered.Matched.Count} matched rules)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StructureAgent] ❌ Viewport CSS JSON save failed (non-fatal): {ex.Message}");
        }
    }

    // ── Segment extraction ────────────────────────────────────────────────────

    public string? ExtractSegmentFromSVG(string svgContent, string segmentId, string pageComponentId)
    {
        var escaped = Regex.Escape(segmentId);
        var openTagRe = new Regex($@"<g\s[^>]*data-segment-id=""{escaped}""[^>]*>");
        var match = openTagRe.Match(svgContent);
        if (!match.Success) return null;

        int depth = 0, i = match.Index, len = svgContent.Length;
        while (i < len)
        {
            if (svgContent[i] == '<' && i + 1 < len && svgContent[i + 1] == 'g' &&
                i + 2 < len && (svgContent[i + 2] == ' ' || svgContent[i + 2] == '>'))
            { depth++; i += 2; }
            else if (svgContent[i] == '<' && svgContent.AsSpan(i).StartsWith("</g>"))
            { depth--; if (depth == 0) { i += 4; break; } i += 4; }
            else i++;
        }

        var gContent = svgContent[match.Index..i];
        var wMatch = Regex.Match(match.Value, @"data-w=""(\d+)""");
        var hMatch = Regex.Match(match.Value, @"data-h=""(\d+)""");
        var w = wMatch.Success ? int.Parse(wMatch.Groups[1].Value) : 0;
        var h = hMatch.Success ? int.Parse(hMatch.Groups[1].Value) : 0;

        var normalised = new Regex(@"transform=""translate\([\d.-]+\s*,\s*[\d.-]+\)""")
            .Replace(gContent, @"transform=""translate(0,0)""", 1);

        var defsMatch = Regex.Match(svgContent, @"<defs>([\s\S]*?)</defs>");
        var defs = defsMatch.Success ? $"\n  <defs>{defsMatch.Groups[1].Value}</defs>" : string.Empty;

        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg"
     xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape"
     width="{w}" height="{h}"
     viewBox="0 0 {w} {h}"
     data-snapstak-type="tree"
     data-segment-id="{segmentId}"
     data-page-component-id="{pageComponentId}">{defs}
{normalised}
</svg>
""";
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void WritePageMapCssJs(IReadOnlyList<PageMapEntry> pageMap, string componentDir, string componentId)
    {
        var mergedCss = new Models.Css.CssJson();
        foreach (var s in pageMap)
        {
            if (string.IsNullOrEmpty(s.CssB64)) continue;
            try
            {
                var css = JsonConvert.DeserializeObject<Models.Css.CssJson>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(s.CssB64)));
                if (css != null)
                {
                    mergedCss.Matched.AddRange(css.Matched);
                    mergedCss.Behavior.AddRange(css.Behavior);
                    mergedCss.Media.AddRange(css.Media);
                    mergedCss.Keyframes.AddRange(css.Keyframes);
                }
            }
            catch { }
        }
        if (!mergedCss.IsEmpty)
            _storage.WriteCssJson(componentDir, componentId, mergedCss);
    }

    private void WriteSourceHtml(IReadOnlyList<PageMapEntry> pageMap, string componentDir, string componentId)
    {
        foreach (var s in pageMap)
        {
            if (string.IsNullOrEmpty(s.Tag) || string.IsNullOrEmpty(s.HtmlB64)) continue;
            try
            {
                var html = Encoding.UTF8.GetString(Convert.FromBase64String(s.HtmlB64));
                if (html.Length > 0) _storage.WriteSourceHtml(componentDir, componentId, s.Tag, html);
            }
            catch { }
        }
    }

    private static StructureResult Error(string msg) => new() { Success = false, Error = msg };
}