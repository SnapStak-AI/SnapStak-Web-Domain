using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Models.Pillars;

/// <summary>
/// Pillar 4: Objective — What must it achieve.
/// The only pillar not derived from the DOM. Always user-defined.
/// Inferred at extraction time from live viewport. Confirmed at conversion time.
/// Serialised to {componentId}_objective.json on disk.
/// </summary>
public sealed class ObjectiveData
{
    [JsonProperty("component_id")]           public string   ComponentId          { get; set; } = string.Empty;
    [JsonProperty("device_type")]            public string?  DeviceType           { get; set; }
    [JsonProperty("screen_width_target")]    public int?     ScreenWidthTarget    { get; set; }
    [JsonProperty("screen_size_label")]      public string?  ScreenSizeLabel      { get; set; }
    /// <summary>1 = all CSS breakpoints captured, 0 = single viewport.</summary>
    [JsonProperty("all_breakpoints")]        public int      AllBreakpoints       { get; set; }
    /// <summary>
    /// The exact px widths the CSS declares — source of truth for @media thresholds.
    /// The AI reads these to write correct @media queries. Never hardcoded.
    /// </summary>
    [JsonProperty("captured_breakpoints")]   public int[]?   CapturedBreakpoints  { get; set; }
    [JsonProperty("framework")]              public string?  Framework            { get; set; }
    [JsonProperty("additional_intent")]      public string?  AdditionalIntent     { get; set; }
    [JsonProperty("conversion_requested_at")]public string   ConversionRequestedAt{ get; set; } = DateTime.UtcNow.ToString("O");
}
