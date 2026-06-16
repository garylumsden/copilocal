using System.Text.Json;

using Copilocal.Infrastructure;

namespace Copilocal.Providers;

internal sealed partial class ProviderHub
{
    internal enum WarmStatus { Ok, Suspect, Failed }

    /// <summary>Send one tiny completion to validate the model actually responds.
    /// Catches broken GPU execution providers (500/garbage) and flags reasoning models
    /// (answer lands in a 'reasoning'/'reasoning_content' field) - on the chat/completions
    /// wire those produce empty 'content', making Copilot loop / Ollama 400. The caller
    /// switches reasoning models to the Responses wire API to avoid that.</summary>
    internal (WarmStatus Status, string Detail, bool Reasoning) WarmUp(string baseUrl, string model, string? bearerToken = null)
    {
        try
        {
            // Build JSON by hand: reflection-based JsonSerializer is disabled under AOT.
            string esc = Json.Escape(model);
            // A short instruction that still yields real prose: fast to generate, yet long
            // enough (> 8 chars) that the LooksGarbled smoke-test stays live to catch
            // broken execution providers that emit looping/garbage output.
            string payload =
                "{\"model\":\"" + esc + "\"," +
                "\"messages\":[{\"role\":\"user\",\"content\":\"Reply with one short sentence confirming you are ready.\"}]," +
                "\"max_tokens\":48,\"temperature\":0}";
            var (ok, status, body) = http.PostJson($"{baseUrl}/chat/completions", payload, 120_000, bearerToken);
            if (!ok)
                return (WarmStatus.Failed, HttpFailureDetail(status, body), false);

            // Reasoning models separate their thinking from the answer; field name varies
            // by provider: Ollama uses "reasoning", LM Studio/vLLM use "reasoning_content".
            bool reasoning = !string.IsNullOrWhiteSpace(ExtractMessageField(body, "reasoning"))
                          || !string.IsNullOrWhiteSpace(ExtractMessageField(body, "reasoning_content"));
            string text = ExtractMessageField(body, "content");
            if (string.IsNullOrWhiteSpace(text))
            {
                if (reasoning)
                    return (WarmStatus.Failed,
                        "reasoning model: it returns its thinking separately and leaves 'content' empty on the " +
                        "chat/completions wire, so Copilot sees blank answers (and Ollama returns " +
                        "\"400 invalid message content type: <nil>\"). The Responses wire API fixes this; " +
                        "if unavailable here, pick a non-reasoning model (e.g. qwen2.5-coder:14b).",
                        true);
                return (WarmStatus.Failed, "empty response", false);
            }
            if (LooksGarbled(text))
                return (WarmStatus.Suspect, $"output looks garbled: \"{Trim(text)}\"", reasoning);
            return (WarmStatus.Ok, Trim(text), reasoning);
        }
        catch (HttpRequestException ex) { return (WarmStatus.Failed, (ex.InnerException ?? ex).Message, false); }
        catch (OperationCanceledException ex) { return (WarmStatus.Failed, (ex.InnerException ?? ex).Message, false); }
    }

    internal enum ToolStatus { Ok, NotNative, Inconclusive }

    /// <summary>Validate the model performs *native* tool calling on the chat/completions
    /// wire, which Copilot's agentic loop depends on. Some models (e.g. Ollama's
    /// qwen2.5-coder) are advertised as tool-capable yet emit the call as plain text in
    /// 'content' with tool_calls null - that silently breaks Copilot. We use tool_choice
    /// "auto" with a generous token budget on purpose: "required" is unreliable on Ollama
    /// for thinking models, and a small budget makes a reasoning model burn its tokens
    /// thinking before it can emit the call (finish_reason "length" -> truncated, undispatchable
    /// tool call). This substantive prompt also surfaces *conditional* reasoners (e.g.
    /// gemma) that the trivial warm-up prompt misses, so we return a Reasoning flag for
    /// the caller to route via the Responses wire. Best-effort: anything other than a clear
    /// "emits-as-text" failure is reported as inconclusive (don't block).</summary>
    internal (ToolStatus Status, string Detail, bool Reasoning) ProbeToolCalling(string baseUrl, string model, string? bearerToken = null)
    {
        try
        {
            string esc = Json.Escape(model);
            string payload =
                "{\"model\":\"" + esc + "\"," +
                "\"messages\":[{\"role\":\"user\",\"content\":\"Use the get_time tool to report the current time.\"}]," +
                "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"get_time\"," +
                "\"description\":\"Get the current time\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}]," +
                "\"tool_choice\":\"auto\",\"max_tokens\":512,\"temperature\":0}";
            var (ok, status, body) = http.PostJson($"{baseUrl}/chat/completions", payload, 120_000, bearerToken);
            if (!ok) return (ToolStatus.Inconclusive, HttpFailureDetail(status, body), false);
            bool reasoning = !string.IsNullOrWhiteSpace(ExtractMessageField(body, "reasoning"))
                          || !string.IsNullOrWhiteSpace(ExtractMessageField(body, "reasoning_content"));
            if (HasToolCall(body)) return (ToolStatus.Ok, "native tool calling OK", reasoning);
            if (LooksLikeToolCallText(ExtractMessageField(body, "content")))
                return (ToolStatus.NotNative,
                    "model emits tool calls as plain text (tool_calls is empty), which breaks Copilot's " +
                    "agentic loop. Pick a model with native tool calling (e.g. granite4, qwen3, llama3.2).",
                    reasoning);
            return (ToolStatus.Inconclusive, "model did not call the tool", reasoning);
        }
        catch (HttpRequestException ex) { return (ToolStatus.Inconclusive, (ex.InnerException ?? ex).Message, false); }
        catch (OperationCanceledException ex) { return (ToolStatus.Inconclusive, (ex.InnerException ?? ex).Message, false); }
    }

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
    static bool TryFirstMessage(JsonDocument d, out JsonElement message)
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

    /// <summary>True if the provider exposes an OpenAI Responses endpoint (/v1/responses).
    /// Ollama and LM Studio do; this lets reasoning models work without the
    /// chat/completions null-content 400. A 404/405 means it isn't implemented.</summary>
    internal bool SupportsResponses(string baseUrl, string? bearerToken = null)
    {
        try
        {
            // 404/405 => the route isn't implemented; any other status means it exists.
            var (_, status, _) = http.PostJson($"{baseUrl}/responses", "{}", 10_000, bearerToken);
            return status is not (404 or 405);
        }
        catch (HttpRequestException)
        {
            // best-effort: failed probe means keep chat/completions.
            return false;
        }
        catch (OperationCanceledException)
        {
            // best-effort: failed probe means keep chat/completions.
            return false;
        }
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

    static string Trim(string t)
    {
        string one = t.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return one.Length > 60 ? one[..60] + "..." : one;
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

    static string StringField(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    static string SingleLine(string text)
    {
        string line = (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        const int max = 180;
        return line.Length <= max ? line : $"{line[..max]}…";
    }
}
