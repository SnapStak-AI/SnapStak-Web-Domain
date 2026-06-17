// content.js — SnapStak Page Extractor
// Runs inside the live page context (MAIN world).
// Injected dynamically by bridge.js via window.postMessage.
// Uses getBoundingClientRect() and getComputedStyle() — real browser only.
// Produces TWO snapshots: visible elements and invisible elements.
//
// Communication: window.postMessage (not chrome.runtime — unavailable in MAIN world)
// bridge.js relays messages between this script and background.js

'use strict';

// Re-injection guard
if (window.__snapstak_loaded__) {
    // Already loaded — remove old listener and re-register
    window.removeEventListener('message', window.__snapstak_onmessage__);
}
window.__snapstak_loaded__ = true;

// ── postMessage request/response handler ──────────────────────────────────────
// Mirrors the chrome.runtime.onMessage pattern using window.postMessage.
// bridge.js sends:  { __snapstak: true, type, requestId, ...args }
// We respond with: { __snapstak_response: true, requestId, result }

window.__snapstak_onmessage__ = async function (event) {
    if (!event.data?.__snapstak) return;
    const { type, requestId } = event.data;

    function respond(result) {
        window.postMessage({
            __snapstak_response: true,
            requestId,
            result,
        }, '*');
    }

    try {
        switch (type) {
            case 'DISCOVER_BREAKPOINTS': {
                const widths = new Set();
                for (const sheet of document.styleSheets) {
                    let rules;
                    try { rules = sheet.cssRules; } catch { continue; }
                    for (const rule of rules) {
                        if (!(rule instanceof CSSMediaRule)) continue;
                        const cond = rule.conditionText || rule.media?.mediaText || '';
                        const matches = cond.matchAll(/min-width\s*:\s*([\d.]+)px/gi);
                        for (const m of matches) {
                            const w = Math.round(parseFloat(m[1]));
                            if (w > 0 && w < 4000) widths.add(w);
                        }
                    }
                }
                respond({ breakpoints: Array.from(widths).sort((a, b) => a - b), primaryWidth: window.innerWidth });
                break;
            }
            case 'GET_VIEWPORT_WIDTH':
                respond({ width: window.innerWidth });
                break;
            case 'GET_VIEWPORT_HEIGHT':
                respond({ height: window.innerHeight });
                break;
            case 'EXTRACT_MOBILE':
                respond(await extractMobile(event.data.mode || 'visible'));
                break;
            case 'EXTRACT_PAGE':
                respond(await extractPage(event.data.mode || 'visible', event.data.skipImageWait || false));
                break;
            case 'START_COMPONENT_SELECT':
                ComponentSelector.start();
                respond({ success: true });
                break;
            case 'STOP_COMPONENT_SELECT':
                ComponentSelector.stop();
                respond({ success: true });
                break;
            case 'EXTRACT_ELEMENT':
                respond(await extractElement(event.data.element));
                break;
            case 'EXTRACT_SEGMENT_BEHAVIOUR':
                respond(await extractSegmentBehaviour(event.data.segmentId));
                break;
            case 'EXTRACT_HIDDEN_FOR_SEGMENT':
                respond(await extractHiddenForSegment(event.data.segmentId));
                break;
            default:
                break;
        }
    } catch (err) {
        respond({ success: false, error: err.message });
    }
};

window.addEventListener('message', window.__snapstak_onmessage__);

// Signal to bridge.js that content.js is loaded and ready
window.postMessage({ __snapstak_ready: true }, '*');

async function waitForImages(label) {
    // ── Inject scanning beam overlay ─────────────────────────────────────────
    // label: optional string shown in the pill badge (default: desktop text)
    const overlay = document.createElement('div');
    overlay.id = '__snapstak_scanner__';
    overlay.style.cssText = [
        'position:fixed',
        'top:0', 'left:0',
        'width:100%', 'height:100%',
        'pointer-events:none',
        'z-index:2147483647',
        'overflow:hidden',
    ].join(';');

    overlay.innerHTML = `
    <div id="__snapstak_beam__" style="
      position: absolute;
      left: 0;
      width: 100%;
      height: 6px;
      background: linear-gradient(
        to bottom,
        transparent 0%,
        rgba(56, 189, 248, 0.15) 20%,
        rgba(56, 189, 248, 0.7) 45%,
        rgba(186, 230, 253, 1.0) 50%,
        rgba(56, 189, 248, 0.7) 55%,
        rgba(56, 189, 248, 0.15) 80%,
        transparent 100%
      );
      box-shadow:
        0 0 8px 2px rgba(56, 189, 248, 0.6),
        0 0 20px 6px rgba(56, 189, 248, 0.3),
        0 0 40px 12px rgba(56, 189, 248, 0.15);
      border-radius: 50%;
      transform: scaleX(1.02);
      top: 0;
    "></div>
    <div style="
      position: fixed;
      bottom: 24px;
      left: 50%;
      transform: translateX(-50%);
      background: rgba(0,0,0,0.75);
      border: 1px solid rgba(56,189,248,0.6);
      border-radius: 20px;
      padding: 6px 16px;
      font-family: -apple-system, sans-serif;
      font-size: 12px;
      font-weight: 600;
      color: #38BDF8;
      letter-spacing: 0.08em;
      backdrop-filter: blur(8px);
      white-space: nowrap;
    ">${label || '⬡ SNAPSTAK · DECONSTRUCTING THE WEB'}</div>
  `;
    document.body.appendChild(overlay);

    const beam = overlay.querySelector('#__snapstak_beam__');
    const viewH = window.innerHeight;

    // ── Native browser smooth scroll — runs on compositor thread, never throttled ──
    // window.scrollTo({ behavior: 'smooth' }) bypasses ALL JS throttling.
    // Use 'scrollend' event to detect completion (Chrome 109+).
    const totalHeight = document.body.scrollHeight;

    // Set CSS scroll-behavior on html element for maximum compatibility
    document.documentElement.style.scrollBehavior = 'smooth';

    function nativeScrollTo(targetY) {
        return new Promise(resolve => {
            // Use scrollend if available (Chrome 109+), otherwise poll
            if ('onscrollend' in window) {
                window.addEventListener('scrollend', resolve, { once: true });
                window.scrollTo({ top: targetY, behavior: 'smooth' });
            } else {
                // Fallback: poll until scroll position stops changing
                window.scrollTo({ top: targetY, behavior: 'smooth' });
                let last = window.scrollY;
                const poll = setInterval(() => {
                    beam.style.top = (viewH / 2) + 'px';
                    if (window.scrollY === last && Math.abs(window.scrollY - targetY) < 5) {
                        clearInterval(poll);
                        resolve();
                    }
                    last = window.scrollY;
                }, 100);
            }
            // Update beam position during scroll
            const beamInterval = setInterval(() => {
                beam.style.top = (viewH / 2) + 'px';
            }, 16);
            window.addEventListener('scrollend', () => clearInterval(beamInterval), { once: true });
        });
    }

    // ── STEP 1: Scroll DOWN to bottom ─────────────────────────────────────────
    const _widthBefore = window.innerWidth;
    await nativeScrollTo(totalHeight);
    await new Promise(r => setTimeout(r, 300));

    // ── STEP 2: Scroll UP to top ──────────────────────────────────────────────
    await nativeScrollTo(0);

    // Restore scroll behaviour
    document.documentElement.style.scrollBehavior = '';

    // ── STEP 3: Wait for layout to stabilize after scroll ─────────────────────
    // Scrolling can trigger responsive reflows. Force layout recalc and wait.
    window.dispatchEvent(new Event('resize'));
    await new Promise(r => setTimeout(r, 600));

    const images = Array.from(document.images).filter(img => !img.complete);
    if (images.length > 0) {
        await Promise.race([
            Promise.all(images.map(img => new Promise(r => {
                img.addEventListener('load', r);
                img.addEventListener('error', r);
            }))),
            new Promise(r => setTimeout(r, 3000)),
        ]);
    }

    // ── STEP 4: Settle then remove beam ───────────────────────────────────────
    await new Promise(r => setTimeout(r, 300));
    overlay.remove();
    console.log('[SnapStak] Scan complete. ScrollY:', window.scrollY);
}

// =============================================================================
// SVG SPRITE PRE-FETCHER
// Autosport (and many sites) use external SVG sprite sheets:
//   <svg><use xlink:href="/path/sprite.svg#icon-name"></use></svg>
// The browser has already fetched and cached the sprite file when loading the page.
// Fetching it again is instant — it comes from the browser cache, no network request.
// =============================================================================
async function prefetchSVGSprites(rootEl) {
    const spriteCache = new Map(); // spriteURL → parsed Document
    const symbolCache = new Map(); // "spriteURL#id" → inlined SVG string

    const _spriteRoot = rootEl || document;
    const svgEls = Array.from(_spriteRoot.querySelectorAll('svg'));
    const spriteURLs = new Set();

    // ── Also scan same-origin iframes — sprite sheets loaded inside embeds
    // (e.g. live-timing widgets) are invisible to document.querySelectorAll.
    // Cross-origin iframes throw on contentDocument access — catch and skip.
    const _iframeDocuments = [];
    try {
        const _iframes = Array.from(document.querySelectorAll('iframe'));
        for (const _iframe of _iframes) {
            try {
                const _iDoc = _iframe.contentDocument || _iframe.contentWindow?.document;
                if (_iDoc && _iDoc.readyState !== 'uninitialized') _iframeDocuments.push(_iDoc);
            } catch (_) { /* cross-origin — skip */ }
        }
    } catch (_) { }

    const _allSVGEls = [
        ...svgEls,
        ..._iframeDocuments.flatMap(d => Array.from(d.querySelectorAll('svg'))),
    ];

    for (const svgEl of _allSVGEls) {
        const useEl = svgEl.querySelector('use');
        if (!useEl) continue;
        const XLINK_NS = 'http://www.w3.org/1999/xlink';
        const href = useEl.getAttributeNS(XLINK_NS, 'href')
            || useEl.getAttribute('href')
            || useEl.getAttribute('xlink:href')
            || '';
        if (!href.includes('.svg#')) continue;
        const [spriteURL] = href.split('#');
        if (spriteURL) spriteURLs.add(spriteURL);
    }

    // Fetch each unique sprite sheet — instant from browser cache
    const fetchPromises = Array.from(spriteURLs).map(async (spriteURL) => {
        try {
            const abs = new URL(spriteURL, window.location.origin).href;
            const res = await fetch(abs, { cache: 'force-cache' });
            if (!res.ok) return;
            const text = await res.text();
            const parser = new DOMParser();
            const doc = parser.parseFromString(text, 'image/svg+xml');
            spriteCache.set(spriteURL, doc);
            // Also store under absolute URL key
            if (abs !== spriteURL) spriteCache.set(abs, doc);
            console.log('[SnapStak] Sprite from cache:', spriteURL);
        } catch (e) {
            console.warn('[SnapStak] Failed to read sprite:', spriteURL, e.message);
        }
    });

    await Promise.all(fetchPromises);

    // Build lookup: "spriteURL#symbolId" → standalone SVG string
    for (const [spriteURL, doc] of spriteCache.entries()) {
        const symbols = doc.querySelectorAll('symbol');
        for (const sym of symbols) {
            const symId = sym.getAttribute('id');
            if (!symId) continue;
            const vb = sym.getAttribute('viewBox') || '0 0 24 24';
            const inner = sym.innerHTML;
            const _symbolIsLogo = /\bfill\s*=\s*["'](?!none|currentColor)[^"']/i.test(inner);
            const fixedInner = _symbolIsLogo ? inner : inner.replace(
                /<(path|line|polyline|polygon|circle|rect|ellipse)(\s[^>]*)?\/>/gi,
                (match, tagName, attrs) => {
                    const a = attrs || '';
                    if (!/\bfill\s*=/.test(a) && !/\bstroke\s*=/.test(a)) {
                        return `<${tagName}${a} fill="none" stroke="currentColor"/>`;
                    }
                    return match;
                }
            );
            const inlined = _symbolIsLogo
                ? `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${vb}" width="100%" height="100%">${fixedInner}</svg>`
                : `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${vb}" width="100%" height="100%" style="color:inherit;fill:currentColor;">${fixedInner}</svg>`;
            // Store under relative, absolute, and hash-only keys
            symbolCache.set(`${spriteURL}#${symId}`, inlined);
            try {
                const absSpriteURL = new URL(spriteURL, window.location.origin).href;
                if (absSpriteURL !== spriteURL) symbolCache.set(`${absSpriteURL}#${symId}`, inlined);
            } catch (e) { }
        }
        console.log('[SnapStak] Cached', symbols.length, 'symbols from', spriteURL);
    }

    // ── Inline symbols already in page DOM (hash-only href="#id") ─────────────
    const _inlineSVGs = Array.from(document.querySelectorAll('svg'));
    let _inlineCount = 0;
    for (const _svg of _inlineSVGs) {
        const _symbols = Array.from(_svg.querySelectorAll('symbol'));
        for (const _sym of _symbols) {
            const _symId = _sym.getAttribute('id');
            if (!_symId) continue;
            const _vb = _sym.getAttribute('viewBox') || '0 0 24 24';
            const _inner = _sym.innerHTML;
            const _isLogo = /\bfill\s*=\s*["'](?!none|currentColor)[^"']/i.test(_inner);
            const _fixed = _isLogo ? _inner : _inner.replace(
                /<(path|line|polyline|polygon|circle|rect|ellipse)(\s[^>]*)?\/?>/gi,
                (match, tagName, attrs) => {
                    const a = attrs || '';
                    if (!/\bfill\s*=/.test(a) && !/\bstroke\s*=/.test(a)) {
                        return `<${tagName}${a} fill="none" stroke="currentColor"/>`;
                    }
                    return match;
                }
            );
            const _inlined = _isLogo
                ? `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${_vb}" width="100%" height="100%">${_fixed}</svg>`
                : `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${_vb}" width="100%" height="100%" style="color:inherit;fill:currentColor;">${_fixed}</svg>`;
            symbolCache.set(`#${_symId}`, _inlined);
            try {
                symbolCache.set(`${window.location.href.split('#')[0]}#${_symId}`, _inlined);
                symbolCache.set(`${window.location.origin}#${_symId}`, _inlined);
            } catch (e) { }
            _inlineCount++;
        }
    }
    if (_inlineCount > 0) console.log(`[SnapStak] Cached ${_inlineCount} inline symbols from page DOM`);

    console.log('[SnapStak] SVG symbol cache size:', symbolCache.size);
    return symbolCache;
}

// =============================================================================
// STAMP SEGMENT IDs
//
// Walks the DOM and stamps data-segment-id="ss_{8hex}_{unixTs}" onto every
// layout container — an element that:
//   1. Has layout CSS: display flex/grid/block/inline-block/table-cell
//   2. Has explicit dimensions (width > 0, height > 0)
//   3. Directly contains at least one text node, media element, or actionable
//      element (a, button, input, select, textarea)
//
// Also stamps data-responsive="true" on containers that hold a <picture>,
// <source srcset>, or <img srcset> — indicating responsive media.
//
// The same _runTs is shared across all segments in one extraction run.
// Each container gets a unique 8-char hex string.
// =============================================================================
function stampSegmentIds(runTs) {
    const LAYOUT_DISPLAYS = new Set(['flex', 'inline-flex', 'grid', 'inline-grid', 'block', 'inline-block', 'table-cell', 'table-row', 'list-item']);
    const ACTIONABLE_TAGS = new Set(['a', 'button', 'input', 'select', 'textarea']);
    const MEDIA_TAGS = new Set(['img', 'video', 'audio', 'picture', 'canvas', 'svg']);
    const SKIP_TAGS = new Set(['script', 'style', 'noscript', 'head', 'html', 'meta', 'link']);

    // Generate a unique 8-char hex string
    function hex8() {
        return Math.floor(Math.random() * 0xFFFFFFFF).toString(16).padStart(8, '0');
    }

    // Does this element directly contain text, media, or actionable children?
    function isLayoutContainer(el) {
        const cs = window.getComputedStyle(el);
        const display = cs.display || '';

        // Must have a layout display value
        if (!LAYOUT_DISPLAYS.has(display)) return false;

        // Must have real dimensions
        const rect = el.getBoundingClientRect();
        if (rect.width < 2 || rect.height < 2) return false;

        // Must directly contain qualifying content
        // Check direct text nodes
        for (const node of el.childNodes) {
            if (node.nodeType === Node.TEXT_NODE && node.textContent.trim()) return true;
        }
        // Check direct element children for media or actionable tags
        for (const child of el.children) {
            const ct = child.tagName.toLowerCase();
            if (MEDIA_TAGS.has(ct) || ACTIONABLE_TAGS.has(ct)) return true;
        }

        return false;
    }

    // Is this container holding responsive media?
    function isResponsiveMedia(el) {
        // Contains a <picture> element
        if (el.querySelector('picture')) return true;
        // Contains an <img> with srcset
        const imgs = el.querySelectorAll('img[srcset], img[data-srcset]');
        if (imgs.length > 0) return true;
        // Contains a <source> with srcset
        const sources = el.querySelectorAll('source[srcset]');
        if (sources.length > 0) return true;
        return false;
    }

    // Is this element a deliberate 1px (or 2px) visual divider/separator?
    // These are intentional design elements — full-width, height ≤ 2px,
    // with a background color or border. Never dropped on size alone.
    function isDivider(el) {
        const rect = el.getBoundingClientRect();
        if (rect.height > 2 || rect.width < 50) return false;
        const cs = window.getComputedStyle(el);
        const hasBg = cs.backgroundColor && cs.backgroundColor !== 'rgba(0, 0, 0, 0)' && cs.backgroundColor !== 'transparent';
        const hasBorder = cs.borderTopWidth && parseFloat(cs.borderTopWidth) > 0;
        return hasBg || hasBorder;
    }

    const LANDMARK_TAGS = new Set(['header', 'footer', 'nav', 'main', 'section', 'article', 'aside', 'form']);

    // Walk the entire DOM
    const allEls = document.querySelectorAll('*');
    let count = 0;
    for (const el of allEls) {
        const tag = el.tagName.toLowerCase();
        if (SKIP_TAGS.has(tag)) continue;

        // Stamp segmentId on layout containers, semantic landmarks, OR deliberate dividers
        if (!el.dataset.segmentId && (isLayoutContainer(el) || LANDMARK_TAGS.has(tag) || isDivider(el))) {
            const rect = el.getBoundingClientRect();
            if (rect.width >= 2 && (rect.height >= 2 || isDivider(el))) {
                el.dataset.segmentId = `ss_${hex8()}_${runTs}`;
                count++;
            }
        }

        // Stamp responsive independently — any element containing responsive media
        if (!el.dataset.responsive && isResponsiveMedia(el)) {
            el.dataset.responsive = 'true';
        }
    }
    console.log(`[SnapStak] Stamped ${count} segmentIds (ts: ${runTs})`);
}

// =============================================================================
// MOBILE EXTRACTION — called by background.js AFTER CDP sets inner window to 390px
// 1. translateX centres the 390px page visually in the browser window
// 2. waitForImages() runs the full beam scan — user watches the centred 390px page
// 3. removeProperty restores before serializeDOM() — coordinates are clean 390px
// =============================================================================
// ── expandHorizontalCarousels ─────────────────────────────────────────────────
// Finds all horizontal scroll/carousel containers in the DOM, scrolls through
// each item programmatically, and stamps absolute page coordinates onto each
// off-screen child via data-snapstak-carousel-rect. serializeDOM then reads
// these stamps and serializes every item as if it were in the visible viewport.
//
// Why this approach instead of setting overflow:visible (the vertical method):
// Setting overflow:visible on a flex carousel causes items to reflow — they
// collapse to their natural width and stack. The layout is destroyed. Instead,
// we keep the carousel intact, scroll to each item, read its live rect, store
// the absolute coordinates, then restore scroll position to 0. serializeDOM
// picks up the stamps and places items at correct absolute positions in the SVG.
//
// Detects carousels by: scrollWidth > clientWidth + 4 AND overflowX auto/scroll.
// Also detects scroll-snap containers (scroll-snap-type set).
// Capped at 40 items per carousel to avoid infinite loops on mega-carousels.
// All scroll positions restored to 0 after capture.
async function expandHorizontalCarousels() {
    const CAROUSEL_ATTR = 'data-snapstak-carousel-rect';
    const MAX_ITEMS = 40;
    const stamped = [];

    // Find all horizontal scroll containers
    const allEls = Array.from(document.querySelectorAll('*'));
    const carousels = allEls.filter(el => {
        const cs = window.getComputedStyle(el);
        const ox = cs.overflowX;
        if (ox !== 'auto' && ox !== 'scroll') return false;
        if (el.scrollWidth <= el.clientWidth + 4) return false;
        if (!el.children.length) return false;
        return true;
    });

    if (carousels.length === 0) return;
    console.log(`[SnapStak] expandHorizontalCarousels: ${carousels.length} carousel(s) found`);

    for (const carousel of carousels) {
        // Read container position BEFORE any scrolling — scroll position does not
        // affect getBoundingClientRect on the container itself.
        const containerRect = carousel.getBoundingClientRect();
        const containerTop = Math.round(containerRect.top + window.scrollY);
        const containerLeft = Math.round(containerRect.left + window.scrollX);
        const savedScrollLeft = carousel.scrollLeft;

        const items = Array.from(carousel.children);
        if (items.length === 0) continue;

        // Scroll to position 0 first — get a clean measurement of item dimensions
        // while the first item is fully visible.
        carousel.scrollLeft = 0;
        await new Promise(r => requestAnimationFrame(r));

        // Measure the first item to get item width and gap from the live browser.
        // All items in a carousel share the same width (set by CSS min-width/width).
        const firstRect = items[0].getBoundingClientRect();
        const itemH = Math.round(firstRect.height);

        let processed = 0;
        for (let i = 0; i < items.length && processed < MAX_ITEMS; i++) {
            const item = items[i];

            // x position = container's absolute left + item's offsetLeft within container.
            // offsetLeft is the item's distance from the carousel's left edge — independent
            // of scroll position. This gives the true absolute x for the SVG.
            const itemAbsX = containerLeft + item.offsetLeft;
            const itemAbsY = containerTop;

            // For width: scroll this item into view and measure live — captures
            // any item-specific width differences (first/last partial items etc).
            carousel.scrollLeft = item.offsetLeft;
            await new Promise(r => requestAnimationFrame(r));
            const itemRect = item.getBoundingClientRect();
            const itemAbsW = Math.round(itemRect.width);
            const itemAbsH = Math.round(itemRect.height) || itemH;

            if (itemAbsW < 2 || itemAbsH < 2) continue;

            // Stamp item with correct absolute coordinates
            item.setAttribute(CAROUSEL_ATTR, JSON.stringify({
                x: itemAbsX,
                y: itemAbsY,
                w: itemAbsW,
                h: itemAbsH,
                carouselIndex: i,
                carouselLeft: containerLeft,
                carouselTop: containerTop,
            }));
            stamped.push(item);

            // Stamp all descendants using their live rects (item is in view).
            // Their screen position is accurate now — offset by containerLeft - containerLeft
            // cancels, so we only need to add containerLeft to left + scrollX.
            for (const child of Array.from(item.querySelectorAll('*'))) {
                const cr = child.getBoundingClientRect();
                if (cr.width < 1 && cr.height < 1) continue;
                // child's absolute x = container left + (child screen left - container screen left) + item.offsetLeft
                const childAbsX = containerLeft + item.offsetLeft + Math.round(cr.left - containerRect.left - item.offsetLeft + carousel.scrollLeft);
                const childAbsY = Math.round(cr.top + window.scrollY);
                child.setAttribute(CAROUSEL_ATTR, JSON.stringify({
                    x: childAbsX,
                    y: childAbsY,
                    w: Math.round(cr.width),
                    h: Math.round(cr.height),
                    carouselIndex: i,
                }));
                stamped.push(child);
            }

            processed++;
        }

        // Restore scroll position
        carousel.scrollLeft = savedScrollLeft;
        await new Promise(r => requestAnimationFrame(r));

        console.log(`[SnapStak] expandHorizontalCarousels: stamped ${processed} items | container x=${containerLeft} y=${containerTop}`);
    }

    console.log(`[SnapStak] expandHorizontalCarousels: ${stamped.length} elements stamped total`);
    return stamped;
}

async function extractMobile(mode) {
    console.log('[SnapStak] Extracting mobile DOM snapshot at 390px...');

    const svgSymbolCache = await prefetchSVGSprites();

    // Visually centre the mobile page in the full browser window.
    // MUST apply translateX to <body> NOT <html>: a transform on <html> creates
    // a new containing block which breaks position:fixed on the beam overlay,
    // causing it to scroll away with the page instead of staying fixed.
    // Applying to <body> keeps position:fixed elements anchored to the viewport.
    const _monitorW = window.screen ? window.screen.availWidth : 1920;
    const _mobileW = window.innerWidth;
    const _offset = Math.max(0, Math.round((_monitorW - _mobileW) / 2));
    if (_offset > 0) {
        document.body.style.setProperty('transform', `translateX(${_offset}px)`, 'important');
        document.body.style.setProperty('transition', 'none', 'important');
    }

    // Full beam scan — beam and pill stay fixed to viewport during mobile scroll
    await waitForImages('⬡ SNAPSTAK · SCANNING MOBILE LAYOUT');

    // Remove visual centering BEFORE measurement — serializeDOM needs clean coords
    document.body.style.removeProperty('transform');
    document.body.style.removeProperty('transition');
    await new Promise(r => requestAnimationFrame(r));

    // ── Expand horizontal carousels — stamp off-screen item coords ────────────
    // Must run after visual centering is removed (clean coords) and before
    // serializeDOM so every carousel item gets an absolute-position stamp.
    await expandHorizontalCarousels();

    const { visible, invisible } = (mode === 'hidden') ? { visible: [], invisible: [] } : serializeDOM(svgSymbolCache);
    const hiddenComponents = (mode === 'visible') ? [] : await serializeHiddenComponents(svgSymbolCache);

    // ── ConteX Law — Structure Pillar: root background colour ────────────────
    // serializeDOM captures each element's own computed backgroundColor.
    // The page root (body/html) holds the true surface colour — the header itself
    // is transparent and inherits it. Without this walk the mobile root rect is
    // always transparent, forcing the AI to guess the background.
    // Mirrors the identical fix in extractElement() — browser is source of truth.
    if (visible.length > 0) {
        if (!visible[0].cssProps) visible[0].cssProps = {};
        if (!visible[0].cssProps.backgroundColor ||
            visible[0].cssProps.backgroundColor === 'rgba(0, 0, 0, 0)' ||
            visible[0].cssProps.backgroundColor === 'transparent') {
            let _bgNode = document.body;
            while (_bgNode && _bgNode !== document.documentElement) {
                const _bg = window.getComputedStyle(_bgNode).backgroundColor;
                if (_bg && _bg !== 'rgba(0, 0, 0, 0)' && _bg !== 'transparent') {
                    visible[0].cssProps.backgroundColor = _bg;
                    console.log('[SnapStak] Mobile root background resolved from ancestor:', _bg);
                    break;
                }
                _bgNode = _bgNode.parentElement;
            }
        }
    }

    // ── ConteX Law — Behaviour Pillar — capture CSS at live mobile viewport ──
    // extractComponentCSS is defined later in this file — called here on document.body
    // so every CSS rule active at the current 449px viewport width is captured.
    // This is the mobile Behaviour source — it travels to the server where it is
    // saved as _viewport_449px_css.json and merged into the Behaviour AI prompt.
    let componentCSS = null;
    let componentJS = null;
    try {
        componentCSS = (function extractComponentCSS(rootEl) {
            const elements = [rootEl, ...rootEl.querySelectorAll('*')];
            const elementSet = new Set(elements);
            const matchedRules = [];
            const behaviorRules = [];
            const mediaRules = [];
            const keyframes = [];
            const usedAnimations = new Set();
            for (const el of elements) {
                const cs = window.getComputedStyle(el);
                const anim = cs.animationName;
                if (anim && anim !== 'none') anim.split(',').forEach(a => usedAnimations.add(a.trim()));
            }
            function stripPseudos(sel) {
                return sel.replace(/:{1,2}(hover|focus|focus-within|focus-visible|active|visited|checked|disabled|enabled|placeholder-shown|placeholder|before|after|selection|marker|backdrop|first-child|last-child|first-of-type|last-of-type|only-child|only-of-type|empty|root|target|not\([^)]*\)|nth-child\([^)]*\)|nth-of-type\([^)]*\)|is\([^)]*\)|where\([^)]*\)|has\([^)]*\))/gi, '').replace(/\.\S+:\S+/g, '').trim();
            }
            function selectorMatchesComponent(sel) {
                const base = stripPseudos(sel);
                if (!base) return true;
                try { return Array.from(document.querySelectorAll(base)).some(el => elementSet.has(el)); } catch (_) { return false; }
            }
            function isBehaviorSelector(sel) {
                return /:(hover|focus|focus-within|focus-visible|active|disabled|checked)/i.test(sel);
            }
            try {
                for (const sheet of Array.from(document.styleSheets)) {
                    let rules;
                    try { rules = sheet.cssRules; } catch (_) { continue; }
                    if (!rules) continue;
                    // ── ConteX Law V5: resolve CSS custom properties at capture time ──
                    // rule.style.cssText contains raw var(--token) references that the AI
                    // cannot resolve. getPropertyValue() asks the live browser for the exact
                    // computed value — the same mechanism used by the desktop CSS capture.
                    // Both the token name AND the resolved value are stored so the AI
                    // receives exact px/font/color values, never guesses.
                    const _elStyle = window.getComputedStyle(rootEl);
                    const _docStyle = window.getComputedStyle(document.documentElement);
                    function _resolveVars(cssText) {
                        return (cssText || '').replace(/var\(\s*(--[a-zA-Z0-9_-]+)\s*(?:,[^)]+)?\)/g, (match, prop) => {
                            const val = _elStyle.getPropertyValue(prop).trim() || _docStyle.getPropertyValue(prop).trim();
                            return val ? val : match; // preserve token if browser cannot resolve
                        });
                    }
                    for (const rule of Array.from(rules)) {
                        if (rule instanceof CSSStyleRule) {
                            const sel = rule.selectorText || '';
                            if (selectorMatchesComponent(sel)) {
                                const entry = { selector: sel, properties: _resolveVars(rule.style.cssText) };
                                if (isBehaviorSelector(sel)) behaviorRules.push(entry);
                                else matchedRules.push(entry);
                            }
                        } else if (rule instanceof CSSMediaRule) {
                            const mediaText = rule.conditionText || rule.media?.mediaText || '';
                            const matchedInMedia = [];
                            for (const mr of Array.from(rule.cssRules || [])) {
                                if (mr instanceof CSSStyleRule) {
                                    const sel = mr.selectorText || '';
                                    if (selectorMatchesComponent(sel)) matchedInMedia.push({ selector: sel, properties: _resolveVars(mr.style.cssText) });
                                }
                            }
                            if (matchedInMedia.length) mediaRules.push({ media: mediaText, rules: matchedInMedia });
                        } else if (rule instanceof CSSKeyframesRule) {
                            if (usedAnimations.has(rule.name)) keyframes.push({ name: rule.name, cssText: rule.cssText });
                        }
                    }
                }
            } catch (_) { }
            return { matched: matchedRules, behavior: behaviorRules, media: mediaRules, keyframes };
        })(document.body);
    } catch (_mobileCSSerr) {
        console.warn('[SnapStak] Mobile CSS capture failed (non-fatal):', _mobileCSSerr.message);
    }

    // ── ConteX Law — Structure Pillar: page dimensions + landmark map ────────
    // Mobile was missing pageWidth, pageHeight, pageMap — causing:
    //   1. SVG height hardcoded to 900 — content clipped at 900px.
    //   2. snapstak:pagemap absent — segment boundaries lost.
    const mobilePageWidth = Math.max(document.body.scrollWidth, document.documentElement.scrollWidth);
    const mobilePageHeight = Math.max(document.body.scrollHeight, document.documentElement.scrollHeight);

    const MOBILE_LANDMARK_TAGS = new Set(['header', 'footer', 'nav', 'main', 'section', 'article', 'aside', 'form']);
    const mobilePageMap = [];
    try {
        const _mobileLandmarks = Array.from(document.querySelectorAll('*')).filter(el => {
            const tag = (el.tagName || '').toLowerCase();
            if (!MOBILE_LANDMARK_TAGS.has(tag)) return false;
            if (!el.dataset.segmentId) return false;
            const rect = el.getBoundingClientRect();
            if (rect.width < 2 || rect.height < 2) return false;
            let ancestor = el.parentElement;
            while (ancestor && ancestor !== document.body) {
                const aTag = (ancestor.tagName || '').toLowerCase();
                if (MOBILE_LANDMARK_TAGS.has(aTag) && ancestor.dataset.segmentId) return false;
                ancestor = ancestor.parentElement;
            }
            return true;
        });
        for (const el of _mobileLandmarks) {
            const tag = (el.tagName || '').toLowerCase();
            const sid = el.dataset.segmentId;
            const rect = el.getBoundingClientRect();
            const label = el.id || el.getAttribute('aria-label') || el.className.split(' ')[0] || tag;
            mobilePageMap.push({
                segmentId: sid, tag,
                label: label.slice(0, 80),
                x: Math.round(rect.left + window.scrollX),
                y: Math.round(rect.top + window.scrollY),
                w: Math.round(rect.width),
                h: Math.round(rect.height),
                cssB64: '', jsB64: '',
                htmlB64: (() => {
                    try {
                        const _bytes = new TextEncoder().encode(el.outerHTML || '');
                        let _bin = '';
                        for (const b of _bytes) _bin += String.fromCharCode(b);
                        return btoa(_bin);
                    } catch (_) { return ''; }
                })(),
            });
        }
        console.log(`[SnapStak] Mobile page map: ${mobilePageMap.length} landmarks | ${mobilePageWidth}x${mobilePageHeight}px`);
    } catch (_mapErr) {
        console.warn('[SnapStak] Mobile page map failed (non-fatal):', _mapErr.message);
    }

    return {
        success: true,
        domSnapshot: {
            elements: visible,
            pageWidth: mobilePageWidth,
            pageHeight: mobilePageHeight,
            pageMap: mobilePageMap,
        },
        hiddenComponents,
        componentCSS,
        componentJS,
    };
}

