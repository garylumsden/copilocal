using Spectre.Console;

using Copilocal.Providers;

namespace Copilocal.Launch;

/// <summary>Pre-launch guards that warn before copilocal hands Copilot a model likely to fail:
/// one that doesn't advertise tool calling, or whose context window is too small for Copilot's
/// large agentic prompt. Interactive runs may override (default No); non-interactive picks are
/// blocked so a scripted launch fails fast with a clear reason instead of a cryptic 500.</summary>
internal static class Preflight
{
    /// <summary>Run all guards. Returns true if the launch should proceed.</summary>
    internal static bool Ok(MenuItem m, bool interactive, ProviderHub providers) =>
        ToolCallingOk(m, interactive) && ContextOk(m, interactive, providers);

    // ---------------- tool calling ----------------

    static bool ToolCallingOk(MenuItem m, bool interactive)
    {
        if (m.Tools) return true;   // advertised; the runtime probe still validates Ollama/LM Studio.
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
                $"[yellow]{Markup.Escape(m.Model)} does not advertise tool calling.[/]\n" +
                "Copilot CLI's agentic loop depends on native tool calls — without them it can't run\n" +
                "tools, so you'll typically get errors or an unresponsive session.\n\n" +
                "[dim]Pick a model whose current catalog entry explicitly reports tool support.[/]")
            .Header("Model lacks tool calling").BorderColor(Color.Yellow).RoundedBorder());
        return AskLaunchAnyway(interactive);
    }

    // ---------------- context window ----------------

    internal static bool ContextOk(MenuItem m, bool interactive, ProviderHub providers)
    {
        int ctx = providers.ModelContextLength(m);   // 0 = unknown -> can't judge, don't block
        if (ctx == 0 || ctx >= ProviderHub.RecommendedContext) return true;

        AnsiConsole.WriteLine();
        bool tooSmall = ctx < ProviderHub.MinContext;
        AnsiConsole.Write(new Panel(ContextBody(m, ctx, providers, tooSmall))
            .Header(tooSmall ? "Context window too small" : "Context window below recommendation")
            .BorderColor(Color.Yellow)
            .RoundedBorder());

        if (tooSmall) return AskLaunchAnyway(interactive);
        if (!interactive) return true;
        return AnsiConsole.Prompt(new ConfirmationPrompt("Launch with reduced usable context?") { DefaultValue = true });
    }

    static string ContextBody(MenuItem m, int ctx, ProviderHub providers, bool tooSmall) => m.Provider switch
    {
        "Ollama" => OllamaBody(ctx, providers, tooSmall),
        "Foundry" => FoundryBody(ctx, tooSmall),
        "LM Studio" => LmStudioBody(ctx, tooSmall),
        _ => GenericBody(ctx, tooSmall),
    };

    static string OllamaBody(int ctx, ProviderHub providers, bool tooSmall)
    {
        int env = providers.OllamaContextLength();
        string state = env == 0
            ? $"[yellow]Ollama loaded this model with {ctx} tokens[/]."
            : $"[yellow]OLLAMA_CONTEXT_LENGTH is {env}[/], and the effective model context is {ctx} tokens.";
        return state + "\n" +
            ContextImpact(tooSmall) + "\n\n" +
            "[dim]Set at least 64000 tokens for coding tools, or 131072 when memory allows.\n" +
            "Use the Ollama app setting, or set OLLAMA_CONTEXT_LENGTH and restart Ollama.\n" +
            "Run `ollama ps` after load to verify the active allocation.[/]";
    }

    static string FoundryBody(int ctx, bool tooSmall) =>
        $"[yellow]This Foundry Local variant has a {ctx}-token compiled context[/].\n" +
        ContextImpact(tooSmall) + "\n\n" +
        "[dim]Run `foundry model list --variants` and choose a tool-capable variant with a\n" +
        "larger context. Confirm the value with `foundry model info <model> -o json`.[/]";

    static string LmStudioBody(int ctx, bool tooSmall) =>
        $"[yellow]This model is loaded with a {ctx}-token context[/].\n" +
        ContextImpact(tooSmall) + "\n\n" +
        "[dim]Reload it with a larger value, for example:\n" +
        "  lms load <model> --context-length 131072\n" +
        "Use the model maximum only when system memory and GPU memory allow it.[/]";

    static string GenericBody(int ctx, bool tooSmall) =>
        $"[yellow]This model's context is {ctx} tokens[/].\n" +
        ContextImpact(tooSmall) + "\n\n" +
        "[dim]Load this model with a larger context, or choose a model with a larger window.[/]";

    static string ContextImpact(bool tooSmall) => tooSmall
        ? $"Copilot's static prompt and tools can consume much of this window. copilocal requires at least {ProviderHub.MinContext} tokens before scripted launches."
        : $"The model can run, but GitHub recommends at least {ProviderHub.RecommendedContext} tokens for best results. Long sessions will compact earlier.";

    static bool AskLaunchAnyway(bool interactive) =>
        interactive && AnsiConsole.Prompt(new ConfirmationPrompt("Launch anyway?") { DefaultValue = false });
}
