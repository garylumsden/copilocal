using System.Text;
using Spectre.Console;

using Copilocal.Cli;
using Copilocal.Infrastructure;
using Copilocal.Launch;
using Copilocal.Providers;
using Copilocal.Ui;

namespace Copilocal;

/// <summary>Entry point and interactive orchestration: discover local models, present the
/// picker, and delegate launching, installing, and configuration to dedicated units.</summary>
internal static class Program
{
    internal static int Main(string[] argv)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch (IOException)
        {
            // best-effort: older/redirected consoles may reject UTF-8 output.
        }
        IProcessRunner proc = new ProcessRunner();
        IHttpGateway http = new HttpGateway();
        var providers = new ProviderHub(proc, http);
        var installer = new ProviderInstaller(proc, http);
        var launcher = new Launcher(providers, proc);

        var cli = CommandLineArgs.Parse(argv);
        // A stable session id lets us resume the same conversation with a new model.
        string? sessionId = cli.WantsManagedSession ? Guid.NewGuid().ToString() : null;

        AnsiConsole.WriteLine();
        Banner.Draw();
        AnsiConsole.MarkupLine("[grey58 italic]      Pick a local model · launch GitHub Copilot CLI against it[/]\n");

        EnsureCopilotCli(providers, installer, cli.Interactive);

        bool resuming = false;
        MenuItem? lastLaunched = null;   // to unload when switching models on continue

