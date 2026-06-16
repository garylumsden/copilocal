using System.Text.Json;

namespace Copilocal.Providers;

internal static class ProviderParsers
{
    internal static IEnumerable<string> ParseLiteLlmModels(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array) yield break;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idEl)
                    || idEl.ValueKind != JsonValueKind.String) continue;
                string id = (idEl.GetString() ?? "").Trim();
                if (id.Length == 0 || !seen.Add(id)) continue;
                yield return id;
            }
        }
    }

    internal static IEnumerable<(string Id, string LoadId, bool Tools)> ParseFoundry(string json)
    {
        var s = json.IndexOf('{'); var e = json.LastIndexOf('}');
        if (s < 0 || e <= s) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json[s..(e + 1)]); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array) yield break;
            foreach (var m in models.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                string display = Str(m, "displayName");
                if (display.Length == 0) continue;   // need a name to show in the menu
                string variantId = Str(m, "id");
                string alias = Str(m, "alias");
                bool tools = m.TryGetProperty("supportsToolCalling", out var tc) && tc.ValueKind == JsonValueKind.True;
                // Load/info/unload use the concrete variant id (with its ":version") so the exact
                // cached variant is targeted; the bare alias lets Foundry auto-pick a device
                // (e.g. the small-context NPU build) regardless of which variant the user chose.
                string loadId = variantId.Length > 0 ? variantId : (alias.Length > 0 ? alias : display);
                yield return (display, loadId, tools);
            }
        }
    }

    internal static IEnumerable<string> ParseLmStudio(string json)
    {
        var s = json.IndexOf('['); var e = json.LastIndexOf(']');
        if (s < 0 || e <= s) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json[s..(e + 1)]); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                string type = Str(m, "type");
                if (type == "embedding") continue;
                string id = Str(m, "modelKey");
                if (id.Length > 0) yield return id;
            }
        }
    }

    static string Str(JsonElement parent, string prop) =>
        parent.TryGetProperty(prop, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    internal static int NumOrZero(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
