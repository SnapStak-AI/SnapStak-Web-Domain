'use strict';

const WASM_ORIGIN = 'http://localhost:5174';
const pending     = new Map();

window.addEventListener('message', (event) => {
    const data = event.data;
    if (!data || typeof data !== 'object') return;

    // ── SNAPSTAK_READY? from WASM app ─────────────────────────────────────────
    if (data.type === 'SNAPSTAK_READY?' && event.origin === WASM_ORIGIN) {
        console.log('[SnapStak bridge] SNAPSTAK_READY? received — replying BRIDGE_READY');
        window.postMessage({ type: 'SNAPSTAK_BRIDGE_READY' }, WASM_ORIGIN);
        return;
    }

    // ── SNAPSTAK_INJECT from background.js (via executeScript) ───────────────
    if (data.type === 'SNAPSTAK_INJECT' && data.source) {
        // Guard against double injection — ignore if already injecting
        if (window.__snapstak_injecting__) return;
        window.__snapstak_injecting__ = true;
        console.log('[SnapStak bridge] SNAPSTAK_INJECT received — injecting engine...');
        injectScript(data.source);
        return;
    }

    // ── Response from content.js → forward to background.js ──────────────────
    if (data.__snapstak_response === true) {
        const p = pending.get(data.requestId);
        if (p) {
            clearTimeout(p.timeout);
            pending.delete(data.requestId);
            p.resolve(data.result);
        }
        return;
    }

    // ── content.js signals it is ready ────────────────────────────────────────
    if (data.__snapstak_ready === true) {
        window.__snapstak_injecting__ = false;
        console.log('[SnapStak bridge] content.js ready — sending CONTENT_READY to background');
        chrome.runtime.sendMessage({ type: 'CONTENT_READY' })
            .then(() => console.log('[SnapStak bridge] CONTENT_READY acknowledged'))
            .catch(err => console.error('[SnapStak bridge] CONTENT_READY send failed:', err.message));
        return;
    }
});

// ── Commands from background.js → forward to content.js ──────────────────────
chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (!message.__snapstak_cmd) return false;
    console.log('[SnapStak bridge] Command received from background:', message.type);
    sendCommandToContent(message)
        .then(sendResponse)
        .catch(err => sendResponse({ success: false, error: err.message }));
    return true;
});

const ENGINE_SRC = 'http://localhost:5174/engine/content.js';

// ── Inject script into MAIN world ─────────────────────────────────────────────
// Strategy 1 — src= pointing to localhost:5174
// Most pages allow http://localhost:* in their CSP.
// Strategy 2 — inline textContent fallback for pages without strict CSP.

function injectScript(source) {
    const old = document.getElementById('__snapstak_engine__');
    if (old) old.remove();

    const script  = document.createElement('script');
    script.id     = '__snapstak_engine__';
    script.src    = ENGINE_SRC;
    script.onload = () => {
        console.log('[SnapStak bridge] Engine loaded via src=');
        script.remove();
    };
    script.onerror = () => {
        console.warn('[SnapStak bridge] src= blocked — trying inline');
        script.remove();
        injectInline(source);
    };
    (document.head || document.documentElement).appendChild(script);
}

function injectInline(source) {
    const script       = document.createElement('script');
    script.id          = '__snapstak_engine__';
    script.textContent = source;
    (document.head || document.documentElement).appendChild(script);
    script.remove();
    console.log('[SnapStak bridge] Engine injected inline');
}

// ── Send command to content.js and return a Promise ───────────────────────────
function sendCommandToContent(message) {
    return new Promise((resolve, reject) => {
        const requestId = `${Date.now()}_${Math.random().toString(36).slice(2)}`;

        const timeout = setTimeout(() => {
            pending.delete(requestId);
            reject(new Error(`content.js timed out on: ${message.type}`));
        }, 30000);

        pending.set(requestId, { resolve, reject, timeout });

        window.postMessage({ __snapstak: true, requestId, ...message }, '*');
    });
}