        while (true)
        {
            var launchCfg = LaunchConfig.Load();
            bool liteEnabled = launchCfg.LiteLlmEnabled;
            bool includeLocalProviders = !(liteEnabled && launchCfg.HideLocalProvidersWhenLiteLlm);

            AnsiConsole.MarkupLine(includeLocalProviders
                ? "[grey58]Discovering local models…[/]"
                : "[grey58]Discovering models…[/]");
            var models = providers.GatherModels(includeLocalProviders, liteEnabled, (name, count) =>
                AnsiConsole.MarkupLine(count < 0
                    ? $"  [yellow]…[/] {Markup.Escape(name)} [grey58]— still warming up, skipped (try again)[/]"
                    : count > 0
                        ? $"  [green]✓[/] {Markup.Escape(name)} [grey58]— {count} model{(count == 1 ? "" : "s")}[/]"
                        : $"  [grey46]·[/] {Markup.Escape(name)} [grey58]— no models[/]"));
            AnsiConsole.WriteLine();
            var missing = MissingProviders(providers, launchCfg, includeLocalProviders);
            var emptyInstalled = EmptyInstalledProviders(models, providers, launchCfg, includeLocalProviders);

            // ----- non-interactive pick (single shot) -----
            if (cli.Pick >= 1 && cli.Pick <= models.Count)
            {
                var item = models[cli.Pick - 1];
                if (!Preflight.Ok(item, cli.Interactive, providers)) return 0;
                launcher.Launch(item, Options(cli, sessionId, resuming));
                return launcher.LastExitCode;
            }

            // ----- build main menu -----
            string installRow = missing.Count > 0
                ? $"⚙  Install provider: {string.Join(", ", missing.Select(p => p.Name))}"
                : "⚙  Install / manage providers";
            const string configRow = "⚙  Configure launch options";
            const string manageLiteLlmRow = "⚙  Manage LiteLLM runtime";
            string quitRow = resuming ? "✖  Exit" : "✖  Quit";

            var prompt = new SelectionPrompt<MenuItem>()
                .Title(resuming
                    ? $"Continue session [teal]{Short(sessionId)}[/] with which [teal]model[/]?  [dim](↑/↓, Enter)[/]:"
                    : includeLocalProviders
                        ? "Select a [teal]local model[/]  [dim](↑/↓, Enter to launch)[/]:"
                        : "Select a [teal]model[/]  [dim](↑/↓, Enter to launch)[/]:")
                .PageSize(Math.Clamp(models.Count + emptyInstalled.Count + (liteEnabled ? 6 : 5), 5, 20))
                .UseConverter(m => m.Display)
                .EnableSearch();

            foreach (var grp in models.GroupBy(m => m.Provider))
                prompt.AddChoiceGroup(
                    new MenuItem { Kind = MenuItemKind.Header, Provider = grp.Key, Model = grp.Key },
                    grp);

            var control = new List<MenuItem>();
            // Installed providers with zero models: offer to show how to add one.
            foreach (var info in emptyInstalled)
                control.Add(new MenuItem { Kind = MenuItemKind.ModelHelp, Provider = info.Key, Model = $"📥  {info.Name}: no models — how to add one" });
            control.Add(new MenuItem { Kind = MenuItemKind.Control, ControlAction = ControlAction.Configure, Model = configRow, Provider = "" });
            if (liteEnabled)
                control.Add(new MenuItem { Kind = MenuItemKind.Control, ControlAction = ControlAction.ManageLiteLlm, Model = manageLiteLlmRow, Provider = "" });
            if (missing.Count > 0)
                control.Add(new MenuItem { Kind = MenuItemKind.Control, ControlAction = ControlAction.Install, Model = installRow, Provider = "" });
            control.Add(new MenuItem { Kind = MenuItemKind.Control, ControlAction = ControlAction.Quit, Model = quitRow, Provider = "" });
            prompt.AddChoices(control);

            var chosen = AnsiConsole.Prompt(prompt);

            if (chosen.Kind == MenuItemKind.ModelHelp)
            {
                ShowModelHelp(ProviderInfo.ByKey(chosen.Provider));
                continue;
            }
            if (chosen.Kind == MenuItemKind.Control)
            {
                switch (chosen.ControlAction)
                {
                    case ControlAction.Quit:
                        return 0;
                    case ControlAction.Configure:
                        LaunchOptionsPage.Show(providers);
                        break;
                    case ControlAction.ManageLiteLlm:
                        InstallFlow.ManageLiteLlm(installer);
                        break;
                    case ControlAction.Install:
                        InstallFlow.Run(missing, installer);
                        break;
                }
                continue;
            }

            // Preflight guard: warn (and gate) on missing tool-calling or too-small context
            // before launch; go back to the picker unless the user chooses to launch anyway.
            if (!Preflight.Ok(chosen, cli.Interactive, providers)) continue;

            // Continuing with a different model: unload the previous one to free its VRAM.
            if (lastLaunched is not null && (lastLaunched.Provider != chosen.Provider || lastLaunched.Model != chosen.Model))
            {
                var prev = lastLaunched;
                AnsiConsole.Status().Start($"Unloading {prev.Model}...", _ => providers.Unload(prev));
                AnsiConsole.MarkupLine($"[dim]Unloaded previous model {Markup.Escape(prev.Provider)} / {Markup.Escape(prev.Model)}.[/]");
            }

            bool launched = launcher.Launch(chosen, Options(cli, sessionId, resuming));

            // dry-run, declined warm-up, or a user-managed session => we're done.
            if (cli.DryRun || !launched || sessionId is null) return 0;

            // Copilot exited: surface the captured resume id/name, then loop back to
            // the model picker so the user can continue with a different model (or Exit).
            ShowSessionSaved(sessionId, cli.SessionName);
            resuming = true;        // next launch resumes the same session id
            lastLaunched = chosen;  // remember so we can unload it if the user switches
        }
    }

    static LaunchOptions Options(CommandLineArgs cli, string? sessionId, bool resuming) =>
        new(cli.DryRun, cli.Interactive, cli.Offline, sessionId, cli.SessionName, resuming, cli.CopilotArgs);

    const string CopilotDocsUrl = "https://github.com/github/copilot-cli";

    // copilocal launches `copilot`, so a missing CLI means nothing works. Offer to install it
    // (winget, Windows) at startup; on other OSes or non-interactive runs just point at the docs.
    static void EnsureCopilotCli(ProviderHub providers, ProviderInstaller installer, bool interactive)
    {
        if (providers.HasCopilot) return;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
                "[yellow]GitHub Copilot CLI (`copilot`) isn't on your PATH.[/]\n" +
                "copilocal launches it once you pick a model — it won't work without it.\n\n" +
                $"[dim]Docs:[/] [link={CopilotDocsUrl}]{CopilotDocsUrl}[/]")
            .Header("Copilot CLI not found").BorderColor(Color.Yellow).RoundedBorder());

        if (!interactive || !OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[dim]Install it (e.g. winget on Windows, Homebrew on macOS), then re-run copilocal.[/]");
            return;
        }

        if (!AnsiConsole.Prompt(new ConfirmationPrompt("Install GitHub Copilot CLI now? [dim](winget GitHub.Copilot)[/]") { DefaultValue = true }))
            return;

        bool ok = false;
        AnsiConsole.Status().Start("Installing GitHub Copilot CLI (winget)...", _ => ok = installer.InstallCopilot());
        AnsiConsole.MarkupLine(ok
            ? "[green]✓[/] Copilot CLI installed. [dim]Restart your terminal (or re-run copilocal) so[/] [white]copilot[/] [dim]is on PATH, then sign in by running[/] [white]copilot[/][dim].[/]"
            : $"[red]✗[/] Install failed. Install manually: [link={CopilotDocsUrl}]{CopilotDocsUrl}[/]");
    }

    static void ShowSessionSaved(string sessionId, string? sessionName)
    {
        string label = sessionName is { Length: > 0 } ? $"{Markup.Escape(sessionName)} [dim]({Short(sessionId)})[/]" : $"[teal]{Markup.Escape(sessionId)}[/]";
        string resumeArg = sessionName is { Length: > 0 } ? $"\"{sessionName}\"" : Short(sessionId);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
                $"Session {label} saved.\n" +
                $"[dim]Resume manually any time with:[/]  copilot --resume={resumeArg}\n" +
                $"[dim]Or pick a new model below to continue this session.[/]")
            .Header("Copilot session ended").BorderColor(Color.Grey).RoundedBorder());
    }

    internal static string Short(string? id) => id is { Length: >= 8 } ? id[..8] : id ?? "";

    // ---------------- per-provider model help ----------------

    static void ShowModelHelp(ProviderInfo p)
    {
        string extra = p.Key switch
        {
            "Ollama" => "[dim]Browse the library, then pull any model:[/]",
            "Foundry" => "[dim]List available models with[/] [white]foundry model list[/][dim], then download one:[/]",
            "LM Studio" => "[dim]Use the GUI 'Discover' tab, or the CLI:[/]",
            "LiteLLM" => "[dim]Use setup wizard or docker compose profile:[/]",
            _ => "[dim]Add a model with:[/]",
        };
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
                $"[yellow]{Markup.Escape(p.Name)} is installed but has no models.[/]\n\n" +
                $"{extra}\n" +
                $"  [white]{Markup.Escape(p.PullCmd)}[/]\n\n" +
                $"[dim]Browse models:[/]  [link={p.ModelsDocsUrl}]{Markup.Escape(p.ModelsDocsUrl)}[/]\n" +
                $"[dim]Docs:[/]          [link={p.DocsUrl}]{Markup.Escape(p.DocsUrl)}[/]\n\n" +
                "[grey58]Add a model, then return here — press Enter to re-scan.[/]")
            .Header($"Add a model for {Markup.Escape(p.Name)}").BorderColor(Color.Teal).RoundedBorder());
        AnsiConsole.Markup("[grey58]Press Enter to continue…[/]");
        Console.ReadLine();
    }

    // ---------------- helpers ----------------

    static List<ProviderInfo> MissingProviders(ProviderHub providers, LaunchConfig cfg, bool includeLocalProviders)
    {
        var list = new List<ProviderInfo>();
        if (includeLocalProviders)
        {
            if (!providers.HasOllama) list.Add(ProviderInfo.Ollama);
            if (!providers.HasLmStudio) list.Add(ProviderInfo.LmStudio);
            if (!providers.HasFoundry) list.Add(ProviderInfo.Foundry);
        }
        if (!cfg.LiteLlmEnabled) list.Add(ProviderInfo.LiteLlm);
        return list;
    }

    // Installed providers that returned zero models (ordered as ProviderInfo.All).
    static List<ProviderInfo> EmptyInstalledProviders(
        List<MenuItem> models,
        ProviderHub providers,
        LaunchConfig cfg,
        bool includeLocalProviders)
    {
        var have = models.Select(m => m.Provider).ToHashSet();
        var list = new List<ProviderInfo>();
        if (includeLocalProviders)
        {
            if (providers.HasOllama && !have.Contains("Ollama")) list.Add(ProviderInfo.Ollama);
            if (providers.HasFoundry && !have.Contains("Foundry")) list.Add(ProviderInfo.Foundry);
            if (providers.HasLmStudio && !have.Contains("LM Studio")) list.Add(ProviderInfo.LmStudio);
        }
        if (cfg.LiteLlmEnabled && !have.Contains("LiteLLM")) list.Add(ProviderInfo.LiteLlm);
        return list;
    }
}
