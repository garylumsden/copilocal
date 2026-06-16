using Spectre.Console;

using Copilocal.Infrastructure;
using Copilocal.Providers;

namespace Copilocal.Launch;

/// <summary>Per-launch options resolved from the CLI args and the interactive flow.</summary>
internal sealed record LaunchOptions(
    bool DryRun,
    bool Interactive,
    bool Offline,
    string? SessionId,
    string? SessionName,
    bool Resuming,
    List<string> CopilotArgs);

/// <summary>Validates the chosen model, builds the BYOK environment, and launches copilot
/// against it. The env vars are set only on the copilot child process, never persisted.</summary>
internal sealed class Launcher(ProviderHub providers, IProcessRunner proc)
{
    const int OutputTokenContextDivisor = 4;  // leave most context for prompt/tool payload.
    const int MinAutoOutputTokens = 1_024;    // keep enough completion room for Copilot replies.
    const int MaxAutoOutputTokens = 8_192;    // cap local-model completions at practical size.
    const int PromptTokenReserve = 512;       // reserve overhead for provider framing.
    const int LiteLlmTransientRetries = 3;    // allow proxy/model boot grace period.
    const int LiteLlmRetryDelayMs = 700;

    /// <summary>Exit code of the most recently launched copilot process (0 if none launched).</summary>
    internal int LastExitCode { get; private set; }