async function extractPage(mode, skipImageWait) {
    console.log('[SnapStak] Extracting page DOM snapshot...');

    // ── Force desktop layout so responsive Tailwind classes apply ─────────────
    // Breakpoint classes like md:w-5/12 and d:flex only activate at >=768px.
    // If the window is narrow those containers collapse to zero-width and their
    // children are filtered as invisible. Inject a temporary override stylesheet
    // that forces the md/d breakpoint rules to apply at ALL widths during capture.
    // SKIPPED at 390px — mobile capture must see real mobile layout.
    const _isMobileViewport = window.innerWidth <= 390;
    const _desktopOverride = document.createElement('style');
    _desktopOverride.id = '__snapstak_desktop_override__';
    _desktopOverride.textContent = [
        // Tailwind md: prefix  (768px breakpoint)
        '.md\\:w-5\\/12  { width: 41.666667% !important; }',
        '.md\\:w-7\\/12  { width: 58.333333% !important; }',
        '.md\\:w-4\\/12  { width: 33.333333% !important; }',
        '.md\\:flex      { display: flex    !important; }',
        '.md\\:grid      { display: grid    !important; }',
        '.md\\:block     { display: block   !important; }',
        '.md\\:col-span-2{ grid-column: span 2 / span 2 !important; }',
        // Autosport custom d: prefix (desktop breakpoint)
        '.d\\:flex         { display: flex    !important; }',
        '.d\\:block        { display: block   !important; }',
        '.d\\:justify-center { justify-content: center !important; }',
        // Hidden elements that should show on desktop
        '.hidden.d\\:flex  { display: flex    !important; }',
        '.hidden.d\\:block { display: block   !important; }',
        // Force flex-row on md:flex-row containers (hero panel side-by-side layout)
        '.md\\:flex-row    { flex-direction: row !important; }',
        // Force grid layout for md:grid containers
        '.md\\:grid-cols-2 { grid-template-columns: repeat(2, minmax(0, 1fr)) !important; }',
        // Force min-width on md:min-w-* so flex children don't collapse
        '[class*="md:min-w"] { min-width: 0 !important; }',
        // max-md overrides must be neutralised at desktop
        '.max-md\\:!w-\\[100vw\\] { width: auto !important; max-width: none !important; }',
    ].join('\n');
    if (!_isMobileViewport) {
        document.head.appendChild(_desktopOverride);
        // Give the browser one frame to reflow with the new rules
        await new Promise(r => requestAnimationFrame(() => setTimeout(r, 100)));
    }

    // Visual centering for mobile capture:
    // CDP sets window.innerWidth to mobile width — the page reflows.
    // Apply translateX to <body> (NOT <html>) so the mobile content is visually
    // centred in the full browser window. Using <body> preserves position:fixed
    // on the beam overlay — a transform on <html> breaks position:fixed.
    const _isMobileCapture = _isMobileViewport;
    if (_isMobileCapture) {
        const _monitorW = window.screen ? window.screen.availWidth : 1920;
        const _offset = Math.max(0, Math.round((_monitorW - window.innerWidth) / 2));
        if (_offset > 0) {
            document.body.style.setProperty('transform', `translateX(${_offset}px)`, 'important');
            document.body.style.setProperty('transition', 'none', 'important');
        }
    }

    // Wait for lazy-loaded images and fonts to settle
    if (!skipImageWait) await waitForImages();

    // Pre-fetch SVG sprite sheets so inline SVGs render correctly
    const svgSymbolCache = await prefetchSVGSprites();

    // ── Stamp segmentIds onto layout containers ───────────────────────────────
    // Single Unix timestamp for the entire run — all segments share it.
    const _runTs = Math.floor(Date.now() / 1000);
    stampSegmentIds(_runTs);

    // ── Build page map — TOP-LEVEL landmark elements only ───────────────────
    // Scans the DOM for structural landmark elements stamped with a segmentId.
    // Only includes elements that are NOT descendants of another stamped landmark
    // — this gives us the true top-level page components only.
    const LANDMARK_TAGS_MAP = new Set(['header', 'footer', 'nav', 'main', 'section', 'article', 'aside', 'form']);
    const pageMap = [];
    const _landmarkEls = Array.from(document.querySelectorAll('*')).filter(el => {
        const tag = (el.tagName || '').toLowerCase();
        if (!LANDMARK_TAGS_MAP.has(tag)) return false;
        if (!el.dataset.segmentId) return false;
        const rect = el.getBoundingClientRect();
        if (rect.width < 2 || rect.height < 2) return false;
        // Skip if nested inside another stamped landmark
        let ancestor = el.parentElement;
        while (ancestor && ancestor !== document.body) {
            const aTag = (ancestor.tagName || '').toLowerCase();
            if (LANDMARK_TAGS_MAP.has(aTag) && ancestor.dataset.segmentId) return false;
            ancestor = ancestor.parentElement;
        }
        return true;
    });
    for (const el of _landmarkEls) {
        const tag = (el.tagName || '').toLowerCase();
        const sid = el.dataset.segmentId;
        const rect = el.getBoundingClientRect();
        const label = el.id || el.getAttribute('aria-label') || el.className.split(' ')[0] || tag;

        // Extract CSS and JS for this landmark — same as single component path
        let cssB64 = '';
        let jsB64 = '';
        try {
            const beh = await extractSegmentBehaviour(sid);
            // TextEncoder produces UTF-8 bytes — safe for all unicode, no deprecated unescape
            const _toB64 = (obj) => {
                const bytes = new TextEncoder().encode(JSON.stringify(obj));
                let bin = '';
                for (const b of bytes) bin += String.fromCharCode(b);
                return btoa(bin);
            };
            if (beh.componentCSS) cssB64 = _toB64(beh.componentCSS);
            if (beh.componentJS) jsB64 = _toB64(beh.componentJS);
        } catch (e) {
            console.warn(`[SnapStak] CSS/JS extraction failed for landmark ${sid}:`, e.message);
        }

        pageMap.push({
            segmentId: sid,
            tag,
            label: label.slice(0, 80),
            x: Math.round(rect.left + window.scrollX),
            y: Math.round(rect.top + window.scrollY),
            w: Math.round(rect.width),
            h: Math.round(rect.height),
            cssB64,
            jsB64,
            // Pillar 1C: exact source HTML of this landmark.
            // Class names encode visual properties directly — hover colours,
            // dividers, spacing tokens — that are lost when converting to SVG.
            // The code generator uses this to transcribe class-based styling verbatim.
            htmlB64: (() => {
                try {
                    const _html = el.outerHTML || '';
                    const _bytes = new TextEncoder().encode(_html);
                    let _bin = '';
                    for (const b of _bytes) _bin += String.fromCharCode(b);
                    return btoa(_bin);
                } catch (_e) { return ''; }
            })(),
        });
    }
    console.log(`[SnapStak] Page map: ${pageMap.length} top-level landmark segments`);

    // Capture only what the mode needs
    // Remove visual centering BEFORE measurement — serializeDOM needs clean 390px coordinates.
    if (_isMobileCapture) {
        document.body.style.removeProperty('transform');
        document.documentElement.style.removeProperty('transition');
        await new Promise(r => requestAnimationFrame(r));
    }

    // ── Expand horizontal carousels — stamp off-screen item coords ────────────
    // Must run after stampSegmentIds and before serializeDOM.
    await expandHorizontalCarousels();

    const { visible, invisible } = (mode === 'hidden') ? { visible: [], invisible: [] } : serializeDOM(svgSymbolCache);
    const hiddenComponents = (mode === 'visible') ? [] : await serializeHiddenComponents(svgSymbolCache);
    const meta = extractMeta();

    const pageWidth = Math.max(document.body.scrollWidth, document.documentElement.scrollWidth);
    const pageHeight = Math.max(document.body.scrollHeight, document.documentElement.scrollHeight);

    console.log('[SnapStak] Visible elements:   ' + visible.length);
    console.log('[SnapStak] Invisible elements: ' + invisible.length);
    console.log('[SnapStak] Page: ' + pageWidth + 'x' + pageHeight);

    // ── Images are downloaded by the server to the assets folder ────────────
    // content.js passes raw URLs only — no base64 embedding needed here.
    console.log('[SnapStak] Images to download by server: ' +
        visible.filter(e => e.tag === 'img' && e.src && !e.src.startsWith('data:')).length);

    // Remove the desktop layout override now that snapshot is complete
    const _override = document.getElementById('__snapstak_desktop_override__');
    if (_override) _override.remove();

    // ── Capture the Influence pillar ─────────────────────────────────────────
    // Browser name/version, OS, screen dimensions, viewport, DPR.
    // Must be captured AFTER the desktop override is removed so viewport
    // reflects the real user environment, not the forced layout state.
    const influence = (function captureInfluence() {
        const ua = navigator.userAgent || '';

        let browserName = 'Unknown';
        let browserVersion = 'Unknown';
        if (/Edg\//.test(ua)) {
            browserName = 'Edge';
            browserVersion = (ua.match(/Edg\/([\d.]+)/) || [])[1] || 'Unknown';
        } else if (/OPR\//.test(ua)) {
            browserName = 'Opera';
            browserVersion = (ua.match(/OPR\/([\d.]+)/) || [])[1] || 'Unknown';
        } else if (/Chrome\//.test(ua)) {
            browserName = 'Chrome';
            browserVersion = (ua.match(/Chrome\/([\d.]+)/) || [])[1] || 'Unknown';
        } else if (/Firefox\//.test(ua)) {
            browserName = 'Firefox';
            browserVersion = (ua.match(/Firefox\/([\d.]+)/) || [])[1] || 'Unknown';
        } else if (/Safari\//.test(ua) && /Version\//.test(ua)) {
            browserName = 'Safari';
            browserVersion = (ua.match(/Version\/([\d.]+)/) || [])[1] || 'Unknown';
        }

        let osName = 'Unknown';
        let osVersion = 'Unknown';
        if (/Windows NT/.test(ua)) {
            osName = 'Windows';
            const ntVersion = (ua.match(/Windows NT ([\d.]+)/) || [])[1] || '';
            const ntMap = { '10.0': '10/11', '6.3': '8.1', '6.2': '8', '6.1': '7' };
            osVersion = ntMap[ntVersion] || ntVersion;
        } else if (/Mac OS X/.test(ua)) {
            osName = 'macOS';
            osVersion = ((ua.match(/Mac OS X ([\d_]+)/) || [])[1] || '').replace(/_/g, '.');
        } else if (/Android/.test(ua)) {
            osName = 'Android';
            osVersion = (ua.match(/Android ([\d.]+)/) || [])[1] || 'Unknown';
        } else if (/iPhone|iPad|iPod/.test(ua)) {
            osName = 'iOS';
            osVersion = ((ua.match(/OS ([\d_]+)/) || [])[1] || '').replace(/_/g, '.');
        } else if (/Linux/.test(ua)) {
            osName = 'Linux';
            osVersion = 'Unknown';
        }

        return {
            browserName,
            browserVersion,
            osName,
            osVersion,
            screenWidth: window.screen.width,
            screenHeight: window.screen.height,
            devicePixelRatio: window.devicePixelRatio || 1,
            viewportWidth: window.innerWidth,
            viewportHeight: window.innerHeight,
            userAgent: ua,
            capturedAt: new Date().toISOString(),
            // Media feature queries — captured at extraction time from the live browser.
            // Dark mode and reduced motion affect rendering decisions at code generation time.
            prefersColorScheme: window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light',
            prefersReducedMotion: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'reduce' : 'no-preference',
        };
    })();

    // ── Capture the Objective pillar (Phase 1 — inferred at extraction time) ──
    // Phase 2 (device target, screen width, framework) is confirmed by the user
    // in the popup at conversion time. Null fields are filled there.
    const objective = (function captureObjective() {
        const vw = window.innerWidth;
        const dpr = window.devicePixelRatio || 1;

        let deviceTypeInferred;
        if (vw <= 480) {
            deviceTypeInferred = 'mobile';
        } else if (vw <= 1024) {
            deviceTypeInferred = 'tablet';
        } else {
            deviceTypeInferred = 'desktop';
        }

        const screenPresets = [
            { label: 'Mobile: iPhone SE', width: 375, device: 'mobile' },
            { label: 'Mobile: iPhone 15', width: 390, device: 'mobile' },
            { label: 'Mobile: iPhone 15 Plus', width: 430, device: 'mobile' },
            { label: 'Tablet: iPad', width: 768, device: 'tablet' },
            { label: 'Tablet: iPad Pro 11"', width: 834, device: 'tablet' },
            { label: 'Tablet: iPad Pro 13"', width: 1024, device: 'tablet' },
            { label: 'Desktop: 1280px', width: 1280, device: 'desktop' },
            { label: 'Desktop: 1440px', width: 1440, device: 'desktop' },
            { label: 'Desktop: 1920px', width: 1920, device: 'desktop' },
            { label: 'All Breakpoints', width: null, device: 'all' },
        ];

        return {
            deviceTypeInferred,
            deviceTypeSelected: null,
            screenWidthTarget: null,
            screenSizeLabel: null,
            allBreakpoints: false,
            framework: null,
            additionalIntent: null,
            screenPresets,
            captureViewportWidth: vw,
            captureDevicePixelRatio: dpr,
        };
    })();

    return {
        success: true,
        domSnapshot: { elements: visible, pageWidth, pageHeight, pageMap },
        domSnapshotHidden: { elements: invisible, pageWidth, pageHeight },
        hiddenComponents,
        meta,
        influence,
        objective,
    };
}



