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
                "[dim]Pick a model whose catalog entry supports tool calling (most qwen2.5 and\n" +
                "phi-4-mini variants do).[/]")
            .Header("Model lacks tool calling").BorderColor(Color.Yellow).RoundedBorder());
        return AskLaunchAnyway(interactive);
    }

    // ---------------- context window ----------------

    static bool ContextOk(MenuItem m, bool interactive, ProviderHub providers)
    {
        int ctx = providers.ModelContextLength(m);   // 0 = unknown -> can't judge, don't block
        if (ctx == 0 || ctx >= ProviderHub.MinContext) return true;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(ContextBody(m, ctx, providers))
            .Header("Context window too small").BorderColor(Color.Yellow).RoundedBorder());
        return AskLaunchAnyway(interactive);
    }

    static string ContextBody(MenuItem m, int ctx, ProviderHub providers) => m.Provider switch
    {
        "Ollama" => OllamaBody(providers),
        "Foundry" => FoundryBody(ctx),
        "LM Studio" => LmStudioBody(ctx),
        _ => GenericBody(ctx),
    };

    static string OllamaBody(ProviderHub providers)
    {
        int env = providers.OllamaContextLength();
        string state = env == 0
            ? "[yellow]OLLAMA_CONTEXT_LENGTH is not set[/], so Ollama defaults to a 4096-token context."
            : $"[yellow]OLLAMA_CONTEXT_LENGTH is {env}[/] — below the {ProviderHub.MinContext} copilocal considers safe.";
        return state + "\n" +
            "Copilot's system prompt + tools are larger, so the prompt gets truncated —\n" +
            "you'll see blank replies, a \"continue\" loop, or [white]400 invalid message content type: <nil>[/].\n\n" +
            "[dim]Set a roomier context (PowerShell), then restart Ollama and re-run copilocal:[/]\n" +
            "  [white]setx OLLAMA_CONTEXT_LENGTH 131072[/]   [dim](clamped to each model's max)[/]";
    }

    static string FoundryBody(int ctx) =>
        $"[yellow]This model's context is {ctx} tokens[/] — far below Copilot's prompt (often 20k+).\n" +
        "Foundry's NPU/OpenVINO variant is compiled with a small fixed context, so Copilot's\n" +
        $"request overflows it: [white]input_ids size … exceeds max length ({ctx})[/].\n\n" +
        "[dim]Use the GPU or CPU variant of this model instead — those are 32768 tokens, e.g.\n" +
        "foundry model download <model>-generic-gpu (or -generic-cpu / -openvino-gpu).[/]";

    static string LmStudioBody(int ctx) =>
        $"[yellow]This model is loaded with a {ctx}-token context[/] — below the {ProviderHub.MinContext} copilocal considers safe.\n" +
        "Copilot's prompt is larger and gets truncated (blank/garbled replies, or\n" +
        "\"n_keep >= n_ctx ... load the model with a larger context length\").\n\n" +
        "[dim]In LM Studio's load dialog, set the Context Length to the model maximum (not the\n" +
        "smaller custom default), then re-run copilocal.[/]";

    static string GenericBody(int ctx) =>
        $"[yellow]This model's context is {ctx} tokens[/] — below the {ProviderHub.MinContext} copilocal considers safe.\n" +
        "Copilot's prompt is larger and may be truncated.\n\n" +
        "[dim]Load this model with a larger context, or pick one with a bigger window.[/]";

    static bool AskLaunchAnyway(bool interactive) =>
        interactive && AnsiConsole.Prompt(new ConfirmationPrompt("Launch anyway?") { DefaultValue = false });
}
