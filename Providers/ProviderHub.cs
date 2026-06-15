using System.Text.Json;

using Copilocal.Infrastructure;
using Copilocal.Launch;

namespace Copilocal.Providers;

/// <summary>Discovers installed providers, their models, and performs installs.</summary>
internal sealed class ProviderHub(IProcessRunner proc, IHttpGateway http)
{
    string? _ollama, _foundry, _lms;
    bool _ollamaSet, _foundrySet, _lmsSet;

    internal string OllamaExe
    {
        get
        {
            if (!_ollamaSet) { _ollama = proc.Which("ollama") ?? Path.Join(LocalAppData, "Programs", "Ollama", "ollama.exe"); _ollamaSet = true; }
            return _ollama!;
        }
    }

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

    internal string FoundryExe
    {
        get
        {
            if (!_foundrySet) { _foundry = proc.Which("foundry") ?? Path.Join(LocalAppData, "Microsoft", "WindowsApps", "foundry.exe"); _foundrySet = true; }
            return _foundry!;
        }
    }

    internal string LmsExe
    {
        get
        {
            if (!_lmsSet) { _lms = proc.Which("lms") ?? Path.Join(UserProfile, ".lmstudio", "bin", "lms.exe"); _lmsSet = true; }
            return _lms!;
        }
    }

    internal static string LmStudioApp => Path.Join(LocalAppData, "Programs", "LM Studio", "LM Studio.exe");

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal bool HasOllama => File.Exists(OllamaExe);
    internal bool HasFoundry => File.Exists(FoundryExe);
    internal bool HasLmStudio => File.Exists(LmsExe) || File.Exists(LmStudioApp);

    /// <summary>True when the GitHub Copilot CLI (`copilot`) is resolvable on PATH — the tool
    /// copilocal ultimately launches.</summary>
    internal bool HasCopilot => proc.Which("copilot") is not null;

    // ---------------- model gathering ----------------

    // Discovery commands are local and fast when warm (<0.5s) but a provider whose
    // background service is cold can block while it wakes. Cap the per-command wait so one
    // slow provider can't stall for the default 30s...
    const int DiscoverTimeoutMs = 15000;
    // ...and cap the *total* discovery wait so a cold/stuck provider can't hang startup:
    // we render with whoever responded and mark stragglers (they'll be warm next run).
    const int DiscoverBudgetMs = 8000;

    internal List<MenuItem> GatherModels(
        bool includeLocalProviders = true,
        bool includeLiteLlm = false,
        Action<string, int>? onProvider = null)
    {
        // Query only installed providers, in parallel; report each provider's result
        // as it lands so the UI can log progress instead of looking like a hang.
        var pending = new List<(string Name, Task<List<MenuItem>> Task)>();
        if (includeLocalProviders)
        {
            if (HasOllama) pending.Add(("Ollama", Task.Run(GatherOllama)));
            if (HasFoundry) pending.Add(("Foundry Local", Task.Run(GatherFoundry)));
            if (HasLmStudio) pending.Add(("LM Studio", Task.Run(GatherLmStudio)));
        }
        if (includeLiteLlm)
            pending.Add(("LiteLLM", Task.Run(GatherLiteLlm)));

        var items = new List<MenuItem>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (pending.Count > 0)
        {
            int remaining = DiscoverBudgetMs - (int)sw.ElapsedMilliseconds;
            if (remaining <= 0) break;   // out of budget; abandon stragglers
            int idx = Task.WaitAny(pending.Select(p => p.Task).ToArray(), remaining);
            if (idx < 0) break;          // budget elapsed waiting
            var result = pending[idx].Task.Result;
            onProvider?.Invoke(pending[idx].Name, result.Count);
            items.AddRange(result);
            pending.RemoveAt(idx);
        }
        // Providers that didn't finish in time (cold service); -1 signals "skipped".
        foreach (var p in pending)
            onProvider?.Invoke(p.Name, -1);
        return items;
    }