// =============================================================================
// HIDDEN COMPONENT SERIALIZER
// Captures UI components hidden at page load as a component catalogue.
//
// KEY INSIGHT: getComputedStyle() works fully on display:none elements.
// offsetLeft / offsetTop / offsetWidth / offsetHeight give real CSS layout
// geometry relative to nearest positioned ancestor — even when hidden.
// We use these instead of getBoundingClientRect() which returns zeros.
//
// Each hidden ROOT component becomes one entry in hiddenComponents[].
// Its child elements are captured with positions RELATIVE to the component
// root (offsetLeft/offsetTop walking up to the root).
// The SVG serializer then stacks components vertically.
// =============================================================================
async function serializeHiddenComponents(svgSymbolCache) {
    svgSymbolCache = svgSymbolCache || new Map();

    // ── Helpers ────────────────────────────────────────────────────────────────

    function classStr(el) {
        return typeof el.className === 'string' ? el.className : '';
    }

    function resolveComponentType(el) {
        var role = (el.getAttribute && el.getAttribute('role') || '').toLowerCase();
        var id = (el.id || '').toLowerCase();
        var cls = classStr(el).toLowerCase();
        if (role === 'dialog' || cls.includes('modal') || cls.includes('dialog')) return 'Modal';
        if (role === 'navigation' || cls.includes('drawer') || id.includes('drawer')) return 'Drawer';
        if (role === 'menu' || cls.includes('dropdown') || cls.includes('submenu')) return 'Dropdown';
        if (el.hasAttribute && el.hasAttribute('popover')) return 'Popover';
        if (cls.includes('popover')) return 'Popover';
        if (cls.includes('sidebar')) return 'Sidebar';
        if (cls.includes('panel') || cls.includes('widget')) return 'Panel';
        if (cls.includes('menu')) return 'Menu';
        if (cls.includes('nav')) return 'Nav';
        return 'HiddenPanel';
    }

    function resolveLabel(el) {
        return el.getAttribute('aria-label') ||
            el.getAttribute('aria-labelledby') ||
            el.id ||
            el.getAttribute('data-component') ||
            el.tagName.toLowerCase();
    }

    // Read all visual CSS from an element (works on hidden elements)
    function readCSS(el) {
        var cs = window.getComputedStyle(el);
        return {
            backgroundColor: cs.backgroundColor || '',
            backgroundImage: cs.backgroundImage || '',
            border: cs.border || '',
            borderRadius: cs.borderRadius || '',
            borderColor: cs.borderColor || '',
            borderWidth: cs.borderWidth || '',
            boxShadow: cs.boxShadow || '',
            color: cs.color || '',
            fontFamily: cs.fontFamily || '',
            fontSize: cs.fontSize || '',
            fontWeight: cs.fontWeight || '',
            fontStyle: cs.fontStyle || '',
            lineHeight: cs.lineHeight || '',
            letterSpacing: cs.letterSpacing || '',
            textAlign: cs.textAlign || '',
            textTransform: cs.textTransform || '',
            textDecoration: cs.textDecoration || '',
            padding: cs.padding || '',
            paddingTop: cs.paddingTop || '',
            paddingRight: cs.paddingRight || '',
            paddingBottom: cs.paddingBottom || '',
            paddingLeft: cs.paddingLeft || '',
            display: cs.display || '',
            flexDirection: cs.flexDirection || '',
            alignItems: cs.alignItems || '',
            justifyContent: cs.justifyContent || '',
            gap: cs.gap || '',
            alignSelf: cs.alignSelf || '',
            flexGrow: cs.flexGrow || '',
            flexShrink: cs.flexShrink || '',
            position: cs.position || '',
            opacity: cs.opacity || '',
            zIndex: cs.zIndex || '',
            overflow: cs.overflow || '',
            whiteSpace: cs.whiteSpace || '',
            width: cs.width || '',
            height: cs.height || '',
            minWidth: cs.minWidth || '',
            minHeight: cs.minHeight || '',
            maxWidth: cs.maxWidth || '',
            maxHeight: cs.maxHeight || '',
            objectFit: cs.objectFit || '',
            cursor: cs.cursor || '',
            pointerEvents: cs.pointerEvents || '',
            listStyleType: cs.listStyleType || '',
        };
    }

    // Get position of el relative to a given ancestor root element.
    // Uses offsetLeft/offsetTop chain — works on hidden elements.
    function getRelativeOffset(el, root) {
        var x = 0, y = 0;
        var node = el;
        while (node && node !== root && node !== document.body) {
            x += node.offsetLeft || 0;
            y += node.offsetTop || 0;
            node = node.offsetParent;
            // Safety: if offsetParent escapes the root, stop
            if (node && root.contains && !root.contains(node)) break;
        }
        return { x: Math.round(x), y: Math.round(y) };
    }

    // Get CSS dimension as number (e.g. "320px" → 320, "100%" → 0 fallback)
    function cssDim(val, fallback) {
        if (!val) return fallback || 0;
        var n = parseFloat(val);
        return isNaN(n) ? (fallback || 0) : Math.round(n);
    }

    // Get direct text content (text nodes only, not descendant element text)
    function getDirectText(el) {
        var t = '';
        for (var i = 0; i < el.childNodes.length; i++) {
            var n = el.childNodes[i];
            if (n.nodeType === 3) t += n.textContent;
        }
        return t.trim().slice(0, 500);
    }

    // Walk all descendants of a hidden root, collecting element data.
    // Positions are relative to rootEl via offsetLeft/offsetTop.
    function walkHiddenEl(el, rootEl, elCounter, depth, rootRect, rootH) {
        if (depth > 25) return [];
        var tag = el.tagName ? el.tagName.toLowerCase() : '';

        var SKIP_TAGS = { script: 1, noscript: 1, style: 1, head: 1, meta: 1, link: 1, title: 1, base: 1 };
        if (SKIP_TAGS[tag]) return [];

        var cs = readCSS(el);

        // Use getBoundingClientRect — element is visible (fixed positioned) so this works
        var elRect = el.getBoundingClientRect();
        var rootR = rootRect || { left: 0, top: 0 };
        var offX = Math.round(elRect.left - rootR.left);
        var offY = Math.round(elRect.top - rootR.top);
        var off = { x: offX, y: offY };

        // Dimensions from getBoundingClientRect — accurate since element is visible
        var w = Math.round(elRect.width) || cssDim(cs.width, 0);
        var h = Math.round(elRect.height) || cssDim(cs.height, 0);
        // img inside a collapsed sub-accordion: getBoundingClientRect().height = 0 or wrong
        // due to flex container stretch. Read the stamped data-snapstak-h attribute if present.
        if (tag === 'img' && el.dataset && el.dataset.snapstkH) {
            h = parseInt(el.dataset.snapstkH, 10) || h;
        }

        // Determine element type
        var TEXT_TAGS = {
            h1: 1, h2: 1, h3: 1, h4: 1, h5: 1, h6: 1, p: 1, span: 1,
            strong: 1, em: 1, b: 1, i: 1, u: 1, label: 1,
            legend: 1, dt: 1, dd: 1, th: 1, td: 1, blockquote: 1,
            code: 1, pre: 1, time: 1, figcaption: 1, li: 1, a: 1
        };
        var IMG_TAGS = { img: 1, picture: 1 };
        var SVG_TAGS = { svg: 1 };

        var elType = SVG_TAGS[tag] ? 'icon'
            : IMG_TAGS[tag] ? 'image'
                : TEXT_TAGS[tag] ? 'text'
                    : 'box';

        // Text content — only capture on genuine text tags or true leaf nodes.
        // NEVER capture text on a text element that has text-tag children —
        // those children will render the same text causing duplicates.
        var TEXT_TAGS_CHECK = {
            h1: 1, h2: 1, h3: 1, h4: 1, h5: 1, h6: 1, p: 1, span: 1,
            strong: 1, em: 1, b: 1, i: 1, u: 1, label: 1,
            legend: 1, dt: 1, dd: 1, th: 1, td: 1, blockquote: 1,
            code: 1, pre: 1, time: 1, figcaption: 1, li: 1, a: 1
        };
        var textContent = '';
        if (elType === 'text') {
            // Check ALL descendants — if any is a text tag, let them render instead
            var hasTextDescendant = false;
            var allDesc = el.querySelectorAll ? el.querySelectorAll('*') : [];
            for (var td = 0; td < allDesc.length; td++) {
                var descTag = allDesc[td].tagName ? allDesc[td].tagName.toLowerCase() : '';
                if (TEXT_TAGS_CHECK[descTag]) { hasTextDescendant = true; break; }
            }
            if (!hasTextDescendant) {
                textContent = (el.innerText || el.textContent || '').trim().slice(0, 500);
            } else {
                elType = 'box'; // demote — children will render the text
            }
        } else if (el.children.length === 0) {
            // True leaf node — safe to promote to text
            textContent = (el.innerText || el.textContent || '').trim().slice(0, 500);
            if (textContent) elType = 'text';
        }
        // Box elements with children: never capture text

        // Image source
        var imgSrc = '';
        if (elType === 'image') {
            imgSrc = el.currentSrc || el.src || el.getAttribute('src') || '';
            // Also check srcset
            if (!imgSrc && el.srcset) imgSrc = el.srcset.split(',')[0].trim().split(' ')[0];
        }

        // SVG sprite icon — resolve from svgSymbolCache exactly as __serializeRoot does.
        // Produces svgDataURI so the icon renders correctly in the output.
        if (tag === 'svg') {
            if (w > 1 && h > 1) {
                var _iconColor = cs.color || '#ffffff';
                var _svgMarkup = null;
                var _useEl = el.querySelector('use');
                if (_useEl) {
                    var _XLINK = 'http://www.w3.org/1999/xlink';
                    var _href = _useEl.getAttributeNS(_XLINK, 'href')
                        || _useEl.getAttribute('href')
                        || _useEl.getAttribute('xlink:href')
                        || '';
                    if (_href.includes('.svg#') || (_href.startsWith('#') && _href.length > 1)) {
                        var _absHref = _href;
                        if (_href.startsWith('/') || _href.startsWith('./') || _href.startsWith('../')) {
                            try { _absHref = new URL(_href, window.location.origin).href; } catch (e) { }
                        }
                        var _cached = svgSymbolCache.get(_absHref) || svgSymbolCache.get(_href);
                        if (_cached) {
                            _svgMarkup = _cached
                                .replace(/fill="currentColor"/gi, 'fill="' + _iconColor + '"')
                                .replace(/stroke="currentColor"/gi, 'stroke="' + _iconColor + '"');
                        }
                    }
                }
                if (!_svgMarkup) {
                    _svgMarkup = el.outerHTML
                        .replace(/fill="currentColor"/gi, 'fill="' + _iconColor + '"')
                        .replace(/stroke="currentColor"/gi, 'stroke="' + _iconColor + '"');
                    if (!_svgMarkup.includes('xmlns=')) {
                        _svgMarkup = _svgMarkup.replace('<svg ', '<svg xmlns="http://www.w3.org/2000/svg" ');
                    }
                    // Resolve CSS vars inside <style> blocks
                    var _ds1 = window.getComputedStyle(document.documentElement);
                    _svgMarkup = _svgMarkup.replace(/(<style[^>]*>)([\s\S]*?)(<\/style>)/gi, function (m, o, css, c) {
                        return o + css.replace(/rgb\(var\((--[^)]+)\)\)/g, function (m2, p) { var v = _ds1.getPropertyValue(p.trim()).trim(); return v ? 'rgb(' + v + ')' : m2; })
                            .replace(/var\((--[^)]+)\)/g, function (m2, p) { var v = _ds1.getPropertyValue(p.trim()).trim(); return v || m2; }) + c;
                    });
                }
                var _svgDataURI = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(_svgMarkup);
                return [{
                    internalId: 'hel_' + (elCounter[0]++),
                    tag: 'svg',
                    type: 'icon',
                    isSVGIcon: true,
                    svgDataURI: _svgDataURI,
                    rect: { x: off.x, y: off.y, width: w, height: h },
                    cssProps: { color: _iconColor, fill: cs.fill || 'currentColor' },
                    ariaLabel: el.getAttribute && el.getAttribute('aria-label') || '',
                    role: el.getAttribute && el.getAttribute('role') || '',
                    alt: '',
                }];
            }
            return []; // zero-size SVG — skip
        }

        // Background image
        var bgImage = '';
        if (cs.backgroundImage && cs.backgroundImage !== 'none') {
            var m = cs.backgroundImage.match(/url\(["']?([^"')]+)["']?\)/);
            if (m) bgImage = m[1];
        }

        // Border radius
        var borderRadius = 0;
        var brMatch = (cs.borderRadius || '').match(/([\d.]+)(px|%)/);
        if (brMatch) borderRadius = Math.round(parseFloat(brMatch[1]));

        var entry = {
            internalId: 'hel_' + (elCounter[0]++),
            tag: tag,
            type: elType,
            rect: { x: off.x, y: off.y, width: w, height: h },
            textContent: textContent,
            imgSrc: imgSrc,
            bgImage: bgImage,
            borderRadius: borderRadius,
            cssProps: cs,
            componentAttr: el.getAttribute && el.getAttribute('data-component') || '',
            ariaLabel: el.getAttribute && el.getAttribute('aria-label') || '',
            role: el.getAttribute && el.getAttribute('role') || '',
            alt: (elType === 'image' && el.alt) ? el.alt : '',
        };

        var results = [entry];

        // Recurse into children (skip void tags)
        var VOID = { img: 1, input: 1, video: 1, audio: 1, hr: 1, br: 1, meta: 1, link: 1, source: 1 };
        for (var i = 0; i < el.children.length; i++) {
            var child = el.children[i];
            var childTag = child.tagName ? child.tagName.toLowerCase() : '';
            if (!VOID[childTag]) {
                var childResults = walkHiddenEl(child, rootEl, elCounter, depth + 1, rootRect, rootH);
                for (var j = 0; j < childResults.length; j++) {
                    results.push(childResults[j]);
                }
            }
        }

        return results;
    }

    // ── Find hidden root components ───────────────────────────────────────────
    // Walk the entire document body looking for elements that are:
    //   - display:none OR visibility:hidden OR [hidden] attribute
    //   - NOT inside another already-found hidden root
    // These are the component roots.

    var hiddenRoots = [];
    var visited = new Set();

    function findHiddenRoots(el, insideHidden) {
        if (!el || el.nodeType !== 1) return;
        var tag = el.tagName ? el.tagName.toLowerCase() : '';
        var SKIP = { script: 1, noscript: 1, style: 1, head: 1, meta: 1, link: 1, title: 1 };
        if (SKIP[tag]) return;

        var cs = window.getComputedStyle(el);
        var isDisplayNone = cs.display === 'none';
        var isHiddenAttr = el.hasAttribute && el.hasAttribute('hidden');
        var isHidden = isDisplayNone || isHiddenAttr;

        if (isHidden && !insideHidden) {
            // This is a hidden root — capture it, don't recurse further
            // (we'll walk it separately with walkHiddenEl)
            if (!visited.has(el)) {
                visited.add(el);
                hiddenRoots.push(el);
            }
            return; // don't recurse — children belong to this component
        }

        // Recurse into visible children looking for hidden roots inside them
        for (var i = 0; i < el.children.length; i++) {
            findHiddenRoots(el.children[i], isHidden);
        }
    }

    findHiddenRoots(document.body, false);

    // ── Process each hidden root ──────────────────────────────────────────────
    var compCounter = [0];
    var elCounter = [0];
    var components = [];

    for (var ci = 0; ci < hiddenRoots.length; ci++) {
        var root = hiddenRoots[ci];
        var rootTag = root.tagName ? root.tagName.toLowerCase() : '';

        // Skip tiny/empty roots (no real content)
        var rootText = (root.innerText || root.textContent || '').trim();
        var rootChildren = root.querySelectorAll ? root.querySelectorAll('*').length : 0;
        if (!rootText && rootChildren === 0) continue;
        // Skip roots with almost no content (< 2 children, no text)
        if (rootChildren < 2 && !rootText) continue;

        var compId = 'hc_' + (compCounter[0]++);
        var compType = resolveComponentType(root);
        var compLabel = resolveLabel(root);

        // ── Temporarily make component visible for accurate measurement ──────────
        // ── Make component measurable by unhiding entire ancestor chain ───────────
        // offsetLeft/offsetTop returns 0 when ANY ancestor is display:none.
        // Also handles height:0 + overflow:hidden (common CSS hide technique).
        // Walk UP the DOM, temporarily fix every hidden ancestor, measure, restore.
        // ── Measure scroll state BEFORE any ancestor expansion ─────────────────────
        // The ancestor loop sets height:auto/overflow:visible which destroys clientHeight.
        // Capture the true visible height NOW, before the DOM is manipulated.
        var _preExpandClientH = root.clientHeight || 0;
        var _preExpandScrollH = root.scrollHeight || 0;
        var _rootIsScrollable = _preExpandClientH > 0 && _preExpandScrollH > _preExpandClientH * 1.2;
        var _scrollableClientH = _rootIsScrollable ? _preExpandClientH : 0;

        var hiddenAncestors = [];
        var node = root;
        while (node && node !== document.body) {
            var nodeCS = window.getComputedStyle(node);
            var needsFix = false;
            var saved = { el: node, display: node.style.display, visibility: node.style.visibility, height: node.style.height, overflow: node.style.overflow, maxHeight: node.style.maxHeight, overflowY: node.style.overflowY, transform: node.style.transform };
            if (nodeCS.display === 'none') {
                node.style.setProperty('display', 'block', 'important');
                node.style.setProperty('visibility', 'hidden', 'important');
                needsFix = true;
            }
            // Also fix height:0 or max-height:0 — hides without display:none
            // Check overflow, overflow-y, and clip separately as shorthand may differ
            var isHeightZero = parseFloat(nodeCS.height) === 0;
            var isMaxHgtZero = parseFloat(nodeCS.maxHeight) === 0;
            var isOverflowHide = nodeCS.overflow === 'hidden' || nodeCS.overflowY === 'hidden' || nodeCS.overflow === 'clip' || nodeCS.overflowY === 'clip';
            if ((isHeightZero || isMaxHgtZero) && isOverflowHide) {
                node.style.height = 'auto';
                node.style.maxHeight = 'none';
                node.style.overflow = 'visible';
                node.style.overflowY = 'visible';
                needsFix = true;
            }
            // Also fix transform:scaleY(0) which collapses height visually
            if (nodeCS.transform && nodeCS.transform !== 'none' && nodeCS.transform.indexOf('matrix') !== -1) {
                var m = nodeCS.transform.match(/matrix\(([^)]+)\)/);
                if (m) {
                    var vals = m[1].split(',').map(parseFloat);
                    // matrix(scaleX,0,0,scaleY,tx,ty) — scaleY is vals[3]
                    if (Math.abs(vals[3]) < 0.01) {
                        node.style.transform = 'none';
                        needsFix = true;
                    }
                }
            }
            if (needsFix) hiddenAncestors.push(saved);
            node = node.parentElement;
        }
        void root.offsetHeight; // force layout

        // After fixing ancestors, root itself may STILL be display:none via its own CSS class
        // Fix the root directly if needed
        var rootCS2 = window.getComputedStyle(root);
        var _rootOwnDisplay = root.style.display;
        var _rootOwnVis = root.style.visibility;
        var _rootFixed = false;
        if (rootCS2.display === 'none') {
            root.style.setProperty('display', 'block', 'important');
            root.style.setProperty('visibility', 'hidden', 'important');
            _rootFixed = true;
            void root.offsetHeight;
        }

        var rootRect = root.getBoundingClientRect();
        // ConteX Law — Structure pillar: popover elements in the top layer get
        // width:100vw from the browser, making getBoundingClientRect().width return
        // the full viewport width. scrollWidth gives the intrinsic content width.
        var _rootIsPopover = root.hasAttribute && root.hasAttribute('popover');
        var rootW = _rootIsPopover
            ? (root.scrollWidth || Math.round(rootRect.width) || window.innerWidth)
            : (Math.round(rootRect.width) || root.scrollWidth || window.innerWidth);
        // Use the pre-expansion clientHeight for scrollable containers.
        // By this point ancestor styles have been manipulated — root.clientHeight is no longer reliable.
        var _rawH = Math.round(rootRect.height) || 0;
        var rootH = Math.min(
            _scrollableClientH || _rawH || root.clientHeight || 0,
            window.innerHeight
        );

        // Hard fallback: if height still 0, force auto on root itself and re-measure
        if (rootH === 0) {
            var _rh = root.style.height;
            var _ro = root.style.overflow;
            var _rm = root.style.maxHeight;
            root.style.height = 'auto';
            root.style.maxHeight = 'none';
            root.style.overflow = 'visible';
            void root.offsetHeight;
            rootRect = root.getBoundingClientRect();
            // Still cap to viewport — never expand beyond visible area
            rootH = Math.min(
                Math.round(rootRect.height) || root.clientHeight || 0,
                window.innerHeight
            );
            // Don't restore yet — keep open while walking
            var _rootForcedOpen = true;
        }

        // Walk while fully laid out
        var elements = walkHiddenEl(root, root, elCounter, 0, rootRect, rootH);

        // ── Stamp segmentId on root NOW — while it has a real rect ───────────
        // Must happen before restoration so getBoundingClientRect is valid.
        // The attribute persists after display:none is restored, so
        // extractSegmentBehaviour can find it by querySelector even when hidden.
        if (!root.dataset.segmentId) {
            root.dataset.segmentId = 'ss_hc_' + compId + '_' + Math.floor(Date.now() / 1000);
        }
        var _hiddenSegmentId = root.dataset.segmentId;

        // Restore root's own fix if applied
        if (_rootFixed) {
            root.style.removeProperty('display');
            root.style.removeProperty('visibility');
        }

        // Restore root forced-open if we did it
        if (_rootForcedOpen) {
            root.style.height = _rh;
            root.style.overflow = _ro;
            root.style.maxHeight = _rm;
        }

        // Restore all ancestors in reverse order
        for (var ai = hiddenAncestors.length - 1; ai >= 0; ai--) {
            var s = hiddenAncestors[ai];
            s.el.style.removeProperty('display');
            s.el.style.removeProperty('visibility');
            s.el.style.height = s.height;
            s.el.style.overflow = s.overflow;
            s.el.style.maxHeight = s.maxHeight;
            s.el.style.overflowY = s.overflowY;
            s.el.style.transform = s.transform;
        }

        // Filter out entries with no visual content (zero size, no text, no image)
        // Also filter out elements that fall outside the visible rootH (scroll overflow)
        elements = elements.filter(function (e) {
            var hasContent = e.textContent || e.imgSrc || e.bgImage || e.inputType ||
                (e.rect.width > 0 && e.rect.height > 0);
            var isVisible = e.rect.y < rootH; // only elements within visible height
            return hasContent && isVisible;
        });

        if (elements.length === 0) continue;

        // ── Extract Behaviour pillar for this hidden component (sync) ─────────
        // No network requests — page has been fully scrolled so all scripts are
        // already loaded in the DOM. extractSegmentBehaviour reads inline handlers
        // and inline scripts only. No fetch() calls. No hangs.
        var cssB64 = '';
        var jsB64 = '';
        try {
            var _beh = await extractSegmentBehaviour(_hiddenSegmentId);
            var _toB64 = function (obj) {
                var bytes = new TextEncoder().encode(JSON.stringify(obj));
                var bin = '';
                for (var bi = 0; bi < bytes.length; bi++) bin += String.fromCharCode(bytes[bi]);
                return btoa(bin);
            };
            if (_beh.componentCSS) cssB64 = _toB64(_beh.componentCSS);
            if (_beh.componentJS) jsB64 = _toB64(_beh.componentJS);
        } catch (e) {
            console.warn('[SnapStak] Behaviour extraction failed for hidden component ' + compId + ':', e.message);
        }

        components.push({
            componentId: compId,
            componentType: compType,
            label: compLabel,
            tag: rootTag,
            width: rootW || window.innerWidth,
            height: rootH,
            elements: elements,
            segmentId: _hiddenSegmentId,
            cssB64,
            jsB64,
        });
    }

    return components;
}



// Allow extractElement to call serializeHiddenComponents with specific roots
serializeHiddenComponents.__fromRoots = function (roots, svgSymbolCache) {
    svgSymbolCache = svgSymbolCache || new Map();
    var components = [];
    for (var i = 0; i < roots.length; i++) {
        var partial = serializeHiddenComponents.__serializeRoot(roots[i], svgSymbolCache);
        if (partial) components.push(partial);
    }
    return components;
};

