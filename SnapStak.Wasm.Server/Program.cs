// ── ASP.NET Core server ───────────────────────────────────────────────────────

using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Net.Http.Headers;

// ── Output root ───────────────────────────────────────────────────────────────
// Default path — used on first run before the user saves settings via the UI.
// Once the user saves settings in the Settings tab, the value is read from
// snapstak_settings.json in the same folder as the server executable.
const string DefaultOutputRoot = @"C:\ConteX Dev\SnapStak.Wasm\output";

// Load persisted settings if they exist
var settingsFilePath = Path.Combine(AppContext.BaseDirectory, "snapstak_settings.json");
var serverSettings = ServerSettings.Load(settingsFilePath);

var OutputRoot = string.IsNullOrWhiteSpace(serverSettings.OutputRoot)
    ? DefaultOutputRoot
    : serverSettings.OutputRoot;

Storage.Configure(OutputRoot);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Increase Kestrel request timeout to 2 minutes — Mobile subnet scan takes time
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(2);
});

var app = builder.Build();
Pipeline.Configure(serverSettings); // configure plugin enabled/path settings before first request
app.UseCors();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// ── Mobile pairing + file transfer services ───────────────────────────────────
var mobilePairing = new MobilePairingServer(serverSettings);
var mobileTransfer = new MobileFileTransferServer(mobilePairing, OutputRoot);
mobilePairing.Start();
mobileTransfer.Start();

var sseClients = new ConcurrentDictionary<string, HttpResponse>();
string? lastSnapshot = null;

// ── Snapshot — receives DOM capture from the Chrome extension ─────────────────

app.MapPost("/api/snapshot", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    lastSnapshot = body;

    TransformRequest? req = null;
    try { req = JsonConvert.DeserializeObject<TransformRequest>(body); }
    catch (Exception ex) { Console.WriteLine($"[Server] ❌ Deserialize failed: {ex.Message}"); }

    // Always strip viewportSnapshots before relaying to SSE clients.
    // The mobile DOM snapshots are large (~500KB) and have already been
    // processed by Pipeline.Transform above. Sending them over SSE causes
    // deserialization failures in the WASM client, silently dropping the snapshot.
    string sseBody = body;
    try
    {
        var jObj = Newtonsoft.Json.Linq.JObject.Parse(body);
        jObj.Remove("viewportSnapshots");
        sseBody = jObj.ToString(Newtonsoft.Json.Formatting.None);
    }
    catch { /* use original body if stripping fails */ }
    lastSnapshot = sseBody;

    // Relay to SSE clients immediately — before Transform runs
    foreach (var (id, response) in sseClients)
    {
        try { await response.WriteAsync($"data: {sseBody}\n\n"); await response.Body.FlushAsync(); }
        catch { sseClients.TryRemove(id, out _); }
    }

    // Run Transform on background thread — never blocks the SSE relay
    if (req != null)
    {
        var reqCopy = req;
        _ = Task.Run(async () =>
        {
            try { await Pipeline.TransformAsync(reqCopy); }
            catch (Exception ex) { Console.WriteLine($"[Server] ❌ Transform failed: {ex.Message}\n{ex.StackTrace}"); }
        });
    }

    return Results.Ok(new { success = true });
});

// ── SSE — pushes snapshots to the WASM client in real time ───────────────────

app.MapGet("/api/events", async (HttpContext ctx) =>
{
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    await ctx.Response.StartAsync();

    var id = Guid.NewGuid().ToString("N");
    sseClients[id] = ctx.Response;

    if (lastSnapshot != null)
    {
        try { await ctx.Response.WriteAsync($"data: {lastSnapshot}\n\n"); await ctx.Response.Body.FlushAsync(); }
        catch { }
    }

    using var cts = new CancellationTokenSource();
    _ = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
            try { await Task.Delay(20000, cts.Token); await ctx.Response.WriteAsync(": keepalive\n\n", cts.Token); await ctx.Response.Body.FlushAsync(cts.Token); }
            catch { break; }
    }, cts.Token);

    var tcs = new TaskCompletionSource();
    ctx.RequestAborted.Register(() => tcs.TrySetResult());
    await tcs.Task;
    cts.Cancel();
    sseClients.TryRemove(id, out _);
});

