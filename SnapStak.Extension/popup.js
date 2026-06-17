// popup.js — SnapStak Extension Popup
// All CON10X logic lives in the WASM app at http://localhost:5174
'use strict';

const dot          = document.getElementById('dot');
const statusLabel  = document.getElementById('statusLabel');
const pageUrlEl    = document.getElementById('pageUrl');
const noticeArea   = document.getElementById('noticeArea');
const btnDeconstruct = document.getElementById('btnDeconstruct');
const btnSelect    = document.getElementById('btnSelect');

(async function init() {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (tab?.url) pageUrlEl.textContent = tab.url;

    const { online } = await bg('PING_WASM');

    if (online) {
        dot.className        = 'dot on';
        statusLabel.textContent = 'Engine running';
        btnDeconstruct.disabled = false;
        btnSelect.disabled      = false;
    } else {
        dot.className        = 'dot off';
        statusLabel.textContent = 'Engine offline';
        showNotice(
            'CON10X engine is not running.<br>' +
            'Start it with <code style="color:#faa;">dotnet run</code> in SnapStak.Wasm.Server/',
            'warn');
    }
})();

btnDeconstruct.addEventListener('click', async () => {
    setWorking(btnDeconstruct, true);
    clearNotice();
    try {
        const result = await bg('EXTRACT_PAGE_RESPONSIVE');
        if (result.success) {
            showNotice(
                `Snapshot sent — <strong style="color:#6fc;">${result.objectCount}</strong> elements captured.<br>` +
                `The CON10X engine is ready to generate code.`,
                'ok');
            setTimeout(() => chrome.tabs.create({ url: 'http://localhost:5174' }), 800);
        } else {
            showNotice(result.error || 'Extraction failed.', 'err');
        }
    } catch (err) {
        showNotice(err.message, 'err');
    } finally {
        setWorking(btnDeconstruct, false);
    }
});

btnSelect.addEventListener('click', async () => {
    try {
        await bg('START_SELECT');
        window.close();
    } catch (err) {
        showNotice(err.message, 'err');
    }
});

function bg(type, data = {}) {
    return chrome.runtime.sendMessage({ type, ...data });
}

function showNotice(html, type) {
    noticeArea.innerHTML = `<div class="notice n-${type}">${html}</div>`;
}

function clearNotice() {
    noticeArea.innerHTML = '';
}

function setWorking(btn, working) {
    btn.disabled = working;
    btn.classList.toggle('spinning', working);
}