serializeHiddenComponents.__serializeRoot = function (rootEl, svgSymbolCache) {
    // Produces the EXACT same structure as serializeHiddenComponents / walkHiddenEl
    // so the server's serializeHiddenSVG can process it without modification.
    svgSymbolCache = svgSymbolCache || new Map();

    var elCounter = [0];
    var compCounter = [0];

    function resolveComponentType(el) {
        var role = (el.getAttribute && el.getAttribute('role') || '').toLowerCase();
        var id = (el.id || '').toLowerCase();
        var cls = (typeof el.className === 'string' ? el.className : '').toLowerCase();
        if (role === 'dialog' || cls.includes('modal') || cls.includes('dialog')) return 'Modal';
        if (role === 'navigation' || cls.includes('drawer') || id.includes('drawer')) return 'Drawer';
        if (role === 'menu' || cls.includes('dropdown') || cls.includes('submenu')) return 'Dropdown';
        if (cls.includes('popover')) return 'Popover';
        if (cls.includes('sidebar')) return 'Sidebar';
        if (cls.includes('panel') || cls.includes('widget')) return 'Panel';
        if (cls.includes('menu')) return 'Menu';
        if (cls.includes('nav')) return 'Nav';
        return 'SelectedComponent';
    }

    function resolveLabel(el) {
        return el.getAttribute('aria-label') ||
            el.getAttribute('aria-labelledby') ||
            el.id ||
            el.getAttribute('data-component') ||
            el.tagName.toLowerCase();
    }

    function resolveElementType(el, tag) {
        if (tag === 'img' || tag === 'picture') return 'image';
        if (tag === 'svg') return 'svg';
        if (tag === 'button' || (tag === 'input' && (el.type === 'button' || el.type === 'submit'))) return 'button';
        if (tag === 'a') return 'link';
        if (tag === 'input' || tag === 'textarea' || tag === 'select') return 'input';
        if (tag === 'video') return 'video';
        if (/^h[1-6]$/.test(tag)) return 'heading';
        if (tag === 'p' || tag === 'span' || tag === 'label') return 'text';
        if (tag === 'ul' || tag === 'ol' || tag === 'li') return 'list';
        if (tag === 'nav') return 'nav';
        if (tag === 'header' || tag === 'footer' || tag === 'main' || tag === 'section' || tag === 'article') return 'section';
        return 'container';
    }

    function readCSS(el) {
        var cs = window.getComputedStyle(el);
        return {
            backgroundColor: cs.backgroundColor || '',
            backgroundImage: cs.backgroundImage || '',
            border: cs.border || '',
            borderRadius: cs.borderRadius || '',
            borderColor: cs.borderColor || '',
            borderWidth: cs.borderWidth || '',
            boxShadow: cs.boxShadow || '',
            color: cs.color || '',
            fontFamily: cs.fontFamily || '',
            fontSize: cs.fontSize || '',
            fontWeight: cs.fontWeight || '',
            fontStyle: cs.fontStyle || '',
            lineHeight: cs.lineHeight || '',
            letterSpacing: cs.letterSpacing || '',
            textAlign: cs.textAlign || '',
            textTransform: cs.textTransform || '',
            textDecoration: cs.textDecoration || '',
            padding: cs.padding || '',
            paddingTop: cs.paddingTop || '',
            paddingRight: cs.paddingRight || '',
            paddingBottom: cs.paddingBottom || '',
            paddingLeft: cs.paddingLeft || '',
            display: cs.display || '',
            flexDirection: cs.flexDirection || '',
            alignItems: cs.alignItems || '',
            justifyContent: cs.justifyContent || '',
            gap: cs.gap || '',
            alignSelf: cs.alignSelf || '',
            flexGrow: cs.flexGrow || '',
            flexShrink: cs.flexShrink || '',
            position: cs.position || '',
            opacity: cs.opacity || '',
            zIndex: cs.zIndex || '',
            overflow: cs.overflow || '',
            whiteSpace: cs.whiteSpace || '',
            width: cs.width || '',
            height: cs.height || '',
            minWidth: cs.minWidth || '',
            minHeight: cs.minHeight || '',
            maxWidth: cs.maxWidth || '',
            maxHeight: cs.maxHeight || '',
            objectFit: cs.objectFit || '',
            cursor: cs.cursor || '',
            pointerEvents: cs.pointerEvents || '',
            listStyleType: cs.listStyleType || '',
        };
    }

    // Walk matching walkHiddenEl — positions relative to rootEl
    // Skips hidden elements (display:none, visibility:hidden) — we only want
    // what's VISIBLE in the selected component, not its hidden dropdown children.
    function walkEl(el, rootEl, elCounter, depth, rootRect) {
        if (depth > 25) return [];
        var tag = el.tagName ? el.tagName.toLowerCase() : '';
        var SKIP = { script: 1, noscript: 1, style: 1, head: 1, meta: 1, link: 1, title: 1, base: 1 };
        if (SKIP[tag]) return [];

        // ── Compute geometry relative to rootRect ──────────────────────────────
        // Uses getBoundingClientRect — the browser's source of truth.
        // Do NOT skip based on display:none here: when __serializeRoot force-shows
        // the root panel, its children may still compute as display:none under their
        // own CSS even though they are visually laid out. We need their geometry.
        var _elRect = el.getBoundingClientRect();
        var x = Math.round(_elRect.left - rootRect.left);
        var y = Math.round(_elRect.top - rootRect.top);
        var w = Math.round(_elRect.width);
        var h = Math.round(_elRect.height);

        var cs = readCSS(el);

        // Skip elements with no geometry AND no text/image content.
        // Do NOT skip purely on display:none — the panel root has been force-shown
        // and children may have valid geometry even if CSS still reports none.
        var hasContent = !!(
            (el.innerText || el.textContent || '').trim() ||
            (tag === 'img' && (el.src || el.getAttribute('src'))) ||
            (tag === 'svg') ||
            (cs.backgroundImage && cs.backgroundImage !== 'none')
        );
        if (w === 0 && h === 0 && !hasContent) return [];

        // Skip elements that fall entirely outside the root component bounds
        if (x + w < -rootRect.width && !hasContent) return [];

        // ── Scrollable container clip ─────────────────────────────────────────────
        // rootH was set from pre-expansion clientHeight for scrollable containers.
        // DISABLED: clipping below the visible fold drops chevrons and content
        // on accordion rows that are below the scroll position. We capture the
        // full expanded component — all rows must be included regardless of y.
        // if (rootH > 0 && y >= rootH) return [];

        // Extract text content
        var textContent = (el.innerText || el.textContent || '').trim().slice(0, 500);

        // Image src
        var imgSrc = '';
        if (tag === 'img') imgSrc = el.src || el.getAttribute('src') || '';
        if (tag === 'picture') {
            var imgEl = el.querySelector('img');
            if (imgEl) imgSrc = imgEl.src || '';
        }

        // SVG elements — exact same sprite resolution as serializeDOM lines 867-990
        if (tag === 'svg') {
            if (w > 1 && h > 1) {
                var _iconColor = cs.color || '#ffffff';
                var svgMarkup = null;

                var useEl = el.querySelector('use');
                if (useEl) {
                    var XLINK = 'http://www.w3.org/1999/xlink';
                    var href = useEl.getAttributeNS(XLINK, 'href')
                        || useEl.getAttribute('href')
                        || useEl.getAttribute('xlink:href')
                        || '';
                    if (href.includes('.svg#') || (href.startsWith('#') && href.length > 1)) {
                        var absHref = href;
                        if (href.startsWith('/') || href.startsWith('./') || href.startsWith('../')) {
                            try { absHref = new URL(href, window.location.origin).href; } catch (e) { }
                        }
                        var cached = svgSymbolCache.get(absHref) || svgSymbolCache.get(href);
                        if (cached) {
                            svgMarkup = cached
                                .replace(/fill="currentColor"/gi, 'fill="' + _iconColor + '"')
                                .replace(/stroke="currentColor"/gi, 'stroke="' + _iconColor + '"');
                        } else {
                            // Sprite not cached — placeholder
                            svgMarkup = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ' + w + ' ' + h + '"'
                                + ' width="' + w + '" height="' + h + '">'
                                + '<rect width="' + w + '" height="' + h + '" rx="2" fill="#333333" stroke="#9333EA" stroke-width="1"/>'
                                + '</svg>';
                        }
                    }
                }

                if (!svgMarkup) {
                    // Inline SVG — embed outerHTML with currentColor resolved
                    svgMarkup = el.outerHTML
                        .replace(/fill="currentColor"/gi, 'fill="' + _iconColor + '"')
                        .replace(/stroke="currentColor"/gi, 'stroke="' + _iconColor + '"');
                    if (!svgMarkup.includes('xmlns=')) {
                        svgMarkup = svgMarkup.replace('<svg ', '<svg xmlns="http://www.w3.org/2000/svg" ');
                    }
                    // Resolve CSS vars inside <style> blocks
                    var _ds2 = window.getComputedStyle(document.documentElement);
                    svgMarkup = svgMarkup.replace(/(<style[^>]*>)([\s\S]*?)(<\/style>)/gi, function (m, o, css, c) {
                        return o + css.replace(/rgb\(var\((--[^)]+)\)\)/g, function (m2, p) { var v = _ds2.getPropertyValue(p.trim()).trim(); return v ? 'rgb(' + v + ')' : m2; })
                            .replace(/var\((--[^)]+)\)/g, function (m2, p) { var v = _ds2.getPropertyValue(p.trim()).trim(); return v || m2; }) + c;
                    });
                }

                var svgDataURI = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svgMarkup);
                var svgEntry = {
                    internalId: 'hel_' + (elCounter[0]++),
                    tag: 'svg',
                    id: el.id || '',
                    className: typeof el.className === 'string' ? el.className : '',
                    isSVGIcon: true,
                    svgDataURI: svgDataURI,
                    hidden: false,
                    cssProps: { color: _iconColor, fill: cs.fill || 'currentColor' },
                    rect: { x: x, y: y, width: w, height: h },
                };
                return [svgEntry]; // never recurse into SVG children
            }
            return []; // zero-size SVG — skip
        }

        // Background image
        var bgImage = '';
        if (cs.backgroundImage && cs.backgroundImage !== 'none') bgImage = cs.backgroundImage;

        // Border radius as number
        var borderRadius = 0;
        var brMatch = cs.borderRadius && cs.borderRadius.match(/^(\d+(?:\.\d+)?)px/);
        if (brMatch) borderRadius = Math.round(parseFloat(brMatch[1]));

        var elType = resolveElementType(el, tag);

        var entry = {
            internalId: 'hel_' + (elCounter[0]++),
            tag: tag,
            type: elType,
            rect: { x: x, y: y, width: w, height: h },
            textContent: textContent,
            imgSrc: imgSrc,
            bgImage: bgImage,
            borderRadius: borderRadius,
            cssProps: cs,
            componentAttr: el.getAttribute && el.getAttribute('data-component') || '',
            ariaLabel: el.getAttribute && el.getAttribute('aria-label') || '',
            role: el.getAttribute && el.getAttribute('role') || '',
            alt: (elType === 'image' && el.alt) ? el.alt : '',
        };

        var results = [entry];

        var VOID = { img: 1, input: 1, video: 1, audio: 1, hr: 1, br: 1, meta: 1, link: 1, source: 1 };
        for (var i = 0; i < el.children.length; i++) {
            var child = el.children[i];
            var childTag = child.tagName ? child.tagName.toLowerCase() : '';
            if (!VOID[childTag]) {
                var childResults = walkEl(child, rootEl, elCounter, depth + 1, rootRect);
                for (var j = 0; j < childResults.length; j++) results.push(childResults[j]);
            }
        }

        return results;
    }

    // ── ConteX Law: force-show the root before measuring ──────────────────────
    // Hidden panels (popover=auto, display:none) return getBoundingClientRect()
    // as {0,0,0,0} when hidden. Walk the ancestor chain and temporarily unhide
    // everything — same technique as serializeHiddenComponents main loop.
    // This is the source of truth for panel geometry; it MUST be measured live.
    var _srHiddenAncestors = [];
    var _srNode = rootEl;
    while (_srNode && _srNode !== document.body) {
        var _srCs = window.getComputedStyle(_srNode);
        var _srSaved = {
            el: _srNode,
            display: _srNode.style.display,
            visibility: _srNode.style.visibility,
            height: _srNode.style.height,
            overflow: _srNode.style.overflow,
            maxHeight: _srNode.style.maxHeight,
        };
        var _srNeedsFix = false;
        if (_srCs.display === 'none') {
            _srNode.style.setProperty('display', 'block', 'important');
            _srNode.style.setProperty('visibility', 'hidden', 'important');
            _srNeedsFix = true;
        }
        if ((parseFloat(_srCs.height) === 0 || parseFloat(_srCs.maxHeight) === 0) &&
            (_srCs.overflow === 'hidden' || _srCs.overflowY === 'hidden' ||
                _srCs.overflow === 'clip' || _srCs.overflowY === 'clip')) {
            _srNode.style.height = 'auto';
            _srNode.style.maxHeight = 'none';
            _srNode.style.overflow = 'visible';
            _srNeedsFix = true;
        }
        if (_srNeedsFix) _srHiddenAncestors.push(_srSaved);
        _srNode = _srNode.parentElement;
    }
    // Also handle popover=auto — showPopover() makes it enter the top layer
    var _srUsedShowPopover = false;
    if (rootEl.hasAttribute && rootEl.hasAttribute('popover')) {
        try { rootEl.showPopover(); _srUsedShowPopover = true; } catch (e) { }
    }
    void rootEl.offsetHeight; // force layout reflow

    var rootRect = rootEl.getBoundingClientRect();

    // ConteX Law — Structure pillar: popover elements enter the browser top layer
    // when showPopover() is called. The top layer assigns them width:100vw by default,
    // so getBoundingClientRect().width returns the full viewport width regardless of
    // the element's own CSS (e.g. w-max, grid layout, fixed panel width).
    // scrollWidth reads the element's intrinsic content width — the real visual width.
    // For popover elements, scrollWidth is the source of truth for width.
    // For non-popover elements, getBoundingClientRect().width is correct.
    var _isPopover = rootEl.hasAttribute && rootEl.hasAttribute('popover');
    var rootW;
    if (_isPopover) {
        // Use scrollWidth — intrinsic content width, not the top-layer 100vw default
        rootW = rootEl.scrollWidth || Math.round(rootRect.width) || window.innerWidth;
    } else {
        rootW = Math.round(rootRect.width) || rootEl.scrollWidth || window.innerWidth;
    }

    var rootH = Math.min(
        Math.round(rootRect.height) || rootEl.clientHeight || 0,
        window.innerHeight
    );

    // Hard fallback: if still 0 height, force root open and re-measure
    var _srForcedOpen = false;
    if (rootH === 0) {
        rootEl.style.height = 'auto';
        rootEl.style.maxHeight = 'none';
        rootEl.style.overflow = 'visible';
        rootEl.style.setProperty('display', 'block', 'important');
        rootEl.style.setProperty('visibility', 'hidden', 'important');
        void rootEl.offsetHeight;
        var _srRect2 = rootEl.getBoundingClientRect();
        rootH = Math.min(Math.round(_srRect2.height) || rootEl.clientHeight || 0, window.innerHeight);
        rootRect = _srRect2;
        _srForcedOpen = true;
    }

    var rootTag = rootEl.tagName ? rootEl.tagName.toLowerCase() : 'div';

    var elements = walkEl(rootEl, rootEl, elCounter, 0, rootRect);

    // ── Restore all temporarily unhidden ancestors ────────────────────────────
    if (_srUsedShowPopover) {
        try { rootEl.hidePopover(); } catch (e) { }
    }
    if (_srForcedOpen) {
        rootEl.style.height = '';
        rootEl.style.maxHeight = '';
        rootEl.style.overflow = '';
        rootEl.style.removeProperty('display');
        rootEl.style.removeProperty('visibility');
    }
    for (var _ri = _srHiddenAncestors.length - 1; _ri >= 0; _ri--) {
        var _rs = _srHiddenAncestors[_ri];
        _rs.el.style.display = _rs.display;
        _rs.el.style.visibility = _rs.visibility;
        _rs.el.style.height = _rs.height;
        _rs.el.style.overflow = _rs.overflow;
        _rs.el.style.maxHeight = _rs.maxHeight;
    }

    // Filter out zero-size, no-content elements — same filter as serializeHiddenComponents
    elements = elements.filter(function (e) {
        return e.textContent || e.imgSrc || e.svgDataURI || e.bgImage ||
            (e.rect.width > 0 && e.rect.height > 0);
    });

    return {
        componentId: 'hc_' + (compCounter[0]++),
        componentType: resolveComponentType(rootEl),
        label: resolveLabel(rootEl),
        tag: rootTag,
        width: rootW,
        height: rootH,
        elements: elements,
    };
};

