using SnapStak.Wasm.Client.Engine.RulesEngine.Prompts;
using SnapStak.Wasm.Client.Models.Css;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Requests;

namespace SnapStak.Wasm.Client.Services;

/// <summary>
/// WASM equivalent of RulesEngineCom.
/// No database — rules are embedded in the prompt builders.
/// All prompt-building methods delegate to the existing static prompt classes.
/// </summary>
public sealed class RulesEngineService
{
    public string BuildCssBehaviourPrompt(string componentId, CssJson css,
        string? htmlSkeleton, string? sourceHtml, InfluenceData? influence)
        => CssBehaviourPrompt.Build(componentId, css, htmlSkeleton, sourceHtml, influence);

    public string BuildJsBehaviourPrompt(string componentId, object js)
        => JsBehaviourPrompt.Build(componentId, js);

    public string BuildConteXCodePrompt(ConteXCodePromptParams p)
        => ConteXCodePrompt.Build(p);
}
