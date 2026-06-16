using System.Text.Json;

using Copilocal.Infrastructure;
using Copilocal.Launch;

namespace Copilocal.Providers;

/// <summary>Discovers installed providers, their models, and performs installs.</summary>
internal sealed partial class ProviderHub(IProcessRunner proc, IHttpGateway http)
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
}
