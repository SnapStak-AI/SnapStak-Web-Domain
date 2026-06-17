using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Models.Pillars;

/// <summary>
/// Pillar 3: Influence — What shaped it.
/// Browser name and version, OS, screen resolution, device pixel ratio,
/// viewport dimensions, user agent, and media feature queries.
/// All captured at the moment of DOM serialisation. Never invented.
/// Serialised to {componentId}_influence.json on disk.
/// </summary>
public sealed class InfluenceData
{
    [JsonProperty("component_id")]           public string  ComponentId          { get; set; } = string.Empty;
    [JsonProperty("browser_name")]           public string? BrowserName          { get; set; }
    [JsonProperty("browser_version")]        public string? BrowserVersion        { get; set; }
    [JsonProperty("os_name")]                public string? OsName               { get; set; }
    [JsonProperty("os_version")]             public string? OsVersion            { get; set; }
    [JsonProperty("screen_width")]           public int?    ScreenWidth          { get; set; }
    [JsonProperty("screen_height")]          public int?    ScreenHeight         { get; set; }
    [JsonProperty("device_pixel_ratio")]     public double? DevicePixelRatio     { get; set; }
    [JsonProperty("viewport_width")]         public int?    ViewportWidth        { get; set; }
    [JsonProperty("viewport_height")]        public int?    ViewportHeight       { get; set; }
    [JsonProperty("user_agent")]             public string? UserAgent            { get; set; }
    [JsonProperty("captured_at")]            public string  CapturedAt           { get; set; } = DateTime.UtcNow.ToString("O");
    [JsonProperty("prefers_color_scheme")]   public string  PrefersColorScheme   { get; set; } = "light";
    [JsonProperty("prefers_reduced_motion")] public string  PrefersReducedMotion { get; set; } = "no-preference";
}
