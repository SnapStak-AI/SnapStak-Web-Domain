// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Derived from the ConteX Law plugin contract for SnapStak.
// See: https://github.com/snapstak/contex-law

using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Svg;

namespace SnapStak.Wasm.Client.Engine.Plugins;

// ─────────────────────────────────────────────────────────────────────────────
// IConteXTranslatorPlugin — two-phase contract (v2)
//
// MIGRATION GUIDE from v1 (single-method Translate(TranslatorContext)):
//
//   The old contract assumed all inputs were already present in memory.
//   The new contract splits translation into two phases so that the host can
//   pre-fetch remote resources (images, fonts, icons) before Phase 2 runs:
//
//   Phase 1 — DeclareResources(TranslatorBundle)
//       Walk the bundle, collect every URL that needs to be downloaded.
//       Return the deduplicated URL list. The host fetches them all in
//       parallel. Return [] if the plugin is self-contained (no network).
//
//   Phase 2 — Translate(TranslatorBundle, IReadOnlyDictionary<string,byte[]>)
//       All declared URLs are now pre-fetched. The dictionary maps each URL
//       to its bytes. Missing keys mean the fetch failed — plugins must
//       decide whether to fail hard (throw) or silently skip the asset.
//       Return [] to signal "no output for this bundle" (not an error).
//       Throw only for genuine programming errors.
//
// BACKWARD COMPATIBILITY
//   The old TranslatorContext-based overload is retained as a default
//   interface method so existing plugins (e.g. the annotated-SVG plugin)
//   compile unchanged. New plugins targeting the Penpot binary format
//   implement the two-phase overloads directly.
//
// THREADING
//   Both methods may be called from a thread-pool thread. Implementations
//   must be thread-safe. They must not write shared state.
// ─────────────────────────────────────────────────────────────────────────────

