using System.Text;
using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Services;

/// <summary>
/// Calls OpenRouter directly from the browser using the user's own API key.
/// No SnapStak server ever sees the key or the prompt content.
///
/// Before every request, fetches the live model list from models.snapstak.ai.
/// This increments the SnapStak usage counter and ensures the model ID is
/// always current. Falls back to cached/default models if the endpoint is down.
/// </summary>
public sealed class AiGatewayService
{
    private readonly HttpClient _http;
    private readonly ModelService _models;

    public AiGatewayService(HttpClient http, ModelService models)
    {
        _http = http;
        _models = models;
    }

    private static readonly int[] RetryableCodes = [429, 500, 502, 503, 504];

    /// <summary>
    /// Fetches the live model list (incrementing the usage counter), resolves
    /// the model ID, then calls OpenRouter with the given prompt.
    /// </summary>
    public async Task<string> QueryAsync(
        string prompt,
        string apiKey,
        string modelId,
        int maxTokens = 64000,
        double temperature = 0.1,
        int maxRetries = 3)
    {
        // ── Fetch live model list — increments usage counter ──────────────────
        // This is the marketing analytics trigger. Every generation = one fetch.
        // The resolved modelId is used if found in the live list;
        // otherwise the server-nominated default is used.
        var modelList = await _models.FetchModelsAsync();
        var resolvedId = modelList.Models.Any(m => m.Id == modelId)
            ? modelId
            : modelList.Default;

        // ── Call OpenRouter ───────────────────────────────────────────────────
        var body = JsonConvert.SerializeObject(new
        {
            model = resolvedId,
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = maxTokens,
            temperature,
        });

        var delayMs = 1000;
        for (int attempt = 1; attempt <= maxRetries + 1; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
            req.Headers.Add("HTTP-Referer", "https://snapstak.ai");
            req.Headers.Add("X-Title", "SnapStak CON10X");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req);

            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                if (code == 402)
                    throw new InvalidOperationException("INSUFFICIENT_CREDITS");
                if (RetryableCodes.Contains(code) && attempt <= maxRetries)
                {
                    await Task.Delay(delayMs);
                    delayMs = Math.Min(delayMs * 2, 10000);
                    continue;
                }
                var err = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"OpenRouter {code}: {err[..Math.Min(300, err.Length)]}");
            }

            var json = await resp.Content.ReadAsStringAsync();
            dynamic? parsed = JsonConvert.DeserializeObject(json);
            string text = parsed?.choices?[0]?.message?.content ?? string.Empty;
            return text;
        }

        throw new InvalidOperationException("AI gateway: max retries exceeded.");
    }
}