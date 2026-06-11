namespace Copilocal;

internal enum MenuItemKind
{
    Model,
    Header,
    Control,
    ModelHelp,
}

internal enum ControlAction
{
    None,
    Configure,
    Install,
    Quit,
}

/// <summary>A row in the picker: a runnable model, a group header, or a control action.</summary>
internal sealed class MenuItem
{
    internal required MenuItemKind Kind { get; init; }
    internal ControlAction ControlAction { get; init; }
    internal string Provider { get; init; } = "";      // Ollama | Foundry | LM Studio
    internal string? BaseUrl { get; set; }              // OpenAI-compatible base (models only)
    internal string Model { get; init; } = "";          // model id / control label
    internal string? LoadAlias { get; init; }           // Foundry: alias used for `foundry model load`
    internal bool Tools { get; init; } = true;          // advertises tool-calling

    internal string Display => Kind switch
    {
        MenuItemKind.Header => $"[teal bold]{Esc(Provider)}[/]",
        MenuItemKind.Control or MenuItemKind.ModelHelp => Model,
        _ => $"[dim]{Provider,-9}[/] {Esc(Model)}{(Tools ? "" : "  [yellow](no tool-calling)[/]")}",
    };

    static string Esc(string s) => s.Replace("[", "[[").Replace("]", "]]");
}