public interface IConteXTranslatorPlugin
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Stable short identifier: output filename key, log prefix, and registry
    /// key. Must match <c>^[a-z][a-z0-9-]{1,30}$</c>.
    /// </summary>
    string Key { get; }

    /// <summary>Human-readable name for logs and the UI plugin list.</summary>
    string DisplayName { get; }

    /// <summary>Semver-style version; bump when the output format changes.</summary>
    string Version { get; }

    /// <summary>
    /// File extension of the output, including the leading dot.
    /// Must not contain path separators.
    /// Examples: ".penpot", ".svg", ".penpot.svg"
    /// </summary>
    string FileExtension { get; }

    // ── Phase 1 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Walk the bundle and return every remote URL whose bytes are needed by
    /// <see cref="Translate(TranslatorBundle, IReadOnlyDictionary{string,byte[]})"/>.
    ///
    /// Rules:
    ///   • Return a deduplicated list — the host may ignore duplicates but
    ///     plugins should not rely on that.
    ///   • Return <see cref="Array.Empty{T}"/> when no network is needed.
    ///   • Must be pure and side-effect-free.
    ///   • Must not throw. Return [] on any internal error.
    ///
    /// The host fetches all URLs in parallel after this call returns, then
    /// passes the results to Phase 2.
    /// </summary>
    IReadOnlyList<string> DeclareResources(TranslatorBundle bundle);

    /// <summary>
    /// Produce the translated file bytes.
    ///
    /// <paramref name="fetchedResources"/> maps every URL returned by
    /// <see cref="DeclareResources"/> to its bytes. A URL that failed to
    /// fetch is absent from the dictionary — the plugin decides how to handle
    /// missing assets (skip, placeholder, or hard-fail via exception).
    ///
    /// Return <see cref="Array.Empty{T}"/> to signal "no output for this bundle"
    /// (the host skips the write). Throw only for genuine programming errors.
    /// </summary>
    byte[] Translate(
        TranslatorBundle bundle,
        IReadOnlyDictionary<string, byte[]> fetchedResources);

    // ── Backward-compatible shim (v1 → v2 bridge) ────────────────────────────

    /// <summary>
    /// Legacy single-phase entry point preserved for the annotated-SVG plugin
    /// and any other plugin that needs no network access.
    ///
    /// The default implementation builds a <see cref="TranslatorBundle"/> from
    /// the old <see cref="TranslatorContext"/> and delegates to the two-phase
    /// overloads, passing an empty fetch map. Plugins that override
    /// <see cref="Translate(TranslatorBundle, IReadOnlyDictionary{string,byte[]})"/>
    /// do not need to override this method.
    ///
    /// Plugins that still use only the v1 signature can override this method
    /// and return empty from the two-phase overloads.
    /// </summary>
    byte[] Translate(TranslatorContext context)
    {
        var bundle = TranslatorBundle.FromContext(context);
        var urls = DeclareResources(bundle);
        // No network — pass empty map.
        return Translate(bundle,
            new Dictionary<string, byte[]>(StringComparer.Ordinal));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TranslatorBundle — v2 input model
//
// Supersedes TranslatorContext. The bundle carries both the desktop and mobile
// SvgNode trees (the MAUI client path may supply both viewports in a single
// export run), plus all four ConteX Law pillars, plus the hidden-component list
// that lives on every session capture.
//
// Design decisions:
//   • Desktop is always present; Mobile is nullable (desktop-only captures).
//   • Influence and Objective are optional — not every capture fills them.
//   • The factory method FromContext bridges old single-tree callers without
//     duplicating the bundle construction logic.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TranslatorBundle
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Source component ID from the session manifest.
    /// Format: "{hostname}_{unix-epoch}" — e.g. "example.com_1713512345".
    /// </summary>
    public required string ComponentId { get; init; }

    // ── Viewports ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Desktop SvgNode tree. Always present.
    /// Carries the tree, viewport dimensions, source URL, and page-map.
    /// </summary>
    public required TranslatorViewport Desktop { get; init; }

    /// <summary>
    /// Mobile SvgNode tree. Null when the session was captured desktop-only.
    /// Width typically 375–428 px; height typically 812–932 px.
    /// </summary>
    public required TranslatorViewport? Mobile { get; init; }

    // ── Segments ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Carved segments from the master capture (Header, Main, Footer, Nav…).
    /// Each segment has its own SvgNode tree clipped to the segment bounding box.
    /// Empty list for master-only captures.
    /// </summary>
    public required IReadOnlyList<TranslatorSegment> Segments { get; init; }

    /// <summary>
    /// Hidden components (drawer, modal, tooltip…) captured alongside the
    /// main visible DOM. Most plugins emit them as hidden pages or layers.
    /// Empty list when none were captured.
    /// </summary>
    public required IReadOnlyList<TranslatorHiddenComponent> HiddenComponents { get; init; }

    // ── ConteX Law pillars ────────────────────────────────────────────────────

    /// <summary>
    /// Pillar 3 — Influence: browser, OS, viewport, DPR, media features.
    /// Null when not captured.
    /// </summary>
    public InfluenceData? Influence { get; init; }

    /// <summary>
    /// Pillar 4 — Objective: device type, target width, framework, breakpoints.
    /// Null when not set by the user.
    /// </summary>
    public ObjectiveData? Objective { get; init; }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a v2 bundle from the legacy v1 context. Used by the backward-
    /// compatible default <see cref="IConteXTranslatorPlugin.Translate(TranslatorContext)"/>
    /// shim. Not intended for new callers.
    /// </summary>
    public static TranslatorBundle FromContext(TranslatorContext ctx) => new()
    {
        ComponentId = ctx.ComponentId,
        Desktop = new TranslatorViewport
        {
            Tree = ctx.Tree,
            Width = ctx.Options.Width,
            Height = ctx.Options.Height,
            SourceUrl = ctx.Options.SourceUrl,
            Title = ctx.Options.Title,
        },
        Mobile = null,
        Segments = Array.Empty<TranslatorSegment>(),
        // HiddenComponent (Dom model) carries Elements, not a pre-built SvgNode
        // tree. The v1 shim path has no SvgDeserializer in scope here, so we
        // produce empty-tree wrappers. Callers that have a full SvgNode tree for
        // each hidden component should use TranslatorBundle directly.
        HiddenComponents = ctx.HiddenComponents
            ?.Select(h => new TranslatorHiddenComponent
            {
                ComponentId = h.ComponentId,
                Label = h.Label ?? h.ComponentType,
                Tree = Array.Empty<SvgNode>(),
                Width = 0,
                Height = 0,
            })
            .ToArray()
            ?? Array.Empty<TranslatorHiddenComponent>(),
        Influence = ctx.Influence,
        Objective = ctx.Objective,
    };
}

