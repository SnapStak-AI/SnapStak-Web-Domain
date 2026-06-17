using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using SnapStak.Wasm.Client.Engine.Constructor;
using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Requests;
using SnapStak.Wasm.Client.Storage;

namespace SnapStak.Wasm.Client.Services;

/// <summary>
/// WASM port of ConstructorCom.
/// Key difference: scaffold zip is built in-memory (byte[]) instead of writing
/// to disk. The caller receives the zip bytes and triggers a browser download
/// via JS interop or the File System Access API.
/// </summary>
public sealed class ConstructorService
{
    private readonly IPillarStorage     _storage;
    private readonly RulesEngineService _rules;
    private readonly AiGatewayService   _ai;
    private readonly BehaviourAgentService _behaviour;
    private readonly ObjectiveService   _objective;

    public ConstructorService(IPillarStorage s, RulesEngineService r, AiGatewayService a,
        BehaviourAgentService b, ObjectiveService o)
    { _storage = s; _rules = r; _ai = a; _behaviour = b; _objective = o; }

    // ── GenerateAsync ─────────────────────────────────────────────────────────

    public async Task<WasmGenerateResult> GenerateAsync(GenerateRequest request)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrEmpty(request.ComponentId)) return GenError("componentId is required.");
            if (string.IsNullOrEmpty(request.UserUuid))    return GenError("uuid is required.");
            if (string.IsNullOrEmpty(request.Framework))   return GenError("framework is required.");

            var componentDir = _storage.ResolveComponentDir(request.UserUuid, request.ComponentId);

            // Update Objective pillar
            var viewportSnapshots   = _storage.ListViewportSnapshots(request.UserUuid, request.ComponentId, request.PageComponentId);
            var capturedBpWidths    = request.CapturedBreakpoints?.Length > 0
                ? request.CapturedBreakpoints
                : viewportSnapshots.Select(v => v.Width).ToArray();
            var sortedBpWidths = capturedBpWidths.Length > 0
                ? capturedBpWidths.Distinct().OrderBy(w => w).ToArray() : null;

            var modeInstruction = request.UnifiedMode
                ? "UNIFIED COMPONENT: Produce ONE single component that integrates ALL visible and hidden states."
                : "SEPARATE COMPONENTS: Produce each hidden component as a standalone separate component file.";

            _objective.UpdateObjective(request.UserUuid, request.ComponentId,
                request.Framework, request.ScreenWidthTarget,
                request.DeviceType, modeInstruction, sortedBpWidths);

            // Load pillars
            var svgString = _storage.ReadSvg(request.UserUuid, request.ComponentId, request.PageComponentId);
            if (string.IsNullOrEmpty(svgString)) return GenError($"Structure SVG not found: {request.ComponentId}");

            var (behaviourCss, behaviourJs) = _storage.ReadBehaviour(request.UserUuid, request.ComponentId);
            var influence  = _storage.ReadInfluence(componentDir, request.ComponentId);
            var objective  = _storage.ReadObjective(componentDir, request.ComponentId);
            var rawCss     = _storage.ReadCssJson(request.UserUuid, request.ComponentId);
            var hiddenEls  = _storage.ReadHiddenElements(request.UserUuid, request.ComponentId, request.PageComponentId);
            var sourceHtml = _storage.ReadSourceHtml(request.UserUuid, request.ComponentId, request.PageComponentId);

            // Run Behaviour AI if .md files missing
            if (!_storage.BehaviourMdExists(componentDir, request.ComponentId) && rawCss != null && !rawCss.IsEmpty)
            {
                await _behaviour.WriteBehaviourDescriptionsAsync(new BehaviourRequest
                {
                    UserUuid     = request.UserUuid,
                    ComponentId  = request.ComponentId,
                    ComponentDir = componentDir,
                    ApiKey       = request.ApiKey,
                    ModelId      = !string.IsNullOrEmpty(request.PassiveModel) ? request.PassiveModel : request.ModelId,
                    ComponentCss = rawCss,
                    ComponentJs  = request.ComponentJs,
                    SourceHtml   = sourceHtml,
                    Influence    = influence,
                });
                (behaviourCss, behaviourJs) = _storage.ReadBehaviour(request.UserUuid, request.ComponentId);
            }

            // Attach mode instruction to objective
            if (objective != null)
            {
                objective.AdditionalIntent = modeInstruction;
                if (sortedBpWidths?.Length > 0) objective.CapturedBreakpoints = sortedBpWidths;
            }

            // Deserialize SVG
            var deserialized = SvgDeserializer.DeserializeSVG(svgString);
            if (deserialized.Objects.Count == 0) return GenError("SVG deserialization produced no objects.");

            // Build SVG skeleton
            var svgSkeleton = BuildSvgSkeleton(svgString, deserialized.ImageMap, deserialized.IconMap);

            // Build prompt
            var promptParams = new ConteXCodePromptParams
            {
                ComponentId      = request.ComponentId,
                Framework        = request.Framework,
                StyleOutput      = request.StyleOutput,
                Language         = request.Language,
                SvgSkeleton      = svgSkeleton,
                Css              = string.Empty,
                RawCss           = rawCss,
                BehaviourCss     = behaviourCss ?? string.Empty,
                BehaviourJs      = behaviourJs  ?? string.Empty,
                Influence        = influence,
                Objective        = objective,
                HiddenElements   = hiddenEls,
                SourceHtml       = sourceHtml,
            };

            var prompt   = _rules.BuildConteXCodePrompt(promptParams);
            var rawAi    = await _ai.QueryAsync(prompt, request.ApiKey, request.ModelId);
            sw.Stop();

            var (componentCode, componentCss) = ParseAiResponse(rawAi);
            if (string.IsNullOrWhiteSpace(componentCode)) return GenError("AI returned empty componentCode.");

            componentCode = SanitiseFunctionName(componentCode, request.ComponentId);

            // Build in-memory zip
            var componentName = ToPascalCase(request.ComponentId);
            var zipBytes = BuildZipInMemory(
                request.ComponentId, componentName, request.Framework,
                request.StyleOutput, request.Language,
                componentCode, componentCss,
                deserialized.IconMap, deserialized.ImageMap);

            return new WasmGenerateResult
            {
                Success       = true,
                ComponentName = componentName,
                Framework     = request.Framework,
                StyleOutput   = request.StyleOutput,
                Language      = request.Language,
                ZipBytes      = zipBytes,
                Stats = new GenerateStats
                {
                    DurationMs  = sw.ElapsedMilliseconds,
                    Model       = request.ModelId,
                    PromptChars = prompt.Length,
                    ObjectCount = deserialized.Objects.Count,
                    FileCount   = 1,
                },
            };
        }
        catch (Exception ex) { return GenError(ex.Message); }
    }

    // ── In-memory zip ─────────────────────────────────────────────────────────

    private static byte[] BuildZipInMemory(
        string componentId, string componentName, string fw,
        string styleOutput, string language,
        string code, string css,
        Dictionary<string, string> iconMap,
        Dictionary<string, string> imageMap)
    {
        var files = new Dictionary<string, byte[]>();
        var isTs        = language.ToLowerInvariant() == "ts";
        var useTailwind = styleOutput.ToLowerInvariant() == "tailwind";
        var ext         = GetExt(fw, isTs);
        var projectName = Regex.Replace(componentName.ToLowerInvariant(), @"[^a-z0-9]", "-");

        files[$"src/components/{componentName}.{ext}"] = Utf8(code);
        if (!useTailwind && !string.IsNullOrWhiteSpace(css))
            files[$"src/styles/{componentName}.css"] = Utf8(css);
        foreach (var (id, svg) in iconMap)
            files[$"public/assets/icons/{id}.svg"] = Utf8(svg);
        foreach (var (id, url) in imageMap)
            files[$"public/assets/images/{id}.url.txt"] = Utf8(url);

        files["README.md"] = Utf8($"# {projectName}\n\nGenerated by SnapStak CON10X.\n\n```bash\nnpm install\nnpm run dev\n```\n");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                var entry = zip.CreateEntry($"{projectName}/{path}");
                using var s = entry.Open();
                s.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string GetExt(string fw, bool ts) => fw switch
    {
        "react" or "nextjs" or "solidjs" => ts ? "tsx" : "jsx",
        "vue"   or "nuxt"                => "vue",
        "svelte"                         => "svelte",
        "angular"                        => "ts",
        _                                => "js",
    };

    // ── SVG skeleton ──────────────────────────────────────────────────────────

    private static string BuildSvgSkeleton(string svg,
        Dictionary<string, string> imageMap, Dictionary<string, string> iconMap)
    {
        var s = svg;
        s = Regex.Replace(s, @"<defs[\s\S]*?</defs>\s*", string.Empty);
        s = Regex.Replace(s, @"<snapstak:[a-z]+[^>]*>[\s\S]*?</snapstak:[a-z]+>", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<rect([^>]*)fill=""url\(#imgpat_(el_[^)]+)\)""([^>]*?)>", m =>
        {
            var elId   = m.Groups[2].Value;
            var url    = imageMap.GetValueOrDefault(elId, "");
            var extM   = Regex.Match(url, @"\.([a-zA-Z0-9]+)(?:\?|$)");
            var ext    = extM.Success ? extM.Groups[1].Value.ToLowerInvariant() : "jpg";
            return $"<rect{m.Groups[1].Value}fill=\"[image:/assets/images/{elId}.{ext}]\"{m.Groups[3].Value}>";
        });
        if (iconMap.Count > 0)
            s = Regex.Replace(s, @"<svg\s([^>]*?)id=""(el_[^""]+)""([^>]*?)>([\s\S]*?)</svg>", m =>
            {
                var elId = m.Groups[2].Value;
                if (!iconMap.ContainsKey(elId)) return m.Value;
                var wM = Regex.Match(m.Groups[1].Value + m.Groups[3].Value, @"\bwidth=""([^""]+)""");
                var hM = Regex.Match(m.Groups[1].Value + m.Groups[3].Value, @"\bheight=""([^""]+)""");
                return $"<img src=\"/assets/icons/{elId}.svg\"{(wM.Success ? $" width=\"{wM.Groups[1].Value}\"" : "")}{(hM.Success ? $" height=\"{hM.Groups[1].Value}\"" : "")}>";
            });
        s = Regex.Replace(s, @"\s+filter=""url\([^)]+\)""", string.Empty);
        s = Regex.Replace(s, @"\s+clip-path=""url\([^)]+\)""", string.Empty);
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        return s;
    }

    // ── AI response parser ────────────────────────────────────────────────────

    private static (string Code, string Css) ParseAiResponse(string raw)
    {
        try
        {
            var cleaned = raw.Trim().TrimStart("```json".ToCharArray()).TrimStart("```".ToCharArray()).TrimEnd("```".ToCharArray()).Trim();
            var f = cleaned.IndexOf('{'); var l = cleaned.LastIndexOf('}');
            if (f >= 0 && l > f) cleaned = cleaned[f..(l + 1)];
            var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(cleaned);
            if (d != null)
                return (d.GetValueOrDefault("componentCode") ?? d.GetValueOrDefault("code", "")!,
                        d.GetValueOrDefault("componentCSS")  ?? d.GetValueOrDefault("css", "")!);
        }
        catch { }
        var cm = Regex.Match(raw, @"""componentCode""\s*:\s*""([\s\S]*?)(?<!\\)""");
        var sm = Regex.Match(raw, @"""componentCSS""\s*:\s*""([\s\S]*?)(?<!\\)""");
        return (cm.Success ? Unescape(cm.Groups[1].Value) : string.Empty,
                sm.Success ? Unescape(sm.Groups[1].Value) : string.Empty);
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\r", "\r")
         .Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static string SanitiseFunctionName(string code, string componentId)
    {
        var name = ToPascalCase(componentId);
        return Regex.Replace(code, @"export\s+default\s+function\s+([A-Za-z0-9_$-]+)\s*\(",
            _ => $"export default function {name}(");
    }

    public static string ToPascalCase(string id)
    {
        var fw = new HashSet<string> { "react","nextjs","vue","nuxt","angular","svelte","astro","html","css","tailwind" };
        var parts = id.Split('_').ToList();
        while (parts.Count > 1 && (Regex.IsMatch(parts[^1], @"^\d+$") || fw.Contains(parts[^1].ToLowerInvariant())))
            parts.RemoveAt(parts.Count - 1);
        return string.Join("_", parts)
            .Split(new[] { "--", "-", "_" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + (w.Length > 1 ? w[1..].ToLowerInvariant() : ""))
            .Aggregate(string.Concat);
    }

    private static WasmGenerateResult GenError(string msg) => new() { Success = false, Error = msg };
}

/// <summary>WASM generate result — carries zip bytes instead of a disk path.</summary>
public sealed class WasmGenerateResult
{
    public bool         Success       { get; set; }
    public string       ComponentName { get; set; } = string.Empty;
    public string       Framework     { get; set; } = string.Empty;
    public string       StyleOutput   { get; set; } = string.Empty;
    public string       Language      { get; set; } = string.Empty;
    public byte[]?      ZipBytes      { get; set; }
    public GenerateStats Stats        { get; set; } = new();
    public string?      Error         { get; set; }
}
