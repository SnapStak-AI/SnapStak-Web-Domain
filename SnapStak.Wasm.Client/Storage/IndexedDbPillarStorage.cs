using Microsoft.JSInterop;
using Newtonsoft.Json;
using SnapStak.Wasm.Client.Models.Css;
using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Session;

namespace SnapStak.Wasm.Client.Storage;

/// <summary>
/// Blazor WASM IPillarStorage — IndexedDB backend with AES-GCM encryption.
/// Every value is encrypted by PillarEncryption before write and decrypted
/// after read. Dev mode uses plaintext with a "plain:" prefix. Production
/// uses AES-GCM 256 via the Web Crypto API (SnapStakCrypto.js).
/// Key scheme: "{userUuid}/{componentId}/{filename}"
/// </summary>
public sealed class IndexedDbPillarStorage : IPillarStorage
{
    private readonly IJSRuntime _js;
    private readonly PillarEncryption _crypto;

    public IndexedDbPillarStorage(IJSRuntime js, PillarEncryption crypto)
    {
        _js = js;
        _crypto = crypto;
    }

    private static string K(string dir, string file) => $"{dir.TrimEnd('/')}/{file}";
    private static string Dir(string u, string c) => $"{u}/{c}";

    private void Set(string key, string value)
        => _js.InvokeVoidAsync("__snapstak_idb.set", key, _crypto.Encrypt(value))
              .GetAwaiter().GetResult();

    private string? Get(string key)
    {
        try
        {
            var stored = _js.InvokeAsync<string?>("__snapstak_idb.get", key)
                            .GetAwaiter().GetResult();
            return stored == null ? null : _crypto.Decrypt(stored);
        }
        catch { return null; }
    }

    private bool Exists(string key)
        => _js.InvokeAsync<bool>("__snapstak_idb.exists", key).GetAwaiter().GetResult();

    private List<string> ListKeys(string prefix)
        => _js.InvokeAsync<List<string>>("__snapstak_idb.listKeys", prefix)
              .GetAwaiter().GetResult();

    private void DelPrefix(string prefix)
        => _js.InvokeVoidAsync("__snapstak_idb.deletePrefix", prefix).GetAwaiter().GetResult();

    public string ResolveComponentDir(string u, string c) => Dir(u, c);
    public string ResolveIconsDir(string d) => $"{d.TrimEnd('/')}/icons";
    public string ResolveSessionRoot(string u) => u;

    public void WriteSvg(string d, string c, string v) => Set(K(d, $"{c}.svg"), v);
    public string? ReadSvg(string u, string c, string? page = null)
    {
        var v = Get(K(Dir(u, c), $"{c}.svg"));
        if (v != null) return v;
        return page == null ? null : Get(K($"{u}/{page}/components/{c}", $"{c}.svg"));
    }
    public void WriteViewportSvg(string d, string c, int w, string v)
        => Set(K(d, $"{c}_viewport_{w}px.svg"), v);

    public void WriteCssMd(string d, string c, string v) => Set(K(d, $"{c}_css.md"), v);
    public void WriteJsMd(string d, string c, string v) => Set(K(d, $"{c}_js.md"), v);
    public (string? Css, string? Js) ReadBehaviour(string u, string c)
    {
        var d = Dir(u, c);
        return (Get(K(d, $"{c}_css.md")), Get(K(d, $"{c}_js.md")));
    }
    public bool BehaviourMdExists(string d, string c) => Exists(K(d, $"{c}_css.md"));

    public void WriteCssJson(string d, string c, object v) => Set(K(d, $"{c}_css.json"), JsonConvert.SerializeObject(v));
    public void WriteJsJson(string d, string c, object v) => Set(K(d, $"{c}_js.json"), JsonConvert.SerializeObject(v));
    public CssJson? ReadCssJson(string u, string c)
    {
        var raw = Get(K(Dir(u, c), $"{c}_css.json"));
        if (raw == null) return null;
        try { return JsonConvert.DeserializeObject<CssJson>(raw); } catch { return null; }
    }
    public void WriteViewportCssJson(string d, string c, int w, object v)
        => Set(K(d, $"{c}_viewport_{w}px_css.json"), JsonConvert.SerializeObject(v));

