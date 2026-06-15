using Spectre.Console;

using Copilocal.Launch;
using Copilocal.Providers;

namespace Copilocal.Ui;

/// <summary>The "Configure launch options" page: toggles, reasoning effort, token overrides,
/// and free-form extra args, persisted to ~/.copilocal/config.json.</summary>
internal static class LaunchOptionsPage
{
    internal static void Show(ProviderHub providers)
    {
        var cfg = LaunchConfig.Load();
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[teal]Configure copilot launch options[/]").LeftJustified());
        AnsiConsole.MarkupLine("[dim]These are written to[/] [white]" + Markup.Escape(LaunchConfig.FilePath) + "[/] [dim]and applied every launch.[/]");

        // Toggle flags from the catalog (pre-selected from the saved config).
        var catalog = LaunchConfig.Catalog;
        var ms = new MultiSelectionPrompt<string>()
            .Title("Toggle flags [dim](space = toggle, enter = confirm)[/]:")
            .NotRequired()
            .PageSize(Math.Clamp(catalog.Length + 2, 10, 24))
            .InstructionsText("[dim](space toggle · enter confirm · none = stock launch)[/]");
        foreach (var (label, token) in catalog)
        {
            var item = ms.AddChoice(label);
            if (cfg.Flags.Contains(token)) item.Select();
        }
        var selected = AnsiConsole.Prompt(ms);

        cfg.Flags = new HashSet<string>();
        foreach (var (label, token) in catalog.Where(c => selected.Contains(c.Label)))
            cfg.Flags.Add(token);

        // Reasoning effort (single choice; "(unset)" leaves the flag off).
        const string unset = "(unset)";
        var effort = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Reasoning effort [dim](--reasoning-effort)[/]:")
                .AddChoices(new[] { unset }.Concat(LaunchConfig.ReasoningEfforts)));
        cfg.ReasoningEffort = effort == unset ? "" : effort;

        // Token limits (blank/0 = auto-derive from the model's context window).
        cfg.MaxPromptTokens = AskInt("Max prompt tokens [dim](blank = auto from context)[/]:", cfg.MaxPromptTokens);
        cfg.MaxOutputTokens = AskInt("Max output tokens [dim](blank = auto)[/]:", cfg.MaxOutputTokens);

        // Free-form extra args for anything not covered above.
        string extra = AnsiConsole.Prompt(
            new TextPrompt<string>("Extra raw copilot args [dim](space-separated, optional)[/]:")
                .AllowEmpty()
                .DefaultValue(cfg.ExtraArgs)
                .ShowDefaultValue(cfg.ExtraArgs.Length > 0));
        cfg.ExtraArgs = (extra ?? "").Trim();

        // LiteLLM provider controls.
        cfg.LiteLlmEnabled = AnsiConsole.Prompt(new ConfirmationPrompt(
            "Enable [teal]LiteLLM[/] provider in picker?") { DefaultValue = cfg.LiteLlmEnabled });

        if (cfg.LiteLlmEnabled)
        {
            string baseUrl = AnsiConsole.Prompt(
                new TextPrompt<string>("LiteLLM base URL [dim](OpenAI-compatible, /v1 default)[/]:")
                    .AllowEmpty()
                    .DefaultValue(cfg.LiteLlmBaseUrl)
                    .ShowDefaultValue(true));
            cfg.LiteLlmBaseUrl = LaunchConfig.NormalizeBaseUrl(baseUrl);

            string envVar = AnsiConsole.Prompt(
                new TextPrompt<string>("LiteLLM key env var [dim](preferred secret path)[/]:")
                    .AllowEmpty()
                    .DefaultValue(cfg.LiteLlmApiKeyEnvVar)
                    .ShowDefaultValue(true));
            cfg.LiteLlmApiKeyEnvVar = string.IsNullOrWhiteSpace(envVar) ? LaunchConfig.DefaultLiteLlmApiKeyEnvVar : envVar.Trim();

            string apiKey = AnsiConsole.Prompt(
                new TextPrompt<string>("LiteLLM API key [dim](optional plain-text fallback; leave blank to use env var only)[/]:")
                    .AllowEmpty()
                    .DefaultValue(cfg.LiteLlmApiKey)
                    .ShowDefaultValue(cfg.LiteLlmApiKey.Length > 0));
            cfg.LiteLlmApiKey = (apiKey ?? "").Trim();

            cfg.LiteLlmRuntimeMode = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("LiteLLM runtime mode [dim](used by install/manage flows)[/]:")
                    .AddChoices(LaunchConfig.LiteLlmRuntimeModes));

            cfg.HideLocalProvidersWhenLiteLlm = AnsiConsole.Prompt(new ConfirmationPrompt(
                "Hide local providers [dim](Ollama/Foundry/LM Studio)[/] when LiteLLM is enabled?")
            { DefaultValue = cfg.HideLocalProvidersWhenLiteLlm });
        }
        else
        {
            cfg.HideLocalProvidersWhenLiteLlm = false;
        }

        cfg.Save();

        var preview = cfg.ToArgs(providers.ConfiguredMcpServers);
        AnsiConsole.MarkupLine("[green]✓ Saved.[/]");
        AnsiConsole.MarkupLine(preview.Count > 0
            ? $"[dim]copilot will launch with:[/] [white]{Markup.Escape(string.Join(' ', preview))}[/]"
            : "[dim]No extra flags — stock copilot launch.[/]");
        AnsiConsole.Markup("[grey58]Press Enter to continue…[/]");
        Console.ReadLine();
    }

    // Prompt for an optional non-negative integer; blank or 0 returns 0 (= auto).
    static int AskInt(string title, int current)
    {
        string s = AnsiConsole.Prompt(
            new TextPrompt<string>(title)
                .AllowEmpty()
                .DefaultValue(current > 0 ? current.ToString() : "")
                .ShowDefaultValue(current > 0)
                .Validate(v => string.IsNullOrWhiteSpace(v) || (int.TryParse(v, out var n) && n >= 0)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Enter a non-negative number (or leave blank)[/]")));
        return int.TryParse(s, out var val) && val > 0 ? val : 0;
    }
}
