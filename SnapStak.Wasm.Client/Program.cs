using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SnapStak.Wasm.Client.Engine.Plugins;
using SnapStak.Wasm.Client.Services;
using SnapStak.Wasm.Client.Storage;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<SnapStak.Wasm.Client.App>("#app");

// ── Encryption ────────────────────────────────────────────────────────────────────────────────
// Development : enabled: false  → data stored with "plain:" prefix, readable
// Production  : enabled: true   → AES-GCM 256-bit via Web Crypto API
var isDev = builder.HostEnvironment.IsDevelopment();
builder.Services.AddScoped(sp =>
    new PillarEncryption(
        sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(),
        enabled: !isDev));

// ── Storage ───────────────────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IPillarStorage, IndexedDbPillarStorage>();

// ── Translator plugins ────────────────────────────────────────────────────────────────
// Reflection-based discovery of every IConteXTranslatorPlugin implementation
// in the loaded assemblies. Plugins live under Engine/Plugins/<n>/ and are
// compiled into the app — no runtime .dll loading (not supported in Blazor WASM).
builder.Services.AddScoped<TranslatorPluginHost>();

// ── CON10X engine ──────────────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<ModelService>();
builder.Services.AddScoped<AiGatewayService>();
builder.Services.AddScoped<RulesEngineService>();
builder.Services.AddScoped<InfluenceService>();
builder.Services.AddScoped<ObjectiveService>();
builder.Services.AddScoped<StructureAgentService>(sp =>
    new SnapStak.Wasm.Client.Services.StructureAgentService(
        sp.GetRequiredService<SnapStak.Wasm.Client.Storage.IPillarStorage>(),
        sp.GetRequiredService<SnapStak.Wasm.Client.Engine.Plugins.TranslatorPluginHost>(),
        new System.Net.Http.HttpClient { BaseAddress = new Uri("http://localhost:5174/") }));
builder.Services.AddScoped<BehaviourAgentService>();
builder.Services.AddScoped<ConstructorService>();

// ── Application services ──────────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<LicenceService>();
builder.Services.AddScoped<ConteXPipelineService>();
builder.Services.AddScoped<SnapshotBridgeService>();
builder.Services.AddScoped<MobilePairingService>();
builder.Services.AddScoped<MobileFileTransferService>();

// ── Plugin settings ──────────────────────────────────────────────────────────────────────────
// Manages translator plugin on/off toggles and output paths.
// Persists to localStorage and syncs to the CON10X server on every save.
// Requires HttpClient for the server sync call — uses a dedicated instance
// pointed at the local CON10X server, separate from the OpenRouter client.
builder.Services.AddScoped<PluginSettingsService>(sp =>
    new PluginSettingsService(
        sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(),
        new System.Net.Http.HttpClient { BaseAddress = new Uri("http://localhost:5174/") }));

// ── HTTP client for OpenRouter AI ─────────────────────────────────────────────────────
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
    Timeout = TimeSpan.FromSeconds(3600),
});

var app = builder.Build();

await app.RunAsync();