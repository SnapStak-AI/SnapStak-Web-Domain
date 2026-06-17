// SnapStakCrypto.js — AES-GCM 256-bit encryption for SnapStak pillar data.
// Called by PillarEncryption.cs via Blazor IJSRuntime.
// Dev mode is handled entirely in C# ("plain:" prefix) — this file is only
// called when encryption is enabled (production builds).
//
// Key derivation: PBKDF2(subscriptionToken, deviceSalt, 100000, SHA-256) → AES-GCM-256
// Storage format: Base64( IV[12] || Ciphertext+AuthTag )
// Salt: random 16 bytes generated on first run, persisted in localStorage.
(function () {
    'use strict';
    const SALT_KEY = 'snapstak_crypto_salt';
    const ITER = 100000;
    let _key = null;

    function getSalt() {
        const s = localStorage.getItem(SALT_KEY);
        if (s) return b64ToBytes(s);
        const salt = crypto.getRandomValues(new Uint8Array(16));
        localStorage.setItem(SALT_KEY, bytesToB64(salt));
        return salt;
    }

    async function initKey(token) {
        const enc = new TextEncoder();
        const km = await crypto.subtle.importKey('raw', enc.encode(token), 'PBKDF2', false, ['deriveKey']);
        _key = await crypto.subtle.deriveKey(
            { name: 'PBKDF2', salt: getSalt(), iterations: ITER, hash: 'SHA-256' },
            km, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    }

    async function encrypt(plaintext) {
        if (!_key) throw new Error('[SnapStakCrypto] Not initialised.');
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const ct = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, _key, new TextEncoder().encode(plaintext));
        const buf = new Uint8Array(12 + ct.byteLength);
        buf.set(iv); buf.set(new Uint8Array(ct), 12);
        return bytesToB64(buf);
    }

    async function decrypt(stored) {
        if (!_key) throw new Error('[SnapStakCrypto] Not initialised.');
        const buf = b64ToBytes(stored);
        const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: buf.slice(0, 12) }, _key, buf.slice(12));
        return new TextDecoder().decode(plain);
    }

    // ── Encrypted localStorage helpers ────────────────────────────────────────
    // Used for sensitive values: api key, sub token, licence valid, sub status.
    // Values are stored as "enc:<base64>" so we can detect unencrypted legacy
    // values and return them safely rather than crashing.

    const ENC_PREFIX = 'enc:';

    async function setEncrypted(key, value) {
        if (!_key) throw new Error('[SnapStakCrypto] Not initialised — cannot encrypt localStorage value.');
        const ct = await encrypt(value);
        localStorage.setItem(key, ENC_PREFIX + ct);
    }

    async function getEncrypted(key) {
        const stored = localStorage.getItem(key);
        if (!stored) return null;
        // Legacy plain value — return as-is so old installs don't break on upgrade
        if (!stored.startsWith(ENC_PREFIX)) return stored;
        if (!_key) throw new Error('[SnapStakCrypto] Not initialised — cannot decrypt localStorage value.');
        return await decrypt(stored.slice(ENC_PREFIX.length));
    }

    // ── Decrypt engine ────────────────────────────────────────────────────────
    // Decrypts content.enc in memory and returns the JavaScript source string.
    // Format: [12 bytes IV][ciphertext + 16 bytes GCM auth tag]
    // Called by SnapStakBridge.js before injecting content.js into the page.
    // The decrypted source is never written to disk.

    async function decryptEngine(encryptedBytes) {
        if (!_key) throw new Error('[SnapStakCrypto] Not initialised — cannot decrypt engine.');
        const buf = encryptedBytes instanceof Uint8Array ? encryptedBytes : new Uint8Array(encryptedBytes);
        const iv = buf.slice(0, 12);
        const data = buf.slice(12); // ciphertext + auth tag (auth tag is last 16 bytes)
        const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv }, _key, data);
        return new TextDecoder().decode(plain);
    }

    function bytesToB64(b) { return btoa(String.fromCharCode(...b)); }
    function b64ToBytes(s) { return Uint8Array.from(atob(s), c => c.charCodeAt(0)); }

    window.__snapstak_crypto = { initKey, encrypt, decrypt, setEncrypted, getEncrypted, decryptEngine };
})();