    /// <summary>Start the provider, validate the model, set the BYOK env, and run copilot.
    /// Returns true when copilot was actually launched (false for dry-run / declined warm-up /
    /// launch failure). The child's exit code is captured in <see cref="LastExitCode"/>.</summary>
    internal bool Launch(MenuItem m, LaunchOptions opts)
    {
        // Dedicated air-gap choice (default off). COPILOT_OFFLINE stops Copilot CLI
        // contacting GitHub's servers and disables telemetry.
        bool offline = opts.Offline;
        if (opts.Interactive)
            offline = AnsiConsole.Prompt(
                new ConfirmationPrompt("Run [yellow]air-gapped[/]? [dim](COPILOT_OFFLINE - no GitHub contact or telemetry)[/]")
                {
                    DefaultValue = opts.Offline,   // defaults to off unless --offline passed
                });

        string baseUrl = AnsiConsole.Status().Start("Starting provider...", _ => providers.EnsureServer(m, preload: !opts.DryRun));
        m.BaseUrl = baseUrl;
        var cfg = LaunchConfig.Load();
        string apiKey = ResolveProviderApiKey(m, cfg);
        bool isLiteLlm = string.Equals(m.Provider, "LiteLLM", StringComparison.Ordinal);
        string? warmupBearer = isLiteLlm ? apiKey : null;
        if (isLiteLlm && apiKey.Length == 0)
        {
            AnsiConsole.MarkupLine(
                "[red]LiteLLM API key not configured.[/] Set an env var (default [white]LITELLM_MASTER_KEY[/]) " +
                "or configure a launch-option key in [white]Configure launch options[/].");
            LastExitCode = 2;
            return false;
        }

        bool useResponses = false;

        // Validate the model actually responds (catches broken GPU EPs that 500 or emit garbage).
        if (!opts.DryRun)
        {
            var warm = AnsiConsole.Status().Start("Warming up model...", _ => providers.WarmUp(baseUrl, m.Model, warmupBearer));
            if (isLiteLlm)
                for (int retry = 0; retry < LiteLlmTransientRetries && IsTransientWarmupFailure(warm); retry++)
                {
                    Thread.Sleep(LiteLlmRetryDelayMs);
                    warm = AnsiConsole.Status().Start("Warming up model...", _ => providers.WarmUp(baseUrl, m.Model, warmupBearer));
                }

            // Reasoning models answer in a 'reasoning' field; the OpenAI Responses wire
            // API handles that cleanly (no empty/null 'content' -> no Ollama 400).
            if (warm.Reasoning && providers.SupportsResponses(baseUrl, warmupBearer))
            {
                useResponses = true;
                AnsiConsole.MarkupLine("[teal]Reasoning model detected[/] — using the OpenAI [white]Responses[/] wire API [dim](/v1/responses)[/].");
            }

            switch (warm.Status)
            {
                case ProviderHub.WarmStatus.Failed:
                    // A reasoning model we can route via /v1/responses isn't a real failure.
                    if (warm.Reasoning && useResponses) break;
                    AnsiConsole.MarkupLine($"[red]Warm-up failed:[/] {Markup.Escape(warm.Detail)}");
                    if (opts.Interactive && !AnsiConsole.Prompt(new ConfirmationPrompt("Launch Copilot anyway?") { DefaultValue = false }))
                        return false;
                    break;
                case ProviderHub.WarmStatus.Suspect:
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(warm.Detail)}");
                    if (opts.Interactive && !AnsiConsole.Prompt(new ConfirmationPrompt("Launch Copilot anyway?") { DefaultValue = true }))
                        return false;
                    break;
                case ProviderHub.WarmStatus.Ok when !useResponses:
                    // Copilot's agentic loop needs native tool calling. Some models are
                    // advertised as tool-capable but emit the call as plain text (breaks
                    // Copilot). The probe's substantive prompt also surfaces *conditional*
                    // reasoners (e.g. gemma) the trivial warm-up missed: those must use the
                    // Responses wire, or their reasoning burns the chat output budget and
                    // truncates the tool call (finish_reason "length" -> undispatchable).
                    var tool = AnsiConsole.Status().Start("Checking tool calling...", _ => providers.ProbeToolCalling(baseUrl, m.Model, warmupBearer));
                    if (isLiteLlm)
                        for (int retry = 0; retry < LiteLlmTransientRetries && IsTransientProbeFailure(tool); retry++)
                        {
                            Thread.Sleep(LiteLlmRetryDelayMs);
                            tool = AnsiConsole.Status().Start("Checking tool calling...", _ => providers.ProbeToolCalling(baseUrl, m.Model, warmupBearer));
                        }
                    if (tool.Reasoning && providers.SupportsResponses(baseUrl, warmupBearer))
                    {
                        useResponses = true;
                        AnsiConsole.MarkupLine("[teal]Reasoning model detected[/] [dim](during tool probe)[/] — using the OpenAI [white]Responses[/] wire API [dim](/v1/responses)[/].");
                    }
                    else if (tool.Status == ProviderHub.ToolStatus.NotNative)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(tool.Detail)}");
                        if (opts.Interactive && !AnsiConsole.Prompt(new ConfirmationPrompt("Launch Copilot anyway?") { DefaultValue = false }))
                            return false;
                    }
                    break;
            }
        }

        var env = new Dictionary<string, string>
        {
            ["COPILOT_PROVIDER_BASE_URL"] = baseUrl,
            ["COPILOT_PROVIDER_TYPE"] = "openai",
            ["COPILOT_MODEL"] = m.Model,
            ["COPILOT_PROVIDER_API_KEY"] = apiKey,
        };
        if (useResponses) env["COPILOT_PROVIDER_WIRE_API"] = "responses";
        if (offline) env["COPILOT_OFFLINE"] = "true";

        // Align Copilot's token budget with the model's real context window. Unknown local
        // models fall back to Copilot's generic defaults, which can overshoot the loaded
        // context and truncate to empty/garbled output. Config overrides win; else derive.
        var (maxPrompt, maxOutput) = TokenLimits(cfg, m, providers);
        if (maxOutput > 0) env["COPILOT_PROVIDER_MAX_OUTPUT_TOKENS"] = maxOutput.ToString();
        if (maxPrompt > 0) env["COPILOT_PROVIDER_MAX_PROMPT_TOKENS"] = maxPrompt.ToString();

        string airgap = offline ? "  [yellow](air-gapped)[/]" : "";
        string resumeNote = opts.Resuming && opts.SessionId is not null ? $"  [dim](resuming {Program.Short(opts.SessionId)})[/]" : "";
        AnsiConsole.MarkupLine($"-> [green][[{Markup.Escape(m.Provider)}]][/] {Markup.Escape(m.Model)}  [dim]@ {baseUrl}[/]{airgap}{resumeNote}");
        if (maxPrompt > 0)
            AnsiConsole.MarkupLine($"[dim]Token budget: prompt {maxPrompt}, output {maxOutput}.[/]");

        if (opts.DryRun)
        {
            AnsiConsole.MarkupLine($"[dim](dry-run) COPILOT_PROVIDER_BASE_URL={baseUrl}  COPILOT_MODEL={m.Model}  COPILOT_OFFLINE={(offline ? "true" : "<unset>")}[/]");
            return false;
        }

        // Inject a stable --session-id so the same conversation can be resumed with a
        // different model. Name it on first launch only (resume by id afterwards).
        var finalArgs = new List<string>();
        if (opts.SessionId is not null)
        {
            finalArgs.Add($"--session-id={opts.SessionId}");
            if (!opts.Resuming && opts.SessionName is { Length: > 0 }) finalArgs.Add($"--name={opts.SessionName}");
        }

        // Apply user-configured launch flags (set via the "Configure launch options" menu).
        finalArgs.AddRange(cfg.ToArgs(providers.ConfiguredMcpServers));
        finalArgs.AddRange(opts.CopilotArgs);

        string copilot = proc.Which("copilot") ?? "copilot";
        try { LastExitCode = proc.RunInherit(copilot, finalArgs, env); return true; }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to launch copilot:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[dim]Install it: [link=https://github.com/github/copilot-cli]https://github.com/github/copilot-cli[/][/]");
            LastExitCode = 127;   // conventional "command not found" so --pick fails non-zero
            return false;
        }
    }

    /// <summary>Derive Copilot's token budget from a model's real context window (config overrides win).</summary>
    internal static (int Prompt, int Output) TokenLimits(LaunchConfig cfg, MenuItem m, ProviderHub providers) =>
        TokenLimits(cfg, providers.ModelContextLength(m));

    internal static (int Prompt, int Output) TokenLimits(LaunchConfig cfg, int ctx)
    {
        int output = cfg.MaxOutputTokens > 0
            ? cfg.MaxOutputTokens
            : (ctx > 0 ? Math.Clamp(ctx / OutputTokenContextDivisor, MinAutoOutputTokens, MaxAutoOutputTokens) : 0);
        int prompt = cfg.MaxPromptTokens > 0
            ? cfg.MaxPromptTokens
            : (ctx > 0 ? Math.Max(PromptTokenReserve, ctx - output - PromptTokenReserve) : 0);
        return (prompt, output);
    }

    internal static string ResolveProviderApiKey(MenuItem m, LaunchConfig cfg)
    {
        if (!string.Equals(m.Provider, "LiteLLM", StringComparison.Ordinal))
            return "local";

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

    static bool IsTransientWarmupFailure((ProviderHub.WarmStatus Status, string Detail, bool Reasoning) warm) =>
        warm.Status == ProviderHub.WarmStatus.Failed && IsTransientHttpOrNetwork(warm.Detail);

    static bool IsTransientProbeFailure((ProviderHub.ToolStatus Status, string Detail, bool Reasoning) probe) =>
        probe.Status == ProviderHub.ToolStatus.Inconclusive && IsTransientHttpOrNetwork(probe.Detail);

    static bool IsTransientHttpOrNetwork(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        string d = detail.ToLowerInvariant();
        return d.StartsWith("http 5", StringComparison.Ordinal)
            || d.StartsWith("http 429", StringComparison.Ordinal)
            || d.Contains("connection refused", StringComparison.Ordinal)
            || d.Contains("actively refused", StringComparison.Ordinal)
            || d.Contains("timed out", StringComparison.Ordinal)
            || d.Contains("timeout", StringComparison.Ordinal)
            || d.Contains("temporarily unavailable", StringComparison.Ordinal);
    }
}
