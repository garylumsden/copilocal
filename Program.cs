using System.Text;
using Spectre.Console;

namespace Copilocal;

internal static class Program
{
    const int OutputTokenContextDivisor = 4;  // leave most context for prompt/tool payload.
    const int MinAutoOutputTokens = 1_024;    // keep enough completion room for Copilot replies.
    const int MaxAutoOutputTokens = 8_192;    // cap local-model completions at practical size.
    const int PromptTokenReserve = 512;       // reserve overhead for provider framing.

    internal static int Main(string[] argv)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch (Exception)
        {
            // best-effort: older/redirected consoles may reject UTF-8 output.
        }
        IProcessRunner proc = new ProcessRunner();
        IHttpGateway http = new HttpGateway();
        var providers = new Providers(proc, http);
        var installer = new ProviderInstaller(proc, http);

        var args = new List<string>(argv);
        bool dryRun = ExtractFlag(args, "--dry-run");
        bool offlineFlag = ExtractFlag(args, "--offline");
        string? sessionName = ExtractValue(args, "--name");
        int pick = ExtractInt(args, "--pick");
        bool interactive = pick < 1;
        // anything after "--" (or whatever is left) is forwarded to copilot
        if (args.Count > 0 && args[0] == "--") args.RemoveAt(0);
        var copilotArgs = args;

        // If the user is driving sessions themselves, don't manage one for them.
        bool userSession = copilotArgs.Any(a =>
            a == "-r" || a == "--continue" ||
            a.StartsWith("--resume") || a.StartsWith("--session-id") ||
            a == "-n" || a.StartsWith("--name"));
        // A stable session id lets us resume the same conversation with a new model.
        string? sessionId = (interactive && !dryRun && !userSession) ? Guid.NewGuid().ToString() : null;

        AnsiConsole.WriteLine();
        DrawBanner();
        AnsiConsole.MarkupLine("[grey58 italic]      Pick a local model · launch GitHub Copilot CLI against it[/]\n");

        bool resuming = false;
        MenuItem? lastLaunched = null;   // to unload when switching models on continue