// =============================================================================
// DOM SERIALIZER
// Captures ALL elements — separates visible from invisible.
// Visible   = has size AND is not display:none/visibility:hidden/opacity:0
// Invisible = display:none OR visibility:hidden OR opacity:0 OR zero size
// =============================================================================
function serializeDOM(svgSymbolCache = new Map(), rootEl = null) {
    const MAX_DEPTH = 30;
    const visible = [];
    const invisible = [];
    let counter = 0;

    // SVG deduplication: track captured SVG data URIs to prevent same icon
    // appearing twice when the DOM has duplicate SVG nodes (e.g. logo in both
    // sticky header AND mobile nav, or spinner siblings at same position).
    // Key: svgDataURI string — if two SVGs produce identical markup they are the same icon.
    const _svgMarkupSeen = new Set();

    // Snapshot scroll position ONCE — all rects are relative to this
    const scrollX = window.scrollX || 0;
    const scrollY = window.scrollY || 0;
    console.log('[SnapStak] Serializing at scrollY=' + scrollY);

    // Track all text strings already captured — prevents duplicates across the tree

    const TEXT_TAGS = new Set([
        'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
        'p', 'span', 'strong', 'em', 'b', 'i', 'u',
        'label', 'legend', 'dt', 'dd',
        'th', 'td', 'caption',
        'blockquote', 'code', 'pre', 'time',
    ]);

    const VOID_TAGS = new Set([
        'input', 'img', 'video', 'audio', 'hr', 'br',
        'meta', 'link', 'source', 'track', 'wbr', 'embed',
    ]);

    // Tags we never want to capture at all
    const SKIP_TAGS = new Set([
        'script', 'noscript', 'style', 'head', 'html',
        'meta', 'link', 'title', 'base',
    ]);

    function getDirectText(el) {
        let t = '';
        for (const n of el.childNodes) {
            if (n.nodeType === Node.TEXT_NODE) t += n.textContent;
        }
        return t.trim().slice(0, 300);
    }

    function serialize(el, depth, parentId) {
        if (depth > MAX_DEPTH) return;
        // SVGSVGElement is NOT an HTMLElement — it inherits from SVGElement.
        // Without this check, every <svg> element is silently skipped before
        // the tag === 'svg' handler at line 656 ever gets a chance to run.
        // Allow HTMLElement (all normal tags) AND SVGSVGElement (<svg> tags).
        if (!(el instanceof HTMLElement) && !(el instanceof SVGSVGElement)) return;

        const tag = el.tagName.toLowerCase();
        if (SKIP_TAGS.has(tag)) return;

        // Skip spinners/loaders — animated elements that are loading state UI, not content.
        // Detect by: infinite CSS animation OR classname matching spinner/loader patterns.
        // Check both the element itself AND its parent (spinner wrapper divs often hold the svg).
        const _isSpinner = (() => {
            const _cls = (typeof el.className === 'string' ? el.className : '') +
                (el.parentElement && typeof el.parentElement.className === 'string' ? ' ' + el.parentElement.className : '');
            if (/spinner|loading|loader|skeleton|spin/i.test(_cls)) return true;
            const _cs = window.getComputedStyle(el);
            if (_cs.animationName && _cs.animationName !== 'none' &&
                _cs.animationIterationCount === 'infinite') return true;
            // Also check parent element animation
            if (el.parentElement) {
                const _pcs = window.getComputedStyle(el.parentElement);
                if (_pcs.animationName && _pcs.animationName !== 'none' &&
                    _pcs.animationIterationCount === 'infinite') return true;
            }
            return false;
        })();
        if (_isSpinner) return;

        // SVG elements — resolve sprite references and embed as data URI
        if (tag === 'svg') {
            const svgRect = el.getBoundingClientRect();
            const cs0 = window.getComputedStyle(el);

            // ── Skip hidden SVG elements ──────────────────────────────────────
            // The isHidden gate below only runs for non-svg tags. SVG icons that
            // are display:none, inside a hidden nav drawer, or off-screen at x=0
            // with no valid page position must be filtered here before capture.
            if (cs0.display === 'none' || cs0.visibility === 'hidden') return;
            // Skip if any ancestor is display:none (collapsed mobile nav, hidden drawer)
            {
                let _svgAnc = el.parentElement;
                let _svgAncDepth = 0;
                while (_svgAnc && _svgAnc !== document.body && _svgAncDepth < 10) {
                    const _svgAncCs = window.getComputedStyle(_svgAnc);
                    if (_svgAncCs.display === 'none' || _svgAncCs.visibility === 'hidden') return;
                    _svgAnc = _svgAnc.parentElement;
                    _svgAncDepth++;
                }
            }
            // getBoundingClientRect returns 0 for SVG icons inside collapsed accordion rows
            // even after expansion, because the parent container hasn't reflowed yet.
            // Fall back to the width/height attributes on the SVG element itself — these are
            // the authored dimensions and are always correct for sprite icons.
            const _svgW = svgRect.width > 1 ? svgRect.width
                : parseFloat(el.getAttribute('width')) || parseFloat(cs0.width) || 0;
            const _svgH = svgRect.height > 1 ? svgRect.height
                : parseFloat(el.getAttribute('height')) || parseFloat(cs0.height) || 0;
            if (_svgW > 1 && _svgH > 1 && cs0.display !== 'none') {
                // Deduplicate: skip if identical SVG markup was already captured.
                // Autosport has the logo in both the sticky header and the mobile nav —
                // both produce identical outerHTML so markup dedup catches it cleanly.
                // We check AFTER building svgMarkup below, before pushing to visible[].
                let svgMarkup = null;

                // Color source truth via canvas pixel sampling.
                // Instead of guessing which CSS property holds the icon color,
                // we ask the browser: what color did you ACTUALLY paint here?
                // Color: walk UP from the SVG's PARENT (not the SVG itself).
                // SVG elements have a UA-stylesheet color:black that overrides inheritance.
                // The actual white/grey color is on the containing button/anchor/div.
                // Skip black and transparent — first non-black ancestor color is the icon color.
                const _iconColor = (() => {
                    // 1. Check fill on the SVG element itself — getComputedStyle is the source of truth
                    const _svgCs = window.getComputedStyle(el);
                    const _svgFill = _svgCs.fill || '';
                    if (_svgFill && _svgFill !== 'rgb(0, 0, 0)' && _svgFill !== 'rgba(0, 0, 0, 0)'
                        && _svgFill !== 'none' && !_svgFill.startsWith('url(')) {
                        return _svgFill;
                    }
                    const _svgColor = _svgCs.color || '';
                    if (_svgColor && _svgColor !== 'rgb(0, 0, 0)' && _svgColor !== 'rgba(0, 0, 0, 0)') {
                        return _svgColor;
                    }
                    // 2. Walk ancestors — first non-black color wins
                    let _n = el.parentElement;
                    while (_n) {
                        const _ncs = window.getComputedStyle(_n);
                        const _nFill = _ncs.fill || '';
                        if (_nFill && _nFill !== 'rgb(0, 0, 0)' && _nFill !== 'rgba(0, 0, 0, 0)'
                            && _nFill !== 'none' && !_nFill.startsWith('url(')) {
                            return _nFill;
                        }
                        const _c = _ncs.color || '';
                        if (_c && _c !== 'rgb(0, 0, 0)' && _c !== 'rgba(0, 0, 0, 0)') return _c;
                        _n = _n.parentElement;
                    }
                    return 'rgb(240, 240, 240)'; // fallback white
                })();

                // Pattern: <use xlink:href="/path/sprite.svg#symbol-id">
                const useEl = el.querySelector('use');
                if (useEl) {
                    // Modern Chrome (90+) deprecated xlink:href — getAttribute('xlink:href')
                    // returns null even when the attribute exists in the markup.
                    // Must use getAttributeNS with the XLink namespace to read it correctly.
                    const XLINK = 'http://www.w3.org/1999/xlink';
                    const href = useEl.getAttributeNS(XLINK, 'href')
                        || useEl.getAttribute('href')
                        || useEl.getAttribute('xlink:href')
                        || '';
                    if (href.includes('.svg#') || (href.startsWith('#') && href.length > 1)) {
                        // Normalise to absolute URL — the prefetch cache keys are absolute,
                        // but the href in markup may be root-relative (/path/sprite.svg#id).
                        // Try absolute form first, fall back to raw href.
                        let absHref = href;
                        if (href.startsWith('/') || href.startsWith('./') || href.startsWith('../')) {
                            try { absHref = new URL(href, window.location.origin).href; } catch (e) { }
                        }
                        const cached = svgSymbolCache.get(absHref) || svgSymbolCache.get(href);
                        if (cached) {
                            // If the cached symbol has explicit fills it's a multi-colour logo —
                            // use it as-is. Only replace currentColor for line-art icons.
                            const _cachedHasExplicitFills = /\bfill\s*=\s*["'](?!none|currentColor)[^"']/i.test(cached);
                            if (_cachedHasExplicitFills) {
                                svgMarkup = cached;
                            } else {
                                const computedColor = _iconColor;
                                svgMarkup = cached
                                    .replace(/fill="currentColor"/gi, `fill="${computedColor}"`)
                                    .replace(/stroke="currentColor"/gi, `stroke="${computedColor}"`)
                                    .replace(/style="color:inherit;fill:currentColor;"/,
                                        `style="color:${computedColor};"`);
                            }
                            console.log('[SnapStak] Resolved sprite:', href, '| color:', _iconColor);
                        } else {
                            // Symbol not in cache — placeholder with exact browser-measured dimensions.
                            const w = Math.round(_svgW);
                            const h = Math.round(_svgH);
                            const _fs = Math.max(6, Math.round(Math.min(w, h) * 0.35));
                            svgMarkup = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}"`
                                + ` width="${w}" height="${h}">`
                                + `<rect width="${w}" height="${h}" rx="2" fill="#333333" stroke="#9333EA" stroke-width="1"/>`
                                + `<text x="${w / 2}" y="${h / 2}" font-family="sans-serif" font-size="${_fs}" fill="#9333EA"`
                                + ` text-anchor="middle" dominant-baseline="central">Sprite</text>`
                                + `</svg>`;
                            console.warn('[SnapStak] Sprite not cached:', href, '| absHref:', absHref, '| cache size:', svgSymbolCache.size, '| cache keys:', JSON.stringify([...svgSymbolCache.keys()].slice(0, 20)));
                        }
                    }
                }

                // Fallback: no <use>, embed outerHTML directly (works for inline SVGs like logos).
                // Replace only explicit currentColor — never set fill on root element.
                if (!svgMarkup) {
                    const computedColor = _iconColor;
                    // Strip shadow/blur decoration groups — they are drop-shadow artefacts
                    // that render as near-black smears when extracted without a backdrop.
                    // Identified by: opacity < 0.5 on the group AND all child paths also have opacity < 0.5
                    let _rawHTML = el.outerHTML;
                    _rawHTML = _rawHTML.replace(/<g[^>]*\sopacity="0\.\d+"[^>]*>[\s\S]*?<\/g>/g, (match) => {
                        // Only strip if ALL paths inside also have low opacity (pure shadow group)
                        const _pathOpacities = [...match.matchAll(/\sopacity="([\d.]+)"/g)].map(m => parseFloat(m[1]));
                        const _allLow = _pathOpacities.length > 0 && _pathOpacities.every(o => o <= 0.5);
                        return _allLow ? '' : match;
                    });
                    svgMarkup = _rawHTML
                        .replace(/fill="currentColor"/gi, `fill="${computedColor}"`)
                        .replace(/stroke="currentColor"/gi, `stroke="${computedColor}"`)
                        .replace(/rgb\(var\((--[^)]+)\)\)/g, (match, varName) => {
                            const val = window.getComputedStyle(document.documentElement)
                                .getPropertyValue(varName.trim()).trim();
                            return val ? `rgb(${val})` : match;
                        })
                        .replace(/var\((--[^)]+)\)/g, (match, varName) => {
                            const val = window.getComputedStyle(document.documentElement)
                                .getPropertyValue(varName.trim()).trim();
                            return val || match;
                        });
                    // Ensure xmlns is present — required for SVG data URIs to render in <image> tags
                    if (!svgMarkup.includes('xmlns=')) {
                        svgMarkup = svgMarkup.replace('<svg ', '<svg xmlns="http://www.w3.org/2000/svg" ');
                    }
                    // Resolve CSS vars inside <style> blocks
                    const _ds3 = window.getComputedStyle(document.documentElement);
                    svgMarkup = svgMarkup.replace(/(<style[^>]*>)([\s\S]*?)(<\/style>)/gi, function (m, o, css, c) {
                        return o + css.replace(/rgb\(var\((--[^)]+)\)\)/g, function (m2, p) { var v = _ds3.getPropertyValue(p.trim()).trim(); return v ? 'rgb(' + v + ')' : m2; })
                            .replace(/var\((--[^)]+)\)/g, function (m2, p) { var v = _ds3.getPropertyValue(p.trim()).trim(); return v || m2; }) + c;
                    });
                }

                const svgDataURI = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svgMarkup);

                if (el.id || el.getAttribute('aria-label')) {
                    console.log('[SnapStak SVG]', el.id || el.getAttribute('aria-label'),
                        '| markup len:', svgMarkup.length, '| w:', Math.round(_svgW), 'h:', Math.round(_svgH));
                }

                // Dedup: skip only if identical markup AND identical position.
                // Same icon used in multiple rows (e.g. accordion chevrons) MUST all be captured.
                // Only skip true duplicates — same SVG rendered at the exact same screen position.
                const _svgDedupKey = svgMarkup + '|' + Math.round(svgRect.left) + ',' + Math.round(svgRect.top);
                if (_svgMarkupSeen.has(_svgDedupKey)) return;
                _svgMarkupSeen.add(_svgDedupKey);

                visible.push({
                    internalId: 'el_' + (counter++),
                    tag: 'svg',
                    id: el.id || '',
                    className: typeof el.className === 'string' ? el.className : '',
                    isSVGIcon: true,
                    svgDataURI: svgDataURI,
                    hidden: false,
                    parentId: parentId,
                    cssProps: { color: cs0.color || '#ffffff', fill: cs0.fill || 'currentColor' },
                    rect: (() => {
                        // SVG icons inside carousel items: use the stamped absolute coords
                        // if present. Without this the icon reads svgRect at scroll=0
                        // and lands at x=0,y=0 for all off-screen carousel items.
                        const _svgStamp = el.getAttribute('data-snapstak-carousel-rect');
                        if (_svgStamp) {
                            try {
                                const s = JSON.parse(_svgStamp);
                                return { x: s.x, y: s.y, width: s.w, height: s.h };
                            } catch (_) { }
                        }
                        return {
                            x: Math.round(svgRect.left + (window.scrollX || 0)),
                            y: (cs0.position === 'fixed' || cs0.position === 'sticky')
                                ? Math.round(svgRect.top)
                                : Math.round(svgRect.top + (window.scrollY || 0)),
                            width: Math.round(_svgW),
                            height: Math.round(_svgH),
                        };
                    })(),
                });
            }
            return; // never recurse into SVG children
        }

        const cs = window.getComputedStyle(el);
        const _rawRect = el.getBoundingClientRect();

        // ── Carousel stamp override ───────────────────────────────────────────
        // expandHorizontalCarousels() stamps absolute page coords onto off-screen
        // carousel items before serializeDOM runs. If the stamp is present, use
        // those coords — the item was scrolled into view for accurate measurement.
        const _carouselStamp = el.getAttribute('data-snapstak-carousel-rect');
        const _stampedRect = _carouselStamp ? (() => {
            try {
                const s = JSON.parse(_carouselStamp);
                return {
                    left: s.x, top: s.y - (window.scrollY || 0), right: s.x + s.w,
                    bottom: s.y - (window.scrollY || 0) + s.h,
                    width: s.w, height: s.h, x: s.x, y: s.y
                };
            } catch (_) { return null; }
        })() : null;

        // getBoundingClientRect().height returns 0 for flex children with h-full when
        // the parent height comes from the flex container, not from content.
        // offsetHeight correctly resolves h-full — use it as fallback.
        const rect = _stampedRect ||
            ((_rawRect.height < 1 && _rawRect.width > 1 && el.offsetHeight > 0)
                ? {
                    left: _rawRect.left, top: _rawRect.top, right: _rawRect.right, bottom: _rawRect.bottom,
                    width: _rawRect.width, height: el.offsetHeight, x: _rawRect.x, y: _rawRect.y
                }
                : _rawRect);

        // Visible = has real pixel dimensions on screen
        // Invisible = zero size OR explicitly removed from layout (display:none)
        // Note: visibility:hidden and opacity:0 elements still occupy space — treat as visible
        const _semanticTags = new Set(['h1', 'h2', 'h3', 'h4', 'h5', 'h6',
            'p', 'span', 'strong', 'em', 'b', 'i', 'u', 'label', 'legend',
            'dt', 'dd', 'blockquote', 'figcaption']);
        const _tag = el.tagName ? el.tagName.toLowerCase() : '';
        const _hasZeroRect = rect.width < 1 && rect.height < 1;
        const _isAncestorHidden = _hasZeroRect && el.offsetParent === null
            && _tag !== 'body' && _tag !== 'html';
        // input/select/textarea are replaced elements — 0x0 rect even when visible.
        // Preserve them if not display:none so toggle switches are captured.
        const _isFormInput = _tag === 'input' || _tag === 'select' || _tag === 'textarea';

        // ── Thumb overlay check ───────────────────────────────────────────────
        // .ms-item__thumb-info-top and .ms-item__thumb-series are position:absolute
        // overlays rendered on top of article images via CSS absolute positioning.
        // SVG has no absolute positioning — they fall outside their container and
        // render as floating text above images. The HTML confirms this structure:
        // both elements sit directly inside .ms-item__thumb with position:absolute.
        // Skip any element whose computed position is absolute AND whose direct
        // parent contains the class ms-item__thumb — that combination is always
        // an image overlay with no valid SVG representation.
        let _isThumbAbsOverlay = false;
        if (cs.position === 'absolute') {
            const _p = el.parentElement;
            if (_p && typeof _p.className === 'string' && _p.className.includes('ms-item__thumb')) {
                _isThumbAbsOverlay = true;
            }
        }

        const isHidden =
            cs.display === 'none' ||
            _isAncestorHidden ||
            _isThumbAbsOverlay ||
            (_hasZeroRect && !_semanticTags.has(_tag) && !_isFormInput);

        let borderRadiusPx = 0;
        const brStr = cs.borderRadius || cs.borderTopLeftRadius || '';
        const brMatch = brStr.match(/([\d.]+)(%|px)/);
        if (brMatch) {
            borderRadiusPx = brMatch[2] === '%'
                ? (parseFloat(brMatch[1]) / 100) * Math.min(rect.width, rect.height)
                : parseFloat(brMatch[1]);
        }

        const internalId = 'el_' + (counter++);

        // Capture CSS visual properties in the same pass — no second round trip needed
        // cssProps — only non-default values stored. Browser defaults carry zero
        // signal and are stripped here to keep the payload lean.
        // borderTop/TopColor/TopWidth removed — border shorthand is sufficient.
        const cssProps = {};
        if (cs.backgroundColor) cssProps.backgroundColor = cs.backgroundColor;
        if (cs.backgroundImage && cs.backgroundImage !== 'none') cssProps.backgroundImage = cs.backgroundImage;
        if (cs.border) cssProps.border = cs.border;
        if (cs.borderRadius && cs.borderRadius !== '0px') cssProps.borderRadius = cs.borderRadius;
        if (cs.boxShadow && cs.boxShadow !== 'none') cssProps.boxShadow = cs.boxShadow;
        if (cs.color) cssProps.color = cs.color;
        if (cs.fontFamily) cssProps.fontFamily = cs.fontFamily;
        if (cs.fontSize) cssProps.fontSize = cs.fontSize;
        if (cs.fontWeight) cssProps.fontWeight = cs.fontWeight;
        if (cs.fontStyle && cs.fontStyle !== 'normal') cssProps.fontStyle = cs.fontStyle;
        if (cs.lineHeight) cssProps.lineHeight = cs.lineHeight;
        if (cs.letterSpacing && cs.letterSpacing !== 'normal') cssProps.letterSpacing = cs.letterSpacing;
        // Placeholder — overwritten after _ownsLine is computed below.
        cssProps.textAlign = cs.textAlign || '';
        if (cs.textTransform && cs.textTransform !== 'none') cssProps.textTransform = cs.textTransform;
        if (cs.textDecoration && cs.textDecoration !== 'none') cssProps.textDecoration = cs.textDecoration;
        if (cs.whiteSpace && cs.whiteSpace !== 'normal') cssProps.whiteSpace = cs.whiteSpace;
        if (cs.display) cssProps.display = cs.display;
        if (cs.alignItems && cs.alignItems !== 'normal'
            && cs.alignItems !== 'stretch') cssProps.alignItems = cs.alignItems;
        if (cs.justifyContent && cs.justifyContent !== 'normal') cssProps.justifyContent = cs.justifyContent;
        if (cs.flexDirection && cs.flexDirection !== 'row') cssProps.flexDirection = cs.flexDirection;
        if (cs.gap && cs.gap !== 'normal' && cs.gap !== '0px') cssProps.gap = cs.gap;
        if (cs.padding && cs.padding !== '0px') cssProps.padding = cs.padding;
        if (cs.paddingTop && cs.paddingTop !== '0px') cssProps.paddingTop = cs.paddingTop;
        if (cs.paddingBottom && cs.paddingBottom !== '0px') cssProps.paddingBottom = cs.paddingBottom;
        cssProps.paddingLeft = cs.paddingLeft || '';                     // always kept — used for text indent
        if (cs.paddingRight && cs.paddingRight !== '0px') cssProps.paddingRight = cs.paddingRight;
        if (cs.opacity && cs.opacity !== '1') cssProps.opacity = cs.opacity;
        if (cs.position && cs.position !== 'static') cssProps.position = cs.position;
        if (cs.zIndex && cs.zIndex !== 'auto') cssProps.zIndex = cs.zIndex;
        if (cs.overflow && cs.overflow !== 'visible') cssProps.overflow = cs.overflow;
        if (cs.objectFit && cs.objectFit !== 'fill') cssProps.objectFit = cs.objectFit;
        if (cs.alignSelf && cs.alignSelf !== 'auto') cssProps.alignSelf = cs.alignSelf;
        if (cs.flexGrow && cs.flexGrow !== '0') cssProps.flexGrow = cs.flexGrow;
        if (cs.flexShrink && cs.flexShrink !== '1') cssProps.flexShrink = cs.flexShrink;
        // Margin — only kept for margin:auto centering detection
        if (cs.marginLeft && cs.marginLeft !== '0px') cssProps.marginLeft = cs.marginLeft;
        if (cs.marginRight && cs.marginRight !== '0px') cssProps.marginRight = cs.marginRight;
        // CSS Anchor Positioning — captured in cssProps as well as the dedicated entry fields
        // so the server's SVG serializer can write them as data-* attributes on the SVG group.
        const _anchorName = cs.getPropertyValue('anchor-name').trim();
        const _positionAnchor = cs.getPropertyValue('position-anchor').trim();
        if (_anchorName && _anchorName !== 'none') cssProps.anchorName = _anchorName;
        if (_positionAnchor && _positionAnchor !== 'none') cssProps.positionAnchor = _positionAnchor;

        // Owns the full inline line — used for textContent capture AND recursion guard.
        const _ownsLine = (() => {
            if (tag === 'h1' || tag === 'h2' || tag === 'h3' || tag === 'h4' || tag === 'h5' || tag === 'h6'
                || tag === 'p' || tag === 'div' || tag === 'li') {
                // Block/heading elements always own their line — textAlign is intentional
                if (el.children.length === 0) return true;
            }
            if (el.children.length === 0) return false;
            if (tag === 'td' || tag === 'th' || tag === 'tr' || tag === 'table'
                || tag === 'tbody' || tag === 'thead' || tag.indexOf('-') !== -1) return false;
            for (let _i = 0; _i < el.children.length; _i++) {
                const _cd = window.getComputedStyle(el.children[_i]).display;
                const _ct = (el.children[_i].tagName || '').toLowerCase();
                // SVG children must always be recursed into — never treat parent as line owner
                if (_ct === 'svg') return false;
                if (_ct.indexOf('-') !== -1 || (_cd !== 'inline' && _cd !== 'inline-block' && _cd !== 'none'))
                    return false;
            }
            return true;
        })();

        // Now that _ownsLine is known, compute the correct textAlign.
        // _ownsLine elements are layout owners — their textAlign is intentional.
        // Other elements: only use textAlign if it differs from parent (prevents deep cascade).
        cssProps.textAlign = (() => {
            const _ta = cs.textAlign || '';
            if (!_ta || _ta === 'start') return '';
            if (el.style && el.style.textAlign) return _ta;
            if (_ownsLine) return _ta;
            if (!el.parentElement) return _ta;
            const _parentTa = window.getComputedStyle(el.parentElement).textAlign || '';
            return _ta !== _parentTa ? _ta : '';
        })();

        const entry = {
            internalId,
            parentId: parentId || null,
            tag,
            id: el.id || '',
            className: typeof el.className === 'string' ? el.className : '',
            role: el.getAttribute('role') || '',
            ariaLabel: el.getAttribute('aria-label') || '',
            // Extended ARIA — interactive state attributes the AI needs for accessible markup
            ariaExpanded: el.getAttribute('aria-expanded') || '',
            ariaHaspopup: el.getAttribute('aria-haspopup') || '',
            ariaControls: el.getAttribute('aria-controls') || '',
            ariaCurrent: el.getAttribute('aria-current') || '',
            ariaSelected: el.getAttribute('aria-selected') || '',
            // Form / button semantics
            inputType: el.type || '',
            inputName: el.name || '',
            placeholder: el.placeholder || '',
            autocomplete: el.getAttribute('autocomplete') || '',
            checked: el.checked !== undefined ? el.checked : false,
            multiple: el.multiple !== undefined ? el.multiple : false,
            disabled: el.disabled || false,
            readonly: el.readOnly || false,
            required: el.required || false,
            // Link semantics
            target: el.getAttribute('target') || '',
            rel: el.getAttribute('rel') || '',
            // Site-specific data attributes — drive JS behaviour (e.g. data-button, data-command)
            dataAttributes: (() => {
                const _da = {};
                if (el.dataset) {
                    for (const [k, v] of Object.entries(el.dataset)) {
                        // Skip SnapStak internal attributes
                        if (k === 'segmentId' || k === 'responsive' || k === 'snapstkH') continue;
                        if (v !== undefined && v !== '') _da[k] = v;
                    }
                }
                return Object.keys(_da).length ? _da : null;
            })(),
            // ── ConteX Law — Structure Pillar: Browser-native declarative API attributes ──
            // These are standard HTML attributes that are NOT in el.dataset — they drive
            // browser-native behaviour (Popover API, Invoker Commands, Interest API) without
            // any JavaScript. They must be captured here on the element that owns them so the
            // code generator can emit the correct declarative HTML rather than JS polyfills.
            // CSS Anchor Positioning (anchor-name, position-anchor) is read via getComputedStyle
            // because it may be set via stylesheet rules OR inline style — both resolved here.
            popoverAttr: el.getAttribute('popover') || '',
            popoverTarget: el.getAttribute('popovertarget') || '',
            popoverAction: el.getAttribute('popoveraction') || '',
            commandFor: el.getAttribute('commandfor') || '',
            command: el.getAttribute('command') || '',
            interestFor: el.getAttribute('interestfor') || '',
            anchorName: window.getComputedStyle(el).getPropertyValue('anchor-name').trim() || '',
            positionAnchor: window.getComputedStyle(el).getPropertyValue('position-anchor').trim() || '',
            isTextNode: TEXT_TAGS.has(tag),
            // Capture direct text for ALL elements — use getDirectText() to avoid
            // duplicating text that belongs to child elements.
            textContent: (() => {
                // <select>: use selected option text only — innerText returns all options concatenated.
                if (tag === 'select' && el.options && el.selectedIndex >= 0) {
                    return el.options[el.selectedIndex].text.trim().slice(0, 500);
                }
                const ownText = (el.innerText || el.textContent || '').trim();
                if (!ownText) return '';

                // Only capture text on the DEEPEST owner — never duplicate parent text
                // Rule: if ALL of this element's text is already owned by a single child,
                // skip it here (the child will render it).
                if (el.children.length > 0) {
                    // If ALL children are inline (span, a, em etc.) this element owns the full line.
                    // Check this FIRST — before the single-child-owns-all check, which would
                    // incorrectly skip <li><a>text</a></li> and then _ownsLine stops recursion too.
                    const _notTable = tag !== 'td' && tag !== 'th' && tag !== 'tr' && tag !== 'table';
                    const _notCustom = tag.indexOf('-') === -1;
                    if (_notTable && _notCustom) {
                        let _allInline = el.children.length > 0;
                        for (let _ci = 0; _ci < el.children.length; _ci++) {
                            const _cd = window.getComputedStyle(el.children[_ci]).display;
                            const _ct = (el.children[_ci].tagName || '').toLowerCase();
                            if (_ct.indexOf('-') !== -1 || (_cd !== 'inline' && _cd !== 'inline-block' && _cd !== 'none')) {
                                _allInline = false; break;
                            }
                        }
                        if (_allInline) {
                            return ownText.slice(0, 500);
                        }
                    }
                    // Check if any single child contains ALL of this element's text
                    // (only reached when children are NOT all inline)
                    for (const child of el.children) {
                        const childText = (child.innerText || child.textContent || '').trim();
                        if (childText && childText === ownText) {
                            return ''; // child owns all this text — skip parent
                        }
                    }
                    // Only capture direct text nodes (text not inside any child element)
                    const dt = getDirectText(el);
                    if (!dt) return '';
                    return dt;
                }
                // No children - this element fully owns its text.
                return ownText.slice(0, 500);
            })(),
            src: el.currentSrc || el.src || el.getAttribute('src') || '',
            srcset: el.getAttribute('srcset') || el.getAttribute('data-srcset') || '',
            sizes: el.getAttribute('sizes') || el.getAttribute('data-sizes') || '',
            dataSrc: el.getAttribute('data-src') || el.getAttribute('data-lazy-src') || '',
            alt: el.alt || el.getAttribute('alt') || '',
            href: el.href || el.getAttribute('href') || '',
            // Capture inline children colors for mixed-color text rendering (e.g. red "Motorsport Network")
            // Only set when _ownsLine is true AND children have different colors.
            inlineChildren: (() => {
                if (!_ownsLine) return null;
                const kids = [];
                // Walk childNodes in DOM order — text nodes and element nodes exactly as the browser sees them.
                // Preserve whitespace from actual whitespace text nodes between elements.
                for (let _ni = 0; _ni < el.childNodes.length; _ni++) {
                    const _nd = el.childNodes[_ni];
                    if (_nd.nodeType === 3) { // TEXT_NODE — includes raw whitespace between elements
                        const _nt = _nd.textContent || '';
                        if (_nt.trim()) kids.push({ text: _nt, color: cs.color || '' });
                    } else if (_nd.nodeType === 1) { // ELEMENT_NODE
                        const _kt = (_nd.innerText || _nd.textContent || '');
                        if (_kt.trim()) kids.push({ text: _kt, color: window.getComputedStyle(_nd).color || '' });
                    }
                }
                const colors = [...new Set(kids.map(k => k.color))];
                return colors.length > 1 ? kids : null;
            })(),
            isResponsive: !!(el.getAttribute('srcset') || el.getAttribute('data-srcset')),

            // textNodeOffsetX: x offset of the direct text node within this element.
            // When a flex container has element children BEFORE a raw text node
            // (e.g. <a class="flex gap-2.5"><div class="w-48">logo</div> Formula 1</a>),
            // getBoundingClientRect() on the <a> gives x=0 (left edge of logo).
            // But the text "Formula 1" starts AFTER the logo div + gap.
            // Use Range.getBoundingClientRect() on the actual text node to get its
            // true x position — this IS the CSS source of truth, direct from the browser.
            textNodeOffsetX: (() => {
                // Only relevant when element has BOTH element children AND a direct text node
                if (el.children.length === 0) return 0;
                for (let _ni = 0; _ni < el.childNodes.length; _ni++) {
                    const _nd = el.childNodes[_ni];
                    if (_nd.nodeType === Node.TEXT_NODE && _nd.textContent.trim()) {
                        try {
                            const _range = document.createRange();
                            _range.selectNode(_nd);
                            const _tr = _range.getBoundingClientRect();
                            const _er = el.getBoundingClientRect();
                            // Offset of text node relative to element's left edge
                            const _offset = Math.round(_tr.left - _er.left);
                            return _offset > 0 ? _offset : 0;
                        } catch (_) { }
                    }
                }
                return 0;
            })(),

            // For <picture> elements — capture all <source> children
            pictureSources: tag === 'picture'
                ? Array.from(el.querySelectorAll('source')).map(s => ({
                    srcset: s.getAttribute('srcset') || '',
                    sizes: s.getAttribute('sizes') || '',
                    media: s.getAttribute('media') || '',
                    type: s.getAttribute('type') || '',
                }))
                : [],
            borderRadiusPx,
            hidden: isHidden,
            segmentId: el.dataset.segmentId || '',
            responsive: el.dataset.responsive === 'true',
            cssProps,
            parentCssProps: el.parentElement ? (() => {
                const _pcs = window.getComputedStyle(el.parentElement);
                return {
                    display: _pcs.display || '',
                    flexDirection: _pcs.flexDirection || '',
                    alignItems: _pcs.alignItems || '',
                    justifyContent: _pcs.justifyContent || '',
                    textAlign: _pcs.textAlign || '',
                    paddingLeft: _pcs.paddingLeft || '',
                    paddingRight: _pcs.paddingRight || '',
                    width: _pcs.width || '',
                    backgroundColor: _pcs.backgroundColor || '',
                };
            })() : null,
            // Walk UP the DOM to find the layout container that defines text column width.
            // PRIMARY: nearest ancestor with 'flex' (not 'inline-flex') in className
            //   AND no direct text node children — covers 90% of modern responsive layouts.
            // FALLBACK: nearest ancestor that is meaningfully wider than this element —
            //   handles non-flex containers: <ul>, <nav>, <address> parents, etc.
            parentRect: (() => {
                const _captureRect = (_el) => {
                    const _ar = _el.getBoundingClientRect();
                    if (_ar.width < 10) return null;
                    const _cs2 = window.getComputedStyle(_el);
                    const _pl = parseFloat(_cs2.paddingLeft) || 0;
                    const _pr2 = parseFloat(_cs2.paddingRight) || 0;
                    const _pt = parseFloat(_cs2.paddingTop) || 0;
                    const _pb = parseFloat(_cs2.paddingBottom) || 0;
                    return {
                        x: Math.round(_ar.left + scrollX),
                        y: Math.round(_ar.top + scrollY),
                        width: Math.round(_ar.width),
                        height: Math.round(_ar.height),
                        innerWidth: Math.round(_ar.width - _pl - _pr2),
                        innerHeight: Math.round(_ar.height - _pt - _pb),
                        paddingLeft: Math.round(_pl),
                        paddingTop: Math.round(_pt),
                    };
                };
                let _anc = el.parentElement;
                let _fallback = null;
                while (_anc && _anc !== document.body) {
                    const _tag = (_anc.tagName || '').toLowerCase();
                    const _isContainer = _tag === 'div' || _tag === 'section' || _tag === 'article'
                        || _tag === 'ul' || _tag === 'li' || _tag === 'nav'
                        || _tag === 'footer' || _tag === 'main' || _tag === 'header';
                    if (_isContainer) {
                        const _ar = _anc.getBoundingClientRect();
                        if (_ar.width > 10 && _ar.width <= rect.width + 2) {
                            const _r = _captureRect(_anc);
                            if (_r) return _r;
                        }
                        if (!_fallback && _ar.width > 10) {
                            _fallback = _captureRect(_anc);
                        }
                    }
                    _anc = _anc.parentElement;
                }
                return _fallback;
            })(),
            rect: (() => {
                // Zero-rect semantic tags (h3 etc mid-reflow): walk up DOM to find
                // nearest ancestor with a real rect for correct x/y/width/height.
                let _r = rect;
                if (_r.width < 1 && _r.height < 1 &&
                    typeof _semanticTags !== 'undefined' && _semanticTags.has(_tag)) {
                    let _anc = el.parentElement;
                    while (_anc && _anc !== document.body) {
                        const _ar = _anc.getBoundingClientRect();
                        if (_ar.width > 1 && _ar.height > 1) { _r = _ar; break; }
                        _anc = _anc.parentElement;
                    }
                }
                return {
                    x: Math.round(_r.left + scrollX),
                    y: (cs.position === 'fixed' || cs.position === 'sticky')
                        ? Math.round(_r.top)
                        : Math.round(_r.top + scrollY),
                    width: Math.round(_r.width),
                    height: Math.round(_r.height),
                };
            })(),

            // ── textWrapLines: browser-native line break detection ────────────────
            // SVG <tspan> does not support text wrapping. The CON10X Engine must know
            // exactly which words belong to each visual line so it can emit one <tspan>
            // per line that mirrors the original rendered layout precisely.
            //
            // Strategy: use the browser's own Range API to walk every word in the text
            // and detect when its top-edge moves — that is a real line break. We also
            // cross-check against the container rect (width + height) to validate the
            // result. The container width drives the SVG viewBox, and the container
            // height confirms how many lines were rendered.
            //
            // Only computed for text-bearing elements that have more than one word,
            // and only when the element is visible (not hidden). The strip loop below
            // will remove this field if it resolves to an empty array.
            //
            // textWrapContainerW / textWrapContainerH are emitted as sibling fields
            // so the serializer can use the exact measured container dimensions instead
            // of re-computing them from CSS. Both are stripped by the loop when zero.
            ...(() => {
                const _rawText = (el.innerText || el.textContent || '').trim();
                const _empty = { textWrapLines: [], textWrapContainerW: 0, textWrapContainerH: 0 };
                if (!_rawText || isHidden) return _empty;

                const _isTextTag = TEXT_TAGS.has(tag);
                const _isLeaf = el.children.length === 0;
                if (!_isTextTag && !_isLeaf) return _empty;

                const _words = _rawText.split(/\s+/).filter(Boolean);
                if (_words.length <= 1) return _empty;

                const _containerW = Math.round(rect.width);
                const _containerH = Math.round(rect.height);
                if (_containerW < 2 || _containerH < 2) return _empty;

                try {
                    // ── Build a flat character map across ALL text nodes ───────────
                    // Walking only the first direct text node fails when the element
                    // has inline children (<strong>, <em>, <a> etc.) — words inside
                    // or after those children live in different text nodes, so indexOf
                    // returns -1 and we fall back to the inaccurate WrapText() estimator.
                    // TreeWalker visits every text node in DOM order, letting us build
                    // a flat {node, localOffset} array for every character, so any word
                    // can be Range-selected regardless of which text node it sits in.
                    const _charMap = []; // flat index → { node, offset }
                    const _tw = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
                    let _tn = _tw.nextNode();
                    while (_tn) {
                        const _tc = _tn.textContent || '';
                        for (let _ci = 0; _ci < _tc.length; _ci++) {
                            _charMap.push({ node: _tn, offset: _ci });
                        }
                        _tn = _tw.nextNode();
                    }
                    if (_charMap.length === 0) return _empty;

                    // Flat text string matching the char map
                    const _flatText = _charMap.map(c => c.node.textContent[c.offset]).join('');

                    // ── Map each word to its flat char offset ─────────────────────
                    const _wordOffsets = [];
                    let _searchFrom = 0;
                    for (const _w of _words) {
                        const _idx = _flatText.indexOf(_w, _searchFrom);
                        if (_idx === -1) return _empty;
                        _wordOffsets.push({ word: _w, start: _idx, end: _idx + _w.length });
                        _searchFrom = _idx + _w.length;
                    }

                    // ── Walk words, detect line breaks by top-edge change ─────────
                    // Use rect.top captured at the top of serialize() as the baseline.
                    // Both rect.top and the Range word rects are viewport-relative at
                    // the same instant — the subtraction cancels scroll offset correctly.
                    // DO NOT call scrollIntoView here — it changes the scrollbar
                    // visibility which alters the container width and causes the browser
                    // to reflow text at a different width than what was originally rendered,
                    // producing wrong line breaks.
                    const _elTop = rect.top;

                    const _range = document.createRange();
                    const _lines = [];
                    const _lineWidths = []; // browser-measured pixel width per line
                    let _currentLine = [];
                    let _currentLineTop = null;
                    let _currentLineStart = -1; // charMap index of first char of current line
                    let _currentLineEnd = -1;   // charMap index of last char of current line

                    for (const _wo of _wordOffsets) {
                        const _s = _charMap[_wo.start];
                        const _e = _charMap[_wo.end - 1];
                        if (!_s || !_e) { _currentLine.push(_wo.word); continue; }
                        _range.setStart(_s.node, _s.offset);
                        _range.setEnd(_e.node, _e.offset + 1);

                        const _wr = _range.getBoundingClientRect();
                        if (!_wr || _wr.width === 0) {
                            _currentLine.push(_wo.word);
                            continue;
                        }

                        // 3px tolerance absorbs sub-pixel differences on the same line.
                        const _wordTop = Math.round(_wr.top - _elTop);

                        if (_currentLineTop === null) {
                            _currentLineTop = _wordTop;
                            _currentLine.push(_wo.word);
                            _currentLineStart = _wo.start;
                            _currentLineEnd = _wo.end - 1;
                        } else if (Math.abs(_wordTop - _currentLineTop) <= 3) {
                            _currentLine.push(_wo.word);
                            _currentLineEnd = _wo.end - 1;
                        } else {
                            // Flush current line and measure its full pixel width
                            if (_currentLine.length > 0) {
                                _lines.push(_currentLine.join(' '));
                                try {
                                    const _ls = _charMap[_currentLineStart];
                                    const _le = _charMap[_currentLineEnd];
                                    if (_ls && _le) {
                                        _range.setStart(_ls.node, _ls.offset);
                                        _range.setEnd(_le.node, _le.offset + 1);
                                        _lineWidths.push(Math.ceil(_range.getBoundingClientRect().width));
                                    } else { _lineWidths.push(0); }
                                } catch (_) { _lineWidths.push(0); }
                            }
                            _currentLine = [_wo.word];
                            _currentLineTop = _wordTop;
                            _currentLineStart = _wo.start;
                            _currentLineEnd = _wo.end - 1;
                        }
                    }
                    // Flush last line
                    if (_currentLine.length > 0) {
                        _lines.push(_currentLine.join(' '));
                        try {
                            const _ls = _charMap[_currentLineStart];
                            const _le = _charMap[_currentLineEnd];
                            if (_ls && _le) {
                                _range.setStart(_ls.node, _ls.offset);
                                _range.setEnd(_le.node, _le.offset + 1);
                                _lineWidths.push(Math.ceil(_range.getBoundingClientRect().width));
                            } else { _lineWidths.push(0); }
                        } catch (_) { _lineWidths.push(0); }
                    }

                    // Sanity check: if Range detected fewer lines than the container
                    // height implies, the measurement was unreliable. Fall back to WrapText().
                    const _fontSize = parseFloat(cs.fontSize) || 14;
                    const _lineH = parseFloat(cs.lineHeight) || (_fontSize * 1.3);
                    const _expectedLines = Math.round(_containerH / _lineH);
                    if (_expectedLines >= 2 && _lines.length < _expectedLines) return _empty;

                    if (_lines.length > 1) {
                        return {
                            textWrapLines: _lines,
                            textWrapLineWidths: _lineWidths,
                            textWrapContainerW: _containerW,
                            textWrapContainerH: _containerH,
                        };
                    }
                    return _empty;
                } catch (_e) {
                    return _empty;
                }
            })(),
        };

        // Strip empty/falsy entry fields — only send signal, never noise.
        // Receiver uses || '' / || false defaults so missing fields are safe.
        for (const _k of Object.keys(entry)) {
            const _v = entry[_k];
            if (_v === '' || _v === false || _v === null || _v === 0 ||
                (Array.isArray(_v) && _v.length === 0)) {
                delete entry[_k];
            }
        }

        if (isHidden) {
            invisible.push(entry);
        } else {
            visible.push(entry);
        }

        if (!VOID_TAGS.has(tag) && tag !== 'svg' && !_ownsLine) {
            for (const child of el.children) {
                serialize(child, depth + 1, internalId);
            }
        }
    }

    serialize(rootEl || document.body, 0);

    return { visible, invisible };
}

