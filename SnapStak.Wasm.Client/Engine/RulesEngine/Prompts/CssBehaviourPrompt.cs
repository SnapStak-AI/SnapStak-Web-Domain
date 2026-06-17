using SnapStak.Wasm.Client.Models.Css;
using SnapStak.Wasm.Client.Models.Pillars;

namespace SnapStak.Wasm.Client.Engine.RulesEngine.Prompts;

/// <summary>
/// Builds the CSS Behaviour description prompt.
/// Port of buildCSSBehaviourPrompt() in openRouterPromptManager.js.
/// The AI describes WHAT the CSS achieves semantically.
/// It does NOT reproduce raw code.
/// </summary>
internal static class CssBehaviourPrompt
{
    public static string Build(
        string       componentId,
        CssJson      componentCss,
        string?      htmlSkeleton,
        string?      sourceHtml,
        InfluenceData? influence)
    {
        var css     = RenderCssSection(componentCss);
        var captureViewport = influence?.ViewportWidth?.ToString() ?? "[viewport width]";

        var html = htmlSkeleton != null
            ? $"""

HTML STRUCTURE:
This is the HTML skeleton of the component with class names preserved. Use the class names to match elements to their CSS rules and determine exact positions. Every element's flex order within its parent container determines its left-to-right position.

IMPORTANT: Elements with data-snapstak-hidden="true" were display:none at desktop capture time. They are MOBILE-ONLY elements. Match their class names against the @media rules in the CSS to determine at which breakpoint they become visible. Name each one explicitly in the Responsive Layout States section.

IMPORTANT: To identify the site LOGO — look for a standalone button or container that wraps only a single <img> with no text content, and whose CSS width is significantly greater than its height (e.g. 200px wide × 30px tall). This wide-aspect-ratio element IS the logo. Do not describe it as "icon button" or "profile button". Describe it as "site logo image" and state its exact position.

IMPORTANT: Many headers use this layout pattern for flanking elements: the row container has justify-content:center so its normal-flow children are centered. The left and right elements are taken OUT of flow using position:absolute with a left or right offset. This means: elements with position:absolute and a left value are pinned to the LEFT EDGE. Elements with position:absolute and a right value are pinned to the RIGHT EDGE. Elements without position:absolute sit CENTERED in the row. Look for this pattern in the CSS rules — it is more common than CSS order properties for header layouts.

```html
{htmlSkeleton.Substring(0, Math.Min(htmlSkeleton.Length, 50000))}
```

"""
            : string.Empty;

        var sourceHtmlSection = sourceHtml != null
            ? $"""

SOURCE HTML (for colour token resolution only):
The following is the original HTML of this component. Use it ONLY to resolve colour tokens
and identify structural elements (dividers, active states) that are invisible in CSS alone.
DO NOT describe or reproduce the HTML structure — use it to answer these specific questions:
1. Find classes like hover:text-[token] or focus:text-[token] on nav links. Write the resolved hex value explicitly in the Visual States section — never write a CSS variable name.
2. Find <div> elements with class "w-full h-px bg-[token]" — these are horizontal divider lines. Describe them in the Layout Behaviour section.
3. Find nav links with class "text-[accent-token]" (without hover: prefix) — this is the active/current nav item.

```html
{sourceHtml.Substring(0, Math.Min(sourceHtml.Length, 8000))}
```

"""
            : string.Empty;

        return $"""
You are a CSS behaviour analyst working within the ConteX Symbiotic Agentic Context Engine.

Your task is to describe the BEHAVIOUR of CSS rules for a web component.
This description will be used as the Behaviour pillar of the ConteX Law to eliminate hallucination during code generation.

COMPONENT ID: {componentId}
{html}{sourceHtmlSection}
CSS RULES:
{css}

YOUR TASK:
Write a concise markdown document describing the CSS behaviour of this component.
Only include sections that are relevant to this component.

## Element Placement
THIS IS THE MOST IMPORTANT SECTION. A developer must be able to reconstruct the exact layout from this section alone.

For EVERY visible element in the component, state:
1. Its visible label or role (e.g. "logo", "SUBSCRIBE NOW button", "SIGN IN link", "search icon")
2. Which row or band it occupies (top row, bottom row, overlay, etc.)
3. Its exact horizontal position: left edge, left-of-center, centered, right-of-center, or right edge
4. Its position relative to adjacent elements

Do NOT describe CSS classes. Name the actual visible elements by what a user sees.
Do NOT use vague terms like "anchored to layout" or "positioned within a wrapper".
Every named element must have an explicit left/center/right position stated.

## Layout Values
THIS SECTION MUST CONTAIN EXACT NUMBERS. Vague descriptions are forbidden here.

For every flex container, grid container, and positioned element, output the exact computed values in this format:

```
element-role: property:value | property:value | ...
```

Include only properties that are explicitly set (not browser defaults). Cover: display, flex-direction, gap, justify-content, align-items, padding, margin, width, height, position, top, left, right, bottom, z-index, border-radius.

IMPORTANT: If a CSS rule uses var(--...) for a value, do NOT include that property in this section. Only include properties whose values are explicit pixel/rem/colour values.

## Layout Behaviour
What visual arrangement does the CSS create? Describe the flex/grid/positioning intent in one or two sentences per container.

## Visual States
What changes on hover, focus, active, or disabled?

First: scan the raw CSS rules for any explicit :hover, :focus, :active, or :disabled rules and report them with exact property values and transition durations.

If NO explicit pseudo-class rules exist, you MUST still generate hover states for interactive elements by inferring from their semantic role:
- nav links and anchor buttons: opacity:0.7 on hover, cursor:pointer
- call-to-action buttons: filter:brightness(1.1) on hover, cursor:pointer
- icon buttons and utility controls: opacity:0.7 on hover, cursor:pointer

State clearly whether the hover values are captured from the CSS or inferred from element type.

## Typography
Font sizes, weights, and line heights as exact values.

## Colour Semantics
What do the colour choices communicate: brand identity, interactive state, visual hierarchy?

## Animations
Scan the CSS rules for every @keyframes block and every animation/transition property.

For each @keyframes block, reproduce it VERBATIM in a code block. For every element that uses animation or transition, state the visible element name, the exact value, and what triggers it.

If no @keyframes exist, write: "No keyframe animations captured."
If no transition properties exist, write: "No transitions captured."
Never omit this section.

## Responsive Layout States

THIS SECTION IS MANDATORY. Structure it as named states, not a flat list of CSS blocks.

STEP 1: Discard any @media block that ONLY sets CSS custom properties on :root. These are global stylesheet variables with no visible effect on this component.

STEP 2: Identify distinct visual states. Each unique breakpoint condition that changes a VISIBLE ELEMENT's display, size, position, or visibility becomes a named state.

STEP 3: For each state, use this exact format:

### DEFAULT (no media query — captured at {captureViewport}px)
For every element in the component, state its visibility and key layout values:
- [Element name]: VISIBLE | [key dimension]
- [Element name]: HIDDEN (default — revealed at breakpoint X)

### AT [exact @media condition]
List ONLY elements that CHANGE from the default state. Be explicit:
- [Element name]: HIDDEN
- [Element name]: VISIBLE
- [Element name]: MOVES TO [location]

MANDATORY: If the HTML skeleton contains elements with data-snapstak-hidden="true", list each one under DEFAULT as "HIDDEN (mobile-only)" and identify which breakpoint reveals them.

After each named state block, reproduce the exact @media CSS in a fenced code block.
If no @media rules affect visible elements, write: "No responsive layout states captured."

## Overflow and Stacking
Describe z-index values, overflow, and clipping behaviour if present.

STRICT RULES:
- The Layout Values section MUST contain exact numbers. "Spaced apart", "compact", and "enough room" are forbidden.
- The Element Placement section must name every visible element and state its exact horizontal position.
- The Animations section MUST reproduce every @keyframes block verbatim.
- The Responsive Layout States section MUST name every element that changes visibility at each breakpoint. "Certain elements" is forbidden.
- Plain markdown only. Short sentences. No preamble. No closing summary.
- Be specific to this component. Do not give generic CSS advice.
""";
    }

