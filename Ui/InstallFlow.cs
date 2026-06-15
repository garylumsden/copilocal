using Spectre.Console;

using Copilocal.Launch;
using Copilocal.Providers;

namespace Copilocal.Ui;

/// <summary>Interactive "install / manage providers" flow: an informed-decision table plus a
/// checkbox multi-select that installs the chosen runtimes.</summary>
internal static class InstallFlow
{
    internal static void Run(List<ProviderInfo> missing, ProviderInstaller installer)
    {
        if (missing.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]All providers are already installed.[/]");
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
            return;
        }

        foreach (var p in picks)
        {
            AnsiConsole.Status().Start($"Installing {p.Name} ({p.InstallHow})...", _ =>
            {
                bool ok = p.Key == ProviderInfo.LiteLlm.Key
                    ? InstallLiteLlm(installer)
                    : installer.Install(p.Name);
                AnsiConsole.MarkupLine(ok
                    ? $"[green]✓[/] {Markup.Escape(p.Name)} installed."
                    : $"[red]✗[/] {Markup.Escape(p.Name)} install failed (see {p.DocsUrl}).");
            });
            if (p.Name == "LM Studio")
                AnsiConsole.MarkupLine("[dim]Tip: launch LM Studio once so its 'lms' CLI is bootstrapped.[/]");
        }
    }

    internal static void ManageLiteLlm(ProviderInstaller installer)
    {
        while (true)
        {
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
                        "Start LiteLLM runtime",
                        "Stop LiteLLM runtime",
                        "Show runtime status",
                        "Toggle hide local providers",
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
                    cfg.LiteLlmBaseUrl = LaunchConfig.NormalizeBaseUrl(AnsiConsole.Prompt(
                        new TextPrompt<string>("LiteLLM base URL:")
                            .DefaultValue(cfg.LiteLlmBaseUrl)
                            .ShowDefaultValue(true)));
                    cfg.LiteLlmApiKeyEnvVar = AnsiConsole.Prompt(
                        new TextPrompt<string>("LiteLLM key env var:")
                            .DefaultValue(cfg.LiteLlmApiKeyEnvVar)
                            .ShowDefaultValue(true));
                    cfg.LiteLlmApiKey = AnsiConsole.Prompt(
                        new TextPrompt<string>("LiteLLM API key [dim](optional plain-text fallback)[/]:")
                            .AllowEmpty()
                            .DefaultValue(cfg.LiteLlmApiKey)
                            .ShowDefaultValue(cfg.LiteLlmApiKey.Length > 0)).Trim();
                    cfg.Save();
                    AnsiConsole.MarkupLine("[green]✓[/] LiteLLM endpoint/auth updated.");
                    break;

                case "Start LiteLLM runtime":
                    AnsiConsole.Status().Start("Starting LiteLLM runtime...", _ =>
                    {
                        bool ok = installer.StartLiteLlm(cfg);
                        AnsiConsole.MarkupLine(ok ? "[green]✓[/] LiteLLM start requested." : "[red]✗[/] LiteLLM start failed.");
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
                    break;

                case "Toggle hide local providers":
                    cfg.HideLocalProvidersWhenLiteLlm = !cfg.HideLocalProvidersWhenLiteLlm;
                    cfg.Save();
                    AnsiConsole.MarkupLine(cfg.HideLocalProvidersWhenLiteLlm
                        ? "[green]✓[/] Local provider rows will be hidden when LiteLLM is enabled."
                        : "[yellow]✓[/] Local provider rows will be shown.");
                    break;

                case "Back":
                    return;
            }
        }
    }

    static bool InstallLiteLlm(ProviderInstaller installer)
    {
        string mode = AskLiteLlmMode(LaunchConfig.Load().LiteLlmRuntimeMode);
        bool ok = installer.InstallLiteLlm(mode);
        if (!ok) return false;

        var cfg = LaunchConfig.Load();
        cfg.LiteLlmEnabled = true;
        cfg.LiteLlmRuntimeMode = mode;
        cfg.LiteLlmBaseUrl = LaunchConfig.NormalizeBaseUrl(cfg.LiteLlmBaseUrl);
        cfg.Save();

        bool startNow = AnsiConsole.Prompt(new ConfirmationPrompt("Start LiteLLM runtime now?") { DefaultValue = true });
        if (!startNow) return true;
        return installer.StartLiteLlm(cfg);
    }

    static string AskLiteLlmMode(string current)
    {
        var options = new[] { ProviderInstaller.LiteLlmModeDocker, ProviderInstaller.LiteLlmModePython };
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("LiteLLM runtime mode:")
                .AddChoices(options));
    }
}
