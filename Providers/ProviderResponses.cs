using System.Text.Json;

namespace Copilocal.Providers;

internal static class ProviderResponses
{
    /// <summary>True if the chat/completions response carries a non-empty tool_calls array.</summary>
    internal static bool HasToolCall(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (!TryFirstMessage(d, out var msg)) return false;
            return msg.TryGetProperty("tool_calls", out var tc)
                && tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            // best-effort: a malformed body just means "no native tool call seen".
            return false;
        }
    }

    /// <summary>Resolve <c>choices[0].message</c> from a chat/completions body, guarding the shape
    /// so navigation can't throw (only a JSON parse error can).</summary>
    internal static bool TryFirstMessage(JsonDocument d, out JsonElement message)
    {
        message = default;
        var root = d.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return false;
        var first = choices[0];
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("message", out var msg)
            || msg.ValueKind != JsonValueKind.Object) return false;
        message = msg;
        return true;
    }

    /// <summary>Heuristic: the model dumped a tool call into 'content' instead of emitting
    /// native tool_calls. Catches raw JSON (e.g. {"name":...,"arguments":...}) and the
    /// &lt;tool_call&gt; tag wrapping some templates use.</summary>
    internal static bool LooksLikeToolCallText(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        bool jsonShape = content.Contains("\"name\"", StringComparison.Ordinal)
                      && content.Contains("\"arguments\"", StringComparison.Ordinal);
        return jsonShape
            || content.Contains("get_time", StringComparison.Ordinal)
            || content.Contains("tool_call", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ExtractMessageField(string body, string field)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (!TryFirstMessage(d, out var msg)) return "";
            return msg.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            // best-effort: malformed warm-up response becomes empty field.
            return "";
        }
    }

    // Heuristic: a coherent answer to the warm-up prompt is prose that is mostly
    // letters. Flag short repeating units or a low letter ratio. (Avoids a word-count
    // rule - it false-flagged terse-but-valid replies like "Ready to assist!" where
    // short connectives aren't counted - and char-diversity, which is naturally low for
    // longer English prose. Both caused false positives.)
    internal static bool LooksGarbled(string t)
    {
        string nospace = new(t.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (nospace.Length < 8) return false;

        // Short repeating unit, e.g. "UKUKUK" or "._._._".
        foreach (int unit in new[] { 1, 2, 3 })
        {
            if (nospace.Length < unit * 4) continue;
            string head = nospace[..unit];
            bool allSame = true;
            for (int i = 0; i + unit <= nospace.Length; i += unit)
                if (nospace.Substring(i, unit) != head) { allSame = false; break; }
            if (allSame) return true;
        }

        // Mostly digits/punctuation, e.g. "90. 111 161 .222 33r 440 666" or "alpha 123 456".
        int letters = nospace.Count(char.IsLetter);
        if ((double)letters / nospace.Length < 0.55) return true;

        return false;
    }

    internal static string Trim(string t)
    {
        string one = t.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return one.Length > 60 ? one[..60] + "..." : one;
    }

    internal static string HttpFailureDetail(int status, string body)
    {
        string summary = ErrorSummary(body);
        return summary.Length > 0 ? $"HTTP {status}: {summary}" : $"HTTP {status}";
    }

    internal static string ErrorSummary(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var d = JsonDocument.Parse(body);
            var root = d.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    string fromError = StringField(error, "message");
                    if (fromError.Length > 0) return SingleLine(fromError);
                    if (error.ValueKind == JsonValueKind.String)
                        return SingleLine(error.GetString() ?? "");
                }
                string fromMessage = StringField(root, "message");
                if (fromMessage.Length > 0) return SingleLine(fromMessage);
                string fromDetail = StringField(root, "detail");
                if (fromDetail.Length > 0) return SingleLine(fromDetail);
            }
            else if (root.ValueKind == JsonValueKind.String)
                return SingleLine(root.GetString() ?? "");
        }
        catch (JsonException)
        {
            // best-effort: non-JSON bodies fall back to a first-line summary.
        }
        return SingleLine(body);
    }

    internal static string StringField(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    internal static string SingleLine(string text)
    {
        string line = (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        const int max = 180;
        return line.Length <= max ? line : $"{line[..max]}…";
    }
}
