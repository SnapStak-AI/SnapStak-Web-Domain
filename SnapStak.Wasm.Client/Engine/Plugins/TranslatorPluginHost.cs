using System.Net.Http;
using System.Reflection;
using SnapStak.Wasm.Client.Models.Dom;
using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Models.Svg;

namespace SnapStak.Wasm.Client.Engine.Plugins;

// ─────────────────────────────────────────────────────────────────────────────
// TranslatorPluginHost
//
// Discovers every concrete IConteXTranslatorPlugin in the loaded assemblies,
// instantiates each one, and drives both phases of the translation contract
// when the CON10X engine requests output.
//
// ── HOW PLUGINS ARE FOUND ────────────────────────────────────────────────────
//
//   Blazor WASM cannot load assemblies at runtime (AssemblyLoadContext is not
//   available). Plugins therefore live as C# classes inside Engine/Plugins/
//   and are compiled into the app assembly at build time. The reflection scan
//   walks AppDomain.CurrentDomain.GetAssemblies(), which covers both the WASM
//   client and the .NET server hosting process — the same host class works in
//   both environments.
//
// ── HOW TO WRITE A PLUGIN ────────────────────────────────────────────────────
//
//   1. Create a class in any namespace under Engine/Plugins/<YourPlugin>/.
//   2. Implement IConteXTranslatorPlugin — the two-phase contract:
//
//        public sealed class MyPlugin : IConteXTranslatorPlugin
//        {
//            public string Key           => "my-plugin";      // ^[a-z][a-z0-9-]{1,30}$
//            public string DisplayName   => "My Format";
//            public string Version       => "1.0.0";
//            public string FileExtension => ".myformat";      // no path separators
//
//            // Phase 1: return every remote URL whose bytes you need in Phase 2.
//            // Return Array.Empty<string>() if you need no network access.
//            public IReadOnlyList<string> DeclareResources(TranslatorBundle bundle)
//            {
//                return bundle.Desktop.Tree
//                    .SelectMany(FlattenNode)
//                    .Where(n => !string.IsNullOrEmpty(n.ImgSrc)
//                             && n.ImgSrc!.StartsWith("http"))
//                    .Select(n => n.ImgSrc!)
//                    .Distinct()
//                    .ToArray();
//            }
//
//            // Phase 2: all declared URLs are pre-fetched.
//            // fetchedResources[url] = bytes. Missing key = fetch failed — handle it.
//            // Return Array.Empty<byte[]>() to decline output for this bundle.
//            public byte[] Translate(
//                TranslatorBundle bundle,
//                IReadOnlyDictionary<string, byte[]> fetchedResources)
//            {
//                // ... produce your output bytes ...
//            }
//        }
//
//   3. Build. The host discovers your plugin automatically — no registration
//      required. Look for "[TranslatorPluginHost] ✅ Registered 'my-plugin'"
//      in the browser console or server log to confirm.
//
//   RULES:
//     • Parameterless public constructor — required for reflection instantiation.
//     • Key must be unique (first-registered wins on collision).
//     • Pure and side-effect-free — do not touch the filesystem, make HTTP
//       calls, or read shared state in either phase method. The host fetches;
//       the plugin builds.
//     • Phase 1 (DeclareResources) must not throw. Return [] on any error.
//     • Phase 2 (Translate) may throw for genuine programming errors. Returning
//       Array.Empty<byte>() means "I have no output for this bundle" — not an
//       error; the host skips the write silently.
//     • If your plugin needs no network (pure in-memory transform), implement
//       only Translate(TranslatorBundle, IReadOnlyDictionary<string,byte[]>)
//       and return [] from DeclareResources.
//
// ── HOST IS STATELESS (AFTER DISCOVERY) ──────────────────────────────────────
//
//   Safe to register as singleton (server) or scoped (WASM, one per circuit).
//   The plugin instance list is immutable once the first call populates it.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TranslatorPluginHost
{
    // ── Plugin registry ───────────────────────────────────────────────────────

    private IReadOnlyList<IConteXTranslatorPlugin>? _cached;
    private readonly object _lock = new();

    /// <summary>
    /// All discovered translator plugins, one instance per concrete type.
    /// Immutable after the first access. Safe to enumerate multiple times.
    /// </summary>
    public IReadOnlyList<IConteXTranslatorPlugin> Plugins
    {
        get
        {
            if (_cached != null) return _cached;
            lock (_lock)
            {
                _cached ??= Discover();
                return _cached;
            }
        }
    }

    /// <summary>
    /// Force a rediscovery. Only useful in tests that register plugins
    /// dynamically. Production code should treat the plugin set as immutable.
    /// </summary>
    public void Rediscover()
    {
        lock (_lock) { _cached = null; }
    }

    // ── Translation output type ───────────────────────────────────────────────

    /// <summary>
    /// Result of one plugin invocation. Empty <see cref="Bytes"/> with a null
    /// <see cref="Error"/> means the plugin declined to produce output (not an
    /// error — the host skips the write silently).
    /// </summary>
    public readonly record struct TranslationOutput(
        string PluginKey,
        string FileExtension,
        byte[] Bytes,
        Exception? Error);

    // ── Primary entry point: two-phase path ───────────────────────────────────

    /// <summary>
    /// Run every discovered plugin against the given bundle using the full
    /// two-phase contract:
    ///
    ///   Phase 1 — call <see cref="IConteXTranslatorPlugin.DeclareResources"/>
    ///             on every plugin to collect the URLs they need.
    ///   Fetch   — download all declared URLs in parallel using
    ///             <paramref name="http"/>. A null or failed fetch is absent
    ///             from the dict; plugins must handle missing keys gracefully.
    ///   Phase 2 — call <see cref="IConteXTranslatorPlugin.Translate(TranslatorBundle,
    ///             IReadOnlyDictionary{string,byte[]})"/> with the pre-fetched bytes.
    ///
    /// Each plugin is isolated — an exception from one never stops the others.
    /// Pass <c>http: null</c> when running in a context with no network (all
    /// plugins that declare remote URLs will receive an empty dict for those keys).
    /// </summary>
    public async Task<IReadOnlyList<TranslationOutput>> TranslateAllAsync(
        TranslatorBundle bundle,
        HttpClient? http = null)
    {
        if (Plugins.Count == 0) return Array.Empty<TranslationOutput>();

        // ── Phase 1: collect URLs from all plugins ────────────────────────────
        var allUrls = new HashSet<string>(StringComparer.Ordinal);
        var pluginUrls = new Dictionary<IConteXTranslatorPlugin, IReadOnlyList<string>>();

        foreach (var plugin in Plugins)
        {
            IReadOnlyList<string> urls;
            try { urls = plugin.DeclareResources(bundle); }
            catch { urls = Array.Empty<string>(); }
            pluginUrls[plugin] = urls;
            foreach (var u in urls) allUrls.Add(u);
        }

        // ── Fetch: parallel HTTP GETs for every unique URL ────────────────────
        var fetched = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (http != null && allUrls.Count > 0)
        {
            var fetchTasks = allUrls.Select(async url =>
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
                    return (url, bytes, ok: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[TranslatorPluginHost] ⚠️ Fetch failed for '{url}': {ex.Message}");
                    return (url, Array.Empty<byte>(), ok: false);
                }
            });

            foreach (var (url, bytes, ok) in await Task.WhenAll(fetchTasks).ConfigureAwait(false))
            {
                if (ok) fetched[url] = bytes;
            }

            Console.WriteLine(
                $"[TranslatorPluginHost] 📦 Fetched {fetched.Count}/{allUrls.Count} resources.");
        }

        // ── Phase 2: translate ────────────────────────────────────────────────
        var results = new List<TranslationOutput>(Plugins.Count);

        foreach (var plugin in Plugins)
        {
            try
            {
                // Build a read-only view containing only the URLs this plugin
                // declared, so plugins can't accidentally read each other's blobs.
                var pluginFetched = pluginUrls[plugin].Count == 0
                    ? (IReadOnlyDictionary<string, byte[]>)new Dictionary<string, byte[]>()
                    : pluginUrls[plugin]
                        .Where(fetched.ContainsKey)
                        .ToDictionary(u => u, u => fetched[u]);

                var bytes = plugin.Translate(bundle, pluginFetched) ?? Array.Empty<byte>();
                results.Add(new TranslationOutput(plugin.Key, plugin.FileExtension, bytes, null));
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[TranslatorPluginHost] ❌ Plugin '{plugin.Key}' threw: {ex.Message}");
                results.Add(new TranslationOutput(
                    plugin.Key, plugin.FileExtension, Array.Empty<byte>(), ex));
            }
        }

        return results;
    }

    // ── Legacy synchronous entry point (v1 shim) ──────────────────────────────

    /// <summary>
    /// Legacy synchronous overload retained for callers that have not yet
    /// migrated to <see cref="TranslateAllAsync"/>. Builds a
    /// <see cref="TranslatorBundle"/> from the context, runs Phase 1 on every
    /// plugin, but passes an <b>empty</b> fetch map to Phase 2 — remote images
    /// are not fetched.
    ///
    /// Migrate call sites to <see cref="TranslateAllAsync"/> to enable full
    /// image support. This overload is preserved so existing callers compile
    /// unchanged; it will be removed in a future version.
    /// </summary>
    [Obsolete("Migrate to TranslateAllAsync(TranslatorBundle, HttpClient) to enable Phase 1 resource fetching.")]
    public IReadOnlyList<TranslationOutput> TranslateAll(TranslatorContext ctx)
    {
        var bundle = TranslatorBundle.FromContext(ctx);
        var results = new List<TranslationOutput>(Plugins.Count);
        var empty = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var plugin in Plugins)
        {
            try
            {
                var bytes = plugin.Translate(bundle, empty) ?? Array.Empty<byte>();
                results.Add(new TranslationOutput(plugin.Key, plugin.FileExtension, bytes, null));
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[TranslatorPluginHost] ❌ Plugin '{plugin.Key}' threw: {ex.Message}");
                results.Add(new TranslationOutput(
                    plugin.Key, plugin.FileExtension, Array.Empty<byte>(), ex));
            }
        }
        return results;
    }

    // ── Context / bundle builders ─────────────────────────────────────────────

    /// <summary>
    /// Build a <see cref="TranslatorBundle"/> — the v2 input type used by
    /// <see cref="TranslateAllAsync"/>. Accepts the same parameters as the
    /// existing <see cref="BuildContext"/> call sites so the migration is
    /// a one-word rename at each call site.
    /// </summary>
    public static TranslatorBundle BuildBundle(
        List<SvgNode> tree,
        SvgTreeOptions options,
        string componentId,
        InfluenceData? influence = null,
        ObjectiveData? objective = null,
        IReadOnlyList<HiddenComponent>? hiddenComponents = null,
        List<SvgNode>? mobileTree = null,
        SvgTreeOptions? mobileOptions = null,
        IReadOnlyList<TranslatorSegment>? segments = null,
        IReadOnlyList<TranslatorHiddenComponent>? builtHiddenComponents = null)
        => new()
        {
            ComponentId = componentId,
            Desktop = new TranslatorViewport
            {
                Tree = tree,
                Width = options.Width,
                Height = options.Height,
                SourceUrl = options.SourceUrl ?? string.Empty,
                Title = options.Title ?? string.Empty,
            },
            Mobile = mobileTree != null && mobileOptions != null
                ? new TranslatorViewport
                {
                    Tree = mobileTree,
                    Width = mobileOptions.Width,
                    Height = mobileOptions.Height,
                    SourceUrl = mobileOptions.SourceUrl ?? string.Empty,
                    Title = mobileOptions.Title ?? string.Empty,
                }
                : null,
            Segments = segments ?? Array.Empty<TranslatorSegment>(),

            // Priority: pre-built TranslatorHiddenComponent trees (from server pipeline)
            // take precedence over raw HiddenComponent objects (from WASM client path).
            // Pre-built trees already have SvgNode trees populated by the server.
            // Raw HiddenComponent objects are wrapped with empty trees (WASM path —
            // the WASM client runs translators per-component, not in a master bundle).
            HiddenComponents = builtHiddenComponents
                ?? hiddenComponents
                    ?.Select(h => new TranslatorHiddenComponent
                    {
                        ComponentId = h.ComponentId,
                        Label = h.Label ?? h.ComponentType,
                        Tree = Array.Empty<SvgNode>(),
                        Width = 0,
                        Height = 0,
                    })
                    .ToArray()
                ?? Array.Empty<TranslatorHiddenComponent>(),

            Influence = influence,
            Objective = objective,
        };

    /// <summary>
    /// Legacy context builder — kept so existing callers compile unchanged.
    /// For new code, prefer <see cref="BuildBundle"/> and
    /// <see cref="TranslateAllAsync"/>.
    /// </summary>
    public static TranslatorContext BuildContext(
        IReadOnlyList<SvgNode> tree,
        SvgTreeOptions options,
        string componentId,
        InfluenceData? influence = null,
        ObjectiveData? objective = null,
        IReadOnlyList<HiddenComponent>? hiddenComponents = null)
        => new()
        {
            Tree = tree,
            Options = options,
            ComponentId = componentId,
            Influence = influence,
            Objective = objective,
            HiddenComponents = hiddenComponents,
        };

    // ── Reflection-based discovery ────────────────────────────────────────────

    private static List<IConteXTranslatorPlugin> Discover()
    {
        var contract = typeof(IConteXTranslatorPlugin);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instances = new List<IConteXTranslatorPlugin>();

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }
            catch { continue; }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!contract.IsAssignableFrom(type)) continue;

                // Parameterless public constructor — required.
                var ctor = type.GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                {
                    Console.WriteLine(
                        $"[TranslatorPluginHost] ⚠️  {type.FullName} implements " +
                        $"IConteXTranslatorPlugin but has no parameterless constructor — skipped.");
                    continue;
                }

                IConteXTranslatorPlugin instance;
                try { instance = (IConteXTranslatorPlugin)ctor.Invoke(null); }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[TranslatorPluginHost] ⚠️  {type.FullName} threw from its " +
                        $"constructor — skipped. ({ex.Message})");
                    continue;
                }

                var key = instance.Key ?? string.Empty;
                if (!IsValidKey(key))
                {
                    Console.WriteLine(
                        $"[TranslatorPluginHost] ⚠️  {type.FullName} has invalid Key " +
                        $"'{key}' — must match ^[a-z][a-z0-9-]{{1,30}}$. Skipped.");
                    continue;
                }
                if (!seenKeys.Add(key))
                {
                    Console.WriteLine(
                        $"[TranslatorPluginHost] ⚠️  Duplicate plugin Key '{key}' from " +
                        $"{type.FullName} — skipped. First-registered wins.");
                    continue;
                }
                if (string.IsNullOrEmpty(instance.FileExtension) ||
                    instance.FileExtension.IndexOfAny(new[] { '/', '\\' }) >= 0)
                {
                    Console.WriteLine(
                        $"[TranslatorPluginHost] ⚠️  {type.FullName} has invalid " +
                        $"FileExtension '{instance.FileExtension}' — skipped.");
                    continue;
                }

                instances.Add(instance);
                Console.WriteLine(
                    $"[TranslatorPluginHost] ✅  Registered '{instance.Key}' " +
                    $"({instance.DisplayName} v{instance.Version}) → {instance.FileExtension}");
            }
        }

        if (instances.Count == 0)
            Console.WriteLine("[TranslatorPluginHost] ⚠️  No translator plugins registered.");

        return instances;
    }

    private static bool IsValidKey(string k)
    {
        if (string.IsNullOrEmpty(k) || k.Length > 31) return false;
        if (!char.IsLower(k[0])) return false;
        for (int i = 1; i < k.Length; i++)
        {
            var c = k[i];
            if (!(char.IsLower(c) || char.IsDigit(c) || c == '-')) return false;
        }
        return true;
    }
}