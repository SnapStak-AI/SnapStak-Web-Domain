// SnapStakBridge.js
// WASM app side of the SnapStak bridge protocol.
//
// Dev mode  (encryption disabled):
//   Fetches content.js plaintext → sends to bridge.js
//
// Prod mode (encryption enabled):
//   Fetches content.enc (encrypted binary)
//   Decrypts in memory using AES-256-GCM via Web Crypto API
//   Sends decrypted source to bridge.js
//   Decrypted source never written to disk

(function () {
    'use strict';

    const BRIDGE_READY_TIMEOUT = 3000;
    let _engineSource = null;

    // ── Init ──────────────────────────────────────────────────────────────────
    // Loads and decrypts content.enc (or content.js in dev mode) into memory.
    // Called once at app startup.

    async function init(isEncrypted) {
        try {
            if (isEncrypted) {
                // Production — fetch encrypted binary and decrypt in memory
                const res = await fetch('/engine/content.enc');
                if (!res.ok) throw new Error('Could not load engine.');
                const buf = new Uint8Array(await res.arrayBuffer());

                // Decrypt using the same AES key as IndexedDB
                _engineSource = await window.__snapstak_crypto.decryptEngine(buf);
                console.log('[SnapStakBridge] Engine decrypted — ready.');
            } else {
                // Development — fetch plaintext directly
                const res = await fetch('/engine/content.js');
                if (!res.ok) throw new Error('Could not load engine.');
                _engineSource = await res.text();
                console.log('[SnapStakBridge] Engine loaded (dev mode) — ready.');
            }
        } catch (err) {
            console.error('[SnapStakBridge] Init failed:', err.message);
            _engineSource = null;
        }
    }

    // ── Probe ─────────────────────────────────────────────────────────────────
    // Detects whether bridge.js is active in the current tab.

    function probe() {
        return new Promise((resolve) => {
            const timer = setTimeout(() => {
                window.removeEventListener('message', handler);
                resolve(false);
            }, BRIDGE_READY_TIMEOUT);

            function handler(event) {
                if (event.data?.type === 'SNAPSTAK_BRIDGE_READY') {
                    clearTimeout(timer);
                    window.removeEventListener('message', handler);
                    resolve(true);
                }
            }

            window.addEventListener('message', handler);
            window.postMessage({ type: 'SNAPSTAK_READY?' }, '*');
        });
    }

    // ── Inject ────────────────────────────────────────────────────────────────
    // Sends decrypted engine source to bridge.js for injection.
    // The source is never written to disk — memory only.

    async function inject(isEncrypted) {
        // Load engine if not already loaded
        if (!_engineSource) {
            await init(isEncrypted);
            if (!_engineSource) {
                throw new Error('Engine not available. Check server is running.');
            }
        }

        // Check extension is present
        const bridgePresent = await probe();
        if (!bridgePresent) {
            throw new Error('SnapStak extension not detected. Please install the extension.');
        }

        // Send decrypted source to bridge.js — memory to memory, never disk
        window.postMessage({
            type: 'SNAPSTAK_INJECT',
            source: _engineSource,
        }, '*');

        return true;
    }

    // ── Public API ────────────────────────────────────────────────────────────
    window.__snapstak_bridge = { init, probe, inject };
})();

// SSE is handled by SnapStakSse.js (loaded separately in index.html).