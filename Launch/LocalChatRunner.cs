using System.Text.RegularExpressions;
using System.Text;
using Markdig;
using Spectre.Console;

using Copilocal.Infrastructure;
using Copilocal.Providers;
using Copilocal.Ui;

namespace Copilocal.Launch;

/// <summary>Runs a local model-only chat loop without launching GitHub Copilot CLI.</summary>
internal sealed partial class LocalChatRunner(ProviderHub providers, IHttpGateway http)
{
    const int ChatTimeoutMs = 180_000;
    static readonly MarkdownPipeline ChatMarkdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    [GeneratedRegex(@"https?://[^\s\)\]>]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrlRegex();

    internal const string DefaultSystemPrompt = """
        You are copilocal chat mode, a command-line assistant for experimenting with local language models.

        Primary goal: help the user explore, compare, and tune local models quickly and safely.
        Prefer practical, reproducible guidance: short steps, concrete prompts, expected outcomes, and clear next experiments.

        Focus on:
        - model and runtime trade-offs (speed, quality, context window, tool-calling support)
        - setup and troubleshooting for Ollama, Foundry Local, LM Studio, and LiteLLM
        - prompt and parameter tuning (temperature, max tokens, context length)
        - experiment design (A/B prompt checks, capability probes, regression checks)

        Important constraints:
        - This chat mode is model-only and does not execute tools or shell commands.
        - Never claim a command was run or a result was observed unless the user provided it.
        - If the user asks for actions, provide exact commands they can run locally.
        - Be direct, technically accurate, and explicit about uncertainty.
        """;

    internal sealed record ChatMessage(string Role, string Content);
    internal sealed record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);
    internal sealed record MarkdownTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

    internal enum ReplyStatus
    {
        Ok,
        ReasoningOnly,
        ToolCallOnly,
        Invalid,
    }

    internal sealed record ParsedReply(ReplyStatus Status, string Content, string Detail);

    internal bool Run(MenuItem model)
    {
        TerminalUi.ClearScreen();
        string baseUrl = AnsiConsole.Status().Start("Starting provider...", _ => providers.EnsureServer(model, preload: true));
        model.BaseUrl = baseUrl;

        var cfg = LaunchConfig.Load();
        string apiKey = Launcher.ResolveProviderApiKey(model, cfg);
        bool isLiteLlm = string.Equals(model.Provider, "LiteLLM", StringComparison.Ordinal);
        string? bearerToken = isLiteLlm ? apiKey : null;

        if (isLiteLlm && apiKey.Length == 0)
        {
            AnsiConsole.MarkupLine(
                "[red]LiteLLM API key not configured.[/] Set an env var (default [white]LITELLM_MASTER_KEY[/]) " +
                "or configure a launch-option key in [white]Configure launch options[/].");
            return false;
        }

        var warm = AnsiConsole.Status().Start("Checking model...", _ => providers.WarmUp(baseUrl, model.Model, bearerToken));
        if (warm.Status == ProviderHub.WarmStatus.Failed)
            AnsiConsole.MarkupLine($"[yellow]Model warm-up warning:[/] {Markup.Escape(warm.Detail)}");
        else if (warm.Status == ProviderHub.WarmStatus.Suspect)
            AnsiConsole.MarkupLine($"[yellow]Model response looked unusual:[/] {Markup.Escape(warm.Detail)}");

        ShowHeader(model, baseUrl);
        var tokenTracker = new TokenUsageTracker(model.Model);
        using var interrupt = ChatInterruptTrap.Attach();
        tokenTracker.Render();

        var messages = new List<ChatMessage> { new("system", DefaultSystemPrompt) };
        while (true)
        {
            if (ExitOnInterrupt(interrupt, tokenTracker)) return true;

            tokenTracker.Render();
            AnsiConsole.Markup("[deepskyblue1]you>[/] ");
            string? input = ReadLineWithCtrlC(interrupt);
            if (ExitOnInterrupt(interrupt, tokenTracker)) return true;
            if (input is null) return true;

            input = input.TrimEnd();
            if (input.Length == 0) continue;

            var autocomplete = AutocompleteChatCommand(input);
            if (autocomplete.Kind == CommandAutocompleteKind.Resolved)
                input = autocomplete.Command;
            else if (autocomplete.Kind == CommandAutocompleteKind.Ambiguous)
            {
                tokenTracker.Hide();
                string selected = PromptCommandSelection(autocomplete.Typed, autocomplete.Matches);
                tokenTracker.Render();
                input = CanonicalCommand(selected);
            }
            else if (autocomplete.Kind == CommandAutocompleteKind.Unknown)
            {
                tokenTracker.Hide();
                AnsiConsole.MarkupLine(
                    $"[yellow]Unknown command:[/] {Markup.Escape(autocomplete.Typed)} [dim](type [white]/[/] then Enter to browse commands)[/]");
                tokenTracker.Render();
                continue;
            }

            if (IsCommand(input, "/exit") || IsCommand(input, "/quit")) return true;
            if (IsCommand(input, "/help"))
            {
                tokenTracker.Hide();
                ShowHelp();
                tokenTracker.Render();
                continue;
            }
            if (IsCommand(input, "/clear"))
            {
                tokenTracker.Hide();
                messages.Clear();
                messages.Add(new ChatMessage("system", DefaultSystemPrompt));
                tokenTracker.Reset();
                AnsiConsole.MarkupLine("[grey58]Conversation cleared.[/]");
                continue;
            }
            if (IsCommand(input, "/multi"))
            {
                tokenTracker.Hide();
                string? multi = ReadMultilineInput(interrupt);
                tokenTracker.Render();
                if (ExitOnInterrupt(interrupt, tokenTracker)) return true;
                if (string.IsNullOrWhiteSpace(multi)) continue;
                input = multi;
            }

            messages.Add(new ChatMessage("user", input));
            string payload = BuildChatPayload(model.Model, messages);
            var (ok, status, body) = AnsiConsole.Status().Start("Thinking...", _ =>
                http.PostJson($"{baseUrl}/chat/completions", payload, ChatTimeoutMs, bearerToken));
            if (ExitOnInterrupt(interrupt, tokenTracker)) return true;
            if (!ok)
            {
                messages.RemoveAt(messages.Count - 1);
                tokenTracker.Hide();
                AnsiConsole.MarkupLine($"[red]Request failed:[/] {Markup.Escape(HttpFailureDetail(status, body))}");
                tokenTracker.Render();
                continue;
            }

            tokenTracker.Update(ParseUsage(body));
            var reply = ParseAssistantReply(body);
            tokenTracker.Hide();
            switch (reply.Status)
            {
                case ReplyStatus.Ok:
                    messages.Add(new ChatMessage("assistant", reply.Content));
                    RenderAssistantContent(reply.Content);
                    break;
                case ReplyStatus.ReasoningOnly:
                    messages.RemoveAt(messages.Count - 1);
                    AnsiConsole.MarkupLine(
                        "[yellow]Model returned reasoning without assistant content on /chat/completions.[/] " +
                        "Pick a non-reasoning model or launch Copilot mode.");
                    if (reply.Detail.Length > 0)
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(reply.Detail)}[/]");
                    break;
                case ReplyStatus.ToolCallOnly:
                    messages.RemoveAt(messages.Count - 1);
                    AnsiConsole.MarkupLine("[yellow]Model emitted tool calls; chat-only mode does not execute tools.[/]");
                    if (reply.Detail.Length > 0)
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(reply.Detail)}[/]");
                    break;
                default:
                    messages.RemoveAt(messages.Count - 1);
                    AnsiConsole.MarkupLine($"[red]Could not parse model reply:[/] {Markup.Escape(reply.Detail)}");
                    break;
            }
            tokenTracker.Render();
        }
    }

    static bool ExitOnInterrupt(ChatInterruptTrap interrupt, TokenUsageTracker tokenTracker)
    {
        if (!interrupt.Requested) return false;
        tokenTracker.Hide();
        return true;
    }

    static void ShowHeader(MenuItem model, string baseUrl)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
                $"[dim]Provider:[/] {Markup.Escape(model.Provider)}\n" +
                $"[dim]Model:[/] {Markup.Escape(model.Model)}\n" +
                $"[dim]Endpoint:[/] {Markup.Escape(baseUrl)}\n\n" +
                "[dim]Commands:[/] /help, /clear, /multi, /exit")
            .Header("Chat-only mode").BorderColor(Color.Teal).RoundedBorder());
    }

    static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[grey58]/help[/]  Show this help.");
        AnsiConsole.MarkupLine("[grey58]/clear[/] Reset conversation history.");
        AnsiConsole.MarkupLine("[grey58]/multi[/] Enter multiline input mode.");
        AnsiConsole.MarkupLine("[grey58]/exit[/]  Return to model picker.");
        AnsiConsole.MarkupLine("[grey58]/quit[/]  Alias for /exit.");
        AnsiConsole.MarkupLine("[grey58]Tip:[/] type [white]/[/] then Enter for command autocomplete (or use a unique prefix like [white]/h[/]).");
    }

    static bool IsCommand(string input, string command) =>
        input.Trim().Equals(command, StringComparison.OrdinalIgnoreCase);

    static string? ReadMultilineInput(ChatInterruptTrap interrupt)
    {
        AnsiConsole.MarkupLine("[grey58]Multiline mode: enter lines, then type /send on a new line. Use /cancel to abort.[/]");
        var lines = new List<string>();
        while (true)
        {
            if (interrupt.Requested) return null;

            AnsiConsole.Markup("[deepskyblue1]...>[/] ");
            string? line = ReadLineWithCtrlC(interrupt);
            if (interrupt.Requested) return null;
            if (line is null) return null;

            if (IsCommand(line, "/cancel"))
            {
                AnsiConsole.MarkupLine("[grey58]Multiline input canceled.[/]");
                return null;
            }

            if (IsCommand(line, "/send"))
            {
                if (lines.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey58]No multiline content entered.[/]");
                    return null;
                }
                return string.Join('\n', lines);
            }

            lines.Add(line);
        }
    }

    static string? ReadLineWithCtrlC(ChatInterruptTrap interrupt)
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        bool previousTreatAsInput = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            var text = new StringBuilder();
            while (true)
            {
                if (interrupt.Requested) return null;

                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.C)
                {
                    interrupt.Request();
                    Console.WriteLine();
                    return null;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return text.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (text.Length == 0) continue;
                    text.Length--;
                    Console.Write("\b \b");
                    continue;
                }

                if (char.IsControl(key.KeyChar)) continue;

                text.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
        finally
        {
            Console.TreatControlCAsInput = previousTreatAsInput;
        }
    }

    sealed class ChatInterruptTrap : IDisposable
    {
        bool _requested;
        readonly ConsoleCancelEventHandler _handler;

        ChatInterruptTrap()
        {
            _handler = HandleCancel;
            Console.CancelKeyPress += _handler;
        }

        internal bool Requested => _requested;

        internal static ChatInterruptTrap Attach() => new();

        internal void Request()
        {
            _requested = true;
        }

        public void Dispose()
        {
            Console.CancelKeyPress -= _handler;
        }

        void HandleCancel(object? sender, ConsoleCancelEventArgs e)
        {
            _requested = true;
            e.Cancel = true;
        }
    }
}
