using System.Text.Json;

using Copilocal.Launch;

namespace Copilocal.Providers;

internal sealed partial class ProviderHub
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

    List<MenuItem> GatherFoundry()
    {
        var items = new List<MenuItem>();
        if (!HasFoundry) return items;
        var (code, outp, _) = proc.Run(FoundryExe, "cache list -o json", DiscoverTimeoutMs);
        if (code != 0) return items;
        foreach (var (id, loadId, tools) in ParseFoundry(outp))
            items.Add(new MenuItem { Kind = MenuItemKind.Model, Provider = "Foundry", BaseUrl = null, Model = id, LoadAlias = loadId, Tools = tools });
        return items;
    }

    List<MenuItem> GatherLmStudio()
    {
        var items = new List<MenuItem>();
        if (!File.Exists(LmsExe)) return items;
        var (code, outp, _) = proc.Run(LmsExe, "ls --json", DiscoverTimeoutMs);
        if (code != 0) return items;
        foreach (var id in ParseLmStudio(outp))
            items.Add(new MenuItem { Kind = MenuItemKind.Model, Provider = "LM Studio", BaseUrl = "http://localhost:1234/v1", Model = id, Tools = true });
        return items;
    }

    List<MenuItem> GatherLiteLlm()
    {
        var items = new List<MenuItem>();
        var cfg = LaunchConfig.Load();
        if (!cfg.LiteLlmEnabled) return items;
        string baseUrl = LaunchConfig.NormalizeBaseUrl(cfg.LiteLlmBaseUrl);
        string apiKey = ResolveLiteLlmApiKey(cfg);
        const int maxAttempts = 5;
        const int attemptTimeoutMs = 1_200;
        const int retryDelayMs = 500;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                string body = http.GetString($"{baseUrl}/models", attemptTimeoutMs, apiKey);
                foreach (var id in ParseLiteLlmModels(body))
                    items.Add(new MenuItem { Kind = MenuItemKind.Model, Provider = "LiteLLM", BaseUrl = baseUrl, Model = id, Tools = true });
                return items;
            }
            catch (HttpRequestException ex) when (
                attempt < maxAttempts &&
                ex.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden))
            {
                Thread.Sleep(retryDelayMs);
            }
            catch (OperationCanceledException) when (attempt < maxAttempts)
            {
                Thread.Sleep(retryDelayMs);
            }
            catch (JsonException) when (attempt < maxAttempts)
            {
                Thread.Sleep(retryDelayMs);
            }
            catch (InvalidOperationException) when (attempt < maxAttempts)
            {
                Thread.Sleep(retryDelayMs);
            }
            catch (HttpRequestException) { return items; }
            catch (JsonException) { return items; }
            catch (OperationCanceledException) { return items; }
            catch (InvalidOperationException) { return items; }
        }
        return items;
    }

    static string ResolveLiteLlmApiKey(LaunchConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.LiteLlmApiKey))
            return LaunchConfig.NormalizeLiteLlmApiKey(cfg.LiteLlmApiKey);
        if (!string.IsNullOrWhiteSpace(cfg.LiteLlmApiKeyEnvVar))
        {
            string fromNamed = Environment.GetEnvironmentVariable(cfg.LiteLlmApiKeyEnvVar.Trim()) ?? "";
            if (!string.IsNullOrWhiteSpace(fromNamed))
                return LaunchConfig.NormalizeLiteLlmApiKey(fromNamed);
        }
        string fallback = Environment.GetEnvironmentVariable(LaunchConfig.DefaultLiteLlmApiKeyEnvVar) ?? "";
        return LaunchConfig.NormalizeLiteLlmApiKey(fallback);
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
}
