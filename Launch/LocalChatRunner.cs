using System.Text;
using System.Text.Json;
using Spectre.Console;

using Copilocal.Infrastructure;
using Copilocal.Providers;

namespace Copilocal.Launch;

/// <summary>Runs a local model-only chat loop without launching GitHub Copilot CLI.</summary>
internal sealed class LocalChatRunner(ProviderHub providers, IHttpGateway http)
{
    const int ChatTimeoutMs = 180_000;
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
        tokenTracker.Render();
        var messages = new List<ChatMessage> { new("system", DefaultSystemPrompt) };
        while (true)
        {
            tokenTracker.Render();
            AnsiConsole.Markup("[deepskyblue1]you>[/] ");
            string? input = Console.ReadLine();
            if (input is null) return true;
            input = input.TrimEnd();
            if (input.Length == 0) continue;

            if (IsCommand(input, "/exit") || IsCommand(input, "/quit")) return true;
            if (IsCommand(input, "/help")) { ShowHelp(); continue; }
            if (IsCommand(input, "/clear"))
            {
                messages.Clear();
                messages.Add(new ChatMessage("system", DefaultSystemPrompt));
                tokenTracker.Reset();
                AnsiConsole.MarkupLine("[grey58]Conversation cleared.[/]");
                continue;
            }
            if (IsCommand(input, "/multi"))
            {
                string? multi = ReadMultilineInput();
                if (string.IsNullOrWhiteSpace(multi)) continue;
                input = multi;
            }

            messages.Add(new ChatMessage("user", input));
            string payload = BuildChatPayload(model.Model, messages);
            var (ok, status, body) = AnsiConsole.Status().Start("Thinking...", _ =>
                http.PostJson($"{baseUrl}/chat/completions", payload, ChatTimeoutMs, bearerToken));
            if (!ok)
            {
                messages.RemoveAt(messages.Count - 1);
                AnsiConsole.MarkupLine($"[red]Request failed:[/] {Markup.Escape(HttpFailureDetail(status, body))}");
                continue;
            }

            tokenTracker.Update(ParseUsage(body));
            var reply = ParseAssistantReply(body);
            switch (reply.Status)
            {
                case ReplyStatus.Ok:
                    messages.Add(new ChatMessage("assistant", reply.Content));
                    AnsiConsole.MarkupLine($"[springgreen3]assistant>[/] {Markup.Escape(reply.Content)}");
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
    }

    static bool IsCommand(string input, string command) =>
        input.Trim().Equals(command, StringComparison.OrdinalIgnoreCase);

    static string? ReadMultilineInput()
    {
        AnsiConsole.MarkupLine("[grey58]Multiline mode: enter lines, then type /send on a new line. Use /cancel to abort.[/]");
        var lines = new List<string>();
        while (true)
        {
            AnsiConsole.Markup("[deepskyblue1]...>[/] ");
            string? line = Console.ReadLine();
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

    internal static string BuildChatPayload(string model, IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.Append("{\"model\":\"").Append(Json.Escape(model)).Append("\",\"messages\":[");
        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var message = messages[i];
            sb.Append("{\"role\":\"").Append(Json.Escape(message.Role))
              .Append("\",\"content\":\"").Append(Json.Escape(message.Content))
              .Append("\"}");
        }
        sb.Append("],\"stream\":false}");
        return sb.ToString();
    }

    internal static ParsedReply ParseAssistantReply(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!TryFirstMessage(doc, out var message))
                return new ParsedReply(ReplyStatus.Invalid, "", "missing choices[0].message");

            string content = ReadMessageContent(message);
            if (content.Length > 0)
                return new ParsedReply(ReplyStatus.Ok, content, "");

            bool hasToolCalls = message.TryGetProperty("tool_calls", out var toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array
                && toolCalls.GetArrayLength() > 0;
            if (hasToolCalls)
                return new ParsedReply(ReplyStatus.ToolCallOnly, "", ToolCallSummary(toolCalls));

            string reasoning = ReadString(message, "reasoning");
            string reasoningContent = ReadString(message, "reasoning_content");
            string detail = reasoning.Length > 0 ? reasoning : reasoningContent;
            if (detail.Length > 0)
                return new ParsedReply(ReplyStatus.ReasoningOnly, "", Trim(detail));

            return new ParsedReply(ReplyStatus.Invalid, "", "assistant message was empty");
        }
        catch (JsonException ex)
        {
            return new ParsedReply(ReplyStatus.Invalid, "", ex.Message);
        }
    }

    internal static TokenUsage? ParseUsage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object) return null;

            bool hasPrompt = TryReadInt(usage, "prompt_tokens", out int prompt);
            bool hasCompletion = TryReadInt(usage, "completion_tokens", out int completion);
            bool hasTotal = TryReadInt(usage, "total_tokens", out int total);
            if (!hasPrompt && !hasCompletion && !hasTotal) return null;
            if (!hasTotal) total = prompt + completion;
            return new TokenUsage(prompt, completion, total);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static bool TryFirstMessage(JsonDocument doc, out JsonElement message)
    {
        message = default;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0) return false;

