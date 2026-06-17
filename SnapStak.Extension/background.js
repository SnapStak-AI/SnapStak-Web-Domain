'use strict';

const WASM_BASE    = 'http://localhost:5174';
const SNAPSHOT_URL = `${WASM_BASE}/api/snapshot`;
const ENGINE_URL   = `${WASM_BASE}/engine/content.js`;

// ── Message listener ──────────────────────────────────────────────────────────
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (message.type === 'CONTENT_READY') {
        if (sender.tab?.id) {
            markTabReady(sender.tab.id);
        }
        sendResponse({ ok: true });
        return true;
    }

    handleMessage(message)
        .then(sendResponse)
        .catch(err => sendResponse({ success: false, error: err.message }));
    return true;
});

chrome.tabs.onRemoved.addListener(tabId => clearTabReady(tabId));

// ── Tab ready state — persisted in session storage (survives SW sleep) ────────
async function isTabReady(tabId) {
    const result = await chrome.storage.session.get(`ready_${tabId}`);
    return !!result[`ready_${tabId}`];
}

async function markTabReady(tabId) {
    await chrome.storage.session.set({ [`ready_${tabId}`]: true });
}

async function clearTabReady(tabId) {
    await chrome.storage.session.remove(`ready_${tabId}`);
}

// ── Message handler ───────────────────────────────────────────────────────────
async function handleMessage(message) {
    switch (message.type) {
        case 'EXTRACT_PAGE_RESPONSIVE': {
            const tab = await getActiveTab();
            return await extractPageResponsive(tab.id);
        }
        case 'START_SELECT': {
            const tab = await getActiveTab();
            await ensureEngineLoaded(tab.id);
            return await sendToContent(tab.id, { type: 'START_COMPONENT_SELECT' });
        }
        case 'PING_WASM':
            return await pingWasm();
        default:
            throw new Error(`Unknown message: ${message.type}`);
    }
}

// ── Ensure content.js is injected ─────────────────────────────────────────────
async function ensureEngineLoaded(tabId) {
    if (await isTabReady(tabId)) return;

    // Fetch content.js from WASM server
    const res = await fetch(ENGINE_URL);
    if (!res.ok) throw new Error(`Cannot reach SnapStak at ${ENGINE_URL}`);
    const source = await res.text();

    // Post SNAPSTAK_INJECT to bridge.js via executeScript in isolated world
    await chrome.scripting.executeScript({
        target: { tabId },
        func: (src) => {
            window.postMessage({ type: 'SNAPSTAK_INJECT', source: src }, '*');
        },
        args: [source],
        world: 'ISOLATED',
    });

    // Wait up to 5 seconds for bridge.js to confirm content.js is ready
    await waitForTabReady(tabId, 5000);
}

function waitForTabReady(tabId, timeoutMs) {
    return new Promise((resolve, reject) => {
        const deadline = setTimeout(() =>
            reject(new Error('content.js load timeout')), timeoutMs);

        const interval = setInterval(async () => {
            if (await isTabReady(tabId)) {
                clearTimeout(deadline);
                clearInterval(interval);
                resolve();
            }
        }, 150);
    });
}

// ── Send command to content.js via bridge.js ──────────────────────────────────
async function sendToContent(tabId, message) {
    return await chrome.tabs.sendMessage(tabId, {
        __snapstak_cmd: true,
        ...message,
    });
}

