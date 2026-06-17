#!/usr/bin/env node
// encrypt.js — SnapStak build-time content.js encryptor
//
// Encrypts content.js using AES-256-GCM so it can be served as content.enc.
// The WASM app decrypts it in memory using the same key it uses for IndexedDB.
//
// Usage:
//   node encrypt.js --key <32-byte-hex-key>
//   node encrypt.js --key <32-byte-hex-key> --in content.js --out content.enc
//
// The key must be the same 32-byte (256-bit) hex string used as the
// CONTENT_ENC_KEY environment variable in the WASM app's production build.
//
// Output format (binary):
//   [12 bytes IV][N bytes ciphertext + 16 bytes GCM auth tag]
// The IV is prepended so the WASM app can extract it for decryption.

'use strict';

const crypto = require('crypto');
const fs     = require('fs');
const path   = require('path');

// ── Parse args ────────────────────────────────────────────────────────────────
const args = process.argv.slice(2);

function getArg(name) {
    const i = args.indexOf(name);
    return i !== -1 ? args[i + 1] : null;
}

const keyHex = getArg('--key');
const inFile = getArg('--in')  || path.join(__dirname,
    'SnapStak.Wasm.Client', 'wwwroot', 'engine', 'content.js');
const outFile = getArg('--out') || path.join(__dirname,
    'SnapStak.Wasm.Client', 'wwwroot', 'engine', 'content.enc');

if (!keyHex || keyHex.length !== 64) {
    console.error('ERROR: --key must be a 64-character hex string (32 bytes / 256 bits).');
    console.error('Generate one with:');
    console.error('  node -e "console.log(require(\'crypto\').randomBytes(32).toString(\'hex\'))"');
    process.exit(1);
}

// ── Encrypt ───────────────────────────────────────────────────────────────────
const key       = Buffer.from(keyHex, 'hex');
const iv        = crypto.randomBytes(12);       // 96-bit IV — recommended for AES-GCM
const plaintext = fs.readFileSync(inFile);

const cipher = crypto.createCipheriv('aes-256-gcm', key, iv);
const encrypted = Buffer.concat([cipher.update(plaintext), cipher.final()]);
const authTag   = cipher.getAuthTag();          // 16-byte GCM authentication tag

// Output: IV (12) + ciphertext + authTag (16)
// authTag is appended to ciphertext — Web Crypto expects them concatenated
const output = Buffer.concat([iv, encrypted, authTag]);

fs.writeFileSync(outFile, output);

const inputSize  = plaintext.length;
const outputSize = output.length;

console.log(`[encrypt.js] ✓ Encrypted successfully`);
console.log(`  Input:  ${inFile} (${inputSize.toLocaleString()} bytes)`);
console.log(`  Output: ${outFile} (${outputSize.toLocaleString()} bytes)`);
console.log(`  IV:     ${iv.toString('hex')}`);
console.log(`  Overhead: ${outputSize - inputSize} bytes (12 IV + 16 auth tag)`);
