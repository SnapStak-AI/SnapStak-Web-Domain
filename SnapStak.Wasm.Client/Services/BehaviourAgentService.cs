using SnapStak.Wasm.Client.Models.Css;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Storage;
using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Services;

/// <summary>
/// WASM port of BehaviourAgentCom.
/// Runs CSS and JS behaviour AI prompts in parallel.
/// Writes _css.md and _js.md to IndexedDB.
/// </summary>
public sealed class BehaviourAgentService
{
    private readonly IPillarStorage    _storage;
    private readonly RulesEngineService _rules;
    private readonly AiGatewayService  _ai;

    public BehaviourAgentService(IPillarStorage s, RulesEngineService r, AiGatewayService a)
    { _storage = s; _rules = r; _ai = a; }

    public async Task WriteBehaviourDescriptionsAsync(BehaviourRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey)) return;

        var jobs = new List<Task>();

        var css = request.ComponentCss as CssJson;
        if (css != null && !css.IsEmpty)
        {
            var cssPrompt = _rules.BuildCssBehaviourPrompt(
                request.ComponentId, css, request.HtmlSkeleton, request.SourceHtml, request.Influence);

            jobs.Add(Task.Run(async () =>
            {
                try
                {
                    var desc = await _ai.QueryAsync(cssPrompt, request.ApiKey, request.ModelId);
                    _storage.WriteCssMd(request.ComponentDir, request.ComponentId, desc);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BehaviourAgent] CSS failed: {ex.Message}");
                }
            }));
        }

        if (HasJs(request.ComponentJs))
        {
            var jsPrompt = _rules.BuildJsBehaviourPrompt(request.ComponentId, request.ComponentJs!);

            jobs.Add(Task.Run(async () =>
            {
                try
                {
                    var desc = await _ai.QueryAsync(jsPrompt, request.ApiKey, request.ModelId);
                    _storage.WriteJsMd(request.ComponentDir, request.ComponentId, desc);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BehaviourAgent] JS failed: {ex.Message}");
                }
            }));
        }

        await Task.WhenAll(jobs);
    }

    private static bool HasJs(object? js)
    {
        if (js == null) return false;
        try
        {
            var json = JsonConvert.SerializeObject(js);
            return (json.Contains("\"scripts\":[") && !json.Contains("\"scripts\":[]"))
                || (json.Contains("\"inlineHandlers\":[") && !json.Contains("\"inlineHandlers\":[]"));
        }
        catch { return false; }
    }
}