// ── Health ────────────────────────────────────────────────────────────────────

app.MapGet("/health", () => Results.Ok(new { status = "healthy", name = "SnapStak CON10X", time = DateTime.UtcNow }));

// ── Canva Connect API integration ─────────────────────────────────────────────
//
// These four endpoints drive the "Send to Canva" feature in the WASM client.
// Credentials are loaded from environment variables — see canva.env for setup.
//
// Setup checklist (one-time):
//   1. Go to canva.com/developers → Your integrations → Create an integration
//   2. Set redirect URI to: http://localhost:5174/api/canva/callback
//   3. Enable scope: design:content:write
//   4. Copy Client ID and generate a secret
//   5. Set environment variables before starting the server:
//        CANVA_CLIENT_ID      = (from Developer Portal)
//        CANVA_CLIENT_SECRET  = (from Developer Portal)
//        CANVA_REDIRECT_URI   = http://localhost:5174/api/canva/callback
//      See canva.env in the server project root for load instructions.

// 1. Initiate OAuth — redirects the user's browser to Canva's auth page
app.MapGet("/api/canva/auth", (HttpContext ctx) =>
{
    var userId = ctx.Request.Query["userId"].FirstOrDefault();
    if (string.IsNullOrEmpty(userId))
        return Results.BadRequest(new { error = "userId is required" });

    if (string.IsNullOrEmpty(CanvaConfig.ClientId))
        return Results.Problem(
            "CANVA_CLIENT_ID environment variable is not set. " +
            "Fill in canva.env and load it before starting the server.",
            statusCode: 503);

    var authUrl = CanvaRelayService.BuildAuthUrl(userId, out _);
    Console.WriteLine($"[Canva] 🔗 Auth redirect for user {userId}");
    return Results.Redirect(authUrl);
});

// 2. OAuth callback — Canva redirects here after the user clicks Allow
app.MapGet("/api/canva/callback", async (HttpContext ctx) =>
{
    var code = ctx.Request.Query["code"].FirstOrDefault();
    var state = ctx.Request.Query["state"].FirstOrDefault();
    var error = ctx.Request.Query["error"].FirstOrDefault();

    if (!string.IsNullOrEmpty(error))
    {
        Console.WriteLine($"[Canva] ⚠️ User denied Canva access: {error}");
        return Results.Redirect("/?canva=denied");
    }

    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        return Results.BadRequest(new { error = "Missing code or state parameter." });

    try
    {
        var userId = await CanvaRelayService.HandleCallbackAsync(code, state, OutputRoot);
        return Results.Redirect($"/?canva=connected&userId={userId}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Canva] ❌ OAuth callback error: {ex.Message}");
        return Results.Redirect("/?canva=error");
    }
});

// 3. Send to Canva — reads the .canva.pdf, posts it to Canva, returns the edit URL
app.MapPost("/api/canva/send", async (HttpContext ctx) =>
{
    string? componentId = null;
    string? userId = null;

    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var req = System.Text.Json.JsonSerializer.Deserialize<CanvaSendRequest>(body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        componentId = req?.ComponentId;
        userId = req?.UserId;
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, error = $"Invalid request body: {ex.Message}" });
    }

    if (string.IsNullOrEmpty(componentId) || string.IsNullOrEmpty(userId))
        return Results.BadRequest(new { success = false, error = "componentId and userId are required." });

    var result = await CanvaRelayService.SendToCanvaAsync(userId, componentId, OutputRoot);

    if (result.Success)
    {
        Console.WriteLine($"[Canva] ✅ {componentId} → {result.EditUrl}");
        return Results.Ok(new { success = true, editUrl = result.EditUrl, viewUrl = result.ViewUrl });
    }
    else
    {
        Console.WriteLine($"[Canva] ❌ {componentId}: {result.Error}");
        return Results.Ok(new { success = false, error = result.Error });
    }
});

