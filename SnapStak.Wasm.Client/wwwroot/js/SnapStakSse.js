'use strict';

// ── SSE connection for SnapshotBridgeService ──────────────────────────────────
// Called from SnapshotBridgeService.ConnectAsync via JS interop.
// Uses the browser native EventSource API — HttpClient streaming does not
// work reliably in Blazor WASM.

(function () {
    let _sse = null;

    window.__snapstak_sse_connect = function (url, dotNetRef) {
        if (_sse) { _sse.close(); _sse = null; }
        _sse = new EventSource(url);
        _sse.onmessage = function (e) {
            if (e.data && e.data !== ': keepalive') {
                dotNetRef.invokeMethodAsync('OnSseMessage', e.data);
            }
        };
        _sse.onerror = function () {
            if (_sse) { _sse.close(); _sse = null; }
            setTimeout(() => window.__snapstak_sse_connect(url, dotNetRef), 2000);
        };
        console.log('[SnapStakSse] EventSource connected to', url);
    };

    window.__snapstak_sse_disconnect = function () {
        if (_sse) { _sse.close(); _sse = null; }
    };
})();
