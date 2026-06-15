using Spectre.Console;

namespace Copilocal;

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
                bool ok = installer.Install(p.Name);
                AnsiConsole.MarkupLine(ok
                    ? $"[green]✓[/] {Markup.Escape(p.Name)} installed."
                    : $"[red]✗[/] {Markup.Escape(p.Name)} install failed (see {p.DocsUrl}).");
            });
            if (p.Name == "LM Studio")
                AnsiConsole.MarkupLine("[dim]Tip: launch LM Studio once so its 'lms' CLI is bootstrapped.[/]");
        }
    }
}
