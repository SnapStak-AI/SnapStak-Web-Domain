using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SnapStak.Wasm.Client.Models.Css;

/// <summary>
/// Top-level CSS capture from the Chrome extension.
/// Structure: { matched[], behavior[], media[], keyframes[] }
/// Written to {componentId}_css.json.
/// Read by BehaviourAgent and passed to the Rules Engine prompt builder.
/// </summary>
public sealed class CssJson
{
    [JsonProperty("matched")] public List<CssRule> Matched { get; set; } = new();
    [JsonProperty("behavior")] public List<CssRule> Behavior { get; set; } = new();
    [JsonProperty("media")] public List<MediaBlock> Media { get; set; } = new();
    [JsonProperty("keyframes")] public List<string> Keyframes { get; set; } = new();

    public bool IsEmpty =>
        Matched.Count == 0 &&
        Behavior.Count == 0 &&
        Media.Count == 0 &&
        Keyframes.Count == 0;
}

public sealed class CssRule
{
    [JsonProperty("selector")] public string? Selector { get; set; }
    [JsonProperty("cssText")] public string? CssText { get; set; }

    /// <summary>
    /// CSS properties — accepts either a JSON object { "display": "flex" }
    /// or a flat CSS string "display: flex; align-items: center;" and
    /// normalises both into a Dictionary.
    /// </summary>
    [JsonProperty("properties")]
    [JsonConverter(typeof(CssPropertiesConverter))]
    public Dictionary<string, string>? Properties { get; set; }
}

public sealed class MediaBlock
{
    [JsonProperty("media")] public string? Media { get; set; }
    [JsonProperty("mediaQuery")] public string? MediaQuery { get; set; }
    [JsonProperty("query")] public string? Query { get; set; }
    [JsonProperty("rules")] public List<CssRule> Rules { get; set; } = new();

    /// <summary>Resolved media query string from any of the three property names.</summary>
    public string ResolvedMediaQuery =>
        Media ?? MediaQuery ?? Query ?? string.Empty;
}

/// <summary>
/// Handles the properties field arriving as either:
///   - A JSON object: { "display": "flex", "color": "red" }
///   - A flat CSS string: "display: flex; color: red;"
/// Both are normalised into Dictionary&lt;string, string&gt;.
/// </summary>
internal sealed class CssPropertiesConverter : JsonConverter<Dictionary<string, string>?>
{
    public override Dictionary<string, string>? ReadJson(
        JsonReader reader,
        Type objectType,
        Dictionary<string, string>? existingValue,
        bool hasExistingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        // Already a JSON object — deserialize normally
        if (reader.TokenType == JsonToken.StartObject)
        {
            var obj = JObject.Load(reader);
            return obj.Properties()
                      .ToDictionary(p => p.Name, p => p.Value.ToString());
        }

        // Flat CSS string — parse into dictionary
        if (reader.TokenType == JsonToken.String)
        {
            var css = (string?)reader.Value ?? string.Empty;
            return ParseCssString(css);
        }

        // Skip anything unexpected
        reader.Skip();
        return null;
    }

    public override void WriteJson(
        JsonWriter writer,
        Dictionary<string, string>? value,
        JsonSerializer serializer)
        => serializer.Serialize(writer, value);

    private static Dictionary<string, string> ParseCssString(string css)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = declaration.IndexOf(':');
            if (colon < 0) continue;
            string prop = declaration[..colon].Trim();
            string value = declaration[(colon + 1)..].Trim();
            if (!string.IsNullOrEmpty(prop))
                result[prop] = value;
        }
        return result;
    }
}