        var first = choices[0];
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("message", out var msg)
            || msg.ValueKind != JsonValueKind.Object) return false;

        message = msg;
        return true;
    }

    static string ReadMessageContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String)
            return (content.GetString() ?? "").Trim();
        if (content.ValueKind != JsonValueKind.Array) return "";

        var pieces = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddTextPiece(pieces, item.GetString() ?? "");
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) continue;

            AddTextPiece(pieces, ReadString(item, "text"));
            AddTextPiece(pieces, ReadString(item, "content"));
        }
        return string.Join('\n', pieces);
    }

    static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    static bool TryReadInt(JsonElement element, string property, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var prop)) return false;
        switch (prop.ValueKind)
        {
            case JsonValueKind.Number:
                return prop.TryGetInt32(out value);
            case JsonValueKind.String:
                return int.TryParse(prop.GetString(), out value);
            default:
                return false;
        }
    }

    static void AddTextPiece(List<string> pieces, string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length > 0) pieces.Add(trimmed);
    }

    static string ToolCallSummary(JsonElement toolCalls)
    {
        var names = new List<string>();
        foreach (var item in toolCalls.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("function", out var fn)
                || fn.ValueKind != JsonValueKind.Object) continue;
            string name = ReadString(fn, "name");
            if (name.Length > 0) names.Add(name);
        }
        if (names.Count == 0) return "tool_calls present with no assistant text";
        return $"tool_calls present ({string.Join(", ", names)})";
    }

    static string HttpFailureDetail(int status, string body)
    {
        string summary = ErrorSummary(body);
        return summary.Length > 0 ? $"HTTP {status}: {summary}" : $"HTTP {status}";
    }

    static string ErrorSummary(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                        return Trim(error.GetString() ?? "");
                    if (error.ValueKind == JsonValueKind.Object)
                    {
                        string message = ReadString(error, "message");
                        if (message.Length > 0) return Trim(message);
                    }
                }

                string rootMessage = ReadString(root, "message");
                if (rootMessage.Length > 0) return Trim(rootMessage);
            }
        }
        catch (JsonException)
        {
            // fall back to plain-text summary below.
        }

        return Trim(body);
    }

    internal static string BuildTokenUsageLine(string model, int promptTotal, int completionTotal, int total, TokenUsage? last)
    {
        string modelLabel = ShortModelLabel(model);
        return last is null
            ? $"m:{modelLabel} | tok sum:{total} p:{promptTotal} c:{completionTotal} | last:n/a"
            : $"m:{modelLabel} | tok sum:{total} p:{promptTotal} c:{completionTotal} | last:{last.TotalTokens} (p{last.PromptTokens}/c{last.CompletionTokens})";
    }

    static string ShortModelLabel(string model)
    {
        string trimmed = (model ?? "").Trim();
        if (trimmed.Length <= 28) return trimmed;
        return trimmed[..18] + "..." + trimmed[^7..];
    }

    sealed class TokenUsageTracker
    {
        readonly string _model;
        int _promptTotal;
        int _completionTotal;
        int _total;
        TokenUsage? _last;
        int _renderWidth;
        bool _enabled = true;

        internal TokenUsageTracker(string model)
        {
            _model = model;
        }

        internal void Reset()
        {
            _promptTotal = 0;
            _completionTotal = 0;
            _total = 0;
            _last = null;
            Render();
        }

        internal void Update(TokenUsage? usage)
        {
            _last = usage;
            if (usage is null) return;
            _promptTotal += usage.PromptTokens;
            _completionTotal += usage.CompletionTokens;
            _total += usage.TotalTokens;
        }

        internal void Render()
        {
            if (!_enabled || Console.IsOutputRedirected) return;
            try
            {
                int width = Console.WindowWidth;
                int height = Console.WindowHeight;
                if (width < 20 || height < 2) return;

                string text = BuildTokenUsageLine(_model, _promptTotal, _completionTotal, _total, _last);
                int maxLen = Math.Max(10, width - 1);
                if (text.Length > maxLen) text = text[..maxLen];

                int left = Math.Max(0, width - text.Length);
                int top = height - 1;

                int saveLeft = Math.Clamp(Console.CursorLeft, 0, Math.Max(0, width - 1));
                int saveTop = Math.Clamp(Console.CursorTop, 0, Math.Max(0, height - 1));

                Console.SetCursorPosition(left, top);
                if (_renderWidth > text.Length)
                    text += new string(' ', _renderWidth - text.Length);
                Console.Write(text);
                _renderWidth = text.Length;
                Console.SetCursorPosition(saveLeft, saveTop);
            }
            catch (IOException)
            {
                Disable("I/O unavailable");
            }
            catch (InvalidOperationException)
            {
                Disable("cursor control unavailable");
            }
            catch (PlatformNotSupportedException)
            {
                Disable("platform unsupported");
            }
            catch (ArgumentOutOfRangeException)
            {
                // Terminal resize race; skip this render and continue.
            }
        }

        void Disable(string reason)
        {
            if (!_enabled) return;
            _enabled = false;
            AnsiConsole.MarkupLine($"[dim]Token tracker disabled ({Markup.Escape(reason)}).[/]");
        }
    }

    static string Trim(string text)
    {
        string one = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return one.Length > 160 ? one[..160] + "..." : one;
    }
}
