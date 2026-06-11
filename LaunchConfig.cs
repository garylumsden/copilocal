using System.Text;
using System.Text.Json;

namespace Copilocal;

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

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilocal");
    internal static string FilePath => Path.Combine(Dir, "config.json");

    internal static LaunchConfig Load()
    {
        var c = new LaunchConfig();
        try
        {
            if (!File.Exists(FilePath)) return c;
            using var d = JsonDocument.Parse(File.ReadAllText(FilePath));
            var r = d.RootElement;
            if (r.TryGetProperty("flags", out var fa) && fa.ValueKind == JsonValueKind.Array)
                foreach (var el in fa.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String) c.Flags.Add(el.GetString()!);
            c.ReasoningEffort = GetStr(r, "reasoningEffort");
            c.MaxPromptTokens = GetInt(r, "maxPromptTokens");
            c.MaxOutputTokens = GetInt(r, "maxOutputTokens");
            c.ExtraArgs = GetStr(r, "extraArgs");
        }
        catch (Exception)
        {
            // best-effort: invalid or unreadable config should fall back to defaults.
        }
        return c;
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Hand-build JSON: reflection-based JsonSerializer is disabled under AOT.
            var sb = new StringBuilder();
            sb.Append("{\n  \"flags\": [");
            var ordered = Flags.OrderBy(f => f, StringComparer.Ordinal).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                sb.Append(i == 0 ? "\n    \"" : ",\n    \"");
                sb.Append(Json.Escape(ordered[i])).Append('"');
            }
            sb.Append(ordered.Count > 0 ? "\n  ],\n" : "],\n");
            sb.Append($"  \"reasoningEffort\": \"{Json.Escape(ReasoningEffort)}\",\n");
            sb.Append($"  \"maxPromptTokens\": {MaxPromptTokens},\n");
            sb.Append($"  \"maxOutputTokens\": {MaxOutputTokens},\n");
            sb.Append($"  \"extraArgs\": \"{Json.Escape(ExtraArgs)}\"\n}}\n");
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception)
        {
            // best-effort: failing to persist preferences should not block launch.
        }
    }

    /// <summary>Translate the config into copilot CLI arguments.</summary>
    internal List<string> ToArgs(Func<List<string>>? configuredMcpServers = null)
    {
        var a = new List<string>();
        foreach (var f in Flags.OrderBy(f => f, StringComparer.Ordinal))
        {
            if (f == DisableUserMcpsToken)
                foreach (var s in (configuredMcpServers ?? DefaultConfiguredMcpServers)()) a.Add($"--disable-mcp-server={s}");
            else
                a.Add(f);
        }
        if (Array.IndexOf(ReasoningEfforts, ReasoningEffort) >= 0)
            a.Add($"--reasoning-effort={ReasoningEffort}");
        if (!string.IsNullOrWhiteSpace(ExtraArgs)) a.AddRange(SplitArgs(ExtraArgs));
        return a;
    }

    static List<string> DefaultConfiguredMcpServers() =>
        new Providers(new ProcessRunner(), new HttpGateway()).ConfiguredMcpServers();

    static string GetStr(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int GetInt(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

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
