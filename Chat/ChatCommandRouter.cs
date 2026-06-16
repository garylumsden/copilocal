using Spectre.Console;

namespace Copilocal.Chat;

internal static class ChatCommandRouter
{
    sealed record ChatCommandSpec(string Name, string Description);

    static readonly ChatCommandSpec[] ChatCommands =
    [
        new("/help", "Show command help"),
        new("/clear", "Reset conversation history"),
        new("/multi", "Enter multiline compose mode"),
        new("/exit", "Return to model picker"),
        new("/quit", "Alias for /exit"),
    ];

    internal enum CommandAutocompleteKind
    {
        NotACommand,
        Resolved,
        Ambiguous,
        Unknown,
    }

    internal sealed record CommandAutocompleteResult(
        CommandAutocompleteKind Kind,
        string Typed,
        string Command,
        IReadOnlyList<string> Matches);

    internal static CommandAutocompleteResult AutocompleteChatCommand(string input)
    {
        string typed = (input ?? "").Trim();
        if (!LooksLikeSlashCommand(typed))
            return new CommandAutocompleteResult(CommandAutocompleteKind.NotACommand, typed, typed, Array.Empty<string>());

        var matches = MatchChatCommands(typed);
        if (matches.Length == 0)
            return new CommandAutocompleteResult(CommandAutocompleteKind.Unknown, typed, typed, Array.Empty<string>());
        if (matches.Length == 1)
            return new CommandAutocompleteResult(CommandAutocompleteKind.Resolved, typed, CanonicalCommand(matches[0]), matches);
        return new CommandAutocompleteResult(CommandAutocompleteKind.Ambiguous, typed, typed, matches);
    }

    internal static string[] MatchChatCommands(string typed) =>
        ChatCommands
            .Select(c => c.Name)
            .Where(name => name.StartsWith((typed ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();

    internal static string CanonicalCommand(string command) =>
        command.Equals("/quit", StringComparison.OrdinalIgnoreCase) ? "/exit" : command.ToLowerInvariant();

    internal static string PromptCommandSelection(string typed, IReadOnlyList<string> matches)
    {
        var prompt = new SelectionPrompt<string>()
            .Title($"Select command for [white]{Markup.Escape(typed)}[/]:")
            .UseConverter(command => $"{command} - {DescribeCommand(command)}")
            .PageSize(Math.Clamp(matches.Count + 1, 4, 10));
        prompt.AddChoices(matches);
        return AnsiConsole.Prompt(prompt);
    }

    static bool LooksLikeSlashCommand(string typed)
    {
        if (typed.Length == 0 || typed[0] != '/') return false;
        if (typed.IndexOfAny(new[] { ' ', '\t' }) >= 0) return false;
        return typed.IndexOf('/', 1) < 0;
    }

    static string DescribeCommand(string command) =>
        ChatCommands.FirstOrDefault(c => c.Name.Equals(command, StringComparison.OrdinalIgnoreCase))?.Description
        ?? "Run command";
}