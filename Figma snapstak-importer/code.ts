// ─────────────────────────────────────────────────────────────────────────────
// code.ts  —  SnapStak Importer  —  Figma Plugin sandbox
//
// This file runs in Figma's sandboxed plugin environment. It has access to
// the Figma Plugin API (figma.*) but NO access to the network, DOM, or browser
// APIs. All communication with the outside world goes through postMessage via
// the UI iframe (ui.html).
//
// MESSAGE PROTOCOL
//   UI → Sandbox:
//     { type: 'import-svg', svgString: string, title: string }
//       — Import the SVG string as a new Figma frame on the current page.
//
//     { type: 'cancel' }
//       — User cancelled. Close the plugin.
//
//   Sandbox → UI:
//     { type: 'ready' }
//       — Plugin opened. UI can now render.
//
//     { type: 'done', frameName: string }
//       — Import succeeded. Sent before closePlugin().
//
//     { type: 'error', message: string }
//       — Import failed. UI shows the error; plugin stays open.
//
// IMPORT STRATEGY
//   figma.createNodeFromSvg(svgString) — the official Plugin API method
//   that is equivalent to dragging an SVG file into Figma. It decomposes the
//   SVG into native Figma nodes:
//     <g transform="translate(x,y)">  →  Frame (positioned)
//     <rect fill="…">                 →  Rectangle with solid fill
//     <text …>                        →  Text node
//     <image href="data:…">           →  Rectangle with IMAGE fill
//     <filter>/<feDropShadow>         →  Drop shadow effect
//
//   The resulting frame is appended to the current page, named after the SVG
//   root id attribute, and the viewport scrolls to show it.
//
// COMPILE
//   The Figma plugin toolchain compiles code.ts → code.js.
//   From the snapstak-importer/ directory:
//     npm install
//     npm run build        (produces code.js)
//     npm run watch        (for development — rebuilds on save)
//
// LOAD IN FIGMA (development)
//   Figma Desktop → Plugins → Development → Import plugin from manifest…
//   → select snapstak-importer/manifest.json
// ─────────────────────────────────────────────────────────────────────────────

// Figma Plugin API types are provided by @figma/plugin-typings (installed via
// npm). TypeScript will error without them — run `npm install` first.

figma.showUI(__html__, {
  width:  380,
  height: 420,
  title:  "SnapStak Importer",
  themeColors: true,  // adopt Figma's light/dark colour scheme
});

// Tell the UI it can render (the UI waits for this before enabling the button)
figma.ui.postMessage({ type: "ready" });

// ── Message handler ───────────────────────────────────────────────────────────

figma.ui.onmessage = async (msg: PluginMessage) => {

  if (msg.type === "cancel") {
    figma.closePlugin();
    return;
  }

  if (msg.type === "import-svg") {
    await handleImport(msg.svgString, msg.title);
    return;
  }
};

// ── Import handler ────────────────────────────────────────────────────────────

async function handleImport(svgString: string, title: string): Promise<void> {
  try {
    // createNodeFromSvg returns a FrameNode containing the decomposed SVG tree.
    // It is synchronous despite the complex parsing it does internally.
    const frame = figma.createNodeFromSvg(svgString);

    // Name the frame after the component title — shows in the layers panel
    frame.name = title || "SnapStak Import";

    // Place it on the current page
    figma.currentPage.appendChild(frame);

    // Centre it at the current viewport so the user sees it immediately
    const { x, y, width, height } = figma.viewport.bounds;
    frame.x = Math.round(x + (width  - frame.width)  / 2);
    frame.y = Math.round(y + (height - frame.height) / 2);

    // Select the new frame and scroll into view
    figma.currentPage.selection = [frame];
    figma.viewport.scrollAndZoomIntoView([frame]);

    // Notify the UI before closing
    figma.ui.postMessage({ type: "done", frameName: frame.name });

    // Small delay so the UI can show the success state before the panel closes
    await delay(1200);
    figma.closePlugin(`✅ ${frame.name} imported successfully`);

  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : String(err);
    figma.ui.postMessage({ type: "error", message });
    // Don't close — let the user see the error and try again
  }
}

// ── Utilities ─────────────────────────────────────────────────────────────────

function delay(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

// ── Type definitions ──────────────────────────────────────────────────────────

type PluginMessage =
  | { type: "import-svg"; svgString: string; title: string }
  | { type: "cancel" }
  | { type: "ready" }
  | { type: "done"; frameName: string }
  | { type: "error"; message: string };