function extractMeta() {
    return {
        url: window.location.href,
        title: document.title,
        viewport: {
            width: window.innerWidth || 1440,
            height: window.innerHeight || 900,
        },
        scrollWidth: document.documentElement.scrollWidth,
        scrollHeight: document.documentElement.scrollHeight,
        devicePixelRatio: window.devicePixelRatio || 1,
    };
}
// =============================================================================
// COMPONENT SELECTOR — hover parent containers, click to select + extract
// Guard: content.js may be injected twice (manifest + scripting.executeScript)
// =============================================================================
var ComponentSelector = ComponentSelector || (() => {
    const SKIP_TAGS = { html: 1, body: 1, head: 1, script: 1, style: 1, meta: 1, link: 1 };
    const MIN_AREA = 2000; // px2 — ignore tiny elements
    const OVERLAY_ID = '__snapstak_selector_overlay__';
    const CURSOR_ID = '__snapstak_selector_cursor__';

    let active = false;
    let hovered = null;
    let overlayEl = null;
    let cursorEl = null;

    // Detects if an element creates its own CSS stacking context.
    // Nav drawers, modals and overlays almost always do via transform or
    // fixed/sticky positioning — making them natural component boundaries.
    function createsStackingContext(el) {
        const cs = window.getComputedStyle(el);
        const pos = cs.position;
        // fixed/sticky with z-index always creates a stacking context
        if ((pos === 'fixed' || pos === 'sticky') && cs.zIndex !== 'auto') return true;
        // absolute/relative with explicit z-index
        if ((pos === 'absolute' || pos === 'relative') && cs.zIndex !== 'auto') return true;
        // transform (slide-in drawers use this)
        if (cs.transform && cs.transform !== 'none') return true;
        // opacity < 1 (fade-in overlays)
        if (parseFloat(cs.opacity) < 1) return true;
        // will-change, filter, isolation, mix-blend-mode
        if (cs.willChange && cs.willChange !== 'auto') return true;
        if (cs.filter && cs.filter !== 'none') return true;
        if (cs.isolation === 'isolate') return true;
        if (cs.mixBlendMode && cs.mixBlendMode !== 'normal') return true;
        return false;
    }

    function findContainer(el) {
        if (!el || el === document.body || el === document.documentElement) return null;

        // Skip our own injected elements
        if (el.id === OVERLAY_ID || el.id === CURSOR_ID) return null;
        if (el.closest && (el.closest('#' + OVERLAY_ID) || el.closest('#' + CURSOR_ID))) return null;

        const vw = window.innerWidth;
        const vh = window.innerHeight;

        let node = el;
        let best = null;
        let depth = 0;
        const MAX_DEPTH = 25; // deeper walk — drawers can be deeply nested

        while (node && node !== document.body && node !== document.documentElement && depth < MAX_DEPTH) {
            const tag = (node.tagName || '').toLowerCase();

            if (!SKIP_TAGS[tag]) {
                const rect = node.getBoundingClientRect();
                const w = rect.width;
                const h = rect.height;
                const area = w * h;

                if (area >= MIN_AREA) {
                    const cs = window.getComputedStyle(node);
                    const visible = cs.display !== 'none'
                        && cs.visibility !== 'hidden'
                        && parseFloat(cs.opacity) > 0;

                    if (visible) {
                        // Skip full-viewport backdrops — they are not components
                        const fillsViewport = w >= vw * 0.95 && h >= vh * 0.95;
                        if (fillsViewport) { node = node.parentElement; depth++; continue; }

                        // HARD STOP 1: stacking context boundary (transform/fixed/sticky)
                        // This is the drawer/modal itself — it IS the component. Stop here.
                        if (createsStackingContext(node)) {
                            return node;
                        }

                        // HARD STOP 2: semantic landmark elements
                        const LANDMARKS = { nav: 1, header: 1, footer: 1, aside: 1, dialog: 1, main: 1 };
                        if (LANDMARKS[tag]) return node;

                        // Candidate: visible block element with real area
                        best = node;

                        // SOFT STOP: only stop on div/section/article/form — NOT ul/ol/li
                        // because nav drawers contain ul.nav-list inside nav.nav-drawer
                        // and we must keep walking to reach the nav stacking context root.
                        const BLOCKS = { div: 1, section: 1, article: 1, form: 1 };
                        if (BLOCKS[tag] && area >= MIN_AREA * 6 && w < vw * 0.85) {
                            // Don't stop if a stacking context parent exists — keep walking to it
                            let hasStackingParent = false;
                            let p = node.parentElement;
                            for (let i = 0; i < 8 && p && p !== document.body; i++) {
                                if (createsStackingContext(p)) { hasStackingParent = true; break; }
                                p = p.parentElement;
                            }
                            if (!hasStackingParent) break;
                        }
                    }
                }
            }

            node = node.parentElement;
            depth++;
        }

        return best;
    }

    function createOverlay() {
        if (document.getElementById(OVERLAY_ID)) return;

        // If a top-layer element is active (dialog, popover, fullscreen),
        // append our overlay INSIDE it so it renders in the top layer too.
        // Appending to document.body puts it below the top layer and it
        // will be invisible while the dialog is open.
        const topLayers = getTopLayerElements();
        const mountPoint = topLayers.length > 0 ? topLayers[0] : document.body;

        overlayEl = document.createElement('div');
        overlayEl.id = OVERLAY_ID;
        overlayEl.setAttribute('data-no-select', 'true');
        overlayEl.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;border:2px solid #38BDF8;box-shadow:0 0 0 3px rgba(56,189,248,0.3),inset 0 0 0 2000px rgba(56,189,248,0.08);border-radius:3px;display:none';
        mountPoint.appendChild(overlayEl);

        cursorEl = document.createElement('div');
        cursorEl.id = CURSOR_ID;
        cursorEl.setAttribute('data-no-select', 'true');
        cursorEl.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;background:#38BDF8;color:#111111;font:600 11px/1.4 Poppins,sans-serif;padding:3px 8px;border-radius:4px;white-space:nowrap;display:none;box-shadow:0 2px 8px rgba(0,0,0,0.4)';
        mountPoint.appendChild(cursorEl);
    }

    function positionOverlay(el) {
        if (!overlayEl || !el) return;
        const r = el.getBoundingClientRect();
        overlayEl.style.cssText = overlayEl.style.cssText
            .replace(/left:[^;]+;?/, '').replace(/top:[^;]+;?/, '')
            .replace(/width:[^;]+;?/, '').replace(/height:[^;]+;?/, '');
        overlayEl.style.left = r.left + 'px';
        overlayEl.style.top = r.top + 'px';
        overlayEl.style.width = r.width + 'px';
        overlayEl.style.height = r.height + 'px';
        overlayEl.style.display = 'block';

        const tag = (el.tagName || '').toLowerCase();
        const id = el.id ? '#' + el.id : '';
        const cls = typeof el.className === 'string' && el.className.trim()
            ? '.' + el.className.trim().split(/\s+/)[0] : '';
        cursorEl.textContent = tag + (id || cls || '');
        cursorEl.style.left = r.left + 'px';
        cursorEl.style.top = Math.max(0, r.top - 26) + 'px';
        cursorEl.style.display = 'block';
    }

    function hideOverlay() {
        if (overlayEl) overlayEl.style.display = 'none';
        if (cursorEl) cursorEl.style.display = 'none';
    }

    // Returns true if el is a full-viewport backdrop/scrim
    // (the dimming overlay behind a drawer — NOT the drawer itself)
    function isBackdrop(el) {
        if (!el) return false;
        const r = el.getBoundingClientRect();
        const vw = window.innerWidth;
        const vh = window.innerHeight;
        // Full-viewport coverage = backdrop
        if (r.width >= vw * 0.95 && r.height >= vh * 0.95) return true;
        return false;
    }

    // Throttle backdrop scan — only re-scan every 300ms to avoid thrashing

    function onMouseMove(e) {
        // elementsFromPoint returns ALL elements at cursor in z-order (topmost first).
        const all = document.elementsFromPoint(e.clientX, e.clientY);
        let raw = null;
        for (const el of all) {
            if (el.id === OVERLAY_ID || el.id === CURSOR_ID) continue;
            if (el === document.body || el === document.documentElement) continue;
            if (isBackdrop(el)) continue;
            raw = el;
            break;
        }
        if (!raw) return;
        const container = findContainer(raw);
        if (container && container !== hovered) {
            hovered = container;
            positionOverlay(container);
        } else if (!container) {
            hovered = null;
            hideOverlay();
        }
    }

    function onClick(e) {
        if (!hovered) return;
        e.preventDefault();
        e.stopPropagation();
        const selected = hovered;
        stop();
        chrome.runtime.sendMessage({
            type: 'COMPONENT_SELECTED',
            element: serializeSelected(selected),
        });
    }


    function onKeyDown(e) {
        if (e.key === 'Escape') {
            stop();
            chrome.runtime.sendMessage({ type: 'COMPONENT_SELECT_CANCELLED' });
        }
    }

    function serializeSelected(el) {
        const rect = el.getBoundingClientRect();
        const tag = (el.tagName || '').toLowerCase();
        const id = el.id || '';
        const cls = typeof el.className === 'string' ? el.className.trim() : '';
        const ariaLabel = el.getAttribute && (el.getAttribute('aria-label') || '');
        const dataId = el.dataset && (el.dataset.componentId || el.dataset.component || '');
        const suggested = dataId || ariaLabel || id || (cls.split(/\s+/)[0] || tag);

        // Walk up to find nearest ancestor (or self) with a segmentId
        let segmentId = '';
        let node = el;
        while (node && node !== document.body) {
            if (node.dataset && node.dataset.segmentId) {
                segmentId = node.dataset.segmentId;
                break;
            }
            node = node.parentElement;
        }

        return {
            tag, id, cls, suggested,
            segmentId,
            rect: {
                x: Math.round(rect.left + window.scrollX),
                y: Math.round(rect.top + window.scrollY),
                width: Math.round(rect.width),
                height: Math.round(rect.height),
            },
            xpath: getXPath(el),
        };
    }

    function getXPath(el) {
        if (!el || el === document.body) return '/html/body';
        const parts = [];
        let node = el;
        while (node && node.nodeType === 1 && node !== document.documentElement) {
            const tag = node.tagName.toLowerCase();
            let idx = 1;
            let sib = node.previousElementSibling;
            while (sib) { if (sib.tagName === node.tagName) idx++; sib = sib.previousElementSibling; }
            parts.unshift(tag + '[' + idx + ']');
            node = node.parentElement;
        }
        return '/' + parts.join('/');
    }

    // ── Top layer detection ─────────────────────────────────────────────
    // The browser's top layer (dialog[open], [popover], fullscreenElement)
    // sits above all z-index stacking contexts. When active, the browser
    // marks the rest of the document as inert — pointer events, focus and
    // keyboard events are blocked at the browser level, not CSS/JS.
    // Solution: attach our listeners directly to every top-layer element
    // so we receive events regardless of document inertness.
    // This works for ANY element type promoted to the top layer.

    let topLayerTargets = []; // elements we attached listeners to

    function getTopLayerElements() {
        const found = [];

        // 1. :modal matches any element currently in the top layer (dialog, popover, fullscreen)
        try {
            document.querySelectorAll(':modal').forEach(el => {
                if (!found.includes(el)) found.push(el);
            });
        } catch (_) { }

        // 2. Open dialogs (fallback for browsers where :modal isn't supported)
        document.querySelectorAll('dialog[open]').forEach(el => {
            if (!found.includes(el)) found.push(el);
        });

        // 3. Open popovers
        document.querySelectorAll('[popover]:not([popover="manual"])').forEach(el => {
            if (el.matches(':popover-open') && !found.includes(el)) found.push(el);
        });

        // 4. Fullscreen element
        if (document.fullscreenElement && !found.includes(document.fullscreenElement)) {
            found.push(document.fullscreenElement);
        }

        return found;
    }

    function attachTopLayerListeners() {
        topLayerTargets = [];
        const targets = getTopLayerElements();

        if (targets.length === 0) {
            // No top layer — standard page, listen on window
            window.addEventListener('mousemove', onMouseMove, true);
            window.addEventListener('click', onClick, true);
            window.addEventListener('keydown', onKeyDown, true);
            topLayerTargets = [{ target: window, isWindow: true }];
        } else {
            // Top layer active — attach ONLY to the top-layer element(s).
            // The dialog and its descendants are NOT inert (per spec), so
            // mousemove/click inside the dialog will fire on the dialog element.
            // Do NOT also attach to window — it is inert and double-fires.
            targets.forEach(el => {
                el.addEventListener('mousemove', onMouseMove, true);
                el.addEventListener('click', onClick, true);
                el.addEventListener('keydown', onKeyDown, true);
                topLayerTargets.push({ target: el, isWindow: false });
            });
        }
    }

    function detachTopLayerListeners() {
        topLayerTargets.forEach(({ target }) => {
            target.removeEventListener('mousemove', onMouseMove, true);
            target.removeEventListener('click', onClick, true);
            target.removeEventListener('keydown', onKeyDown, true);
        });
        topLayerTargets = [];
    }

    function start() {
        if (active) return;
        active = true;
        createOverlay();
        attachTopLayerListeners();
        document.body.style.cursor = 'crosshair';
    }

    function stop() {
        if (!active) return;
        active = false;
        hovered = null;
        hideOverlay();
        detachTopLayerListeners();
        document.body.style.cursor = '';
        if (overlayEl) { overlayEl.remove(); overlayEl = null; }
        if (cursorEl) { cursorEl.remove(); cursorEl = null; }
    }

    return { start, stop };
})();


// =============================================================================
// FLOATING PANEL — injected directly into the page (no Chrome window)
// =============================================================================