        while (true)
        {
            AnsiConsole.MarkupLine("[grey58]Discovering local models…[/]");
            var models = providers.GatherModels((name, count) =>
                AnsiConsole.MarkupLine(count < 0
                    ? $"  [yellow]…[/] {Markup.Escape(name)} [grey58]— still warming up, skipped (try again)[/]"
                    : count > 0
                        ? $"  [green]✓[/] {Markup.Escape(name)} [grey58]— {count} model{(count == 1 ? "" : "s")}[/]"
                        : $"  [grey46]·[/] {Markup.Escape(name)} [grey58]— no models[/]"));
            AnsiConsole.WriteLine();
            var missing = MissingProviders(providers);
            var emptyInstalled = EmptyInstalledProviders(models, providers);

            // ----- non-interactive pick (single shot) -----
            if (pick >= 1 && pick <= models.Count)
            {
                if (ContextGateOk(models[pick - 1], interactive, providers))
                    Launch(models[pick - 1], dryRun, interactive, offlineFlag, sessionId, sessionName, resuming, copilotArgs, providers, proc);
                return 0;
            }

            // ----- build main menu -----
            string installRow = missing.Count > 0
                ? $"⚙  Install provider: {string.Join(", ", missing.Select(p => p.Name))}"
                : "⚙  Install / manage providers";
            const string configRow = "⚙  Configure launch options";
            string quitRow = resuming ? "✖  Exit" : "✖  Quit";

            var prompt = new SelectionPrompt<MenuItem>()
                .Title(resuming
                    ? $"Continue session [teal]{Short(sessionId)}[/] with which [teal]model[/]?  [dim](↑/↓, Enter)[/]:"
                    : "Select a [teal]local model[/]  [dim](↑/↓, Enter to launch)[/]:")
                .PageSize(Math.Clamp(models.Count + emptyInstalled.Count + 5, 5, 20))
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
                        ConfigureLaunchPage(providers);
                        break;
                    case ControlAction.Install:
                        RunInstallFlow(missing, installer);
                        break;
                }
                continue;
            }

            // Ollama context guard: if too small, warn and go back to the picker
            // (don't kill the app) unless the user chooses to launch anyway.
            if (!ContextGateOk(chosen, interactive, providers)) continue;

            // Continuing with a different model: unload the previous one to free its VRAM.
            if (lastLaunched is not null && (lastLaunched.Provider != chosen.Provider || lastLaunched.Model != chosen.Model))
            {
                var prev = lastLaunched;
                AnsiConsole.Status().Start($"Unloading {prev.Model}...", _ => providers.Unload(prev));
                AnsiConsole.MarkupLine($"[dim]Unloaded previous model {Markup.Escape(prev.Provider)} / {Markup.Escape(prev.Model)}.[/]");
            }

            bool launched = Launch(chosen, dryRun, interactive, offlineFlag, sessionId, sessionName, resuming, copilotArgs, providers, proc);

            // dry-run, declined warm-up, or a user-managed session => we're done.
            if (dryRun || !launched || sessionId is null) return 0;

            // Copilot exited: surface the captured resume id/name, then loop back to
            // the model picker so the user can continue with a different model (or Exit).
            ShowSessionSaved(sessionId, sessionName);
            resuming = true;        // next launch resumes the same session id
            lastLaunched = chosen;  // remember so we can unload it if the user switches
        }
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

    static string Short(string? id) => id is { Length: >= 8 } ? id[..8] : id ?? "";

    // ---------------- install flow (checkbox multi-select + docs) ----------------

    static bool RunInstallFlow(List<ProviderInfo> missing, ProviderInstaller installer)
    {
        if (missing.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]All providers are already installed.[/]");
            return true;
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
            return true;
        }

        foreach (var p in picks)
        {
            AnsiConsole.Status().Start($"Installing {p.Name} ({p.InstallHow})...", _ =>
            {
                bool ok = installer.Install(p.Name);
                AnsiConsole.MarkupLine(ok
                    ? $"[green]✓[/] {Markup.Escape(p.Name)} installed."
                    : $"[red]✗[/] {Markup.Escape(p.Name)} install failed (see {p.DocsUrl}).");
            });
            if (p.Name == "LM Studio")
                AnsiConsole.MarkupLine("[dim]Tip: launch LM Studio once so its 'lms' CLI is bootstrapped.[/]");
        }
        return true;
    }

    // ---------------- banner + per-provider model help ----------------

    static void DrawBanner()
    {
        string[] art =
        {
            " ██████╗ ██████╗ ██████╗ ██╗██╗      ██████╗  ██████╗ █████╗ ██╗     ",
            "██╔════╝██╔═══██╗██╔══██╗██║██║     ██╔═══██╗██╔════╝██╔══██╗██║     ",
            "██║     ██║   ██║██████╔╝██║██║     ██║   ██║██║     ███████║██║     ",
            "██║     ██║   ██║██╔═══╝ ██║██║     ██║   ██║██║     ██╔══██║██║     ",
            "╚██████╗╚██████╔╝██║     ██║███████╗╚██████╔╝╚██████╗██║  ██║███████╗",
            " ╚═════╝ ╚═════╝ ╚═╝     ╚═╝╚══════╝ ╚═════╝  ╚═════╝╚═╝  ╚═╝╚══════╝",
        };
        (int R, int G, int B) top = (45, 212, 191);    // teal
        (int R, int G, int B) bot = (99, 102, 241);     // indigo
        for (int i = 0; i < art.Length; i++)
        {
            double t = i / (double)(art.Length - 1);
            int r = (int)(top.R + (bot.R - top.R) * t);
            int g = (int)(top.G + (bot.G - top.G) * t);
            int b = (int)(top.B + (bot.B - top.B) * t);
            AnsiConsole.Write(new Markup($"[#{r:X2}{g:X2}{b:X2}]{art[i]}[/]\n"));
        }
    }

    // Installed providers that returned zero models (ordered as ProviderInfo.All).
    static List<ProviderInfo> EmptyInstalledProviders(List<MenuItem> models, Providers providers)
    {
        var have = models.Select(m => m.Provider).ToHashSet();
        var list = new List<ProviderInfo>();
        if (providers.HasOllama && !have.Contains("Ollama")) list.Add(ProviderInfo.Ollama);
        if (providers.HasFoundry && !have.Contains("Foundry")) list.Add(ProviderInfo.Foundry);
        if (providers.HasLmStudio && !have.Contains("LM Studio")) list.Add(ProviderInfo.LmStudio);
        return list;
    }

    static void ShowModelHelp(ProviderInfo p)
    {
        string extra = p.Key switch
        {
            "Ollama" => "[dim]Browse the library, then pull any model:[/]",
            "Foundry" => "[dim]List available models with[/] [white]foundry model list[/][dim], then download one:[/]",
            "LM Studio" => "[dim]Use the GUI 'Discover' tab, or the CLI:[/]",
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

    // ---------------- launch options config page ----------------

    static void ConfigureLaunchPage(Providers providers)
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
        foreach (var (label, token) in catalog)
            if (selected.Contains(label)) cfg.Flags.Add(token);

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

    // ---------------- launch ----------------

    // OLLAMA_CONTEXT_LENGTH governs the context Ollama loads models at; unset means a
    // 4096 default, and anything below MinOllamaCtx is too small for Copilot's prompt
    // (empty replies / loops / 400). Warn when unset or too low and, interactively, let
    // the user launch anyway. Returns true if launch should proceed.
    static bool ContextGateOk(MenuItem m, bool interactive, Providers providers)
    {
        if (m.Provider != "Ollama") return true;
        int ctx = providers.OllamaContextLength();
        if (ctx >= Providers.MinOllamaCtx) return true;
        WarnOllamaContext(ctx);
        if (!interactive) return false;
        return AnsiConsole.Prompt(new ConfirmationPrompt("Launch anyway?") { DefaultValue = false });
    }

    static void WarnOllamaContext(int ctx)
    {
        string state = ctx == 0
            ? "[yellow]OLLAMA_CONTEXT_LENGTH is not set[/], so Ollama defaults to a 4096-token context."
            : $"[yellow]OLLAMA_CONTEXT_LENGTH is {ctx}[/] — below the {Providers.MinOllamaCtx} copilocal considers safe.";
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
                state + "\n" +
                "Copilot's system prompt + tools are larger, so the prompt gets truncated —\n" +
                "you'll see blank replies, a \"continue\" loop, or [white]400 invalid message content type: <nil>[/].\n\n" +
                "[dim]Set a roomier context (PowerShell), then restart Ollama and re-run copilocal:[/]\n" +
                "  [white]setx OLLAMA_CONTEXT_LENGTH 131072[/]   [dim](clamped to each model's max)[/]")
            .Header("Ollama context too small").BorderColor(Color.Yellow).RoundedBorder());
    }

    // Derive Copilot's token budget from the model's real context window (config overrides win).
    internal static (int Prompt, int Output) TokenLimits(LaunchConfig cfg, MenuItem m) =>
        TokenLimits(cfg, new Providers(new ProcessRunner(), new HttpGateway()).ModelContextLength(m));

    static (int Prompt, int Output) TokenLimits(LaunchConfig cfg, MenuItem m, Providers providers) =>
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

    static bool Launch(MenuItem m, bool dryRun, bool interactive, bool offlineFlag,
                       string? sessionId, string? sessionName, bool resuming, List<string> copilotArgs,
                       Providers providers, IProcessRunner proc)
    {
        // Dedicated air-gap choice (default off). COPILOT_OFFLINE stops Copilot CLI
        // contacting GitHub's servers and disables telemetry.
        bool offline = offlineFlag;
        if (interactive)
            offline = AnsiConsole.Prompt(
                new ConfirmationPrompt("Run [yellow]air-gapped[/]? [dim](COPILOT_OFFLINE - no GitHub contact or telemetry)[/]")
                {
                    DefaultValue = offlineFlag,   // defaults to off unless --offline passed
                });

        string baseUrl = AnsiConsole.Status().Start("Starting provider...", _ => providers.EnsureServer(m, preload: !dryRun));
        m.BaseUrl = baseUrl;

        if (!m.Tools)
            AnsiConsole.MarkupLine("[yellow]Warning:[/] this model doesn't advertise tool-calling; Copilot CLI may error.");

        bool useResponses = false;

        // Validate the model actually responds (catches broken GPU EPs that 500 or emit garbage).
        if (!dryRun)
        {
            var warm = AnsiConsole.Status().Start("Warming up model...", _ => providers.WarmUp(baseUrl, m.Model));

            // Reasoning models answer in a 'reasoning' field; the OpenAI Responses wire
            // API handles that cleanly (no empty/null 'content' -> no Ollama 400).
            if (warm.Reasoning && providers.SupportsResponses(baseUrl))
            {
                useResponses = true;
                AnsiConsole.MarkupLine("[teal]Reasoning model detected[/] — using the OpenAI [white]Responses[/] wire API [dim](/v1/responses)[/].");
            }

            switch (warm.Status)
            {
                case Providers.WarmStatus.Failed:
                    // A reasoning model we can route via /v1/responses isn't a real failure.
                    if (warm.Reasoning && useResponses) break;
                    AnsiConsole.MarkupLine($"[red]Warm-up failed:[/] {Markup.Escape(warm.Detail)}");
                    if (interactive && !AnsiConsole.Prompt(new ConfirmationPrompt("Launch Copilot anyway?") { DefaultValue = false }))
                        return false;
                    break;
                case Providers.WarmStatus.Suspect:
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(warm.Detail)}");
                    if (interactive && !AnsiConsole.Prompt(new ConfirmationPrompt("Launch Copilot anyway?") { DefaultValue = true }))
                        return false;
                    break;
                case Providers.WarmStatus.Ok when !useResponses:
                    // Copilot's agentic loop needs native tool calling. Some models are
                    // advertised as tool-capable but emit the call as plain text (breaks
                    // Copilot). The probe's substantive prompt also surfaces *conditional*
                    // reasoners (e.g. gemma) the trivial warm-up missed: those must use the
                    // Responses wire, or their reasoning burns the chat output budget and
                    // truncates the tool call (finish_reason "length" -> undispatchable).
                    var tool = AnsiConsole.Status().Start("Checking tool calling...", _ => providers.ProbeToolCalling(baseUrl, m.Model));
                    if (tool.Reasoning && providers.SupportsResponses(baseUrl))
                    {
                        useResponses = true;
                        AnsiConsole.MarkupLine("[teal]Reasoning model detected[/] [dim](during tool probe)[/] — using the OpenAI [white]Responses[/] wire API [dim](/v1/responses)[/].");
                    }
                    else if (tool.Status == Providers.ToolStatus.NotNative)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(tool.Detail)}");
                        if (interactive && !AnsiConsole.Prompt(new ConfirmationPrompt("Launch Copilot anyway?") { DefaultValue = false }))
                            return false;
                    }
                    break;
            }
        }

        var cfg = LaunchConfig.Load();
        var env = new Dictionary<string, string>
        {
            ["COPILOT_PROVIDER_BASE_URL"] = baseUrl,
            ["COPILOT_PROVIDER_TYPE"] = "openai",
            ["COPILOT_MODEL"] = m.Model,
            ["COPILOT_PROVIDER_API_KEY"] = "local",
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
        string resumeNote = resuming && sessionId is not null ? $"  [dim](resuming {Short(sessionId)})[/]" : "";
        AnsiConsole.MarkupLine($"-> [green][[{Markup.Escape(m.Provider)}]][/] {Markup.Escape(m.Model)}  [dim]@ {baseUrl}[/]{airgap}{resumeNote}");
        if (maxPrompt > 0)
            AnsiConsole.MarkupLine($"[dim]Token budget: prompt {maxPrompt}, output {maxOutput}.[/]");

        if (dryRun)
        {
            AnsiConsole.MarkupLine($"[dim](dry-run) COPILOT_PROVIDER_BASE_URL={baseUrl}  COPILOT_MODEL={m.Model}  COPILOT_OFFLINE={(offline ? "true" : "<unset>")}[/]");
            return false;
        }
        // Inject a stable --session-id so the same conversation can be resumed with a
        // different model. Name it on first launch only (resume by id afterwards).
        var finalArgs = new List<string>();
        if (sessionId is not null)
        {
            finalArgs.Add($"--session-id={sessionId}");
            if (!resuming && sessionName is { Length: > 0 }) finalArgs.Add($"--name={sessionName}");
        }

        // Apply user-configured launch flags (set via the "Configure launch options" menu).
        finalArgs.AddRange(cfg.ToArgs(providers.ConfiguredMcpServers));

        finalArgs.AddRange(copilotArgs);

        string copilot = proc.Which("copilot") ?? "copilot";
        try { proc.RunInherit(copilot, finalArgs, env); return true; }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to launch copilot:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[dim]Install it: https://docs.github.com/copilot/how-tos/copilot-cli[/]");
            return false;
        }
    }

    // ---------------- helpers ----------------

    static List<ProviderInfo> MissingProviders(Providers providers)
    {
        var list = new List<ProviderInfo>();
        if (!providers.HasOllama) list.Add(ProviderInfo.Ollama);
        if (!providers.HasLmStudio) list.Add(ProviderInfo.LmStudio);
        if (!providers.HasFoundry) list.Add(ProviderInfo.Foundry);
        return list;
    }

    static bool ExtractFlag(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        if (i < 0) return false;
        args.RemoveAt(i);
        return true;
    }

    static int ExtractInt(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        if (i < 0 || i + 1 >= args.Count) return -1;
        int.TryParse(args[i + 1], out int v);
        args.RemoveRange(i, 2);
        return v;
    }

    static string? ExtractValue(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        if (i < 0 || i + 1 >= args.Count) return null;
        string v = args[i + 1];
        args.RemoveRange(i, 2);
        return v;
    }
}
