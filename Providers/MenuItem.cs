namespace Copilocal.Providers;

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
    ManageLiteLlm,
    Quit,
}

/// <summary>A row in the picker: a runnable model, a group header, or a control action.</summary>
internal sealed class MenuItem
{
    internal required MenuItemKind Kind { get; init; }
    internal ControlAction ControlAction { get; init; }
    internal string Provider { get; init; } = "";      // Ollama | Foundry | LM Studio | LiteLLM
    internal string? BaseUrl { get; set; }              // OpenAI-compatible base (models only)
    internal string Model { get; init; } = "";          // model id / control label
    internal string? LoadAlias { get; init; }           // Foundry: alias used for `foundry model load`
    internal bool Tools { get; init; } = true;          // advertises tool-calling

    internal string Display => Kind switch
    {
        MenuItemKind.Header => $"[teal bold]{Esc(Provider)}[/]",
        MenuItemKind.Control or MenuItemKind.ModelHelp => Model,
        _ => $"[dim]{Provider,-9}[/] {Esc(DisplayModel())}{(Tools ? "" : "  [yellow](no tool-calling)[/]")}",
    };

    string DisplayModel() =>
        Provider == "LiteLLM"
            ? FormatLiteLlmModel(Model)
            : Model;

    static string FormatLiteLlmModel(string model)
    {
        int slash = model.IndexOf('/');
        if (slash <= 0 || slash >= model.Length - 1) return $"[LiteLLM] {model}";
        string prefix = model[..slash];
        string remainder = model[(slash + 1)..];
        string platform = prefix.ToLowerInvariant() switch
        {
            "ollama" => "Ollama",
            "lmstudio" => "LM Studio",
            "foundry" => "Foundry Local",
            _ => prefix,
        };
        return $"[{platform}] {remainder}";
    }

    static string Esc(string s) => s.Replace("[", "[[").Replace("]", "]]");
}
