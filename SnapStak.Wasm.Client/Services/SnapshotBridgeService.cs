using Microsoft.JSInterop;
using Newtonsoft.Json;
using SnapStak.Wasm.Client.Models.Requests;

namespace SnapStak.Wasm.Client.Services;

/// <summary>
/// Listens on the local server SSE stream (/api/events) via the browser
/// native EventSource API (JS interop). HttpClient streaming does not work
/// reliably in Blazor WASM — EventSource is the correct browser primitive.
///
/// The Chrome extension's background.js handles content.js injection into the
/// target tab independently. This service only needs the SSE connection to
/// receive the forwarded snapshot payload.
/// </summary>
public sealed class SnapshotBridgeService : IDisposable
{
    private readonly IJSRuntime _js;
    private DotNetObjectReference<SnapshotBridgeService>? _dotNetRef;

    public event Action<TransformRequest>? OnSnapshot;

    private const string ServerBase = "http://localhost:5174";

    public SnapshotBridgeService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task ConnectAsync()
    {
        // Open SSE connection via browser native EventSource.
        // The extension's background.js handles tab injection — no inject call needed here.
        _dotNetRef = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("__snapstak_sse_connect", ServerBase + "/api/events", _dotNetRef);
    }

    [JSInvokable]
    public void OnSseMessage(string data)
    {
        try
        {
            // data IS the JSON string of TransformRequest — deserialize directly.
            // Do NOT call DeserializeObject<string>(data) first — that extra unwrap
            // returns null because the SSE payload is not a JSON-encoded string,
            // it is the actual object JSON. The snapshot would be silently dropped.
            if (string.IsNullOrWhiteSpace(data)) return;

            var request = JsonConvert.DeserializeObject<TransformRequest>(data);
            if (request != null)
            {
                Console.WriteLine($"[Bridge] Snapshot received — {request.ComponentId}");
                OnSnapshot?.Invoke(request);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bridge] Parse error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _js.InvokeVoidAsync("__snapstak_sse_disconnect");
        _dotNetRef?.Dispose();
    }
}