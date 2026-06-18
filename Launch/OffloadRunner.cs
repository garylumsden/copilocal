using Copilocal.Chat;
using Copilocal.Configuration;
using Copilocal.Infrastructure;
using Copilocal.Providers;

namespace Copilocal.Launch;

/// <summary>Runs a single non-interactive local-model task and returns assistant text.</summary>
internal sealed class OffloadRunner(ProviderHub providers, IHttpGateway http)
{
    const int OffloadTimeoutMs = 180_000;

    internal const string SystemPrompt = """
        You are copilocal offload mode.

        You answer one bounded task using a local model and return concise, practical output.
        Do not claim tools were run unless the caller provided results.
        If information is missing, state the limitation clearly.
        """;

    internal (bool Ok, string Output, string Error) Run(MenuItem model, string prompt, LaunchConfig cfg)
    {
        string baseUrl = providers.EnsureServer(model, preload: true);
        model.BaseUrl = baseUrl;

        string apiKey = Launcher.ResolveProviderApiKey(model, cfg);
        bool isLiteLlm = string.Equals(model.Provider, "LiteLLM", StringComparison.Ordinal);
        string? bearerToken = isLiteLlm ? apiKey : null;
        if (isLiteLlm && apiKey.Length == 0)
            return (false, "", "LiteLLM API key not configured for offload.");

        var warm = providers.WarmUp(baseUrl, model.Model, bearerToken);
        if (warm.Status == ProviderHub.WarmStatus.Failed)
            return (false, "", $"Model warm-up failed: {warm.Detail}");

        var messages = new List<ChatMessage>
        {
            new("system", SystemPrompt),
            new("user", prompt),
        };
        string payload = ChatProtocol.BuildChatPayload(model.Model, messages);
        var (ok, status, body) = http.PostJson($"{baseUrl}/chat/completions", payload, OffloadTimeoutMs, bearerToken);
        if (!ok)
            return (false, "", ChatProtocol.HttpFailureDetail(status, body));

        var reply = ChatProtocol.ParseAssistantReply(body);
        return reply.Status switch
        {
            ReplyStatus.Ok => (true, reply.Content, ""),
            ReplyStatus.ReasoningOnly => (false, "", $"Model returned reasoning without assistant content: {reply.Detail}"),
            ReplyStatus.ToolCallOnly => (false, "", $"Model returned tool call output, not assistant text: {reply.Detail}"),
            _ => (false, "", $"Could not parse model response: {reply.Detail}"),
        };
    }
}
