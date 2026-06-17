using SnapStak.Wasm.Client.Models.Css;
using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Session;

namespace SnapStak.Wasm.Client.Storage;

public interface IPillarStorage
{
    string ResolveComponentDir(string userUuid, string componentId);
    string ResolveIconsDir(string componentDir);
    string ResolveSessionRoot(string userUuid);

    void WriteSvg(string componentDir, string componentId, string svgContent);
    string? ReadSvg(string userUuid, string componentId, string? pageComponentId = null);
    void WriteViewportSvg(string componentDir, string componentId, int viewportWidth, string svgContent);

    void WriteCssMd(string componentDir, string componentId, string content);
    void WriteJsMd(string componentDir, string componentId, string content);
    (string? Css, string? Js) ReadBehaviour(string userUuid, string componentId);
    bool BehaviourMdExists(string componentDir, string componentId);

    void WriteCssJson(string componentDir, string componentId, object css);
    void WriteJsJson(string componentDir, string componentId, object js);
    CssJson? ReadCssJson(string userUuid, string componentId);
    void WriteViewportCssJson(string componentDir, string componentId, int viewportWidth, object css);

    void WriteInfluence(string componentDir, string componentId, InfluenceData influence);
    InfluenceData? ReadInfluence(string componentDir, string componentId);

    void WriteObjective(string componentDir, string componentId, ObjectiveData objective);
    ObjectiveData? ReadObjective(string componentDir, string componentId);

    void WriteHiddenElements(string componentDir, string componentId, List<DomElement> elements);
    void WriteSectionHiddenElements(string componentDir, string componentId, string tag, List<DomElement> elements);
    List<DomElement>? ReadHiddenElements(string userUuid, string componentId, string? pageComponentId);

    void WriteHiddenComponents(string componentDir, string componentId, List<HiddenComponent> components);
    void WriteHiddenComponentsSvg(string componentDir, string componentId, string svgContent);
    void WriteHiddenComponentSvg(string componentDir, string componentId, string hiddenComponentId, string svgContent);
    void WriteHiddenComponentSnapshot(string componentDir, string componentId, string hiddenComponentId, int viewportWidth, object snapshot);

    void WriteSourceHtml(string componentDir, string componentId, string tag, string html);
    string? ReadSourceHtml(string userUuid, string componentId, string? pageComponentId);

    void WriteViewportSnapshot(string componentDir, string componentId, int viewportWidth, object snapshot);
    List<(int Width, string StorageKey)> ListViewportSnapshots(string userUuid, string componentId, string? pageComponentId);

    void WriteIcon(string iconsDir, string internalId, string svgContent);
    string? ReadIcon(string componentDir, string? pageComponentDir, string internalId);

    void WriteSnapshot(string userUuid, object snapshot);
    string? ReadSnapshot(string userUuid);
    string? ReadSnapshotDirect();
    void ClearSnapshot(string userUuid);

    void WriteManifest(string dir, object manifest);
    string? ReadManifestJson(string dir);

    SessionManifest? ReadSessionManifest(string userUuid);
    void RegisterComponentInManifest(string userUuid, string componentId, string componentDir, string zone, string label, bool isMaster = false);
    void UpdateComponentStatus(string userUuid, string componentId, SessionStatus status, string? errorMessage = null, string? zipPath = null, string? downloadToken = null);
    void UpdateSessionStatus(string userUuid, SessionStatus processingStatus, SessionStatus? assemblyStatus = null, string? outputZip = null, string? framework = null, string? platformType = null, string? styleOutput = null, string? language = null, string? errorMessage = null);
    void CleanupSessionFiles(string userUuid, SessionManifest manifest);
    void CleanupPillarFiles(string userUuid, string componentId);

    // ── Translator plugin output ──────────────────────────────────────────────
    //
    // Writes a byte[] produced by a translator plugin, using the plugin's
    // self-declared file extension. Examples:
    //   WriteTranslatorOutput(dir, "example_1234", ".penpot.svg", bytes)
    //     → stored under key "{dir}/example_1234.penpot.svg"
    //   WriteTranslatorOutput(dir, "example_1234", ".fig", bytes)
    //     → stored under key "{dir}/example_1234.fig"
    //
    // The fileExtension includes the leading dot and comes verbatim from the
    // plugin's IConteXTranslatorPlugin.FileExtension property. The storage
    // implementation is binary-safe — SVG/XML translators pass UTF-8 encoded
    // bytes; binary translators (.fig, .sketch) pass raw bytes.
    //
    // Translator outputs live in the same component folder as the master SVG,
    // alongside the pillar files.
    void WriteTranslatorOutput(string componentDir, string componentId, string fileExtension, byte[] bytes);

    static string? InferSectionTag(string componentId)
    {
        var known = new[] { "header", "main", "footer", "nav", "navbar", "section", "article", "aside", "form", "div" };
        var lower = componentId.ToLowerInvariant();
        foreach (var tag in known)
            if (lower.StartsWith(tag + "_")) return tag;
        return null;
    }
}