    public void WriteInfluence(string d, string c, InfluenceData v) => Set(K(d, $"{c}_influence.json"), JsonConvert.SerializeObject(v));
    public InfluenceData? ReadInfluence(string d, string c)
    {
        var raw = Get(K(d, $"{c}_influence.json"));
        if (raw == null) return null;
        try { return JsonConvert.DeserializeObject<InfluenceData>(raw); } catch { return null; }
    }

    public void WriteObjective(string d, string c, ObjectiveData v) => Set(K(d, $"{c}_objective.json"), JsonConvert.SerializeObject(v));
    public ObjectiveData? ReadObjective(string d, string c)
    {
        var raw = Get(K(d, $"{c}_objective.json"));
        if (raw == null) return null;
        try { return JsonConvert.DeserializeObject<ObjectiveData>(raw); } catch { return null; }
    }

    public void WriteHiddenElements(string d, string c, List<DomElement> v) => Set(K(d, $"{c}_hidden.json"), JsonConvert.SerializeObject(v));
    public void WriteSectionHiddenElements(string d, string c, string tag, List<DomElement> v) => Set(K(d, $"{c}_{tag}_hidden.json"), JsonConvert.SerializeObject(v));
    public List<DomElement>? ReadHiddenElements(string u, string c, string? page)
    {
        var raw = Get(K(Dir(u, c), $"{c}_hidden.json"));
        if (raw == null && page != null)
        {
            var tag = IPillarStorage.InferSectionTag(c);
            if (tag != null) raw = Get(K(Dir(u, page), $"{page}_{tag}_hidden.json"));
        }
        if (raw == null) return null;
        try { return JsonConvert.DeserializeObject<List<DomElement>>(raw); } catch { return null; }
    }

    public void WriteHiddenComponents(string d, string c, List<HiddenComponent> v) => Set(K(d, $"{c}_hidden_components.json"), JsonConvert.SerializeObject(v));
    public void WriteHiddenComponentsSvg(string d, string c, string v) => Set(K(d, $"{c}_hidden_components.svg"), v);
    public void WriteHiddenComponentSvg(string d, string c, string hc, string v) => Set(K(d, $"{hc}.svg"), v);
    public void WriteHiddenComponentSnapshot(string d, string c, string hc, int w, object v) => Set(K(d, $"{c}_{hc}_viewport_{w}px.json"), JsonConvert.SerializeObject(v));

    public void WriteSourceHtml(string d, string c, string tag, string v) => Set(K(d, $"{c}_{tag}_source.html"), v);
    public string? ReadSourceHtml(string u, string c, string? page)
    {
        var v = Get(K(Dir(u, c), $"{c}_source.html"));
        if (v != null) return v;
        if (page == null) return null;
        var tag = IPillarStorage.InferSectionTag(c);
        return tag != null ? Get(K(Dir(u, page), $"{page}_{tag}_source.html")) : null;
    }

    public void WriteViewportSnapshot(string d, string c, int w, object v) => Set(K(d, $"{c}_viewport_{w}px.json"), JsonConvert.SerializeObject(v));
    public List<(int Width, string StorageKey)> ListViewportSnapshots(string u, string c, string? page)
    {
        var r = new List<(int, string)>();
        Parse(ListKeys($"{u}/{c}/{c}_viewport_"), r);
        if (page != null && page != c) Parse(ListKeys($"{u}/{page}/{c}_viewport_"), r);
        return r.OrderBy(x => x.Item1).ToList();

        static void Parse(List<string> keys, List<(int, string)> out_)
        {
            foreach (var key in keys)
            {
                if (!key.EndsWith("px.json")) continue;
                var name = key.Split('/').Last();
                var s = name.IndexOf("_viewport_") + "_viewport_".Length;
                var e = name.LastIndexOf("px.json");
                if (s > 0 && e > s && int.TryParse(name[s..e], out var w)) out_.Add((w, key));
            }
        }
    }