// =============================================================================
// EXTRACT SINGLE ELEMENT by xpath
// =============================================================================
async function extractElement(elementInfo) {
    if (!elementInfo || !elementInfo.xpath) throw new Error('No element xpath provided.');

    // getXPath() returns '/body[1]/div[1]/...' — missing the /html prefix.
    // document.evaluate needs a fully-qualified absolute path from the document root.
    // Normalise: if path starts with /body or /html/body keep as-is with /html prepended.
    let xpath = elementInfo.xpath;
    if (xpath === '/html/body') {
        // already correct
    } else if (xpath.startsWith('/body')) {
        xpath = '/html' + xpath;
    } else if (!xpath.startsWith('/html')) {
        xpath = '/html' + xpath;
    }

    const result = document.evaluate(
        xpath, document, null,
        XPathResult.FIRST_ORDERED_NODE_TYPE, null
    );
    const el = result.singleNodeValue;
    if (!el) throw new Error('Could not locate element — page may have changed.');

    // ── Collect CSS custom properties (--variables) used by this element and its descendants
    const cssVars = {};
    const allEls = [el, ...el.querySelectorAll('*')];

    // Walk every stylesheet looking for --variable declarations
    try {
        for (const sheet of document.styleSheets) {
            let rules;
            try { rules = sheet.cssRules; } catch { continue; }
            for (const rule of rules) {
                if (!rule.style) continue;
                for (const prop of rule.style) {
                    if (prop.startsWith('--')) {
                        cssVars[prop] = rule.style.getPropertyValue(prop).trim();
                    }
                }
            }
        }
    } catch (_) { }

    // Also read vars directly on :root / documentElement
    const rootStyle = getComputedStyle(document.documentElement);
    for (const prop of rootStyle) {
        if (prop.startsWith('--')) {
            cssVars[prop] = rootStyle.getPropertyValue(prop).trim();
        }
    }

    // ── Collect the element's outerHTML (for server-side processing)
    const outerHTML = el.outerHTML;

    // ── Capture el's original position BEFORE any DOM expansion.
    // elTop/elLeft must be the stable pre-expansion values.
    const _elRectOrig = el.getBoundingClientRect();
    const elWidth = Math.round(_elRectOrig.width) || el.scrollWidth || window.innerWidth;

    // ── Expand all overflow scroll containers inside el so serializeDOM sees ALL children.
    // Without this, elements scrolled out of the visible viewport have off-screen rects
    // and serializeDOM's visibility filter drops them. The SVG is the source of truth —
    // if serializeDOM doesn't see it, no SVG object is created for it.
    // This mirrors the exact same technique used in serializeHiddenComponents.
    // Walk ALL descendants and expand anything hiding content:
    //   A) overflow:auto/scroll with scrollable content
    //   B) overflow:hidden with clipped OR zero-height content (collapsed accordion)
    //   C) display:none or visibility:hidden — frameworks may fully hide collapsed panels
    //   D) <details> elements — browser natively hides non-<summary> children when closed.
    //      getComputedStyle returns display:block on those children (no CSS involved),
    //      so display:none detection misses it. Must set the "open" attribute directly.
    //   G) max-height:0 + overflow:hidden — WordPress/legacy PHP/Bootstrap CSS accordion
    //      pattern. height is "auto" so scrollHeight === clientHeight (both clipped by
    //      max-height). The scrollHeight check in B would pass incorrectly. Must detect
    //      by reading the computed max-height value directly.
    // ALL are saved and restored after serializeDOM runs.
    const _scrollSaved = [];

    // ── MUST happen BEFORE prefetchSVGSprites — closed <details> hide their <svg><use> children
    // from querySelectorAll, so the sprite URL is never discovered and the cache stays empty.
    const _closedDetails = Array.from(el.querySelectorAll('details:not([open])'));
    for (const det of _closedDetails) {
        det.setAttribute('open', '');
    }
    if (_closedDetails.length > 0) void el.offsetHeight; // reflow so children enter layout

    // ── Prefetch SVG sprite sheets — AFTER <details> expansion so ALL <svg><use> elements
    // are visible to querySelectorAll, including chevrons inside closed accordion rows.
    const svgSymbolCache = await prefetchSVGSprites(el);

    // ── Stamp segmentIds onto layout containers within this component ─────────
    const _runTs = Math.floor(Date.now() / 1000);
    stampSegmentIds(_runTs);
    const _allDescendants = Array.from(el.querySelectorAll('*'));
    for (const node of _allDescendants) {
        const cs = window.getComputedStyle(node);
        const oy = cs.overflowY, ox = cs.overflowX;
        const _ch = parseFloat(cs.height) || 0;
        const _display = cs.display;
        const _visibility = cs.visibility;

        // A: scrollable overflow containers
        const isScrollable = (oy === 'auto' || oy === 'scroll') && node.scrollHeight > node.clientHeight + 2;
        const isScrollableX = (ox === 'auto' || ox === 'scroll') && node.scrollWidth > node.clientWidth + 2;
        // B: clipped hidden content — partially or fully collapsed accordion
        // scrollHeight > clientHeight catches partial; _ch===0 + children catches fully collapsed
        const isHiddenClip = (oy === 'hidden' || ox === 'hidden' || cs.overflow === 'hidden')
            && (node.scrollHeight > node.clientHeight + 2 || (_ch === 0 && node.children.length > 0));
        // C: framework hides collapsed content with display:none or visibility:hidden
        const isInvisible = _display === 'none' || _visibility === 'hidden' || _visibility === 'collapse';
        // G: max-height collapse — WordPress themes, Bootstrap, jQuery slideUp() at rest,
        //    pure CSS checkbox accordions. height is "auto" so scrollHeight === clientHeight
        //    (both are clipped by max-height). B's scrollHeight check cannot detect this.
        //    Detect by reading computed max-height: any value ≤ 1px with overflow:hidden
        //    and at least one child means the content is collapsed via max-height.
        const _maxH = parseFloat(cs.maxHeight);
        const isMaxHeightCollapsed = !isNaN(_maxH) && _maxH <= 1
            && (oy === 'hidden' || ox === 'hidden' || cs.overflow === 'hidden')
            && node.children.length > 0;

        if (!isScrollable && !isScrollableX && !isHiddenClip && !isInvisible && !isMaxHeightCollapsed) continue;

        _scrollSaved.push({
            el: node,
            overflow: node.style.overflow,
            overflowY: node.style.overflowY,
            overflowX: node.style.overflowX,
            height: node.style.height,
            maxHeight: node.style.maxHeight,
            display: node.style.display,
            visibility: node.style.visibility,
        });

        if (isScrollable || isScrollableX || isHiddenClip || isMaxHeightCollapsed) {
            node.style.setProperty('overflow', 'visible', 'important');
            node.style.setProperty('overflow-y', 'visible', 'important');
            node.style.setProperty('overflow-x', 'visible', 'important');
            node.style.setProperty('height', 'auto', 'important');
            node.style.setProperty('max-height', 'none', 'important');
        }
        if (isInvisible) {
            if (_display === 'none')
                node.style.setProperty('display', 'block', 'important');
            if (_visibility === 'hidden' || _visibility === 'collapse')
                node.style.setProperty('visibility', 'visible', 'important');
        }
    }
    void el.offsetHeight; // force first reflow — child nodes expand

    // ── SECOND PASS: after first reflow, any parent containers that still have a
    // fixed height/overflow may now be clipping their newly-expanded children.
    // Re-scan all ancestors and siblings to catch containers whose scrollHeight
    // now exceeds their clientHeight AFTER the first expansion pass.
    const _allDescendants2 = Array.from(el.querySelectorAll('*'));
    for (const node of _allDescendants2) {
        if (node.scrollHeight > node.clientHeight + 2) {
            // Only expand if not already saved/expanded in first pass
            const alreadySaved = _scrollSaved.some(s => s.el === node);
            if (!alreadySaved) {
                _scrollSaved.push({
                    el: node,
                    overflow: node.style.overflow,
                    overflowY: node.style.overflowY,
                    overflowX: node.style.overflowX,
                    height: node.style.height,
                    maxHeight: node.style.maxHeight,
                    display: node.style.display,
                    visibility: node.style.visibility,
                });
                node.style.setProperty('overflow', 'visible', 'important');
                node.style.setProperty('overflow-y', 'visible', 'important');
                node.style.setProperty('overflow-x', 'visible', 'important');
                node.style.setProperty('height', 'auto', 'important');
                node.style.setProperty('max-height', 'none', 'important');
            }
        }
    }
    void el.offsetHeight; // second reflow — parent containers now expand too

    // ── Also expand el itself — it may have overflow:hidden + fixed height that clips children.
    // Save inline styles, force open, then re-read the true expanded height.
    const _elSavedOverflow = el.style.overflow;
    const _elSavedOverflowY = el.style.overflowY;
    const _elSavedOverflowX = el.style.overflowX;
    const _elSavedHeight = el.style.height;
    const _elSavedMaxHeight = el.style.maxHeight;
    el.style.setProperty('overflow', 'visible', 'important');
    el.style.setProperty('overflow-y', 'visible', 'important');
    el.style.setProperty('overflow-x', 'visible', 'important');
    el.style.setProperty('height', 'auto', 'important');
    el.style.setProperty('max-height', 'none', 'important');
    void el.offsetHeight;

    // ── After expansion, re-read el's rect — it now shows the full expanded height.
    // Use this for elHeight (filter window) so all expanded children pass the filter.
    // Keep elTop/elLeft from the pre-expansion rect — position has not moved.
    const elRect = el.getBoundingClientRect();
    const elHeight = Math.round(elRect.height) || el.scrollHeight || 0;

    // ── Fix lazy-loaded img elements that still have zero getBoundingClientRect height
    // after accordion expansion. These are loading="lazy" images inside closed <details>
    // that were never fetched. Strategy:
    //   1. Build a reference map: width attribute → naturalHeight from already-loaded images
    //   2. For zero-height imgs, look up their width in the reference map
    //   3. Fallback: parent offsetHeight
    const _lazyImgSaved = [];
    const _naturalHByWidth = new Map(); // key: width attr string, value: rendered height from browser
    for (const img of Array.from(el.querySelectorAll('img'))) {
        const _ir = img.getBoundingClientRect();
        if (_ir.height > 0 && _ir.width > 0) {
            // This image has a real browser-measured height — use it as reference for same-width siblings
            const _wAttr = img.getAttribute('width') || String(Math.round(_ir.width));
            if (!_naturalHByWidth.has(_wAttr)) {
                _naturalHByWidth.set(_wAttr, Math.round(_ir.height));
            }
        }
    }
    for (const img of Array.from(el.querySelectorAll('img'))) {
        const _ir = img.getBoundingClientRect();
        if (_ir.height > 0) continue; // already has a real height — skip
        const _attrW = img.getAttribute('width') || '';
        const _attrH = parseInt(img.getAttribute('height'), 10) || 0;
        // Look up natural height from a sibling image with the same declared width
        const _refH = _attrW ? (_naturalHByWidth.get(_attrW) || 0) : 0;
        const _parH = img.parentElement ? img.parentElement.offsetHeight : 0;
        const _resolvedH = _attrH || _refH || _parH || 0;
        const _resolvedW = parseInt(_attrW, 10) || Math.round(img.offsetWidth) || (img.parentElement ? img.parentElement.offsetWidth : 0) || 0;
        if (_resolvedW > 0 && _resolvedH > 0) {
            _lazyImgSaved.push({ el: img, width: img.style.width, height: img.style.height, dataH: img.dataset.snapstkH });
            img.style.setProperty('width', _resolvedW + 'px', 'important');
            img.style.setProperty('height', _resolvedH + 'px', 'important');
            img.dataset.snapstkH = _resolvedH; // direct read by walkHiddenEl — bypasses flex stretch
            console.log('[SnapStak] LazyImg fix:', img.alt || img.src, '| resolved:', _resolvedW + 'x' + _resolvedH, '| source: refH=', _refH, 'parH=', _parH);
        } else {
            console.warn('[SnapStak] LazyImg UNRESOLVED:', img.alt || img.src, '| attrW:', _attrW, '| attrH:', _attrH, '| refH:', _refH, '| parH:', _parH);
        }
    }
    if (_lazyImgSaved.length > 0) void el.offsetHeight; // reflow so img rects are now real

    // ── Use the SAME serializeDOM pipeline as the full page — starting from body.
    // Then filter to only elements inside the selected element's subtree.
    // This ensures getComputedStyle picks up inherited backgrounds correctly,
    // exactly as it does for the full page transform.

    // Run the FULL serializeDOM from body — identical to extractPage.
    // Same pipeline, same code, same data.
    const { visible: allVisible } = serializeDOM(svgSymbolCache);

    // ── Restore lazy-img forced dimensions
    for (const s of _lazyImgSaved) {
        s.el.style.removeProperty('width');
        s.el.style.removeProperty('height');
        if (s.width) s.el.style.width = s.width;
        if (s.height) s.el.style.height = s.height;
        if (s.dataH !== undefined) s.el.dataset.snapstkH = s.dataH;
        else delete s.el.dataset.snapstkH;
    }

    // ── Restore all expanded nodes immediately after capture
    for (const s of _scrollSaved) {
        s.el.style.removeProperty('overflow');
        s.el.style.removeProperty('overflow-y');
        s.el.style.removeProperty('overflow-x');
        s.el.style.removeProperty('height');
        s.el.style.removeProperty('max-height');
        s.el.style.removeProperty('display');
        s.el.style.removeProperty('visibility');
        if (s.overflow) s.el.style.overflow = s.overflow;
        if (s.overflowY) s.el.style.overflowY = s.overflowY;
        if (s.overflowX) s.el.style.overflowX = s.overflowX;
        if (s.height) s.el.style.height = s.height;
        if (s.maxHeight) s.el.style.maxHeight = s.maxHeight;
        if (s.display) s.el.style.display = s.display;
        if (s.visibility) s.el.style.visibility = s.visibility;
    }
    // ── Restore closed <details> elements
    for (const det of _closedDetails) {
        det.removeAttribute('open');
    }

    // ── Restore el itself
    el.style.removeProperty('overflow');
    el.style.removeProperty('overflow-y');
    el.style.removeProperty('overflow-x');
    el.style.removeProperty('height');
    el.style.removeProperty('max-height');
    if (_elSavedOverflow) el.style.overflow = _elSavedOverflow;
    if (_elSavedOverflowY) el.style.overflowY = _elSavedOverflowY;
    if (_elSavedOverflowX) el.style.overflowX = _elSavedOverflowX;
    if (_elSavedHeight) el.style.height = _elSavedHeight;
    if (_elSavedMaxHeight) el.style.maxHeight = _elSavedMaxHeight;

    // ── Compute elLeft/elTop by finding el's own entry in allVisible.
    // serializeDOM already computed rect.y correctly for ALL position types
    // (fixed: rect.top only, others: rect.top + scrollY).
    // We MUST use the same value — any re-computation risks mismatch.
    //
    // Strategy: find the element in allVisible whose rect most closely matches
    // el's bounding rect (same x, same width, same height). That entry's rect.y
    // IS the correct elTop — no position-type guessing needed.
    const scrollX = window.scrollX || 0;
    const scrollY = window.scrollY || 0;

    // Expected x from serializeDOM formula (always adds scrollX):
    const _expectedX = Math.round(_elRectOrig.left + scrollX);
    const _expectedW = Math.round(_elRectOrig.width);
    const _expectedH = Math.round(elRect.height); // post-expansion height

    // Find el's own entry: match by x, width, and being a container (not text-only)
    let elTop = null;
    let elLeft = _expectedX;
    for (const e of allVisible) {
        if (!e.rect) continue;
        if (e.rect.x === _expectedX &&
            e.rect.width === _expectedW &&
            Math.abs(e.rect.height - _expectedH) < 50) {
            // This is the dialog element itself
            elTop = e.rect.y;
            elLeft = e.rect.x;
            break;
        }
    }

    // Fallback: if no exact match found, use the smallest rect.y among elements
    // that have the correct x and width (the container top)
    if (elTop === null) {
        let _minY = Infinity;
        for (const e of allVisible) {
            if (!e.rect) continue;
            if (e.rect.x === _expectedX && e.rect.width === _expectedW) {
                if (e.rect.y < _minY) { _minY = e.rect.y; elTop = e.rect.y; }
            }
        }
    }

    // Last resort: replicate serializeDOM's formula
    if (elTop === null) {
        const _elCs = window.getComputedStyle(el);
        const _fixed = _elCs.position === 'fixed' || _elCs.position === 'sticky';
        elTop = _fixed
            ? Math.round(_elRectOrig.top)
            : Math.round(_elRectOrig.top + scrollY);
    }

    // Filter window: elBottom must cover ALL expanded content.
    // After expansion, the tallest child's rect.y + rect.height is the true bottom.
    // Use the maximum rect.y + rect.height among elements near our x/width as elBottom.
    // Simpler: use elTop + elHeight where elHeight = post-expansion getBoundingClientRect height.
    const elRight = elLeft + elWidth;
    const elBottom = elTop + elHeight + 100; // +100px buffer for rounding/border

    const visible = allVisible.filter(e => {
        if (!e.rect) return false;
        const cx = e.rect.x + e.rect.width / 2;
        const cy = e.rect.y + e.rect.height / 2;
        return cx >= elLeft && cx <= elRight && cy >= elTop && cy <= elBottom;
    });

    // Fix 1: Make positions component-relative (subtract component origin).
    // The full page works because body is at x=0,y=0.
    // The component el is at x=elLeft,y=elTop — subtract so el_0 starts at 0,0.
    for (const e of visible) {
        if (e.rect) {
            e.rect.x -= elLeft;
            e.rect.y -= elTop;
        }
    }

    // Fix 2: Set effective background on el_0 (the component root).
    // el itself is transparent — its background comes from an ancestor.
    // Walk up to find the first non-transparent background.
    if (visible.length > 0 && visible[0].cssProps) {
        let node = el;
        while (node && node !== document.documentElement) {
            const bg = window.getComputedStyle(node).backgroundColor;
            if (bg && bg !== 'rgba(0, 0, 0, 0)' && bg !== 'transparent') {
                visible[0].cssProps.backgroundColor = bg;
                break;
            }
            node = node.parentElement;
        }
    }




    // ── Extract all CSS rules that apply to this component's elements.
    // This captures behaviour driven by CSS: hover, focus, active states,
    // transitions, animations, and media queries — the full behavioural truth.
    const componentCSS = (function extractComponentCSS(rootEl) {
        // Collect all DOM elements within the component
        const elements = [rootEl, ...rootEl.querySelectorAll('*')];

        // Build a Set of all element references for fast matching
        const elementSet = new Set(elements);

        const matchedRules = []; // regular rules that match component elements
        const behaviorRules = []; // hover/focus/active/transition/animation rules
        const mediaRules = []; // @media rules containing any matched selectors
        const keyframes = []; // @keyframes used by component animations

        // Collect animation names used by component elements
        const usedAnimations = new Set();
        for (const el of elements) {
            const cs = window.getComputedStyle(el);
            const anim = cs.animationName;
            if (anim && anim !== 'none') {
                anim.split(',').forEach(a => usedAnimations.add(a.trim()));
            }
        }

        // ── Strip all pseudo-classes/elements from a selector for DOM matching ──
        function stripPseudos(sel) {
            return sel
                .replace(/:{1,2}(hover|focus|focus-within|focus-visible|active|visited|checked|disabled|enabled|placeholder-shown|placeholder|before|after|selection|marker|backdrop|first-child|last-child|first-of-type|last-of-type|only-child|only-of-type|empty|root|target|not\([^)]*\)|nth-child\([^)]*\)|nth-of-type\([^)]*\)|is\([^)]*\)|where\([^)]*\)|has\([^)]*\))/gi, '')
                .replace(/\.\S+:\S+/g, '')
                .trim();
        }

        // ── Global selectors — always captured regardless of component match ──
        // body, html, :root, * enforce page-level constraints (min-width, font,
        // box-sizing etc.) that every component inherits. Dropping them loses
        // critical layout rules like body { min-width: 600px }.
        const GLOBAL_SELECTORS = /^(\*|html|body|:root)(\s*,\s*(\*|html|body|:root))*$/i;

        // ── Test if a selector matches the component or is a global rule ──────
        function selectorMatchesComponent(sel) {
            // Global selectors always match — capture everything
            const base = stripPseudos(sel).trim();
            if (!base || GLOBAL_SELECTORS.test(base)) return true;
            try {
                const matched = rootEl.querySelectorAll(base);
                for (const m of matched) { if (elementSet.has(m)) return true; }
                if (rootEl.matches && rootEl.matches(base)) return true;
            } catch (_) {
                // querySelectorAll fails on selectors with colons in class names
                // (e.g. .d:flex, .d:hidden) — fall back to manual class matching
                try {
                    // Extract class names from selector and check if any element has them
                    const classMatches = base.match(/\.([\w\-:./]+)/g);
                    if (classMatches) {
                        const classNames = classMatches.map(c => c.slice(1));
                        for (const el of elementSet) {
                            if (typeof el.className === 'string') {
                                const elClasses = el.className.split(/\s+/);
                                if (classNames.some(cn => elClasses.includes(cn))) return true;
                            }
                        }
                    }
                } catch (_2) { }
            }
            // Ancestor-scoped selectors (e.g. "body:not([data-edtn='uk']) .ms-user-menu")
            // fail rootEl.querySelectorAll because body/html is above rootEl.
            // Restricted to body/html-prefixed selectors only — avoids pulling in page-wide rules.
            if (/^(body|html)[\s:[>+~]/.test(base)) {
                try {
                    const docMatched = document.querySelectorAll(base);
                    for (const m of docMatched) { if (rootEl.contains(m)) return true; }
                } catch (_3) { }
            }
            return false;
        }

        // ── Is this selector a behavioural / pseudo-state rule? ──────────────
        const PSEUDO_RE = /:{1,2}(hover|focus|focus-within|focus-visible|active|visited|checked|disabled|enabled|placeholder|before|after|selection|marker|backdrop|nth-|first-child|last-child|first-of-type|last-of-type)/i;

        function processRule(rule, mediaContext) {
            // ── Regular style rule ────────────────────────────────────────────
            if (rule instanceof CSSStyleRule) {
                const selector = rule.selectorText || '';
                if (!selectorMatchesComponent(selector)) return;

                // Resolve all var(--custom-property) references to their actual computed values.
                // The browser already knows every resolved value — getPropertyValue reads it directly.
                // This eliminates multi-level token chains: rgb(var(--vmsc-menu-bg-soft)) becomes
                // rgb(27, 27, 28) so the AI receives exact values and never needs to guess.
                // Read from rootEl first — captures component-scoped overrides (e.g. dark header theme).
                // Fall back to document.documentElement for global :root definitions.
                const resolvedCSSText = (() => {
                    const elStyle = getComputedStyle(rootEl);
                    const docStyle = getComputedStyle(document.documentElement);
                    return (rule.cssText || '').replace(/var\(\s*(--[a-zA-Z0-9_-]+)\s*(?:,[^)]+)?\)/g, (match, prop) => {
                        const val = elStyle.getPropertyValue(prop).trim() || docStyle.getPropertyValue(prop).trim();
                        return val || match; // fall back to original if not resolved
                    });
                })();

                const cssText = resolvedCSSText;
                const isBehavioral = PSEUDO_RE.test(selector);
                const entry = { selector, cssText, mediaContext: mediaContext || null };
                if (isBehavioral) {
                    behaviorRules.push(entry);
                } else {
                    matchedRules.push(entry);
                }
            }

            // ── @media rule — recurse, applying same pseudo-stripping logic ──
            if (rule instanceof CSSMediaRule) {
                const mediaQuery = rule.conditionText || rule.media?.mediaText || '';
                const childMatches = [];
                for (const childRule of rule.cssRules) {
                    if (childRule instanceof CSSStyleRule) {
                        const sel = childRule.selectorText || '';
                        if (selectorMatchesComponent(sel)) {
                            const elStyle = getComputedStyle(rootEl);
                            const docStyle = getComputedStyle(document.documentElement);
                            const resolvedChild = (childRule.cssText || '').replace(/var\(\s*(--[a-zA-Z0-9_-]+)\s*(?:,[^)]+)?\)/g, (match, prop) => {
                                const val = elStyle.getPropertyValue(prop).trim() || docStyle.getPropertyValue(prop).trim();
                                return val || match;
                            });
                            childMatches.push(resolvedChild);
                        }
                    } else if (childRule instanceof CSSStyleRule === false) {
                        // Nested @supports or @layer inside @media — recurse
                        processRule(childRule, mediaContext);
                    }
                }
                if (childMatches.length > 0) {
                    mediaRules.push({ mediaQuery, rules: childMatches });
                }
            }

            // ── @supports — recurse into its children ────────────────────────
            if (typeof CSSSupportsRule !== 'undefined' && rule instanceof CSSSupportsRule) {
                for (const child of rule.cssRules) processRule(child, mediaContext);
            }

            // ── @layer — recurse into its children ───────────────────────────
            if (typeof CSSLayerBlockRule !== 'undefined' && rule instanceof CSSLayerBlockRule) {
                for (const child of rule.cssRules) processRule(child, mediaContext);
            }

            // ── @container — recurse into its children ───────────────────────
            if (typeof CSSContainerRule !== 'undefined' && rule instanceof CSSContainerRule) {
                for (const child of rule.cssRules) processRule(child, mediaContext);
            }

            // ── @keyframes — capture ALL that match component animation names ─
            // Also capture any whose name appears in the component's class list
            // (animation may be toggled by JS class, so animationName is 'none' at capture time)
            if (rule instanceof CSSKeyframesRule) {
                const name = rule.name;
                if (usedAnimations.has(name)) {
                    keyframes.push(rule.cssText);
                } else {
                    // Check if any element's className references this animation name
                    for (const el of elements) {
                        if (typeof el.className === 'string' && el.className.includes(name)) {
                            keyframes.push(rule.cssText);
                            break;
                        }
                        // Also check style sheets — if any matched rule references it
                        if (matchedRules.some(r => r.cssText.includes(name)) ||
                            behaviorRules.some(r => r.cssText.includes(name))) {
                            keyframes.push(rule.cssText);
                            break;
                        }
                    }
                }
            }
        }

        // Walk all stylesheets
        for (const sheet of document.styleSheets) {
            let rules;
            try { rules = sheet.cssRules; } catch { continue; }
            for (const rule of rules) {
                processRule(rule, null);
            }
        }

        return {
            matched: matchedRules,   // base styles for component elements
            behavior: behaviorRules,  // hover, focus, active, pseudo states
            media: mediaRules,     // responsive breakpoint rules
            keyframes: keyframes,      // animations used by the component
        };
    })(el);

    // ── Extract JavaScript that belongs to this component.
    // Three sources: inline handlers, inline <script> blocks, external scripts.
    // Strategy: build a fingerprint of the component's identifiers, then search
    // every script source for references to those identifiers.
    const componentJS = await (async function extractComponentJS(rootEl, elements) {

        // ── Build fingerprint — every identifier that could be referenced in JS
        const ids = new Set();
        const classes = new Set();
        const dataAttrs = new Set();

        for (const el of elements) {
            // IDs
            if (el.id) ids.add(el.id);

            // Classes
            if (typeof el.className === 'string') {
                el.className.split(/\s+/).filter(Boolean).forEach(c => classes.add(c));
            }

            // data-* attributes
            if (el.dataset) {
                Object.keys(el.dataset).forEach(k => dataAttrs.add('data-' + k.replace(/([A-Z])/g, '-$1').toLowerCase()));
            }

            // Inline event handlers from HTML attributes
            const HANDLERS = ['onclick', 'onmouseover', 'onmouseout', 'onmouseenter',
                'onmouseleave', 'onfocus', 'onblur', 'onchange', 'oninput',
                'onkeydown', 'onkeyup', 'onsubmit', 'ontoggle'];
            for (const h of HANDLERS) {
                const val = el.getAttribute(h);
                if (val) ids.add(val); // capture the handler expression itself
            }
        }

        // Build search terms — things we look for in script content
        const searchTerms = [
            ...Array.from(ids).map(id => [`#${id}`, `getElementById('${id}')`, `getElementById("${id}")`, `querySelector('#${id}')`, `querySelector("#${id}")`]),
            ...Array.from(classes).map(c => [`.${c}`, `getElementsByClassName('${c}')`, `getElementsByClassName("${c}")`, `querySelector('.${c}')`, `querySelector(".${c}")`, `classList.*${c}`]),
            ...Array.from(dataAttrs).map(d => [`[${d}]`, `getAttribute('${d}')`, `getAttribute("${d}")`]),
        ].flat().filter(Boolean);

        if (searchTerms.length === 0) return { inline: [], scripts: [] };

        function scriptMatchesComponent(scriptContent) {
            return searchTerms.some(term => scriptContent.includes(term));
        }

        // ── Source 1: Inline handlers on elements (from outerHTML)
        const inlineHandlers = [];
        const HANDLERS = ['onclick', 'onmouseover', 'onmouseout', 'onmouseenter',
            'onmouseleave', 'onfocus', 'onblur', 'onchange', 'oninput',
            'onkeydown', 'onkeyup', 'onsubmit', 'ontoggle'];
        for (const el of elements) {
            for (const h of HANDLERS) {
                const val = el.getAttribute(h);
                if (val) inlineHandlers.push({ element: el.tagName.toLowerCase(), handler: h, code: val });
            }
        }

        // ── Source 2: Inline <script> blocks — search for component identifiers
        const inlineScripts = [];
        for (const script of document.querySelectorAll('script:not([src])')) {
            const src = script.textContent || '';
            if (src.trim().length === 0) continue;
            if (scriptMatchesComponent(src)) {
                inlineScripts.push({ type: 'inline', content: src.trim().slice(0, 50000) });
            }
        }

        // ── Source 3: External scripts — fetch and search
        const externalScripts = [];
        const externalTags = Array.from(document.querySelectorAll('script[src]'));

        // Limit to reasonable number — skip analytics/tracking scripts
        const SKIP_PATTERNS = /google|analytics|gtag|facebook|twitter|hotjar|intercom|zendesk|cdn\.jsdelivr|cloudflare\/ajax/i;

        const fetchPromises = externalTags
            .filter(s => !SKIP_PATTERNS.test(s.src))
            .slice(0, 10) // max 10 external scripts
            .map(async (script) => {
                try {
                    const abs = new URL(script.src, window.location.origin).href;
                    const res = await fetch(abs);
                    if (!res.ok) return;
                    const text = await res.text();
                    if (scriptMatchesComponent(text)) {
                        externalScripts.push({
                            type: 'external',
                            src: script.src,
                            content: text.slice(0, 50000), // cap at 50kb per script
                        });
                    }
                } catch (_) { }
            });

        await Promise.all(fetchPromises);

        return {
            inlineHandlers,
            scripts: [...inlineScripts, ...externalScripts],
            fingerprint: {
                ids: Array.from(ids),
                classes: Array.from(classes),
                dataAttrs: Array.from(dataAttrs),
            },
        };
    })(el, allEls);

    // ── Capture the Influence pillar.
    // Influence is external context that shapes how Structure and Behaviour
    // express themselves. The extension is the ONLY place this can be captured
    // accurately — it runs inside the live browser with direct access to the
    // navigator API and screen properties.
    const influence = (function captureInfluence() {
        const ua = navigator.userAgent || '';

        // Parse browser name and version from user agent
        let browserName = 'Unknown';
        let browserVersion = 'Unknown';

        if (/Edg\//.test(ua)) {
            browserName = 'Edge';
            browserVersion = (ua.match(/Edg\/([\d.]+)/) || [])[1] || 'Unknown';
        } else if (/OPR\//.test(ua)) {
            browserName = 'Opera';
            browserVersion = (ua.match(/OPR\/([\d.]+)/) || [])[1] || 'Unknown';
        } else if (/Chrome\//.test(ua)) {
            browserName = 'Chrome';
            browserVersion = (ua.match(/Chrome\/([\d.]+)/) || [])[1] || 'Unknown';
        } else if (/Firefox\//.test(ua)) {
            browserName = 'Firefox';
            browserVersion = (ua.match(/Firefox\/([\d.]+)/) || [])[1] || 'Unknown';
        } else if (/Safari\//.test(ua) && /Version\//.test(ua)) {
            browserName = 'Safari';
            browserVersion = (ua.match(/Version\/([\d.]+)/) || [])[1] || 'Unknown';
        }

        // Parse OS name and version from user agent
        let osName = 'Unknown';
        let osVersion = 'Unknown';

        if (/Windows NT/.test(ua)) {
            osName = 'Windows';
            const ntVersion = (ua.match(/Windows NT ([\d.]+)/) || [])[1] || '';
            const ntMap = { '10.0': '10/11', '6.3': '8.1', '6.2': '8', '6.1': '7' };
            osVersion = ntMap[ntVersion] || ntVersion;
        } else if (/Mac OS X/.test(ua)) {
            osName = 'macOS';
            osVersion = ((ua.match(/Mac OS X ([\d_]+)/) || [])[1] || '').replace(/_/g, '.');
        } else if (/Android/.test(ua)) {
            osName = 'Android';
            osVersion = (ua.match(/Android ([\d.]+)/) || [])[1] || 'Unknown';
        } else if (/iPhone|iPad|iPod/.test(ua)) {
            osName = 'iOS';
            osVersion = ((ua.match(/OS ([\d_]+)/) || [])[1] || '').replace(/_/g, '.');
        } else if (/Linux/.test(ua)) {
            osName = 'Linux';
            osVersion = 'Unknown';
        }

        return {
            browserName,
            browserVersion,
            osName,
            osVersion,
            screenWidth: window.screen.width,
            screenHeight: window.screen.height,
            devicePixelRatio: window.devicePixelRatio || 1,
            viewportWidth: window.innerWidth,
            viewportHeight: window.innerHeight,
            userAgent: ua,
            capturedAt: new Date().toISOString(),
            // Media feature queries — captured at extraction time from the live browser.
            // Dark mode and reduced motion affect rendering decisions at code generation time.
            prefersColorScheme: window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light',
            prefersReducedMotion: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'reduce' : 'no-preference',
        };
    })();

    // ── Capture initial Objective pillar data from the extraction environment.
    // Objective is the only pillar that comes partly from the user (device target,
    // screen size, framework) — those are captured in the popup at conversion time.
    // At extraction time we capture what we can infer: the likely device type
    // from the current viewport, which gives the user a sensible default.
    const objective = (function captureObjective() {
        const vw = window.innerWidth;
        const dpr = window.devicePixelRatio || 1;

        // Infer device type from viewport width at time of extraction
        let deviceTypeInferred;
        if (vw <= 480) {
            deviceTypeInferred = 'mobile';
        } else if (vw <= 1024) {
            deviceTypeInferred = 'tablet';
        } else {
            deviceTypeInferred = 'desktop';
        }

        // Common screen size presets for the popup UI to offer as defaults
        const screenPresets = [
            { label: 'Mobile: iPhone SE', width: 375, device: 'mobile' },
            { label: 'Mobile: iPhone 15', width: 390, device: 'mobile' },
            { label: 'Mobile: iPhone 15 Plus', width: 430, device: 'mobile' },
            { label: 'Tablet: iPad', width: 768, device: 'tablet' },
            { label: 'Tablet: iPad Pro 11"', width: 834, device: 'tablet' },
            { label: 'Tablet: iPad Pro 13"', width: 1024, device: 'tablet' },
            { label: 'Desktop: 1280px', width: 1280, device: 'desktop' },
            { label: 'Desktop: 1440px', width: 1440, device: 'desktop' },
            { label: 'Desktop: 1920px', width: 1920, device: 'desktop' },
            { label: 'All Breakpoints', width: null, device: 'all' },
        ];

        return {
            deviceTypeInferred,              // what we infer from the capture viewport
            deviceTypeSelected: null,      // set by user in popup at conversion time
            screenWidthTarget: null,      // set by user in popup at conversion time
            screenSizeLabel: null,      // set by user in popup at conversion time
            allBreakpoints: false,     // set by user in popup at conversion time
            framework: null,      // set by user in popup at conversion time
            additionalIntent: null,      // optional free-text, set by user
            screenPresets,                   // offered to user as selection options
            captureViewportWidth: vw,        // actual viewport width at extraction time
            captureDevicePixelRatio: dpr,    // actual DPR at extraction time
        };
    })();

    return {
        success: true,
        domSnapshot: { elements: visible, pageWidth: elWidth, pageHeight: elHeight },
        componentCSS,
        componentJS,
        influence,
        objective,
        meta: {
            url: window.location.href,
            title: document.title,
            viewport: { width: window.innerWidth, height: window.innerHeight },
        },
        label: elementInfo.suggested || 'component',
    };
}

