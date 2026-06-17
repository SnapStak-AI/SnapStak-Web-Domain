# content.js Encryption

## How it works

In development mode (`dotnet run`):
- WASM fetches `content.js` plaintext from `/engine/content.js`
- No encryption — fast iteration

In production mode (`dotnet publish -c Release`):
- WASM fetches `content.enc` (encrypted binary) from `/engine/content.enc`
- Decrypts in memory using AES-256-GCM with the subscription-derived key
- Decrypted source sent to bridge.js via postMessage — never written to disk
- `content.js` is NOT included in the production publish output

## Before publishing to production

### Step 1 — Generate a content encryption key

This is separate from HMAC_SECRET. Store it securely.

```bash
node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
```

### Step 2 — Encrypt content.js

Run from the solution root (where encrypt.js lives):

```bash
node encrypt.js --key YOUR_64_CHAR_HEX_KEY
```

This reads:  `SnapStak.Wasm.Client/wwwroot/engine/content.js`
Writes:      `SnapStak.Wasm.Client/wwwroot/engine/content.enc`

### Step 3 — Set the key in the WASM app

The WASM app needs the same key to decrypt at runtime.
Add it as a build-time constant in `PillarEncryption.cs`:

```csharp
private const string ContentEncKey = "YOUR_64_CHAR_HEX_KEY";
```

Or pass it via an environment variable during `dotnet publish`.

### Step 4 — Publish

```bash
dotnet publish -c Release
```

The production output will contain `content.enc` but NOT `content.js`.

## Key rotation

If you update content.js:
1. Edit `SnapStak.Wasm.Client/wwwroot/engine/content.js`
2. Run `node encrypt.js --key YOUR_KEY` again
3. Republish

The encryption key only needs to change if you believe it has been compromised.
