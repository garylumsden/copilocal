using System.Text.Json;

using Copilocal.Infrastructure;
using Copilocal.Launch;

namespace Copilocal.Providers;

internal sealed partial class ProviderHub
{
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
}
