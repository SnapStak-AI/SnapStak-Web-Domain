using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Models.Dom;

public sealed class DomSnapshot
{
    [JsonProperty("elements")]  public List<DomElement> Elements  { get; set; } = new();
    [JsonProperty("pageWidth")] public int              PageWidth  { get; set; } = 1440;
    [JsonProperty("pageHeight")]public int              PageHeight { get; set; } = 900;
    [JsonProperty("pageMap")]   public List<PageMapEntry> PageMap { get; set; } = new();
}

public sealed class PageMapEntry
{
    [JsonProperty("tag")]       public string  Tag       { get; set; } = string.Empty;
    [JsonProperty("segmentId")] public string? SegmentId { get; set; }
    [JsonProperty("label")]     public string? Label     { get; set; }
    [JsonProperty("y")]         public int     Y         { get; set; }
    [JsonProperty("h")]         public int     H         { get; set; }
    [JsonProperty("w")]         public int     W         { get; set; }
    [JsonProperty("cssB64")]    public string? CssB64    { get; set; }
    [JsonProperty("jsB64")]     public string? JsB64     { get; set; }
    [JsonProperty("htmlB64")]   public string? HtmlB64   { get; set; }
}