    public void WriteIcon(string d, string id, string v) => Set(K(d, $"{id}.svg"), v);
    public string? ReadIcon(string d, string? pd, string id)
    {
        var v = Get(K($"{d}/icons", $"{id}.svg"));
        return v ?? (pd != null ? Get(K($"{pd}/icons", $"{id}.svg")) : null);
    }

    private string SnapKey(string u) => $"{u}/__pending_snapshot.json";
    private const string SnapDirectKey = "__pending_snapshot.json";
    private const string SnapLocalStorageKey = "snapstak_pending_snapshot";

    public void WriteSnapshot(string u, object v)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(v);
        Set(SnapKey(u), json);
        Set(SnapDirectKey, json);
        try { _js.InvokeVoidAsync("localStorage.setItem", SnapLocalStorageKey, json); }
        catch { }
    }

    public string? ReadSnapshot(string u) => Get(SnapKey(u));

    /// <summary>
    /// Reads the pending snapshot from localStorage — fully synchronous,
    /// no IndexedDB round-trip, no async JS interop delay.
    /// </summary>
    public string? ReadSnapshotDirect()
    {
        try
        {
            if (_js is Microsoft.JSInterop.IJSInProcessRuntime sync)
                return sync.Invoke<string?>("localStorage.getItem", SnapLocalStorageKey);
            return null;
        }
        catch { return null; }
    }
    public void ClearSnapshot(string u) => _js.InvokeVoidAsync("__snapstak_idb.delete", SnapKey(u)).GetAwaiter().GetResult();

    public void WriteManifest(string d, object v) => Set(K(d, "manifest.json"), JsonConvert.SerializeObject(v, Formatting.Indented));
    public string? ReadManifestJson(string d) => Get(K(d, "manifest.json"));

    private string SKey(string u) => $"{u}/__session_manifest.json";
    public SessionManifest? ReadSessionManifest(string u)
    {
        var raw = Get(SKey(u));
        if (raw == null) return null;
        try { return JsonConvert.DeserializeObject<SessionManifest>(raw); } catch { return null; }
    }
    private void WriteSession(string u, SessionManifest m) => Set(SKey(u), JsonConvert.SerializeObject(m, Formatting.Indented));

    public void RegisterComponentInManifest(string u, string c, string d, string zone, string label, bool isMaster = false)
    {
        var m = ReadSessionManifest(u) ?? new SessionManifest { UserUuid = u, CreatedAt = DateTime.UtcNow.ToString("O") };
        m.Components.RemoveAll(x => x.ComponentId == c);
        var files = BuildFiles(d, c);
        m.Components.Add(new SessionComponent { ComponentId = c, Label = label, Zone = zone, Folder = d, Files = files, PillarStatus = BuildStatus(files), IsMaster = isMaster, Status = SessionStatus.Pending, RegisteredAt = DateTime.UtcNow.ToString("O") });
        m.UpdatedAt = DateTime.UtcNow.ToString("O");
        WriteSession(u, m);
    }

    public void UpdateComponentStatus(string u, string c, SessionStatus status, string? err = null, string? zip = null, string? token = null)
    {
        var m = ReadSessionManifest(u); if (m == null) return;
        var e = m.Components.FirstOrDefault(x => x.ComponentId == c); if (e == null) return;
        e.Status = status;
        if (err != null) e.ErrorMessage = err;
        if (zip != null) e.ZipPath = zip;
        if (token != null) e.DownloadToken = token;
        if (status is SessionStatus.Complete or SessionStatus.Failed) e.CompletedAt = DateTime.UtcNow.ToString("O");
        e.Files = BuildFiles(e.Folder, c); e.PillarStatus = BuildStatus(e.Files);
        m.UpdatedAt = DateTime.UtcNow.ToString("O"); WriteSession(u, m);
    }

    public void UpdateSessionStatus(string u, SessionStatus ps, SessionStatus? as_ = null, string? zip = null, string? fw = null, string? pt = null, string? so = null, string? lang = null, string? err = null)
    {
        var m = ReadSessionManifest(u); if (m == null) return;
        m.ProcessingStatus = ps;
        if (as_ != null) m.AssemblyStatus = as_.Value;
        if (zip != null) m.OutputZip = zip;
        if (fw != null) m.Framework = fw;
        if (pt != null) m.PlatformType = pt;
        if (so != null) m.StyleOutput = so;
        if (lang != null) m.Language = lang;
        if (err != null) m.ErrorMessage = err;
        if (ps is SessionStatus.Complete or SessionStatus.Failed) m.CompletedAt = DateTime.UtcNow.ToString("O");
        m.UpdatedAt = DateTime.UtcNow.ToString("O"); WriteSession(u, m);
    }

    public void CleanupSessionFiles(string u, SessionManifest m) { foreach (var comp in m.Components) try { DelPrefix(comp.Folder); } catch { } }
    public void CleanupPillarFiles(string u, string c) => DelPrefix(Dir(u, c));

    private SessionComponentFiles BuildFiles(string d, string c)
    {
        bool E(string s) => Exists(K(d, $"{c}{s}"));
        return new SessionComponentFiles { Structure = E(".svg") ? $"{c}.svg" : null, CssJson = E("_css.json") ? $"{c}_css.json" : null, JsJson = E("_js.json") ? $"{c}_js.json" : null, Influence = E("_influence.json") ? $"{c}_influence.json" : null, Objective = E("_objective.json") ? $"{c}_objective.json" : null, BehaviourCss = E("_css.md") ? $"{c}_css.md" : null, BehaviourJs = E("_js.md") ? $"{c}_js.md" : null };
    }
    private static PillarStatus BuildStatus(SessionComponentFiles f) => new() { Structure = f.Structure != null, Behaviour = f.BehaviourCss != null && f.BehaviourJs != null, Influence = f.Influence != null, Objective = f.Objective != null };

    // ── Translator plugin output ──────────────────────────────────────────────
    //
    // Plugin-owned file extensions — some are text (.svg, .xml, .json), some
    // are binary (.fig, .sketch, .zip). The existing Set/Encrypt pipeline
    // operates on strings, so:
    //   • For text extensions: UTF-8 decode bytes → string → normal path
    //   • For binary extensions: wrap as "b64:<base64>" so the read side
    //     can detect the wrapper and decode back to bytes
    //
    // A corresponding ReadTranslatorOutput(...) reader isn't included here
    // because the current CON10X generation pipeline never reads translator
    // output back — translator output is for export only, consumed by external
    // tools. If a future feature needs to read them back, add the reader then.

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".xml", ".json", ".html", ".htm", ".css", ".js", ".md", ".txt", ".csv", ".tsv", ".yaml", ".yml",
    };

    public void WriteTranslatorOutput(string componentDir, string componentId, string fileExtension, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return;
        if (string.IsNullOrEmpty(fileExtension)) throw new ArgumentException("fileExtension must be non-empty.", nameof(fileExtension));
        if (fileExtension[0] != '.') throw new ArgumentException("fileExtension must start with a dot.", nameof(fileExtension));

        var filename = $"{componentId}{fileExtension}";
        var key = K(componentDir, filename);

        // Check only the final segment of the extension for text/binary classification.
        // ".penpot.svg" → ".svg" (text), ".fig" → ".fig" (binary).
        var lastDot = fileExtension.LastIndexOf('.');
        var finalExt = lastDot >= 0 ? fileExtension[lastDot..] : fileExtension;

        string payload;
        if (TextExtensions.Contains(finalExt))
        {
            payload = System.Text.Encoding.UTF8.GetString(bytes);
        }
        else
        {
            // Binary — Base64-wrap so the string-oriented storage path is lossless.
            payload = "b64:" + Convert.ToBase64String(bytes);
        }

        Set(key, payload);
    }
}