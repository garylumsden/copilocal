using System.Text;
using System.Text.Json;

using Copilocal.Infrastructure;

namespace Copilocal.Launch;

internal sealed partial class LocalChatRunner
{
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

    static string Trim(string text)
    {
        string one = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return one.Length > 160 ? one[..160] + "..." : one;
    }
}