// 4. Connection status — the WASM client calls this on startup to check
//    whether the user has already connected their Canva account
app.MapGet("/api/canva/status", (HttpContext ctx) =>
{
    var userId = ctx.Request.Query["userId"].FirstOrDefault();
    if (string.IsNullOrEmpty(userId))
        return Results.BadRequest(new { error = "userId is required" });

    var tokenPath = Path.Combine(OutputRoot, "local", userId, "canva_tokens.json");
    return Results.Ok(new { connected = File.Exists(tokenPath) });
});


// ── Settings — read/write plugin configuration ───────────────────────────────
//
// GET  /api/settings        — returns current server settings JSON
// POST /api/settings        — saves new settings, reconfigures Storage path
//
// The WASM client posts here whenever the user saves settings in the UI.
// The server persists to snapstak_settings.json so the paths survive restarts.

app.MapGet("/api/settings", () =>
    Results.Ok(serverSettings));

app.MapPost("/api/settings", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var incoming = System.Text.Json.JsonSerializer.Deserialize<ServerSettings>(body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (incoming == null)
            return Results.BadRequest(new { error = "Invalid settings JSON." });

        // Update in-memory state
        serverSettings.OutputRoot = incoming.OutputRoot;
        serverSettings.PenpotEnabled = incoming.PenpotEnabled;
        serverSettings.PenpotOutputPath = incoming.PenpotOutputPath;
        serverSettings.CanvaEnabled = incoming.CanvaEnabled;
        serverSettings.CanvaOutputPath = incoming.CanvaOutputPath;
        serverSettings.FigmaEnabled = incoming.FigmaEnabled;
        serverSettings.FigmaOutputPath = incoming.FigmaOutputPath;
        serverSettings.FramerApiKey = incoming.FramerApiKey;
        serverSettings.FramerProjectUrl = incoming.FramerProjectUrl;

        // Reconfigure storage root immediately — takes effect for next capture
        if (!string.IsNullOrWhiteSpace(serverSettings.OutputRoot))
            Storage.Configure(serverSettings.OutputRoot);

        // Persist to disk
        serverSettings.Save(settingsFilePath);

        Console.WriteLine($"[Settings] ✅ Saved — OutputRoot: {serverSettings.OutputRoot}");
        return Results.Ok(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ── Figma SVG endpoint — serves the .figma.svg for the Figma plugin ───────────
//
// GET /api/figma/svg?componentId={id}
// The SnapStak Importer Figma plugin calls this from its UI iframe.
// Returns the .figma.svg file produced by FigmaTranslatorPlugin.
// No authentication — served from localhost only.
app.MapGet("/api/figma/svg", (HttpContext ctx) =>
{
    var componentId = ctx.Request.Query["componentId"].FirstOrDefault();
    if (string.IsNullOrEmpty(componentId))
        return Results.BadRequest(new { error = "componentId is required" });

    // Sanitise — component IDs are hostname_epoch, no path separators allowed
    if (componentId.IndexOfAny(new[] { '/', '\\', '.' }) >= 0
        && !System.Text.RegularExpressions.Regex.IsMatch(componentId, @"^[\w.-]+$"))
        return Results.BadRequest(new { error = "Invalid componentId" });

    var svgPath = Path.Combine(OutputRoot, "local", componentId, $"{componentId}.figma.svg");
    if (!File.Exists(svgPath))
        return Results.NotFound(new
        {
            error = $"No .figma.svg found for component '{componentId}'. " +
                    "Run a SnapStak capture first, then try again.",
        });

    var bytes = File.ReadAllBytes(svgPath);
    Console.WriteLine($"[Figma] ✅ Serving {componentId}.figma.svg ({bytes.Length} bytes)");
    return Results.File(bytes, "image/svg+xml", $"{componentId}.figma.svg");
});

// ── Framer Server API integration ────────────────────────────────────────────
//
// POST /api/framer/send
// Body: { "componentName": "…", "reactCode": "…", "cssCode": "…" }
// Pushes the generated React component code directly into the user's Framer
// project as a code file using the Framer Server API (framer-api npm package).
//
// No OAuth — the user provides their Framer API key in Settings.
// Requires Node.js 18+ on the server machine.

app.MapPost("/api/framer/send", async (HttpContext ctx) =>
{
    string? componentName = null;
    string? reactCode = null;
    string? cssCode = null;

    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var req = System.Text.Json.JsonSerializer.Deserialize<FramerSendRequest>(body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        componentName = req?.ComponentName;
        reactCode = req?.ReactCode;
        cssCode = req?.CssCode;
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { success = false, error = $"Invalid request: {ex.Message}" });
    }

    if (string.IsNullOrEmpty(componentName) || string.IsNullOrEmpty(reactCode))
        return Results.BadRequest(new { success = false, error = "componentName and reactCode are required." });

    if (string.IsNullOrEmpty(serverSettings.FramerApiKey)
     || string.IsNullOrEmpty(serverSettings.FramerProjectUrl))
        return Results.Ok(new
        {
            success = false,
            error = "Framer API key and project URL are not configured. Add them in Settings → Framer."
        });

    var result = await FramerRelayService.SendToFramerAsync(
        serverSettings.FramerProjectUrl,
        serverSettings.FramerApiKey,
        componentName, reactCode, cssCode);

    if (result.Success)
    {
        Console.WriteLine($"[Framer] ✅ '{componentName}' sent to Framer");
        return Results.Ok(new { success = true, componentName = result.ComponentName });
    }
    else
    {
        Console.WriteLine($"[Framer] ❌ {result.Error}");
        return Results.Ok(new { success = false, error = result.Error });
    }
});

// GET /api/framer/status — returns whether Framer credentials are configured
app.MapGet("/api/framer/status", () =>
    Results.Ok(new
    {
        configured = !string.IsNullOrEmpty(serverSettings.FramerApiKey)
                  && !string.IsNullOrEmpty(serverSettings.FramerProjectUrl),
        projectUrl = serverSettings.FramerProjectUrl,
    }));

// ── Mobile device endpoints (called directly by the Mobile app) ──────────────
//
// Mobile scans the local subnet on port 5174 instead of 5172.
// No extra ports, no firewall rules — everything goes through the existing server.

// GET /discover — Mobile sends this to find Desktop on the network
app.MapGet("/discover", () => Results.Ok(new { service = "SNAPSTAK_HERE", version = "1" }));

// POST /pair — Mobile sends deviceId + PIN after finding Desktop via /discover
app.MapPost("/pair", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    string deviceId = string.Empty;
    string pin = string.Empty;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("deviceId", out var d)) deviceId = d.GetString() ?? string.Empty;
        if (root.TryGetProperty("pin", out var p)) pin = p.GetString() ?? string.Empty;
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid JSON." });
    }

    if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(pin))
        return Results.BadRequest(new { error = "deviceId and pin are required." });

    var mobileIp = ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString();
    var (statusCode, responseBody) = await mobilePairing.HandlePairAsync(deviceId, pin, mobileIp);

    ctx.Response.ContentType = "application/json";
    ctx.Response.StatusCode = statusCode;
    await ctx.Response.WriteAsync(responseBody);
    return Results.Empty;
});