// =============================================================================
// EXTRACT SEGMENT BEHAVIOUR
// Finds the stamped element by segmentId and extracts its CSS rules and JS
// references from the live DOM. Same logic as extractElement's CSS/JS blocks
// but scoped to the segment element — no DOM expansion needed.
// =============================================================================
// extractHiddenForSegment — returns hidden interactive components structurally
// anchored to the given segment (nav dropdowns, drawers, modals that belong to
// this visible component). CSS for these is already captured globally by
// extractSegmentBehaviour — so only Structure (element positions, text, sizes)
// is needed here. ConteX Law Pillar 1: Structure must be complete.
async function extractHiddenForSegment(segmentId) {
    if (!segmentId) return { success: true, hiddenComponents: [] };

    const segmentEl = document.querySelector(`[data-segment-id="${segmentId}"]`);
    if (!segmentEl) return { success: true, hiddenComponents: [] };

    const segmentRect = segmentEl.getBoundingClientRect();

    // Find all hidden roots that are:
    //   A) descendants of the segment element, OR
    //   B) positioned elements whose top-left falls within the segment's bounding box
    //      (dropdowns, popovers that are portalled out of the DOM hierarchy)
    const hiddenRoots = [];
    const visited = new Set();

    function findInSubtree(el) {
        if (!el || el.nodeType !== 1) return;
        const tag = (el.tagName || '').toLowerCase();
        if (['script', 'noscript', 'style', 'head', 'meta', 'link'].includes(tag)) return;
        const cs = window.getComputedStyle(el);
        const isHidden = cs.display === 'none' || el.hasAttribute('hidden');
        if (isHidden && !visited.has(el)) {
            const rootText = (el.innerText || el.textContent || '').trim();
            const rootChildren = el.querySelectorAll ? el.querySelectorAll('*').length : 0;
            if (rootText || rootChildren >= 2) {
                visited.add(el);
                hiddenRoots.push(el);
                return; // don't recurse into captured root
            }
        }
        for (let i = 0; i < el.children.length; i++) {
            if (!isHidden) findInSubtree(el.children[i]);
        }
    }

    // Walk the segment subtree first
    findInSubtree(segmentEl);

    // Also scan document body for portalled elements (fixed/absolute positioned)
    // whose bounding rect overlaps the segment
    if (segmentRect.width > 0 && segmentRect.height > 0) {
        const allHidden = Array.from(document.body.querySelectorAll('*')).filter(el => {
            if (visited.has(el)) return false;
            const cs = window.getComputedStyle(el);
            const isHidden = cs.display === 'none' || el.hasAttribute('hidden');
            if (!isHidden) return false;
            const pos = cs.position;
            if (pos !== 'fixed' && pos !== 'absolute') return false;
            // Use data-segment-id heuristic — hidden roots stamped during serializeHiddenComponents
            return !!el.dataset.segmentId;
        });
        for (const el of allHidden) {
            if (visited.has(el)) continue;
            const rootText = (el.innerText || el.textContent || '').trim();
            const rootChildren = el.querySelectorAll ? el.querySelectorAll('*').length : 0;
            if (rootText || rootChildren >= 2) {
                visited.add(el);
                hiddenRoots.push(el);
            }
        }
    }

    if (hiddenRoots.length === 0) return { success: true, hiddenComponents: [] };

    // Serialize each hidden root into the same structure as serializeHiddenComponents()
    // so the server's loadHiddenComponents() and buildFullBehaviourSkeleton() work unchanged.
    //
    // ConteX Law — Structure Pillar: SVG sprite icons inside hidden panels MUST be
    // resolved from the live browser sprite cache. An empty Map() causes every
    // <use xlink:href="sprite.svg#icon-name"> lookup to fail, producing placeholder
    // rectangles instead of real icons in the AI's Structure source of truth.
    // prefetchSVGSprites() scoped to the segment element loads all sprites referenced
    // by any element inside the segment — instant from the browser's HTTP cache.
    const svgSymbolCache = await prefetchSVGSprites(segmentEl);
    const components = serializeHiddenComponents.__fromRoots(hiddenRoots, svgSymbolCache);

    return { success: true, hiddenComponents: components };
}


async function extractSegmentBehaviour(segmentId) {
    if (!segmentId) throw new Error('segmentId is required.');

    const el = document.querySelector(`[data-segment-id="${segmentId}"]`);
    if (!el) throw new Error(`No element found with data-segment-id="${segmentId}"`);

    const allEls = [el, ...el.querySelectorAll('*')];

    // ── Extract CSS rules scoped to this element ──────────────────────────────
    const componentCSS = (function extractCSS(rootEl) {
        const elements = [rootEl, ...rootEl.querySelectorAll('*')];
        const elementSet = new Set(elements);
        const matchedRules = [];
        const behaviorRules = [];
        const mediaRules = [];
        const keyframes = [];
        const usedAnimations = new Set();

        for (const el of elements) {
            const cs = window.getComputedStyle(el);
            const anim = cs.animationName;
            if (anim && anim !== 'none') anim.split(',').forEach(a => usedAnimations.add(a.trim()));
        }

        // ── Helpers (self-contained in this closure) ─────────────────────────
        const _PSEUDO_RE = /:{1,2}(hover|focus|focus-within|focus-visible|active|visited|checked|disabled|enabled|placeholder|before|after|selection|marker|backdrop|nth-|first-child|last-child|first-of-type|last-of-type)/i;
        const _GLOBAL_SELECTORS = /^(\*|html|body|:root)(\s*,\s*(\*|html|body|:root))*$/i;
        function _stripPseudos(sel) {
            return sel
                .replace(/:{1,2}(hover|focus|focus-within|focus-visible|active|visited|checked|disabled|enabled|placeholder-shown|placeholder|before|after|selection|marker|backdrop|first-child|last-child|first-of-type|last-of-type|only-child|only-of-type|empty|root|target|not\([^)]*\)|nth-child\([^)]*\)|nth-of-type\([^)]*\)|is\([^)]*\)|where\([^)]*\)|has\([^)]*\))/gi, '')
                .trim();
        }
        function _matches(sel) {
            const base = _stripPseudos(sel);
            if (!base || _GLOBAL_SELECTORS.test(base)) return true;
            try {
                const matched = rootEl.querySelectorAll(base);
                for (const m of matched) { if (elementSet.has(m)) return true; }
                if (rootEl.matches && rootEl.matches(base)) return true;
            } catch (_) {
                try {
                    const classMatches = base.match(/\.([\w\-:./]+)/g);
                    if (classMatches) {
                        const classNames = classMatches.map(c => c.slice(1));
                        for (const el of elementSet) {
                            if (typeof el.className === 'string') {
                                const elClasses = el.className.split(/\s+/);
                                if (classNames.some(cn => elClasses.includes(cn))) return true;
                            }
                        }
                    }
                } catch (_2) { }
            }
            if (/^(body|html)[\s:[>+~]/.test(base)) {
                try {
                    const docMatched = document.querySelectorAll(base);
                    for (const m of docMatched) { if (rootEl.contains(m)) return true; }
                } catch (_3) { }
            }
            return false;
        }

        function processRule(rule, mediaContext) {
            if (rule instanceof CSSStyleRule) {
                const selector = rule.selectorText || '';
                if (!_matches(selector)) return;
                // Resolve all var(--custom-property) references to their actual computed values.
                // Read from el first — captures component-scoped overrides (e.g. dark header theme).
                // Fall back to document.documentElement for global :root definitions.
                const resolvedCSSText = (() => {
                    const elStyle = getComputedStyle(el);
                    const docStyle = getComputedStyle(document.documentElement);
                    return (rule.cssText || '').replace(/var\(\s*(--[a-zA-Z0-9_-]+)\s*(?:,[^)]+)?\)/g, (match, prop) => {
                        const val = elStyle.getPropertyValue(prop).trim() || docStyle.getPropertyValue(prop).trim();
                        return val || match;
                    });
                })();
                const isBehavioral = _PSEUDO_RE.test(selector);
                const entry = { selector, cssText: resolvedCSSText, mediaContext: mediaContext || null };
                if (isBehavioral) behaviorRules.push(entry); else matchedRules.push(entry);
            }
            if (rule instanceof CSSMediaRule) {
                const mediaQuery = rule.conditionText || rule.media?.mediaText || '';
                const childMatches = [];
                for (const childRule of rule.cssRules) {
                    if (childRule instanceof CSSStyleRule) {
                        if (_matches(childRule.selectorText || '')) {
                            const elStyle = getComputedStyle(el);
                            const docStyle = getComputedStyle(document.documentElement);
                            const resolvedChild = (childRule.cssText || '').replace(/var\(\s*(--[a-zA-Z0-9_-]+)\s*(?:,[^)]+)?\)/g, (match, prop) => {
                                const val = elStyle.getPropertyValue(prop).trim() || docStyle.getPropertyValue(prop).trim();
                                return val || match;
                            });
                            childMatches.push(resolvedChild);
                        }
                    } else {
                        processRule(childRule, mediaContext);
                    }
                }
                if (childMatches.length > 0) mediaRules.push({ mediaQuery, rules: childMatches });
            }
            if (typeof CSSSupportsRule !== 'undefined' && rule instanceof CSSSupportsRule) {
                for (const child of rule.cssRules) processRule(child, mediaContext);
            }
            if (typeof CSSLayerBlockRule !== 'undefined' && rule instanceof CSSLayerBlockRule) {
                for (const child of rule.cssRules) processRule(child, mediaContext);
            }
            if (typeof CSSContainerRule !== 'undefined' && rule instanceof CSSContainerRule) {
                for (const child of rule.cssRules) processRule(child, mediaContext);
            }
            if (rule instanceof CSSKeyframesRule) {
                const name = rule.name;
                if (usedAnimations.has(name) ||
                    matchedRules.some(r => r.cssText.includes(name)) ||
                    behaviorRules.some(r => r.cssText.includes(name))) {
                    keyframes.push(rule.cssText);
                }
            }
        }

        for (const sheet of document.styleSheets) {
            let rules;
            try { rules = sheet.cssRules; } catch { continue; }
            for (const rule of rules) processRule(rule, null);
        }

        return { matched: matchedRules, behavior: behaviorRules, media: mediaRules, keyframes };
    })(el);

    // ── Extract JS scoped to this element's identifiers ───────────────────────
    const componentJS = await (async function extractJS(rootEl, elements) {
        const ids = new Set();
        const classes = new Set();
        const dataAttrs = new Set();

        for (const el of elements) {
            if (el.id) ids.add(el.id);
            if (typeof el.className === 'string') el.className.split(/\s+/).filter(Boolean).forEach(c => classes.add(c));
            if (el.dataset) Object.keys(el.dataset).forEach(k => dataAttrs.add('data-' + k.replace(/([A-Z])/g, '-$1').toLowerCase()));
        }

        const searchTerms = [
            ...Array.from(ids).map(id => [`#${id}`, `getElementById('${id}')`, `getElementById("${id}")`, `querySelector('#${id}')`, `querySelector("#${id}")`]),
            ...Array.from(classes).map(c => [`.${c}`, `getElementsByClassName('${c}')`, `querySelector('.${c}')`, `querySelector(".${c}")`]),
            ...Array.from(dataAttrs).map(d => [`[${d}]`, `getAttribute('${d}')`, `getAttribute("${d}")`]),
        ].flat().filter(Boolean);

        if (searchTerms.length === 0) return { inlineHandlers: [], scripts: [], fingerprint: { ids: [], classes: [], dataAttrs: [] } };

        function scriptMatchesComponent(src) { return searchTerms.some(t => src.includes(t)); }

        const HANDLERS = ['onclick', 'onmouseover', 'onmouseout', 'onmouseenter', 'onmouseleave',
            'onfocus', 'onblur', 'onchange', 'oninput', 'onkeydown', 'onkeyup', 'onsubmit', 'ontoggle'];
        const inlineHandlers = [];
        for (const el of elements) {
            for (const h of HANDLERS) {
                const val = el.getAttribute(h);
                if (val) inlineHandlers.push({ element: el.tagName.toLowerCase(), handler: h, code: val });
            }
        }

        const inlineScripts = [];
        for (const script of document.querySelectorAll('script:not([src])')) {
            const src = script.textContent || '';
            if (src.trim().length === 0) continue;
            if (scriptMatchesComponent(src)) inlineScripts.push({ type: 'inline', content: src.trim().slice(0, 50000) });
        }

        // Read external scripts from browser cache — no network, no timeout, instant.
        // The page has been fully scrolled so all scripts are already cached.
        // cache:'only-if-cached' + mode:'same-origin' fails immediately if not cached.
        const SKIP_EXT = /google|analytics|gtag|facebook|twitter|hotjar|intercom|zendesk|cdn\.jsdelivr|cloudflare\/ajax/i;
        const externalScripts = [];
        const _extScripts = Array.from(document.querySelectorAll('script[src]'))
            .filter(sc => !SKIP_EXT.test(sc.src))
            .slice(0, 10);
        for (const sc of _extScripts) {
            try {
                const _res = await fetch(sc.src, { cache: 'only-if-cached', mode: 'same-origin' });
                if (!_res.ok) continue;
                const _text = await _res.text();
                if (scriptMatchesComponent(_text)) {
                    externalScripts.push({ type: 'external', src: sc.src, content: _text.slice(0, 50000) });
                }
            } catch (_) {
                // Not in cache or cross-origin — skip silently
            }
        }

        return {
            inlineHandlers,
            scripts: [...inlineScripts, ...externalScripts],
            externalScriptUrls: [],
            fingerprint: { ids: Array.from(ids), classes: Array.from(classes), dataAttrs: Array.from(dataAttrs) },
        };
    })(el, allEls);

    // ── ConteX Law: Influence Pillar — captured here at extraction time.
    // The extension is the only place that has direct access to the live browser
    // environment: viewport dimensions, DPR, user agent, media feature queries.
    // This must travel with componentCSS/componentJS to the server so the AI
    // receives the exact capture context — never a stale page-level copy.
    const influence = (function captureInfluence() {
        const ua = navigator.userAgent || '';
        let browserName = 'Unknown', browserVersion = 'Unknown';
        if (/Edg\//.test(ua)) { browserName = 'Edge'; browserVersion = (ua.match(/Edg\/([\d.]+)/) || [])[1] || 'Unknown'; }
        else if (/OPR\//.test(ua)) { browserName = 'Opera'; browserVersion = (ua.match(/OPR\/([\d.]+)/) || [])[1] || 'Unknown'; }
        else if (/Chrome\//.test(ua)) { browserName = 'Chrome'; browserVersion = (ua.match(/Chrome\/([\d.]+)/) || [])[1] || 'Unknown'; }
        else if (/Firefox\//.test(ua)) { browserName = 'Firefox'; browserVersion = (ua.match(/Firefox\/([\d.]+)/) || [])[1] || 'Unknown'; }
        else if (/Safari\//.test(ua) && /Version\//.test(ua)) { browserName = 'Safari'; browserVersion = (ua.match(/Version\/([\d.]+)/) || [])[1] || 'Unknown'; }

        let osName = 'Unknown', osVersion = 'Unknown';
        if (/Windows NT ([\d.]+)/.test(ua)) { osName = 'Windows'; osVersion = ua.match(/Windows NT ([\d.]+)/)[1]; }
        else if (/Mac OS X ([\d_]+)/.test(ua)) { osName = 'macOS'; osVersion = ua.match(/Mac OS X ([\d_]+)/)[1].replace(/_/g, '.'); }
        else if (/Linux/.test(ua)) { osName = 'Linux'; osVersion = 'Unknown'; }
        else if (/Android ([\d.]+)/.test(ua)) { osName = 'Android'; osVersion = ua.match(/Android ([\d.]+)/)[1]; }
        else if (/iPhone OS ([\d_]+)/.test(ua)) { osName = 'iOS'; osVersion = ua.match(/iPhone OS ([\d_]+)/)[1].replace(/_/g, '.'); }
        else if (/iPad.*OS ([\d_]+)/.test(ua)) { osName = 'iPadOS'; osVersion = ua.match(/OS ([\d_]+)/)[1].replace(/_/g, '.'); }

        return {
            browserName, browserVersion, osName, osVersion,
            screenWidth: window.screen.width,
            screenHeight: window.screen.height,
            devicePixelRatio: window.devicePixelRatio || 1,
            viewportWidth: window.innerWidth,
            viewportHeight: window.innerHeight,
            userAgent: ua,
            capturedAt: new Date().toISOString(),
            prefersColorScheme: window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light',
            prefersReducedMotion: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'reduce' : 'no-preference',
        };
    })();

    return { success: true, componentCSS, componentJS, influence };
}