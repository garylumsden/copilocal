namespace Copilocal.Chat;

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