// ── Mobile pairing — proxy endpoints called by the WASM client ───────────────
//
// The WASM client cannot open TCP sockets. These endpoints relay commands
// from the Blazor UI to the MobilePairingServer and MobileFileTransferServer
// instances that run the actual listeners.

// POST /api/mobile/pairing/generate-pin
// Body: { "paystackUuid": "...", "openRouterKey": "..." }
app.MapPost("/api/mobile/pairing/generate-pin", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    dynamic? req = Newtonsoft.Json.JsonConvert.DeserializeObject(body);
    string paystackUuid = (string?)(req?.paystackUuid) ?? string.Empty;
    string subscriptionCode = (string?)(req?.subscriptionCode) ?? string.Empty;
    string openRouterKey = (string?)(req?.openRouterKey) ?? string.Empty;

    if (string.IsNullOrWhiteSpace(paystackUuid))
        return Results.BadRequest(new { error = "paystackUuid is required." });

    var pin = mobilePairing.GeneratePin(paystackUuid, subscriptionCode, openRouterKey);
    return Results.Ok(new { pin });
});

// GET /api/mobile/pairing/status
// Returns current pairing state so the WASM UI can poll.
app.MapGet("/api/mobile/pairing/status", () =>
{
    var status = mobilePairing.GetStatus();
    return Results.Ok(status);
});