/// <summary>
/// One captured viewport — either desktop or mobile.
/// Carries the fully-resolved SvgNode tree plus the viewport metadata that
/// the Penpot plugin needs to size the root frame and page.
/// </summary>
public sealed class TranslatorViewport
{
    /// <summary>Fully-resolved SvgNode tree for this viewport.</summary>
    public required IReadOnlyList<SvgNode> Tree { get; init; }

    /// <summary>Viewport pixel width. Desktop: typically 1280–1920. Mobile: 375–428.</summary>
    public required int Width { get; init; }

    /// <summary>Viewport pixel height. Used for the root frame height.</summary>
    public required int Height { get; init; }

    /// <summary>Source URL of the captured page (e.g. "https://example.com/pricing").</summary>
    public required string SourceUrl { get; init; }

    /// <summary>Document title at capture time.</summary>
    public required string Title { get; init; }
}

/// <summary>
/// A carved segment — one logical zone (Header, Main, Footer, Nav…) extracted
/// from the master page capture. The SvgNode tree is clipped to the zone's
/// bounding box; X/Y coordinates are page-relative.
/// </summary>
public sealed class TranslatorSegment
{
    /// <summary>
    /// Segment ID — stable across re-captures if the zone label is stable.
    /// Format: "{componentId}_{label}" (slugified).
    /// </summary>
    public required string SegmentId { get; init; }

    /// <summary>Human-readable zone label: "Header", "Hero Section", "Footer".</summary>
    public required string Label { get; init; }

    /// <summary>Zone type: "header" | "main" | "footer" | "nav" | "section" | "article".</summary>
    public required string Zone { get; init; }

    /// <summary>SvgNode tree clipped to this segment.</summary>
    public required IReadOnlyList<SvgNode> Tree { get; init; }

    /// <summary>Pixel width of the segment bounding box.</summary>
    public required int Width { get; init; }

    /// <summary>Pixel height of the segment bounding box.</summary>
    public required int Height { get; init; }
}

/// <summary>
/// A hidden component captured alongside the main DOM —
/// drawers, modals, tooltips, dropdowns that were off-screen at capture time.
/// </summary>
public sealed class TranslatorHiddenComponent
{
    /// <summary>Stable component ID from the session manifest.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Human-readable label: "Mobile Menu", "Cookie Banner".</summary>
    public required string Label { get; init; }

    /// <summary>SvgNode tree for this hidden component.</summary>
    public required IReadOnlyList<SvgNode> Tree { get; init; }

    /// <summary>Pixel width of the hidden component's bounding box.</summary>
    public required int Width { get; init; }

    /// <summary>Pixel height of the hidden component's bounding box.</summary>
    public required int Height { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// TranslatorContext — retained for backward compatibility with v1 plugins
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// V1 translation context. Kept so the annotated-SVG plugin and any other
/// plugin that has not migrated to the two-phase API compiles unchanged.
/// New plugins should use <see cref="TranslatorBundle"/> instead.
/// </summary>
public sealed class TranslatorContext
{
    public required IReadOnlyList<SvgNode> Tree { get; init; }
    public required SvgTreeOptions Options { get; init; }
    public required string ComponentId { get; init; }
    public InfluenceData? Influence { get; init; }
    public ObjectiveData? Objective { get; init; }
    public IReadOnlyList<HiddenComponent>? HiddenComponents { get; init; }
}