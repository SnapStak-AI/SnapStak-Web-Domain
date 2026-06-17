using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Requests;

namespace SnapStak.Wasm.Client.Engine.RulesEngine.Prompts;

/// <summary>
/// THE CONTEX LAW — Code Generation Prompt.
/// "Structure + Behaviour + Influence + Objective fed to AI eliminates hallucination.
///  In any domain. Every time."
///
/// Builds the master prompt for converting a captured web component into
/// production-ready framework code. All four ConteX pillars assembled here.
/// Port of buildConteXCodePrompt() in openRouterPromptManager.js.
/// </summary>
internal static class ConteXCodePrompt
{
    public static string Build(ConteXCodePromptParams p)
    {
        var fw = (p.Framework ?? "react").ToLowerInvariant();
        var style = (p.StyleOutput ?? "css").ToLowerInvariant();
        var lang = (p.Language ?? "js").ToLowerInvariant();
        var useTailwind = style == "tailwind";
        var useTypeScript = lang == "ts";

        var influenceBlock = BuildInfluenceBlock(p.Influence);
        var objectiveBlock = BuildObjectiveBlock(p.Objective);
        var frameworkBlock = BuildFrameworkBlock(fw, useTailwind, useTypeScript);
        var hiddenBlock = BuildHiddenBlock(p.HiddenElements, p.HiddenComponents);
        var sourceHtmlBlock = p.SourceHtml != null
            ? $"\n\n## SOURCE HTML (Pillar 1C — class name resolution only)\n```html\n{p.SourceHtml.Substring(0, Math.Min(p.SourceHtml.Length, 8000))}\n```"
            : string.Empty;

        return $$"""
You are ConteX — a Symbiotic Agentic Context Engine implementing the ConteX Law:
"Structure + Behaviour + Influence + Objective fed to AI eliminates hallucination. In any domain. Every time."

You are a precise front-end code generator. Your task is to produce production-ready {{fw}} code that exactly reconstructs the component described below. You transcribe — you do not interpret.

You must return ONLY a valid JSON object with exactly two keys: "componentCode" and "componentCSS".
No markdown fences. No explanation. No preamble. No closing text.

---

## PILLAR 1: STRUCTURE — What the component IS (exact SVG geometry — source of truth)

The following SVG defines the exact dimensions, positions, hierarchy, colours, typography, and component types of every element. The SVG is always the source of truth. Treat it as immutable fact.

Reading rules:
- translate(X,Y) = exact pixel position of element within its parent
- data-w / data-h = exact pixel dimensions
- data-gap = exact flex/grid gap between children (apply verbatim)
- data-margin="auto" = margin:0 auto (horizontally centered block)
- data-position="absolute" = position:absolute with the stated offsets
- data-display = computed display value (flex, grid, inline-flex, block)
- data-hidden="true" = element is display:none at this viewport (do NOT render it visible)
- data-classes-active = CSS utility classes active at this viewport — use for layout decisions
- data-classes-inactive = CSS utility classes inactive at this viewport — use ONLY to write correct @media rules
- inkscape:label = component type (Button, NavLink, Image, Icon, Text, Container, etc.)
- <text> fill/font-size/font-weight/font-family/text-anchor = exact computed typography values — apply verbatim
- [image:path] = background image — use as src or background-image
- <img src="/assets/icons/{id}.svg"> = SVG icon — reference this exact path in output

```svg
{{p.SvgSkeleton}}
```

---

## PILLAR 1B: STRUCTURE — Deterministic CSS (exact computed values)

```css
{{p.Css}}
```

---

## PILLAR 2: BEHAVIOUR — What the component DOES (semantic descriptions)

### CSS Behaviour
{{(string.IsNullOrWhiteSpace(p.BehaviourCss) ? "(CSS behaviour description not available — use SVG geometry as sole source of truth)" : p.BehaviourCss)}}

### JS Behaviour
{{(string.IsNullOrWhiteSpace(p.BehaviourJs) ? "(No JavaScript behaviour detected for this component)" : p.BehaviourJs)}}

---

## PILLAR 3: INFLUENCE — What SHAPED the component (live capture environment)

{{influenceBlock}}

---

## PILLAR 4: OBJECTIVE — What the component MUST ACHIEVE (confirmed target)

{{objectiveBlock}}

---
{{sourceHtmlBlock}}
{{hiddenBlock}}

---

## FRAMEWORK

{{frameworkBlock}}

---

## OUTPUT RULES

1. Return ONLY: {"componentCode": "...", "componentCSS": "..."}
2. componentCode: complete {{fw}} component, self-contained, no external dependencies except the framework.
3. componentCSS: {{(useTailwind ? "empty string — all styles via Tailwind classes in componentCode." : "all CSS rules scoped to this component. No global resets.")}}
4. Preserve ALL text content exactly as shown in the SVG.
5. Preserve ALL colours exactly as shown in the SVG and CSS.
6. Preserve ALL dimensions proportionally for the target screen width.
7. @media query breakpoints must match the Captured Breakpoint Widths in Pillar 4 — not generic device ranges.
8. data-classes-active: use for layout decisions at the captured viewport.
9. data-classes-inactive: use ONLY to write correct @media rules. NEVER apply inactive classes to the rendered layout.
10. data-gap values must be applied verbatim as gap: Npx on flex/grid containers.
11. data-margin="auto" must be applied as margin: 0 auto.
12. Implement ALL JavaScript behaviour described in Pillar 2 JS Behaviour — interactions, state changes, animations.
13. The function/component name must be valid PascalCase derived from the componentId: {{ToPascalCase(p.ComponentId)}}
14. DO NOT include <script> tags in componentCSS. DO NOT include raw CSS in componentCode.
""";
    }