// POST /api/mobile/pairing/cancel
// Cancels the active PIN and closes the listener.
app.MapPost("/api/mobile/pairing/cancel", () =>
{
    mobilePairing.CancelPin();
    return Results.Ok();
});

// GET /api/mobile/pairing/devices
// Returns the list of paired devices.
app.MapGet("/api/mobile/pairing/devices", () =>
{
    var devices = mobilePairing.GetPairedDevices();
    return Results.Ok(devices);
});

// POST /api/mobile/pairing/remove-device
// Called by Mobile app directly to remove its record from Desktop.
// No callback to Mobile — Mobile handles its own token clearing.
app.MapPost("/api/mobile/pairing/remove-device", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    dynamic? req = Newtonsoft.Json.JsonConvert.DeserializeObject(body);
    string deviceId = (string?)(req?.deviceId) ?? string.Empty;
    mobilePairing.RemoveDevice(deviceId);
    return Results.Ok();
});

// POST /api/mobile/pairing/remove-device-ui
// Called by the Desktop UI when the user clicks Remove.
// Calls Mobile first via /api/unpair — Desktop record only removed after Mobile confirms.
app.MapPost("/api/mobile/pairing/remove-device-ui", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    dynamic? req = Newtonsoft.Json.JsonConvert.DeserializeObject(body);
    string deviceId = (string?)(req?.deviceId) ?? string.Empty;
    var (success, error) = await mobilePairing.RemoveDeviceAndNotifyMobileAsync(deviceId);
    if (!success)
        return Results.BadRequest(new { error });
    return Results.Ok();
});

// ── Mobile file transfer — /receive endpoint (called directly by Mobile app) ──

// POST /receive — Mobile posts encrypted .snapstak files here
app.MapPost("/receive", async (HttpContext ctx) =>
{
    var deviceId = ctx.Request.Headers["X-SnapStak-DeviceId"].FirstOrDefault() ?? string.Empty;
    var filename = ctx.Request.Headers["X-SnapStak-Filename"].FirstOrDefault() ?? "unknown.snapstak";

    if (string.IsNullOrWhiteSpace(deviceId))
        return Results.BadRequest(new { error = "X-SnapStak-DeviceId header is required." });

    if (mobilePairing.FindDevice(deviceId) == null)
        return Results.Json(new { error = "Device not recognised. Pair this device in Desktop Settings." }, statusCode: 403);

    const long maxBytes = 50 * 1024 * 1024;
    if (ctx.Request.ContentLength > maxBytes)
        return Results.Json(new { error = "File exceeds 50 MB limit." }, statusCode: 413);

    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    if (ms.Length > maxBytes)
        return Results.Json(new { error = "File exceeds 50 MB limit." }, statusCode: 413);

    var fileId = mobileTransfer.EnqueueFile(deviceId, filename, ms.ToArray());
    Console.WriteLine($"[Mobile] Received '{filename}' ({ms.Length} bytes) from {deviceId[..Math.Min(8, deviceId.Length)]}...");
    return Results.Ok(new { success = true, fileId });
});

// ── Mobile file transfer — proxy endpoints called by the WASM client ──────────

// GET /api/mobile/files
// Returns the list of received files.
app.MapGet("/api/mobile/files", () =>
{
    var files = mobileTransfer.GetFiles();
    return Results.Ok(files);
});

// GET /api/mobile/files/listening
// Returns whether the file-receive listener on 5173 is active.
app.MapGet("/api/mobile/files/listening", () =>
    Results.Ok(new { listening = mobileTransfer.IsListening }));

// POST /api/mobile/files/dismiss
// Body: { "fileId": "..." }
app.MapPost("/api/mobile/files/dismiss", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    dynamic? req = Newtonsoft.Json.JsonConvert.DeserializeObject(body);
    string fileId = (string?)(req?.fileId) ?? string.Empty;
    mobileTransfer.DismissFile(fileId);
    return Results.Ok();
});