// ── Full page extraction ──────────────────────────────────────────────────────
async function extractPageResponsive(tabId) {
    await ensureEngineLoaded(tabId);

    const desktopResult = await sendToContent(tabId, {
        type: 'EXTRACT_PAGE', mode: 'visible', skipImageWait: false,
    });
    if (!desktopResult?.success) throw new Error(desktopResult?.error || 'Extraction failed.');

    let mobileWidth = 390;
    try {
        const bp = await sendToContent(tabId, { type: 'DISCOVER_BREAKPOINTS' });
        const primaryWidth = desktopResult.influence?.viewportWidth || 1440;
        const below = (bp?.breakpoints || []).filter(w => w < primaryWidth);
        if (below.length > 0) mobileWidth = below[0];
    } catch (_) {}

    let mobileSnapshot = null;
    try {
        mobileSnapshot = await captureMobileSnapshot(tabId, mobileWidth);
    } catch (err) {
        console.warn('[SnapStak] Mobile capture failed:', err.message);
    }

    // Generate componentId from hostname + timestamp (content.js does not set it)
    const _host = (() => {
        try { return new URL(desktopResult.meta?.url || '').hostname.replace(/^www\./, ''); } catch { return 'unknown'; }
    })();
    const _ts   = Math.floor(Date.now() / 1000);
    const _generatedId = `${_host}_${_ts}`;

    const payload = {
        componentId:       _generatedId,
        url:               desktopResult.meta?.url || desktopResult.url,
        domSnapshot:       desktopResult.domSnapshot,
        domSnapshotHidden: desktopResult.domSnapshotHidden  || null,
        hiddenComponents:  desktopResult.hiddenComponents   || [],
        componentCSS:      desktopResult.componentCSS       || null,
        componentJS:       desktopResult.componentJS        || null,
        influence:         desktopResult.influence          || null,
        objective:         desktopResult.objective          || null,
        viewportSnapshots: mobileSnapshot ? [mobileSnapshot] : [],
        client:            'chrome',
    };

    await forwardToWasm(payload);
    return {
        success:     true,
        componentId: payload.componentId,
        objectCount: desktopResult.domSnapshot?.elements?.length || 0,
        url:         payload.url,
    };
}

async function forwardToWasm(payload) {
    const res = await fetch(SNAPSHOT_URL, {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify(payload),
    });
    if (!res.ok) throw new Error(`WASM error: ${await res.text()}`);
}

// ── Mobile CDP capture ────────────────────────────────────────────────────────
async function captureMobileSnapshot(tabId, mobileWidth) {
    const debuggee = { tabId };
    await new Promise((res, rej) =>
        chrome.debugger.attach(debuggee, '1.3', () =>
            chrome.runtime.lastError ? rej(new Error(chrome.runtime.lastError.message)) : res()));
    try {
        await new Promise((res, rej) =>
            chrome.debugger.sendCommand(debuggee, 'Emulation.setDeviceMetricsOverride',
                { width: mobileWidth, height: 844, deviceScaleFactor: 3, mobile: true },
                () => chrome.runtime.lastError ? rej(new Error(chrome.runtime.lastError.message)) : res()));
        await new Promise(r => setTimeout(r, 800));
        const result = await sendToContent(tabId, { type: 'EXTRACT_MOBILE' });
        if (!result?.success) throw new Error(result?.error || 'Mobile extraction failed.');
        return {
            viewportWidth:    mobileWidth,
            deviceType:       'mobile',
            domSnapshot:      result.domSnapshot,
            hiddenComponents: result.hiddenComponents || [],
            componentCSS:     result.componentCSS     || null,
            componentJS:      result.componentJS      || null,
        };
    } finally {
        await new Promise(r => chrome.debugger.sendCommand(debuggee, 'Emulation.clearDeviceMetricsOverride', {}, r)).catch(() => {});
        await new Promise(r => chrome.debugger.detach(debuggee, r)).catch(() => {});
    }
}

async function getActiveTab() {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab) throw new Error('No active tab found.');
    return tab;
}

async function pingWasm() {
    try {
        const res = await fetch(`${WASM_BASE}/health`, { signal: AbortSignal.timeout(3000) });
        return { online: res.ok };
    } catch {
        return { online: false };
    }
}

// ── Debug: verify declarativeNetRequest rules are loaded ──────────────────────
chrome.declarativeNetRequest.getDynamicRules(rules => {
    console.log('[SnapStak] Dynamic rules:', rules.length);
});
chrome.declarativeNetRequest.getEnabledRulesets().then(rulesets => {
    console.log('[SnapStak] Enabled rulesets:', rulesets);
});