    private static string BuildInfluenceBlock(InfluenceData? inf)
    {
        if (inf == null) return "Not available.";
        return $"""
Browser:            {inf.BrowserName ?? "unknown"} {inf.BrowserVersion ?? ""}
OS:                 {inf.OsName ?? "unknown"} {inf.OsVersion ?? ""}
Viewport:           {inf.ViewportWidth?.ToString() ?? "?"} × {inf.ViewportHeight?.ToString() ?? "?"}px
Device Pixel Ratio: {inf.DevicePixelRatio?.ToString() ?? "1"}
Colour Scheme:      {inf.PrefersColorScheme}
Reduced Motion:     {inf.PrefersReducedMotion}
""";
    }

    private static string BuildObjectiveBlock(ObjectiveData? obj)
    {
        if (obj == null) return "Not available.";
        var allBp = obj.AllBreakpoints == 1;
        var bpWidths = obj.CapturedBreakpoints != null && obj.CapturedBreakpoints.Length > 0
            ? string.Join(", ", obj.CapturedBreakpoints.Select(w => $"{w}px"))
            : (obj.ScreenWidthTarget.HasValue ? $"{obj.ScreenWidthTarget}px" : "not captured");

        return $"""
Target Device:             {obj.DeviceType ?? "desktop"}
All Breakpoints Captured:  {(allBp ? "YES" : "NO")}
Captured Breakpoint Widths:{bpWidths}
Target Screen Width:       {obj.ScreenWidthTarget?.ToString() ?? "1440"}px
Framework:                 {obj.Framework ?? "react"}
{(string.IsNullOrWhiteSpace(obj.AdditionalIntent) ? "" : $"Additional Intent:          {obj.AdditionalIntent}")}
""";
    }

