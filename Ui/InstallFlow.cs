using Spectre.Console;

using Copilocal.Launch;
using Copilocal.Providers;

namespace Copilocal.Ui;

/// <summary>Interactive "install / manage providers" flow: an informed-decision table plus a
/// checkbox multi-select that installs the chosen runtimes.</summary>
internal static class InstallFlow
{
    internal static void Run(List<ProviderInfo> missing, ProviderInstaller installer, ProviderHub providers)
    {
        TerminalUi.ClearScreen();
        if (missing.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]All providers are already installed.[/]");
            PauseForContinue();
            return;
        }

        // informed-decision panel: blurb + clickable docs link per provider
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[teal]Provider[/]");
        table.AddColumn("What it is");
        table.AddColumn("Install via");
        table.AddColumn("Docs");
        foreach (var p in missing)
            table.AddRow(
                Markup.Escape(p.Name),
                Markup.Escape(p.Blurb),
                Markup.Escape(p.InstallHow),
                $"[link={p.DocsUrl}]{Markup.Escape(p.DocsUrl)}[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[dim]Space to toggle, Enter to confirm. Read the docs links above to decide.[/]");

        var ms = new MultiSelectionPrompt<ProviderInfo>()
            .Title("Select providers to [green]install[/]:")
            .NotRequired()
            .InstructionsText("[dim](space = toggle, enter = confirm, none = back)[/]")
            .UseConverter(p => $"{p.Name}  [dim]({p.InstallHow})[/]")
            .AddChoices(missing);

        var picks = AnsiConsole.Prompt(ms);
        if (picks.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Nothing selected.[/]");
            PauseForContinue();
            return;
        }

        foreach (var p in picks)
        {
            bool ok;
            if (p.Key == ProviderInfo.LiteLlm.Key)
            {
                ok = InstallLiteLlm(installer, providers);
                AnsiConsole.MarkupLine(ok
                    ? $"[green]✓[/] {Markup.Escape(p.Name)} setup completed."
                    : $"[red]✗[/] {Markup.Escape(p.Name)} setup failed (see {p.DocsUrl}).");
            }
            else
            {
                ok = false;
                AnsiConsole.Status().Start($"Installing {p.Name} ({p.InstallHow})...", _ => ok = installer.Install(p.Name));
                AnsiConsole.MarkupLine(ok
                    ? $"[green]✓[/] {Markup.Escape(p.Name)} installed."
                    : $"[red]✗[/] {Markup.Escape(p.Name)} install failed (see {p.DocsUrl}).");
            }
            if (p.Name == "LM Studio")
                AnsiConsole.MarkupLine("[dim]Tip: launch LM Studio once so its 'lms' CLI is bootstrapped.[/]");
        }