// POST /api/mobile/files/sync
// Called by Transfer.razor Sync button.
// Discovers the paired Mobile device on the LAN, pulls all pending encrypted
// .snapstak files via GET /api/sync/files, downloads each one, decrypts,
// and saves to the output directory.
app.MapPost("/api/mobile/files/sync", async (HttpContext ctx) =>
{
    var devices = mobilePairing.GetPairedDevices();
    if (devices.Count == 0)
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(new { success = false, message = "No paired devices found." }));
        return;
    }

    // Use the most recently paired device that has a known IP.
    var device = devices
        .Where(d => !string.IsNullOrWhiteSpace(d.MobileIp))
        .OrderByDescending(d => d.PairedAt)
        .FirstOrDefault();

    if (device == null)
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(new { success = false, message = "Mobile device IP not available. Re-pair the device and try again." }));
        return;
    }

    var (pulled, message) = await mobileTransfer.SyncFromDeviceAsync(device.DeviceId, device.MobileIp!);
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(
        System.Text.Json.JsonSerializer.Serialize(new { success = pulled >= 0, message }));
});

// ── Fallback — serves the Blazor WASM app for all other routes ────────────────

app.MapFallbackToFile("index.html");

Console.WriteLine("SnapStak CON10X — http://localhost:5174");
Console.WriteLine($"Output folder: {OutputRoot}");
Console.WriteLine("Extension fetches content.js from http://localhost:5174/engine/content.js");
Console.WriteLine("Figma plugin: GET http://localhost:5174/api/figma/svg?componentId={id}");
Console.WriteLine($"Plugins: Penpot={serverSettings.PenpotEnabled} Canva={serverSettings.CanvaEnabled} Figma={serverSettings.FigmaEnabled}");
Console.WriteLine($"Plugin output paths: Penpot='{serverSettings.PenpotOutputPath}' Canva='{serverSettings.CanvaOutputPath}' Figma='{serverSettings.FigmaOutputPath}'");
Console.WriteLine($"Canva integration: {(string.IsNullOrEmpty(CanvaConfig.ClientId) ? "⚠️  CANVA_CLIENT_ID not set — see canva.env" : "✅ credentials loaded")}");
Console.WriteLine("Mobile pairing: GET /discover  POST /pair  (port 5174 — no extra firewall rules needed)");
Console.WriteLine("Mobile file transfer: POST /receive (port 5174)");

// Force plugin discovery at startup so any registration failures are logged immediately
// rather than silently on first capture.
Pipeline.LogPlugins();

app.Run("http://0.0.0.0:5174");

// ── Request model used by /api/canva/send ─────────────────────────────────────

// ── ServerSettings — persisted configuration model ───────────────────────────

public sealed class ServerSettings
{
    [System.Text.Json.Serialization.JsonPropertyName("outputRoot")]
    public string OutputRoot { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("penpotEnabled")]
    public bool PenpotEnabled { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("penpotOutputPath")]
    public string PenpotOutputPath { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("canvaEnabled")]
    public bool CanvaEnabled { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("canvaOutputPath")]
    public string CanvaOutputPath { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("figmaEnabled")]
    public bool FigmaEnabled { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("figmaOutputPath")]
    public string FigmaOutputPath { get; set; } = string.Empty;

    // Framer Server API credentials
    [System.Text.Json.Serialization.JsonPropertyName("framerApiKey")]
    public string FramerApiKey { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("framerProjectUrl")]
    public string FramerProjectUrl { get; set; } = string.Empty;

    public static ServerSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return System.Text.Json.JsonSerializer.Deserialize<ServerSettings>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new ServerSettings();
            }
        }
        catch { /* corrupt file — use defaults */ }
        return new ServerSettings();
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(this,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] ⚠️ Could not save settings file: {ex.Message}");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public sealed class FramerSendRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("componentName")]
    public string? ComponentName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("reactCode")]
    public string? ReactCode { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("cssCode")]
    public string? CssCode { get; set; }
}

public sealed class CanvaSendRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("componentId")]
    public string? ComponentId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("userId")]
    public string? UserId { get; set; }
}