using System.Text;
using System.Text.Json;

namespace Copilocal.Launch;

/// <summary>User-configurable flags applied to the `copilot` launch, persisted to
/// ~/.copilocal/config.json. Set via the "Configure launch options" menu item.</summary>
internal sealed class LaunchConfig
{
    /// <summary>Sentinel token (not a real copilot flag): expands to a
    /// <c>--disable-mcp-server=NAME</c> for every server in ~/.copilot/mcp-config.json.</summary>
    internal const string DisableUserMcpsToken = "@disable-user-mcps";

    /// <summary>Catalog of toggleable boolean options: (menu label, token added to the launch).
    /// Tokens are real copilot flags except <see cref="DisableUserMcpsToken"/>.</summary>
    internal static readonly (string Label, string Token)[] Catalog =
    {
        ("Disable built-in MCP server (github)",               "--disable-builtin-mcps"),
        ("Disable user MCP servers (mcp-config.json)",         DisableUserMcpsToken),
        ("Disable skills (exclude the 'skill' tool)",          "--excluded-tools=skill"),
        ("Allow all tools (--allow-all-tools)",                "--allow-all-tools"),
        ("Allow all file paths (--allow-all-paths)",           "--allow-all-paths"),
        ("Allow all URLs (--allow-all-urls)",                  "--allow-all-urls"),
        ("YOLO - allow everything (--yolo)",                   "--yolo"),
        ("Experimental features (--experimental)",             "--experimental"),
        ("Autopilot mode (--autopilot)",                       "--autopilot"),
        ("Plan mode (--plan)",                                 "--plan"),
        ("No custom instructions (AGENTS.md)",                 "--no-custom-instructions"),
        ("No ask_user - work autonomously (--no-ask-user)",    "--no-ask-user"),
        ("Enable memory (--enable-memory)",                    "--enable-memory"),
        ("Disable remote control (--no-remote)",               "--no-remote"),
        ("Disallow temp-dir access (--disallow-temp-dir)",     "--disallow-temp-dir"),
        ("Reasoning summaries (--enable-reasoning-summaries)", "--enable-reasoning-summaries"),
        ("Show startup banner (--banner)",                     "--banner"),
        ("Screen-reader mode (--screen-reader)",               "--screen-reader"),
        ("No color (--no-color)",                              "--no-color"),
        ("No auto-update (--no-auto-update)",                  "--no-auto-update"),
    };

    internal static readonly string[] ReasoningEfforts = { "none", "low", "medium", "high", "xhigh", "max" };
    internal static readonly string[] LiteLlmRuntimeModes = { "docker", "python" };
    internal const string DefaultLiteLlmBaseUrl = "http://localhost:4000/v1";
    internal const string DefaultLiteLlmApiKeyEnvVar = "LITELLM_MASTER_KEY";

    /// <summary>Enabled tokens from <see cref="Catalog"/>.</summary>
    internal HashSet<string> Flags { get; set; } = new();
    /// <summary>One of <see cref="ReasoningEfforts"/>, or "" to leave unset.</summary>
    internal string ReasoningEffort { get; set; } = "";
    /// <summary>Max prompt tokens (COPILOT_PROVIDER_MAX_PROMPT_TOKENS); 0 = auto from model context.</summary>
    internal int MaxPromptTokens { get; set; }
    /// <summary>Max output tokens (COPILOT_PROVIDER_MAX_OUTPUT_TOKENS); 0 = auto.</summary>
    internal int MaxOutputTokens { get; set; }
    /// <summary>Any other raw copilot args, space-separated.</summary>
    internal string ExtraArgs { get; set; } = "";
    /// <summary>Enable LiteLLM provider discovery and launch path in the picker.</summary>
    internal bool LiteLlmEnabled { get; set; }
    /// <summary>When LiteLLM is enabled, hide local provider rows from the picker.</summary>
    internal bool HideLocalProvidersWhenLiteLlm { get; set; }
    /// <summary>LiteLLM OpenAI-compatible base URL.</summary>
    internal string LiteLlmBaseUrl { get; set; } = DefaultLiteLlmBaseUrl;
    /// <summary>Optional explicit LiteLLM API key (env var remains preferred).</summary>
    internal string LiteLlmApiKey { get; set; } = "";
    /// <summary>Environment variable name to read LiteLLM API key from.</summary>
    internal string LiteLlmApiKeyEnvVar { get; set; } = DefaultLiteLlmApiKeyEnvVar;
    /// <summary>LiteLLM runtime mode selected for install/lifecycle actions.</summary>
    internal string LiteLlmRuntimeMode { get; set; } = "docker";

    static string Dir => Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilocal");
    internal static string FilePath => Path.Join(Dir, "config.json");

