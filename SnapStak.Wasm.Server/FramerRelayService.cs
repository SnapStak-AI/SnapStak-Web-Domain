// ─────────────────────────────────────────────────────────────────────────────
// FramerRelayService.cs  —  SnapStak.Wasm.Server
//
// Pushes SnapStak-generated React component code directly into a Framer project
// via the Framer Server API (framer-api npm package, WebSocket-based).
//
// HOW IT WORKS
//   1. The user generates React code in the CON10X Transform tab.
//   2. They click "Send to Framer".
//   3. The WASM client POSTs to /api/framer/send with the component name,
//      the React code string, and optionally the CSS string.
//   4. This service invokes a small Node.js script (framer-relay.js) that:
//        - Connects to the user's Framer project via their API key
//        - Calls framer.createCodeFile(name, code) to create the component
//        - Disconnects and exits
//   5. The React component appears in the user's Framer project assets panel
//      under Code Files, ready to drag onto the canvas and publish.
//
// FRAMER SERVER API SETUP (one-time per user)
//   1. Open framer.com and go to your project.
//   2. Project Settings → General → Developer → API Keys → Generate Key.
//   3. Copy the key and the project URL (e.g. https://framer.com/projects/abc123)
//   4. Paste both into SnapStak Settings → Framer Integration.
//
// NODE.JS REQUIREMENT
//   The Framer Server API (`framer-api`) is a Node.js package — it cannot run
//   inside .NET directly. This service shells out to Node.js to run
//   framer-relay.js, which is generated alongside this file.
//   Node.js 18+ must be installed on the same machine as the CON10X server.
//   Check with: node --version
//
// SECURITY
//   The Framer API key is stored in snapstak_settings.json alongside other
//   settings. It is never logged or transmitted except to framer.com.
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class FramerRelayService
{
    // Path to the framer-relay.js script — lives alongside the server executable
    private static readonly string RelayScriptPath =
        Path.Combine(AppContext.BaseDirectory, "framer-relay.js");

    // Path to node_modules installed for the relay script
    private static readonly string NodeModulesPath =
        Path.Combine(AppContext.BaseDirectory, "framer-relay-node_modules");

    // ── Send to Framer ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new code file in the user's Framer project containing the
    /// provided React component code.
    /// </summary>
    public static async Task<FramerSendResult> SendToFramerAsync(
        string projectUrl,
        string apiKey,
        string componentName,
        string reactCode,
        string? cssCode = null)
    {
        if (string.IsNullOrWhiteSpace(projectUrl))
            return FramerSendResult.Fail("Framer project URL is not configured. Add it in Settings → Framer.");
        if (string.IsNullOrWhiteSpace(apiKey))
            return FramerSendResult.Fail("Framer API key is not configured. Add it in Settings → Framer.");
        if (string.IsNullOrWhiteSpace(reactCode))
            return FramerSendResult.Fail("React code is empty. Generate code first.");

        // Ensure the relay script and its dependencies exist
        await EnsureRelayScriptAsync();
        var ensureResult = await EnsureNodeModulesAsync();
        if (!ensureResult.Success)
            return ensureResult;

        // Embed the CSS as a comment in the component if provided,
        // so it arrives in Framer as a single self-contained file.
        var fullCode = !string.IsNullOrWhiteSpace(cssCode)
            ? $"/*\n * Styles — paste into a <style> tag or a .css file\n *\n{PrefixLines(cssCode, " * ")}\n */\n\n{reactCode}"
            : reactCode;

        // Write the payload to a temp file — avoids command-line length limits
        // and escaping issues with arbitrary React/CSS code content.
        var payloadPath = Path.Combine(Path.GetTempPath(),
            $"snapstak_framer_{Guid.NewGuid():N}.json");
        try
        {
            var payload = JsonSerializer.Serialize(new FramerRelayPayload
            {
                ProjectUrl = projectUrl,
                ApiKey = apiKey,
                ComponentName = componentName,
                Code = fullCode,
            }, new JsonSerializerOptions { WriteIndented = false });

            await File.WriteAllTextAsync(payloadPath, payload);

            // Run the relay script
            return await RunRelayScriptAsync(payloadPath, componentName);
        }
        finally
        {
            try { File.Delete(payloadPath); } catch { /* cleanup — non-fatal */ }
        }
    }

    // ── Node.js script runner ─────────────────────────────────────────────────

    private static async Task<FramerSendResult> RunRelayScriptAsync(
        string payloadPath, string componentName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{RelayScriptPath}\" \"{payloadPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Tell Node where to find node_modules for framer-api
        psi.Environment["NODE_PATH"] = NodeModulesPath;

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                Console.WriteLine($"[Framer] ✅ '{componentName}' created in Framer project");
                return FramerSendResult.Ok(componentName);
            }
            else
            {
                var error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                Console.WriteLine($"[Framer] ❌ Relay script failed: {error}");
                return FramerSendResult.Fail(
                    $"Failed to create Framer code file: {error.Trim()}");
            }
        }
        catch (Exception ex) when (
            ex.Message.Contains("node") || ex is System.ComponentModel.Win32Exception)
        {
            return FramerSendResult.Fail(
                "Node.js is not installed or not on the PATH. " +
                "Install Node.js 18+ from nodejs.org and restart the CON10X server.");
        }
        catch (Exception ex)
        {
            return FramerSendResult.Fail($"Relay error: {ex.Message}");
        }
    }

    // ── Ensure relay script exists ────────────────────────────────────────────

    private static async Task EnsureRelayScriptAsync()
    {
        if (File.Exists(RelayScriptPath)) return;

        // Write the Node.js relay script alongside the server executable.
        // This script is self-contained — it receives the payload via a temp
        // JSON file, connects to Framer, creates the code file, then exits.
        const string script = """
// framer-relay.js — SnapStak CON10X Framer Server API relay
// Generated by FramerRelayService.cs — do not edit manually.
// Re-generated automatically if deleted.
const fs   = require("fs");
const path = require("path");

// Load framer-api from the relay-specific node_modules
const modulePath = path.join(__dirname, "framer-relay-node_modules", "framer-api");
let connect;
try {
    connect = require(modulePath).connect;
} catch (e) {
    console.error("framer-api not found. Run the CON10X server once to install it automatically.");
    process.exit(1);
}

async function main() {
    const payloadPath = process.argv[2];
    if (!payloadPath) { console.error("No payload path provided."); process.exit(1); }

    let payload;
    try {
        payload = JSON.parse(fs.readFileSync(payloadPath, "utf8"));
    } catch (e) {
        console.error("Failed to read payload: " + e.message);
        process.exit(1);
    }

    const { projectUrl, apiKey, componentName, code } = payload;
    let framer;
    try {
        framer = await connect(projectUrl, apiKey);
    } catch (e) {
        console.error("Failed to connect to Framer: " + e.message);
        process.exit(1);
    }

    try {
        // Check if a code file with this name already exists — update it if so
        const existing = await framer.getCodeFiles?.();
        const match = existing?.find(f => f.name === componentName);

        if (match) {
            // Update the existing file
            await framer.setCodeFile(match.id, code);
            console.log("Updated existing code file: " + componentName);
        } else {
            // Create a new code file
            await framer.createCodeFile(componentName, code);
            console.log("Created new code file: " + componentName);
        }
    } catch (e) {
        console.error("Failed to create/update code file: " + e.message);
        await framer.disconnect();
        process.exit(1);
    }

    await framer.disconnect();
    process.exit(0);
}

main().catch(e => {
    console.error("Unhandled error: " + e.message);
    process.exit(1);
});
""";

        await File.WriteAllTextAsync(RelayScriptPath, script);
        Console.WriteLine($"[Framer] ✅ Relay script written to {RelayScriptPath}");
    }

    // ── Ensure framer-api npm package is installed ────────────────────────────

    private static async Task<FramerSendResult> EnsureNodeModulesAsync()
    {
        var markerFile = Path.Combine(NodeModulesPath, "framer-api", "package.json");
        if (File.Exists(markerFile)) return FramerSendResult.Ok(string.Empty);

        Console.WriteLine("[Framer] 📦 Installing framer-api (first run — takes ~10 seconds)…");
        Directory.CreateDirectory(NodeModulesPath);

        var psi = new ProcessStartInfo
        {
            FileName = "npm",
            Arguments = $"install framer-api --prefix \"{NodeModulesPath}\" --no-save",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = NodeModulesPath,
        };

        try
        {
            using var p = new Process { StartInfo = psi };
            p.Start();
            await p.WaitForExitAsync();

            if (p.ExitCode == 0)
            {
                Console.WriteLine("[Framer] ✅ framer-api installed");
                return FramerSendResult.Ok(string.Empty);
            }
            else
            {
                var err = await p.StandardError.ReadToEndAsync();
                return FramerSendResult.Fail($"npm install failed: {err.Trim()}");
            }
        }
        catch (Exception ex)
        {
            return FramerSendResult.Fail(
                "npm is not installed or not on the PATH. " +
                "Install Node.js 18+ from nodejs.org and restart the CON10X server. " +
                $"Details: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string PrefixLines(string text, string prefix) =>
        string.Join("\n", text.Split('\n').Select(l => prefix + l));
}

// ── Result types ──────────────────────────────────────────────────────────────

public sealed class FramerSendResult
{
    public bool Success { get; private set; }
    public string ComponentName { get; private set; } = string.Empty;
    public string Error { get; private set; } = string.Empty;

    public static FramerSendResult Ok(string name) => new() { Success = true, ComponentName = name };
    public static FramerSendResult Fail(string error) => new() { Success = false, Error = error };
}

// ── Relay payload model ───────────────────────────────────────────────────────

internal sealed class FramerRelayPayload
{
    [JsonPropertyName("projectUrl")] public string ProjectUrl { get; set; } = string.Empty;
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = string.Empty;
    [JsonPropertyName("componentName")] public string ComponentName { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
}