    private static string BuildFrameworkBlock(string fw, bool tailwind, bool ts)
    {
        var tsNote = ts ? " TypeScript" : " JavaScript";

        return fw switch
        {
            "react" or "nextjs" => $"React{(fw == "nextjs" ? " / Next.js" : "")} ({tsNote})\n- Functional components with hooks (useState, useEffect, useCallback)\n- JSX syntax with className instead of class\n- camelCase event handlers (onClick, onChange)\n- Default export\n{(fw == "nextjs" ? "- Use 'use client' directive for interactive components\n- Use next/image for optimised images\n- Use next/link for navigation\n" : "")}",
            "vue" or "nuxt" => $"Vue 3 Composition API{(fw == "nuxt" ? " / Nuxt 3" : "")} ({tsNote})\n- <script setup> syntax\n- ref() and reactive() for state\n- computed() for derived values\n- v-bind (:) and v-on (@) shorthand\n- Structure: <script setup>, <template>, <style scoped>",
            "svelte" => $"Svelte ({tsNote})\n- Reactive declarations with $: syntax\n- {{#if}}, {{#each}}, {{#await}} blocks\n- on: directive for events\n- bind: for two-way binding",
            "angular" => $"Angular ({tsNote})\n- @Component decorator\n- TypeScript with proper typing\n- Angular template syntax: *ngIf, *ngFor, [property], (event)\n- Standalone components",
            "alpine" => "Alpine.js\n- x-data for component state\n- x-on or @ for events\n- x-bind or : for attribute binding\n- x-show, x-if for conditionals",
            "vanilla" => "Vanilla JavaScript\n- No framework dependencies\n- Standard DOM APIs\n- ES6+ syntax",
            _ => $"{fw} ({tsNote})",
        } + (tailwind ? "\n\nSTYLING: Tailwind CSS\n- Convert ALL CSS to Tailwind utility classes\n- Use responsive prefixes: sm:, md:, lg:, xl:\n- Use state variants: hover:, focus:, active:\n- Use arbitrary values when needed: w-[120px], text-[#123456]" : string.Empty);
    }

    private static string BuildHiddenBlock(
        List<DomElement>? hiddenElements,
        List<HiddenComponent>? hiddenComponents)
    {
        if ((hiddenElements == null || hiddenElements.Count == 0) &&
            (hiddenComponents == null || hiddenComponents.Count == 0))
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\n---\n\n## HIDDEN ELEMENTS AND COMPONENTS\n");

        if (hiddenElements != null && hiddenElements.Count > 0)
        {
            sb.AppendLine("### Hidden Elements (display:none at desktop capture — mobile-only)");
            foreach (var el in hiddenElements.Take(200))
            {
                var tag = (el.Tag ?? "div").ToLowerInvariant();
                var cls = el.ClassName?.Trim() ?? string.Empty;
                var txt = (el.TextContent ?? string.Empty).Trim().Substring(0, Math.Min((el.TextContent ?? "").Length, 60));
                var clsAttr = cls.Length > 0 ? $" class=\"{cls}\"" : string.Empty;
                sb.AppendLine($"<{tag}{clsAttr} data-snapstak-hidden=\"true\">{txt}</{tag}>");
            }
        }

        if (hiddenComponents != null && hiddenComponents.Count > 0)
        {
            sb.AppendLine("\n### Hidden Interactive Components (hover/click-revealed)");
            foreach (var comp in hiddenComponents)
            {
                sb.AppendLine($"<!-- {comp.ComponentType}: {comp.Label ?? comp.ComponentId} -->");
                foreach (var el in (comp.Elements ?? new()))
                {
                    var tag = (el.Tag ?? "div").ToLowerInvariant();
                    var txt = (el.TextContent ?? string.Empty).Trim().Substring(0, Math.Min((el.TextContent ?? "").Length, 60));
                    if (el.ImgSrc != null)
                    {
                        var w = el.Rect.Width > 0 ? $" width=\"{Math.Round(el.Rect.Width)}\"" : string.Empty;
                        var h = el.Rect.Height > 0 ? $" height=\"{Math.Round(el.Rect.Height)}\"" : string.Empty;
                        sb.AppendLine($"<img src=\"{el.ImgSrc}\"{w}{h}>");
                    }
                    else if (!string.IsNullOrWhiteSpace(txt))
                    {
                        sb.AppendLine($"<{tag}>{txt}</{tag}>");
                    }
                }
            }
        }

        return sb.ToString();
    }

    private static string ToPascalCase(string componentId)
    {
        var frameworks = new HashSet<string>
            { "react", "nextjs", "next", "vue", "nuxt", "angular", "svelte", "astro", "html", "css", "tailwind" };

        var parts = componentId.Split('_').ToList();
        while (parts.Count > 1)
        {
            var last = parts[^1];
            if (System.Text.RegularExpressions.Regex.IsMatch(last, @"^\d+$") ||
                frameworks.Contains(last.ToLowerInvariant()))
                parts.RemoveAt(parts.Count - 1);
            else break;
        }

        return string.Join("_", parts)
            .Split(new[] { "--", "-", "_" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant())
            .Aggregate(string.Empty, (a, b) => a + b);
    }
}