    /// <summary>Load preferences from <paramref name="path"/> (defaults to <see cref="FilePath"/>).
    /// The <paramref name="path"/> override keeps this unit-testable without touching the real
    /// user config.</summary>
    internal static LaunchConfig Load(string? path = null)
    {
        path ??= FilePath;
        var c = new LaunchConfig();
        try
        {
            if (!File.Exists(path)) return c;
            using var d = JsonDocument.Parse(File.ReadAllText(path));
            var r = d.RootElement;
            if (r.TryGetProperty("flags", out var fa) && fa.ValueKind == JsonValueKind.Array)
                foreach (var el in fa.EnumerateArray().Where(el => el.ValueKind == JsonValueKind.String))
                    c.Flags.Add(el.GetString()!);
            c.ReasoningEffort = GetStr(r, "reasoningEffort");
            c.MaxPromptTokens = GetInt(r, "maxPromptTokens");
            c.MaxOutputTokens = GetInt(r, "maxOutputTokens");
            c.ExtraArgs = GetStr(r, "extraArgs");
            c.LiteLlmEnabled = GetBool(r, "liteLlmEnabled");
            c.HideLocalProvidersWhenLiteLlm = GetBool(r, "hideLocalProvidersWhenLiteLlm");
            c.LiteLlmBaseUrl = NormalizeBaseUrl(GetStr(r, "liteLlmBaseUrl"));
            c.LiteLlmApiKey = NormalizeLiteLlmApiKey(GetStr(r, "liteLlmApiKey"));
            c.LiteLlmApiKeyEnvVar = GetStr(r, "liteLlmApiKeyEnvVar");
            c.LiteLlmRuntimeMode = GetStr(r, "liteLlmRuntimeMode");
            if (string.IsNullOrWhiteSpace(c.LiteLlmApiKeyEnvVar))
                c.LiteLlmApiKeyEnvVar = DefaultLiteLlmApiKeyEnvVar;
            if (Array.IndexOf(LiteLlmRuntimeModes, c.LiteLlmRuntimeMode) < 0)
                c.LiteLlmRuntimeMode = "docker";
        }
        catch (IOException)
        {
            // best-effort: invalid or unreadable config should fall back to defaults.
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort: invalid or unreadable config should fall back to defaults.
        }
        catch (JsonException)
        {
            // best-effort: invalid or unreadable config should fall back to defaults.
        }
        catch (InvalidOperationException)
        {
            // best-effort: a valid-JSON but non-object config root falls back to defaults.
        }
        return c;
    }

    /// <summary>Persist preferences to <paramref name="path"/> (defaults to <see cref="FilePath"/>).
    /// Uses <see cref="Utf8JsonWriter"/> so escaping is correct and AOT-safe (the reflection-based
    /// serializer is unavailable under Native AOT).</summary>
    internal void Save(string? path = null)
    {
        path ??= FilePath;
        string tmpPath = path + ".tmp";
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var stream = File.Create(tmpPath))
            {
                using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteStartArray("flags");
                    foreach (var f in Flags.OrderBy(f => f, StringComparer.Ordinal))
                        w.WriteStringValue(f);
                    w.WriteEndArray();
                    w.WriteString("reasoningEffort", ReasoningEffort);
                    w.WriteNumber("maxPromptTokens", MaxPromptTokens);
                    w.WriteNumber("maxOutputTokens", MaxOutputTokens);
                    w.WriteString("extraArgs", ExtraArgs);
                    w.WriteBoolean("liteLlmEnabled", LiteLlmEnabled);
                    w.WriteBoolean("hideLocalProvidersWhenLiteLlm", HideLocalProvidersWhenLiteLlm);
                    w.WriteString("liteLlmBaseUrl", NormalizeBaseUrl(LiteLlmBaseUrl));
                    w.WriteString("liteLlmApiKey", NormalizeLiteLlmApiKey(LiteLlmApiKey));
                    w.WriteString("liteLlmApiKeyEnvVar", LiteLlmApiKeyEnvVar);
                    w.WriteString("liteLlmRuntimeMode", LiteLlmRuntimeMode);
                    w.WriteEndObject();
                    w.Flush();
                    stream.Flush(true);
                }
            }
            ReplaceFile(tmpPath, path);
        }
        catch (IOException)
        {
            // best-effort: failing to persist preferences should not block launch.
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort: failing to persist preferences should not block launch.
        }
        finally
        {
            TryDelete(tmpPath);
        }
    }

    static void ReplaceFile(string tmpPath, string path)
    {
        try
        {
            File.Move(tmpPath, path, overwrite: true);
            return;
        }
        catch (Exception ex) when (CanFallbackReplace(ex))
        {
        }

        try
        {
            File.Replace(tmpPath, path, null);
            return;
        }
        catch (Exception ex) when (CanFallbackReplace(ex))
        {
        }

        if (File.Exists(path)) File.Delete(path);
        File.Move(tmpPath, path);
    }

    static bool CanFallbackReplace(Exception ex) =>
        ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException;

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Translate the config into copilot CLI arguments. <paramref name="configuredMcpServers"/>
    /// resolves the user's MCP server names (for the disable-user-mcps token); when omitted no
    /// such flags are emitted. The config never reaches for providers/IO itself.</summary>
    internal List<string> ToArgs(Func<List<string>>? configuredMcpServers = null)
    {
        var resolve = configuredMcpServers ?? (static () => new List<string>());
        var a = new List<string>();
        foreach (var f in Flags.OrderBy(f => f, StringComparer.Ordinal))
        {
            if (f == DisableUserMcpsToken)
                foreach (var s in resolve()) a.Add($"--disable-mcp-server={s}");
            else
                a.Add(f);
        }
        if (Array.IndexOf(ReasoningEfforts, ReasoningEffort) >= 0)
            a.Add($"--reasoning-effort={ReasoningEffort}");
        if (!string.IsNullOrWhiteSpace(ExtraArgs)) a.AddRange(SplitArgs(ExtraArgs));
        return a;
    }

    static string GetStr(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int GetInt(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    static bool GetBool(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) && v.GetBoolean();

    internal static string NormalizeBaseUrl(string raw)
    {
        string trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0) return DefaultLiteLlmBaseUrl;
        string noSlash = trimmed.TrimEnd('/');
        return noSlash.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? noSlash : $"{noSlash}/v1";
    }

    internal static string NormalizeLiteLlmApiKey(string raw)
    {
        string trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0) return "";
        return trimmed.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"sk-{trimmed}";
    }

    /// <summary>Split a raw argument string on whitespace, honoring double quotes.</summary>
    static IEnumerable<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool inQuote = false;
        foreach (char ch in s)
        {
            if (ch == '"') inQuote = !inQuote;
            else if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
            }
            else cur.Append(ch);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }
}