    // ── CSS pre-renderer ──────────────────────────────────────────────────────
    // Mirrors the cssJson pre-render block in buildCSSBehaviourPrompt().
    // Outputs real CSS text sections the AI can read directly.

    private static string RenderCssSection(CssJson css)
    {
        var parts = new System.Text.StringBuilder();

        if (css.Keyframes.Count > 0)
        {
            parts.AppendLine("/* ── Keyframe Animations ─────────────────────────────── */");
            foreach (var k in css.Keyframes) parts.AppendLine(k);
        }

        if (css.Media.Count > 0)
        {
            parts.AppendLine("/* ── Responsive Rules (@media) ───────────────────────── */");
            foreach (var m in css.Media)
            {
                var mq = m.ResolvedMediaQuery;
                if (!string.IsNullOrEmpty(mq))
                {
                    parts.AppendLine($"@media {mq} {{");
                    foreach (var r in m.Rules)
                        parts.AppendLine($"  {r.CssText ?? r.Selector}");
                    parts.AppendLine("}");
                }
            }
        }

        if (css.Behavior.Count > 0)
        {
            parts.AppendLine("/* ── Pseudo-class Rules (:hover, :focus, :active) ────── */");
            foreach (var r in css.Behavior)
                parts.AppendLine(r.CssText ?? r.Selector ?? string.Empty);
        }

        if (css.Matched.Count > 0)
        {
            parts.AppendLine("/* ── Structural & Visual Rules ───────────────────────── */");
            foreach (var r in css.Matched)
                parts.AppendLine(r.CssText ?? r.Selector ?? string.Empty);
        }

        return parts.Length > 0 ? parts.ToString() : "(no CSS captured)";
    }
}
