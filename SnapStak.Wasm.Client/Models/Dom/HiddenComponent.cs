using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Models.Dom;

/// <summary>
/// A hidden interactive component captured by the Chrome extension.
/// Drawers, dropdowns, modals, nav menus — revealed on hover/click.
/// Port of the hiddenComponents[] array sent in the request body.
/// </summary>
public sealed class HiddenComponent
{
    [JsonProperty("componentId")]   public string  ComponentId   { get; set; } = string.Empty;
    [JsonProperty("segmentId")]     public string? SegmentId     { get; set; }
    [JsonProperty("componentType")] public string  ComponentType { get; set; } = "HiddenPanel";
    [JsonProperty("label")]         public string? Label         { get; set; }
    [JsonProperty("cssB64")]        public string? CssB64        { get; set; }
    [JsonProperty("jsB64")]         public string? JsB64         { get; set; }
    [JsonProperty("elements")]      public List<DomElement> Elements { get; set; } = new();
}
