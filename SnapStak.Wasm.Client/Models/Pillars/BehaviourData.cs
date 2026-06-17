namespace SnapStak.Wasm.Client.Models.Pillars;

/// <summary>
/// Pillar 2: Behaviour — What does it do.
/// Loaded from {componentId}_css.md and {componentId}_js.md.
/// Written by BehaviourAgent after AI Gateway QUERY completes.
/// </summary>
public sealed class BehaviourData
{
    /// <summary>Content of {componentId}_css.md — semantic CSS description.</summary>
    public string? Css { get; set; }

    /// <summary>Content of {componentId}_js.md — semantic JS description.</summary>
    public string? Js  { get; set; }
}

/// <summary>
/// Request payload for the BehaviourAgent.
/// </summary>
public sealed class BehaviourRequest
{
    public required string  UserUuid      { get; set; }
    public required string  ComponentId   { get; set; }
    public required string  ComponentDir  { get; set; }
    public required string  ApiKey        { get; set; }
    public required string  ModelId       { get; set; }
    public          object? ComponentCss  { get; set; }
    public          object? ComponentJs   { get; set; }
    public          string? HtmlSkeleton  { get; set; }
    public          string? SourceHtml    { get; set; }
    public          Models.Pillars.InfluenceData? Influence { get; set; }
}