    List<MenuItem> GatherOllama()
    {
        var items = new List<MenuItem>();
        if (!HasOllama) return items;
        var (code, outp, _) = proc.Run(OllamaExe, "list", DiscoverTimeoutMs);
        if (code != 0) return items;
        foreach (var line in outp.Split('\n'))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Contains(':') &&
                parts[1].Length == 12 && parts[1].All(Uri.IsHexDigit))
                items.Add(new MenuItem { Kind = MenuItemKind.Model, Provider = "Ollama", BaseUrl = "http://localhost:11434/v1", Model = parts[0], Tools = true });
        }
        return items;
    }

    // ---------------- context checks ----------------
    //
    // Copilot's system prompt + tool schemas are large (often 20k+ tokens). A model whose
    // context window is smaller truncates the prompt -> empty/garbled replies, a "continue"
    // loop, Ollama's "400 invalid message content type: <nil>", or Foundry's "input_ids size
    // ... exceeds max length". MinContext is the floor below which copilocal warns.
    //
    // Per provider: Ollama loads models at OLLAMA_CONTEXT_LENGTH (default 4096 when unset);
    // LM Studio is read from its native REST API; Foundry NPU/OpenVINO variants are compiled
    // with a small fixed context (e.g. 4224) read from `foundry model info`.

    internal const int MinContext = 16384;

    /// <summary>Configured OLLAMA_CONTEXT_LENGTH, or 0 when unset/invalid.</summary>
    internal int OllamaContextLength() =>
        int.TryParse(Environment.GetEnvironmentVariable("OLLAMA_CONTEXT_LENGTH"), out var c) && c > 0 ? c : 0;

    /// <summary>Effective context window (tokens) the model will serve, or 0 if unknown.
    /// Ollama uses OLLAMA_CONTEXT_LENGTH (default 4096); LM Studio is read from its native
    /// REST API (loaded context if loaded, else the model's max); Foundry from `model info`.</summary>
    internal int ModelContextLength(MenuItem m)
    {
        // Each branch is self-protecting (HTTP/JSON guarded), so no wrapper needed.
        switch (m.Provider)
        {
            case "Ollama":
                return OllamaEffectiveContext(m);
            case "LM Studio":
                return LmStudioContextLength(m.Model, m.BaseUrl);
            case "Foundry":
                return FoundryContextLength(m);
            case "LiteLLM":
                return 0;
            default:
                return 0;   // unknown
        }
    }

    /// <summary>Ollama's effective context (tokens) for the model. Prefers the actually-loaded
    /// context from <c>/api/ps</c> (ground truth); otherwise what Ollama will load at —
    /// OLLAMA_CONTEXT_LENGTH (default 4096) clamped to the model's trained max from <c>/api/show</c>.
    /// This avoids assuming a flat 4096 and catches a large env var on a small-context model.</summary>
    int OllamaEffectiveContext(MenuItem m)
    {
        string host = OllamaHost(m.BaseUrl);
        int loaded = OllamaLoadedContext(host, m.Model);
        if (loaded > 0) return loaded;
        int env = OllamaContextLength();
        int want = env > 0 ? env : 4096;
        int max = OllamaModelMaxContext(host, m.Model);
        return max > 0 ? Math.Min(want, max) : want;
    }

    static string OllamaHost(string? baseUrl)
    {
        string host = baseUrl ?? "http://localhost:11434/v1";
        int i = host.IndexOf("/v1", StringComparison.Ordinal);
        return i >= 0 ? host[..i] : host;
    }

    /// <summary>Context a model is currently loaded at, from Ollama's <c>/api/ps</c>, or 0 if it
    /// isn't loaded or Ollama is unreachable.</summary>
    int OllamaLoadedContext(string host, string model)
    {
        try
        {
            using var d = JsonDocument.Parse(http.GetString($"{host}/api/ps", 8_000));
            if (d.RootElement.ValueKind != JsonValueKind.Object
                || !d.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array) return 0;
            foreach (var el in models.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && n.GetString() == model) return NumOrZero(el, "context_length");
            }
            return 0;
        }
        catch (HttpRequestException) { return 0; }
        catch (JsonException) { return 0; }
        catch (OperationCanceledException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>Model's trained maximum context from Ollama's <c>/api/show</c> (the model_info
    /// "&lt;arch&gt;.context_length"), or 0 if unknown. Ollama clamps any requested context to this.</summary>
    int OllamaModelMaxContext(string host, string model)
    {
        try
        {
            var (ok, _, body) = http.PostJson($"{host}/api/show", "{\"model\":\"" + Json.Escape(model) + "\"}", 12_000);
            if (!ok) return 0;
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.ValueKind != JsonValueKind.Object
                || !d.RootElement.TryGetProperty("model_info", out var info)
                || info.ValueKind != JsonValueKind.Object) return 0;
            foreach (var p in info.EnumerateObject())
                if (p.Name.EndsWith(".context_length", StringComparison.Ordinal)
                    && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n))
                    return n;
            return 0;
        }
        catch (HttpRequestException) { return 0; }
        catch (JsonException) { return 0; }
        catch (OperationCanceledException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>Foundry's compiled context window for the model, via `foundry model info -o json`
    /// (its `contextLength`), or 0 if unknown. NPU/OpenVINO variants report a small fixed value
    /// (e.g. 4224) that is far too small for Copilot's prompt.</summary>
    int FoundryContextLength(MenuItem m)
    {
        try
        {
            string alias = m.LoadAlias ?? m.Model;
            var (code, outp, _) = proc.Run(FoundryExe, $"model info {alias} -o json", DiscoverTimeoutMs);
            if (code != 0) return 0;
            int s = outp.IndexOf('{'); int e = outp.LastIndexOf('}');
            if (s < 0 || e <= s) return 0;
            using var d = JsonDocument.Parse(outp[s..(e + 1)]);
            return d.RootElement.ValueKind == JsonValueKind.Object
                && d.RootElement.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.Object
                ? NumOrZero(model, "contextLength") : 0;
        }
        catch (JsonException)
        {
            // best-effort: missing/changed model-info output leaves the context unknown.
            return 0;
        }
    }

    int LmStudioContextLength(string modelId, string? baseUrl)
    {
        try
        {
            string host = baseUrl ?? "http://localhost:1234/v1";
            int i = host.IndexOf("/v1", StringComparison.Ordinal);
            if (i >= 0) host = host[..i];
            // LM Studio 0.4.0+ native REST API (the older /api/v0 is legacy). Models live
            // under "models"; the loaded window is loaded_instances[].config.context_length,
            // and we fall back to the model's max when nothing is loaded yet (LM Studio
            // JIT-loads on the first request, so pre-launch there is usually no instance).
            string body = http.GetString($"{host}/api/v1/models", 8_000);
            using var d = JsonDocument.Parse(body);
            if (!d.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return 0;
            foreach (var el in models.EnumerateArray()
                .Where(el => el.TryGetProperty("key", out var key) && key.GetString() == modelId))
            {
                if (el.TryGetProperty("loaded_instances", out var insts) && insts.ValueKind == JsonValueKind.Array)
                    foreach (var inst in insts.EnumerateArray())
                        if (inst.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
                        {
                            int loaded = NumOrZero(cfg, "context_length");
                            if (loaded > 0) return loaded;
                        }
                return NumOrZero(el, "max_context_length");
            }
            return 0;
        }
        catch (HttpRequestException)
        {
            // best-effort: LM Studio may be stopped or return non-JSON while warming.
            return 0;
        }
        catch (JsonException)
        {
            // best-effort: LM Studio may be stopped or return non-JSON while warming.
            return 0;
        }
        catch (OperationCanceledException)
        {
            // best-effort: LM Studio may be stopped or return non-JSON while warming.
            return 0;
        }
        catch (InvalidOperationException)
        {
            // best-effort: an unexpected JSON shape (e.g. a different service on the port) is unknown.
            return 0;
        }
    }

    static int NumOrZero(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>Names of MCP servers configured in ~/.copilot/mcp-config.json (for disabling them).</summary>
    internal List<string> ConfiguredMcpServers()
    {
        var names = new List<string>();
        try
        {
            string f = Path.Join(UserProfile, ".copilot", "mcp-config.json");
            if (!File.Exists(f)) return names;
            using var d = JsonDocument.Parse(File.ReadAllText(f));
            if (d.RootElement.ValueKind == JsonValueKind.Object
                && d.RootElement.TryGetProperty("mcpServers", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject()) names.Add(p.Name);
        }
        catch (IOException)
        {
            // best-effort: malformed/unreadable MCP config simply means no disables.
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort: malformed/unreadable MCP config simply means no disables.
        }
        catch (JsonException)
        {
            // best-effort: malformed/unreadable MCP config simply means no disables.
        }
        return names;
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
                string display = m.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                if (display.Length == 0) continue;   // need a name to show in the menu
                string variantId = m.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                string alias = m.TryGetProperty("alias", out var al) ? al.GetString() ?? "" : "";
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
                string type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type == "embedding") continue;
                string id = m.TryGetProperty("modelKey", out var k) ? k.GetString() ?? "" : "";
                if (id.Length > 0) yield return id;
            }
        }
    }

    // ---------------- server ensure ----------------

    /// <summary>Make sure the chosen provider's OpenAI server is running (and, when
    /// <paramref name="preload"/>, the Foundry model is loaded); return its base URL.</summary>
    internal string EnsureServer(MenuItem m, bool preload = true)
    {
        switch (m.Provider)
        {
            case "Foundry":
                proc.Run(FoundryExe, "server start", 60000);
                string url = "http://127.0.0.1:5273";
                var (code, outp, _) = proc.Run(FoundryExe, "server status -o json");
                if (code == 0)
                {
                    var s = outp.IndexOf('{'); var e = outp.LastIndexOf('}');
                    if (s >= 0 && e > s)
                        try
                        {
                            using var d = JsonDocument.Parse(outp[s..(e + 1)]);
                            if (d.RootElement.ValueKind == JsonValueKind.Object
                                && d.RootElement.TryGetProperty("webUrls", out var w)
                                && w.ValueKind == JsonValueKind.Array && w.GetArrayLength() > 0
                                && w[0].ValueKind == JsonValueKind.String)
                                url = w[0].GetString() ?? url;
                        }
                        catch (JsonException)
                        {
                            // best-effort: keep Foundry default URL if status JSON is noisy.
                        }
                }
                // Foundry's OpenAI endpoint won't auto-load: load the model now so
                // Copilot's first request doesn't fail with "model is not loaded".
                if (preload)
                    proc.Run(FoundryExe, $"model load {m.LoadAlias ?? m.Model}", 180000);
                return $"{url}/v1";

            case "LM Studio":
                proc.Run(LmsExe, "server start", 60000);
                return m.BaseUrl ?? "http://localhost:1234/v1";

            case "LiteLLM":
                return LaunchConfig.NormalizeBaseUrl(m.BaseUrl ?? LaunchConfig.DefaultLiteLlmBaseUrl);

            default: // Ollama autostarts its server
                return m.BaseUrl ?? "http://localhost:11434/v1";
        }
    }

    /// <summary>Unload a model from memory to free VRAM (best-effort; ignores errors).</summary>
    internal void Unload(MenuItem m)
    {
        // proc.Run captures its own failures (never throws), so unload is inherently best-effort.
        switch (m.Provider)
        {
            case "Foundry": proc.Run(FoundryExe, $"model unload {m.LoadAlias ?? m.Model}", 60000); break;
            case "LM Studio": proc.Run(LmsExe, $"unload {m.Model}", 30000); break;
            case "LiteLLM": break;
            default: proc.Run(OllamaExe, $"stop {m.Model}", 30000); break;   // Ollama
        }
    }

    internal enum WarmStatus { Ok, Suspect, Failed }

    /// <summary>Send one tiny completion to validate the model actually responds.
    /// Catches broken GPU execution providers (500/garbage) and flags reasoning models
    /// (answer lands in a 'reasoning'/'reasoning_content' field) - on the chat/completions
    /// wire those produce empty 'content', making Copilot loop / Ollama 400. The caller
    /// switches reasoning models to the Responses wire API to avoid that.</summary>
    internal (WarmStatus Status, string Detail, bool Reasoning) WarmUp(string baseUrl, string model, string? bearerToken = null)
    {
        try
        {
            // Build JSON by hand: reflection-based JsonSerializer is disabled under AOT.
            string esc = Json.Escape(model);
            // A short instruction that still yields real prose: fast to generate, yet long
            // enough (> 8 chars) that the LooksGarbled smoke-test stays live to catch
            // broken execution providers that emit looping/garbage output.
            string payload =
                "{\"model\":\"" + esc + "\"," +
                "\"messages\":[{\"role\":\"user\",\"content\":\"Reply with one short sentence confirming you are ready.\"}]," +
                "\"max_tokens\":48,\"temperature\":0}";
            var (ok, status, body) = http.PostJson($"{baseUrl}/chat/completions", payload, 120_000, bearerToken);
            if (!ok)
                return (WarmStatus.Failed, HttpFailureDetail(status, body), false);

            // Reasoning models separate their thinking from the answer; field name varies
            // by provider: Ollama uses "reasoning", LM Studio/vLLM use "reasoning_content".
            bool reasoning = !string.IsNullOrWhiteSpace(ExtractMessageField(body, "reasoning"))
                          || !string.IsNullOrWhiteSpace(ExtractMessageField(body, "reasoning_content"));
            string text = ExtractMessageField(body, "content");
            if (string.IsNullOrWhiteSpace(text))
            {
                if (reasoning)
                    return (WarmStatus.Failed,
                        "reasoning model: it returns its thinking separately and leaves 'content' empty on the " +
                        "chat/completions wire, so Copilot sees blank answers (and Ollama returns " +
                        "\"400 invalid message content type: <nil>\"). The Responses wire API fixes this; " +
                        "if unavailable here, pick a non-reasoning model (e.g. qwen2.5-coder:14b).",
                        true);
                return (WarmStatus.Failed, "empty response", false);
            }
            if (LooksGarbled(text))
                return (WarmStatus.Suspect, $"output looks garbled: \"{Trim(text)}\"", reasoning);
            return (WarmStatus.Ok, Trim(text), reasoning);
        }
        catch (HttpRequestException ex) { return (WarmStatus.Failed, (ex.InnerException ?? ex).Message, false); }
        catch (OperationCanceledException ex) { return (WarmStatus.Failed, (ex.InnerException ?? ex).Message, false); }
    }

    internal enum ToolStatus { Ok, NotNative, Inconclusive }

    /// <summary>Validate the model performs *native* tool calling on the chat/completions
    /// wire, which Copilot's agentic loop depends on. Some models (e.g. Ollama's
    /// qwen2.5-coder) are advertised as tool-capable yet emit the call as plain text in
    /// 'content' with tool_calls null - that silently breaks Copilot. We use tool_choice
    /// "auto" with a generous token budget on purpose: "required" is unreliable on Ollama
    /// for thinking models, and a small budget makes a reasoning model burn its tokens
    /// thinking before it can emit the call (finish_reason "length" -> truncated, undispatchable
    /// tool call). This substantive prompt also surfaces *conditional* reasoners (e.g.
    /// gemma) that the trivial warm-up prompt misses, so we return a Reasoning flag for
    /// the caller to route via the Responses wire. Best-effort: anything other than a clear
    /// "emits-as-text" failure is reported as inconclusive (don't block).</summary>
    internal (ToolStatus Status, string Detail, bool Reasoning) ProbeToolCalling(string baseUrl, string model, string? bearerToken = null)
    {
        try
        {
            string esc = Json.Escape(model);
            string payload =
                "{\"model\":\"" + esc + "\"," +
                "\"messages\":[{\"role\":\"user\",\"content\":\"Use the get_time tool to report the current time.\"}]," +
                "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"get_time\"," +
                "\"description\":\"Get the current time\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}]," +
                "\"tool_choice\":\"auto\",\"max_tokens\":512,\"temperature\":0}";
            var (ok, status, body) = http.PostJson($"{baseUrl}/chat/completions", payload, 120_000, bearerToken);
            if (!ok) return (ToolStatus.Inconclusive, HttpFailureDetail(status, body), false);
            bool reasoning = !string.IsNullOrWhiteSpace(ExtractMessageField(body, "reasoning"))
                          || !string.IsNullOrWhiteSpace(ExtractMessageField(body, "reasoning_content"));
            if (HasToolCall(body)) return (ToolStatus.Ok, "native tool calling OK", reasoning);
            if (LooksLikeToolCallText(ExtractMessageField(body, "content")))
                return (ToolStatus.NotNative,
                    "model emits tool calls as plain text (tool_calls is empty), which breaks Copilot's " +
                    "agentic loop. Pick a model with native tool calling (e.g. granite4, qwen3, llama3.2).",
                    reasoning);
            return (ToolStatus.Inconclusive, "model did not call the tool", reasoning);
        }
        catch (HttpRequestException ex) { return (ToolStatus.Inconclusive, (ex.InnerException ?? ex).Message, false); }
        catch (OperationCanceledException ex) { return (ToolStatus.Inconclusive, (ex.InnerException ?? ex).Message, false); }
    }

    /// <summary>True if the chat/completions response carries a non-empty tool_calls array.</summary>
    internal static bool HasToolCall(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (!TryFirstMessage(d, out var msg)) return false;
            return msg.TryGetProperty("tool_calls", out var tc)
                && tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            // best-effort: a malformed body just means "no native tool call seen".
            return false;
        }
    }

    /// <summary>Resolve <c>choices[0].message</c> from a chat/completions body, guarding the shape
    /// so navigation can't throw (only a JSON parse error can).</summary>
    static bool TryFirstMessage(JsonDocument d, out JsonElement message)
    {
        message = default;
        var root = d.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return false;
        var first = choices[0];
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("message", out var msg)
            || msg.ValueKind != JsonValueKind.Object) return false;
        message = msg;
        return true;
    }

    /// <summary>Heuristic: the model dumped a tool call into 'content' instead of emitting
    /// native tool_calls. Catches raw JSON (e.g. {"name":...,"arguments":...}) and the
    /// &lt;tool_call&gt; tag wrapping some templates use.</summary>
    internal static bool LooksLikeToolCallText(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        bool jsonShape = content.Contains("\"name\"", StringComparison.Ordinal)
                      && content.Contains("\"arguments\"", StringComparison.Ordinal);
        return jsonShape
            || content.Contains("get_time", StringComparison.Ordinal)
            || content.Contains("tool_call", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if the provider exposes an OpenAI Responses endpoint (/v1/responses).
    /// Ollama and LM Studio do; this lets reasoning models work without the
    /// chat/completions null-content 400. A 404/405 means it isn't implemented.</summary>
    internal bool SupportsResponses(string baseUrl, string? bearerToken = null)
    {
        try
        {
            // 404/405 => the route isn't implemented; any other status means it exists.
            var (_, status, _) = http.PostJson($"{baseUrl}/responses", "{}", 10_000, bearerToken);
            return status is not (404 or 405);
        }
        catch (HttpRequestException)
        {
            // best-effort: failed probe means keep chat/completions.
            return false;
        }
        catch (OperationCanceledException)
        {
            // best-effort: failed probe means keep chat/completions.
            return false;
        }
    }

    internal static string ExtractMessageField(string body, string field)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (!TryFirstMessage(d, out var msg)) return "";
            return msg.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            // best-effort: malformed warm-up response becomes empty field.
            return "";
        }
    }

    // Heuristic: a coherent answer to the warm-up prompt is prose that is mostly
    // letters. Flag short repeating units or a low letter ratio. (Avoids a word-count
    // rule - it false-flagged terse-but-valid replies like "Ready to assist!" where
    // short connectives aren't counted - and char-diversity, which is naturally low for
    // longer English prose. Both caused false positives.)
    internal static bool LooksGarbled(string t)
    {
        string nospace = new(t.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (nospace.Length < 8) return false;

        // Short repeating unit, e.g. "UKUKUK" or "._._._".
        foreach (int unit in new[] { 1, 2, 3 })
        {
            if (nospace.Length < unit * 4) continue;
            string head = nospace[..unit];
            bool allSame = true;
            for (int i = 0; i + unit <= nospace.Length; i += unit)
                if (nospace.Substring(i, unit) != head) { allSame = false; break; }
            if (allSame) return true;
        }

        // Mostly digits/punctuation, e.g. "90. 111 161 .222 33r 440 666" or "alpha 123 456".
        int letters = nospace.Count(char.IsLetter);
        if ((double)letters / nospace.Length < 0.55) return true;

        return false;
    }

    static string Trim(string t)
    {
        string one = t.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return one.Length > 60 ? one[..60] + "..." : one;
    }

    static string HttpFailureDetail(int status, string body)
    {
        string summary = ErrorSummary(body);
        return summary.Length > 0 ? $"HTTP {status}: {summary}" : $"HTTP {status}";
    }

    static string ErrorSummary(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var d = JsonDocument.Parse(body);
            var root = d.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    string fromError = StringField(error, "message");
                    if (fromError.Length > 0) return SingleLine(fromError);
                    if (error.ValueKind == JsonValueKind.String)
                        return SingleLine(error.GetString() ?? "");
                }
                string fromMessage = StringField(root, "message");
                if (fromMessage.Length > 0) return SingleLine(fromMessage);
                string fromDetail = StringField(root, "detail");
                if (fromDetail.Length > 0) return SingleLine(fromDetail);
            }
            else if (root.ValueKind == JsonValueKind.String)
                return SingleLine(root.GetString() ?? "");
        }
        catch (JsonException)
        {
            // best-effort: non-JSON bodies fall back to a first-line summary.
        }
        return SingleLine(body);
    }

    static string StringField(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    static string SingleLine(string text)
    {
        string line = (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        const int max = 180;
        return line.Length <= max ? line : $"{line[..max]}…";
    }

}
