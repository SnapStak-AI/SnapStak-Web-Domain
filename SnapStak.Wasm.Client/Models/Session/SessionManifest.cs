using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace SnapStak.Wasm.Client.Models.Session;

// ─────────────────────────────────────────────────────────────────────────────
// SESSION MANIFEST
//
// Written to {storageRoot}/{userUUID}/manifest.json by StructureAgentCom
// on every transform POST. Updated in-place as each component is registered.
// Read by ProcessSessionCom to drive the sequential Behaviour + Constructor
// pipeline. One manifest per user — one project at a time.
//
// The manifest is NOT encrypted — it contains only component IDs and path
// references, no pillar content. Must remain readable by the Constructor
// during /assemble without the pillar key.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SessionManifest
{
    // ── Session identity ──────────────────────────────────────────────────────
    [JsonProperty("userUuid")]
    public string UserUuid { get; set; } = string.Empty;

    [JsonProperty("createdAt")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonProperty("updatedAt")]
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("O");

    // ── Target (written when process-session is called) ───────────────────────
    [JsonProperty("framework")]
    public string? Framework { get; set; }

    [JsonProperty("platformType")]
    public string? PlatformType { get; set; }

    [JsonProperty("styleOutput")]
    public string StyleOutput { get; set; } = "css";

    [JsonProperty("language")]
    public string Language { get; set; } = "js";

    // ── Components — appended on every transform POST ─────────────────────────
    [JsonProperty("components")]
    public List<SessionComponent> Components { get; set; } = new();

    // ── Pipeline status ───────────────────────────────────────────────────────
    [JsonProperty("processingStatus")]
    [JsonConverter(typeof(StringEnumConverter))]
    public SessionStatus ProcessingStatus { get; set; } = SessionStatus.Pending;

    [JsonProperty("assemblyStatus")]
    [JsonConverter(typeof(StringEnumConverter))]
    public SessionStatus AssemblyStatus { get; set; } = SessionStatus.Pending;

    [JsonProperty("outputZip")]
    public string? OutputZip { get; set; }

    [JsonProperty("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonProperty("completedAt")]
    public string? CompletedAt { get; set; }
}

public sealed class SessionComponent
{
    // ── Identity ──────────────────────────────────────────────────────────────
    [JsonProperty("componentId")]
    public string ComponentId { get; set; } = string.Empty;

    /// <summary>Human-readable label from zone detection (e.g. "Header", "Main", "Hero Section").</summary>
    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Zone type: header | main | navbar | section | article | form | div | etc.</summary>
    [JsonProperty("zone")]
    public string Zone { get; set; } = string.Empty;

    /// <summary>
    /// True for the master zone component (the full-page SVG the sub-components
    /// were carved from). Excluded from the Behaviour AI + Constructor pipeline —
    /// all meaningful content is already in the sub-components.
    /// </summary>
    [JsonProperty("isMaster")]
    public bool IsMaster { get; set; } = false;

    /// <summary>Absolute path to the component folder on disk.</summary>
    [JsonProperty("folder")]
    public string Folder { get; set; } = string.Empty;

    // ── File inventory — presence confirmed at registration time ──────────────
    [JsonProperty("files")]
    public SessionComponentFiles Files { get; set; } = new();

    // ── Pillar readiness — updated at registration and after Behaviour AI ─────
    [JsonProperty("pillarStatus")]
    public PillarStatus PillarStatus { get; set; } = new();

    // ── Processing status ─────────────────────────────────────────────────────
    [JsonProperty("status")]
    [JsonConverter(typeof(StringEnumConverter))]
    public SessionStatus Status { get; set; } = SessionStatus.Pending;

    [JsonProperty("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonProperty("zipPath")]
    public string? ZipPath { get; set; }

    [JsonProperty("downloadToken")]
    public string? DownloadToken { get; set; }

    [JsonProperty("registeredAt")]
    public string RegisteredAt { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonProperty("completedAt")]
    public string? CompletedAt { get; set; }
}

public sealed class SessionComponentFiles
{
    [JsonProperty("structure")]
    public string? Structure { get; set; }   // {componentId}.svg

    [JsonProperty("cssJson")]
    public string? CssJson { get; set; }     // {componentId}_css.json

    [JsonProperty("jsJson")]
    public string? JsJson { get; set; }      // {componentId}_js.json

    [JsonProperty("influence")]
    public string? Influence { get; set; }   // {componentId}_influence.json

    [JsonProperty("objective")]
    public string? Objective { get; set; }   // {componentId}_objective.json

    [JsonProperty("behaviourCss")]
    public string? BehaviourCss { get; set; } // {componentId}_css.md (written by Behaviour AI)

    [JsonProperty("behaviourJs")]
    public string? BehaviourJs { get; set; }  // {componentId}_js.md  (written by Behaviour AI)
}

public sealed class PillarStatus
{
    [JsonProperty("structure")]
    public bool Structure { get; set; }    // Pillar 1: .svg exists

    [JsonProperty("behaviour")]
    public bool Behaviour { get; set; }   // Pillar 2: _css.md + _js.md exist

    [JsonProperty("influence")]
    public bool Influence { get; set; }   // Pillar 3: _influence.json exists

    [JsonProperty("objective")]
    public bool Objective { get; set; }   // Pillar 4: _objective.json exists
}

public enum SessionStatus
{
    Pending,
    Processing,
    Complete,
    Failed,
}