        PauseForContinue();
    }

    internal static void ManageLiteLlm(ProviderInstaller installer, ProviderHub providers)
    {
        while (true)
        {
            TerminalUi.ClearScreen();
            var cfg = LaunchConfig.Load();
            var status = installer.LiteLlmStatus(cfg);
            string modeLabel = cfg.LiteLlmRuntimeMode == ProviderInstaller.LiteLlmModePython ? "python" : "docker";
            string enabled = cfg.LiteLlmEnabled ? "enabled" : "disabled";

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"LiteLLM [teal]{enabled}[/] · mode [teal]{modeLabel}[/] · status {(status.Running ? "[green]running[/]" : "[yellow]stopped[/]")}\nChoose action:")
                    .AddChoices(
                        "Enable LiteLLM provider",
                        "Disable LiteLLM provider",
                        "Set runtime mode (docker/python)",
                        "Set endpoint + auth",
                        "Add missing local-provider models",
                        "Start LiteLLM runtime",
                        "Stop LiteLLM runtime",
                        "Show runtime status",
                        "Toggle hide local providers",
                        "Reset LiteLLM local setup",
                        "Back"));

            switch (action)
            {
                case "Enable LiteLLM provider":
                    cfg.LiteLlmEnabled = true;
                    cfg.Save();
                    AnsiConsole.MarkupLine("[green]✓[/] LiteLLM provider enabled.");
                    break;

                case "Disable LiteLLM provider":
                    cfg.LiteLlmEnabled = false;
                    cfg.HideLocalProvidersWhenLiteLlm = false;
                    cfg.Save();
                    AnsiConsole.MarkupLine("[yellow]✓[/] LiteLLM provider disabled.");
                    break;

                case "Set runtime mode (docker/python)":
                    cfg.LiteLlmRuntimeMode = AskLiteLlmMode(cfg.LiteLlmRuntimeMode);
                    cfg.Save();
                    AnsiConsole.MarkupLine($"[green]✓[/] Runtime mode set to [white]{cfg.LiteLlmRuntimeMode}[/].");
                    break;

                case "Set endpoint + auth":
                    PromptLiteLlmEndpointAndAuth(cfg);
                    cfg.Save();
                    AnsiConsole.MarkupLine("[green]✓[/] LiteLLM endpoint/auth updated.");
                    break;

                case "Add missing local-provider models":
                    AnsiConsole.Status().Start("Syncing local models into LiteLLM config...", _ =>
                        SyncLocalModelsIntoLiteLlm(installer, providers, cfg));
                    break;

                case "Start LiteLLM runtime":
                    AnsiConsole.Status().Start("Starting LiteLLM runtime...", _ =>
                    {
                        var start = installer.StartLiteLlmWithDetail(cfg);
                        AnsiConsole.MarkupLine(start.Ok
                            ? "[green]✓[/] LiteLLM started."
                            : $"[red]✗[/] LiteLLM start failed: [dim]{Markup.Escape(start.Detail)}[/]");
                        if (start.Ok) ShowLiteLlmLoginDetails(cfg);
                    });
                    break;

                case "Stop LiteLLM runtime":
                    AnsiConsole.Status().Start("Stopping LiteLLM runtime...", _ =>
                    {
                        bool ok = installer.StopLiteLlm(cfg);
                        AnsiConsole.MarkupLine(ok ? "[green]✓[/] LiteLLM stop requested." : "[yellow]✗[/] LiteLLM stop failed or was not running.");
                    });
                    break;

                case "Show runtime status":
                    var now = installer.LiteLlmStatus(cfg);
                    AnsiConsole.MarkupLine(now.Running
                        ? $"[green]✓[/] LiteLLM running: [dim]{Markup.Escape(now.Detail)}[/]"
                        : $"[yellow]·[/] LiteLLM not running: [dim]{Markup.Escape(now.Detail)}[/]");
                    ShowLiteLlmUiLink(cfg);
                    break;

                case "Toggle hide local providers":
                    cfg.HideLocalProvidersWhenLiteLlm = !cfg.HideLocalProvidersWhenLiteLlm;
                    cfg.Save();
                    AnsiConsole.MarkupLine(cfg.HideLocalProvidersWhenLiteLlm
                        ? "[green]✓[/] Local provider rows will be hidden when LiteLLM is enabled."
                        : "[yellow]✓[/] Local provider rows will be shown.");
                    break;

                case "Reset LiteLLM local setup":
                    if (!AnsiConsole.Prompt(new ConfirmationPrompt(
                        "Reset LiteLLM local setup? [dim](stops runtime, removes ~/.copilocal/litellm, resets LiteLLM settings)[/]")
                    { DefaultValue = false }))
                        break;

                    AnsiConsole.Status().Start("Resetting LiteLLM local setup...", _ =>
                    {
                        var reset = installer.ResetLiteLlm();
                        ResetLiteLlmSettings(cfg);
                        AnsiConsole.MarkupLine(reset.Ok
                            ? "[green]✓[/] LiteLLM local setup reset to defaults."
                            : $"[yellow]·[/] LiteLLM reset completed with warnings: [dim]{Markup.Escape(reset.Detail)}[/]");
                    });
                    break;

                case "Back":
                    return;
            }

            PauseForContinue();
        }
    }

    static void PauseForContinue()
    {
        AnsiConsole.Markup("[grey58]Press Enter to continue…[/]");
        Console.ReadLine();
    }

    static bool InstallLiteLlm(ProviderInstaller installer, ProviderHub providers)
    {
        var cfg = LaunchConfig.Load();
        const string useExisting = "Use existing LiteLLM instance (skip local install)";
        const string installLocal = "Install local LiteLLM runtime";
        string setupChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("LiteLLM setup mode:")
                .AddChoices(useExisting, installLocal));

        if (setupChoice == useExisting)
            return ConfigureExistingLiteLlm(cfg);

        string mode = AskLiteLlmMode(cfg.LiteLlmRuntimeMode);
        var install = installer.InstallLiteLlmWithDetail(mode);
        if (!install.Ok)
        {
            AnsiConsole.MarkupLine($"[yellow]·[/] Local LiteLLM install failed: [dim]{Markup.Escape(install.Detail)}[/]");
            bool useExistingFallback = AnsiConsole.Prompt(
                new ConfirmationPrompt("Configure an existing LiteLLM instance instead?")
                { DefaultValue = true });
            return useExistingFallback && ConfigureExistingLiteLlm(cfg);
        }

        cfg.LiteLlmEnabled = true;
        cfg.LiteLlmRuntimeMode = mode;
        cfg.LiteLlmBaseUrl = LaunchConfig.NormalizeBaseUrl(cfg.LiteLlmBaseUrl);
        cfg.Save();

        if (ResolveLiteLlmApiKey(cfg).Length == 0)
        {
            bool configureAuth = AnsiConsole.Prompt(
                new ConfirmationPrompt("LiteLLM key is not configured. Set endpoint/auth now?")
                { DefaultValue = true });
            if (configureAuth)
            {
                PromptLiteLlmEndpointAndAuth(cfg);
                cfg.Save();
            }
        }

        bool importNow = AnsiConsole.Prompt(
            new ConfirmationPrompt("Add all discovered local-provider models to LiteLLM config now?")
            { DefaultValue = true });
        if (importNow)
            SyncLocalModelsIntoLiteLlm(installer, providers, cfg);

        bool startNow = AnsiConsole.Prompt(new ConfirmationPrompt("Start LiteLLM runtime now?") { DefaultValue = true });
        if (!startNow) return true;
        var start = installer.StartLiteLlmWithDetail(cfg);
        if (start.Ok)
        {
            ShowLiteLlmLoginDetails(cfg);
            return true;
        }

        AnsiConsole.MarkupLine($"[yellow]·[/] LiteLLM installed but did not start: [dim]{Markup.Escape(start.Detail)}[/]");
        return true;
    }

    static bool ConfigureExistingLiteLlm(LaunchConfig cfg)
    {
        cfg.LiteLlmEnabled = true;
        PromptLiteLlmEndpointAndAuth(cfg);
        cfg.LiteLlmBaseUrl = LaunchConfig.NormalizeBaseUrl(cfg.LiteLlmBaseUrl);
        cfg.Save();
        ShowLiteLlmUiLink(cfg);
        return true;
    }

    static void PromptLiteLlmEndpointAndAuth(LaunchConfig cfg)
    {
        cfg.LiteLlmBaseUrl = LaunchConfig.NormalizeBaseUrl(AnsiConsole.Prompt(
            new TextPrompt<string>("LiteLLM base URL:")
                .DefaultValue(cfg.LiteLlmBaseUrl)
                .ShowDefaultValue(true)));
        cfg.LiteLlmApiKeyEnvVar = AnsiConsole.Prompt(
            new TextPrompt<string>("LiteLLM key env var:")
                .DefaultValue(cfg.LiteLlmApiKeyEnvVar)
                .ShowDefaultValue(true));
        AnsiConsole.MarkupLine(
            "[dim]Hint:[/] For local LiteLLM runtime, any non-empty secret works (normalized to [white]sk-...[/]). For external LiteLLM, use the real proxy key.");
        string entered = AnsiConsole.Prompt(
            new TextPrompt<string>("LiteLLM API key [dim](required)[/]:")
                .DefaultValue(cfg.LiteLlmApiKey)
                .ShowDefaultValue(cfg.LiteLlmApiKey.Length > 0)
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]LiteLLM API key is required.[/]")
                    : ValidationResult.Success()))
            .Trim();
        cfg.LiteLlmApiKey = LaunchConfig.NormalizeLiteLlmApiKey(entered);
        if (entered.Length > 0 && !entered.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
            AnsiConsole.MarkupLine("[dim]Hint:[/] LiteLLM keys are normalized to [white]sk-...[/].");
        if (cfg.LiteLlmRuntimeMode == ProviderInstaller.LiteLlmModeDocker && cfg.LiteLlmApiKey.Length > 0)
            AnsiConsole.MarkupLine("[dim]Hint:[/] Docker UI login uses [white]admin[/] + your LiteLLM key.");
    }

    static void SyncLocalModelsIntoLiteLlm(ProviderInstaller installer, ProviderHub providers, LaunchConfig cfg)
    {
        var localModels = providers.GatherModels(includeLocalProviders: true, includeLiteLlm: false);
        var result = installer.AddMissingLiteLlmLocalModels(cfg.LiteLlmRuntimeMode, localModels);
        if (!result.Ok)
        {
            AnsiConsole.MarkupLine("[red]✗[/] Could not update LiteLLM config.");
            return;
        }
        if (result.Discovered == 0)
        {
            AnsiConsole.MarkupLine("[yellow]·[/] No local models discovered to add.");
            return;
        }
        if (result.Added == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]·[/] LiteLLM config already has all discovered local models ({result.Existing} existing).");
            return;
        }

        string skippedText = result.Skipped > 0 ? $" [dim]({result.Skipped} skipped)[/]" : "";
        AnsiConsole.MarkupLine($"[green]✓[/] Added {result.Added} local model{(result.Added == 1 ? "" : "s")} to LiteLLM config. [dim]({result.Existing} already present)[/]{skippedText}");
    }

    static void ShowLiteLlmLoginDetails(LaunchConfig cfg)
    {
        string endpoint = LaunchConfig.NormalizeBaseUrl(cfg.LiteLlmBaseUrl);
        string key = ResolveLiteLlmApiKey(cfg);
        if (key.Length == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]·[/] LiteLLM running at [white]{Markup.Escape(endpoint)}[/], but login key is not set.");
            ShowLiteLlmUiLink(cfg);
            return;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] LiteLLM login key: [white]{Markup.Escape(key)}[/]");
        if (cfg.LiteLlmRuntimeMode == ProviderInstaller.LiteLlmModeDocker)
            AnsiConsole.MarkupLine($"[dim]UI credentials:[/] [white]admin[/] / [white]{Markup.Escape(key)}[/]");
        AnsiConsole.MarkupLine("[dim]If UI login fails, check[/] [white]~/.copilocal/litellm/.env[/] [dim]for UI_USERNAME/UI_PASSWORD.[/]");
        ShowLiteLlmUiLink(cfg);
    }

    static void ShowLiteLlmUiLink(LaunchConfig cfg)
    {
        string endpoint = LaunchConfig.NormalizeBaseUrl(cfg.LiteLlmBaseUrl);
        string uiUrl = LiteLlmUiUrl(endpoint);
        AnsiConsole.MarkupLine($"[dim]Endpoint:[/] [link={endpoint}]{Markup.Escape(endpoint)}[/]");
        AnsiConsole.MarkupLine($"[dim]UI:[/] [link={uiUrl}]{Markup.Escape(uiUrl)}[/]");
    }

    static string LiteLlmUiUrl(string endpoint)
    {
        string root = endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? endpoint[..^3]
            : endpoint;
        root = root.TrimEnd('/');
        return $"{root}/ui";
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

    static string AskLiteLlmMode(string current)
    {
        var options = new[] { ProviderInstaller.LiteLlmModeDocker, ProviderInstaller.LiteLlmModePython };
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("LiteLLM runtime mode:")
                .AddChoices(options));
    }

    static void ResetLiteLlmSettings(LaunchConfig cfg)
    {
        cfg.LiteLlmEnabled = false;
        cfg.HideLocalProvidersWhenLiteLlm = false;
        cfg.LiteLlmBaseUrl = LaunchConfig.DefaultLiteLlmBaseUrl;
        cfg.LiteLlmApiKey = "";
        cfg.LiteLlmApiKeyEnvVar = LaunchConfig.DefaultLiteLlmApiKeyEnvVar;
        cfg.LiteLlmRuntimeMode = ProviderInstaller.LiteLlmModeDocker;
        cfg.Save();
    }
}
