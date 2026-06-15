using Copilocal;
using Copilocal.Infrastructure;
using Copilocal.Providers;
using Copilocal.Tests.Fakes;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class ProviderOrchestrationTests
{
    private const string BaseUrl = "http://fake-provider/v1";
    private const string ChatUrl = BaseUrl + "/chat/completions";
    private const string ResponsesUrl = BaseUrl + "/responses";

    [TestMethod]
    public void WarmUp_NormalContent_ReturnsOk()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200,
            body: Completion(content: "A compiler translates source code into executable programs."));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.WarmUp(BaseUrl, "model");

        // Assert
        result.Status.Should().Be(ProviderHub.WarmStatus.Ok);
        result.Reasoning.Should().BeFalse();
        result.Detail.Should().Contain("compiler translates source code");
        http.PostCalls.Should().ContainSingle()
            .Which.Should().Match<FakeHttpGateway.PostCall>(c =>
                c.Url == ChatUrl && c.Json.Contains("\"model\":\"model\"") && c.TimeoutMs == 120_000);
    }

    [TestMethod]
    public void WarmUp_EmptyContentWithReasoning_ReturnsFailedWithReasoningFlag()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200, body: Completion(content: "", reasoning: "thinking"));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.WarmUp(BaseUrl, "reasoner");

        // Assert
        result.Status.Should().Be(ProviderHub.WarmStatus.Failed);
        result.Reasoning.Should().BeTrue();
        result.Detail.Should().Contain("reasoning model");
    }

    [TestMethod]
    public void WarmUp_EmptyContentWithReasoningContent_ReturnsFailedWithReasoningFlag()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200,
            body: Completion(content: "", reasoningContent: "thinking"));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.WarmUp(BaseUrl, "reasoner");

        // Assert
        result.Status.Should().Be(ProviderHub.WarmStatus.Failed);
        result.Reasoning.Should().BeTrue();
        result.Detail.Should().Contain("reasoning model");
    }

    [TestMethod]
    public void WarmUp_EmptyContentWithoutReasoning_ReturnsFailedEmptyResponse()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200, body: Completion(content: ""));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.WarmUp(BaseUrl, "model");

        // Assert
        result.Status.Should().Be(ProviderHub.WarmStatus.Failed);
        result.Reasoning.Should().BeFalse();
        result.Detail.Should().Be("empty response");
    }

    [TestMethod]
    public void WarmUp_NonSuccessHttp_ReturnsFailedHttpStatus()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: false, status: 503, body: "unavailable");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.WarmUp(BaseUrl, "model");

        // Assert
        result.Status.Should().Be(ProviderHub.WarmStatus.Failed);
        result.Reasoning.Should().BeFalse();
        result.Detail.Should().Be("HTTP 503");
    }

    [TestMethod]
    public void WarmUp_GarbledContent_ReturnsSuspect()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200, body: Completion(content: "90. 111 161 .222 33r 440 666"));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.WarmUp(BaseUrl, "model");

        // Assert
        result.Status.Should().Be(ProviderHub.WarmStatus.Suspect);
        result.Reasoning.Should().BeFalse();
        result.Detail.Should().Contain("output looks garbled");
    }

    [TestMethod]
    [DataRow(404)]
    [DataRow(405)]
    public void SupportsResponses_NotFoundOrMethodNotAllowed_ReturnsFalse(int status)
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ResponsesUrl, ok: false, status: status, body: "");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act / Assert
        providers.SupportsResponses(BaseUrl).Should().BeFalse();
    }

    [TestMethod]
    [DataRow(true, 200)]
    [DataRow(false, 400)]
    public void SupportsResponses_OtherStatuses_ReturnsTrue(bool ok, int status)
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ResponsesUrl, ok, status, "");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act / Assert
        providers.SupportsResponses(BaseUrl).Should().BeTrue();
    }

    [TestMethod]
    public void SupportsResponses_PostThrows_ReturnsFalse()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPostException(ResponsesUrl, new HttpRequestException("connection refused"));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.SupportsResponses(BaseUrl);

        // Assert
        result.Should().BeFalse();
    }

    [TestMethod]
    public void ModelContextLength_LmStudioLoadedModel_ReturnsLoadedContextLength()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddGet("http://localhost:1234/api/v1/models", LmStudioModelsJson("""
            {
              "key": "target",
              "loaded_instances": [ { "id": "target", "config": { "context_length": 8192 } } ],
              "max_context_length": 32768
            }
            """));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.ModelContextLength(LmStudioItem("target"));

        // Assert
        result.Should().Be(8192);
    }

    [TestMethod]
    public void ModelContextLength_LmStudioUnloadedModel_ReturnsMaxContextLength()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddGet("http://localhost:1234/api/v1/models", LmStudioModelsJson("""
            {
              "key": "target",
              "loaded_instances": [],
              "max_context_length": 32768
            }
            """));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.ModelContextLength(LmStudioItem("target"));

        // Assert
        result.Should().Be(32768);
    }

    [TestMethod]
    public void ModelContextLength_LmStudioModelNotFound_ReturnsZero()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddGet("http://localhost:1234/api/v1/models", LmStudioModelsJson("""
            {
              "key": "other",
              "loaded_instances": [ { "id": "other", "config": { "context_length": 8192 } } ],
              "max_context_length": 32768
            }
            """));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.ModelContextLength(LmStudioItem("target"));

        // Assert
        result.Should().Be(0);
    }

    [TestMethod]
    public void ModelContextLength_LmStudioMalformedJson_ReturnsZero()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddGet("http://localhost:1234/api/v1/models", "{ not json");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.ModelContextLength(LmStudioItem("target"));

        // Assert
        result.Should().Be(0);
    }

    [TestMethod]
    public void ModelContextLength_FoundryModel_ReturnsContextFromModelInfo()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["foundry"] = @"C:\fake\foundry.exe";
        proc.AddRun(@"C:\fake\foundry.exe", "model info phi4-mini -o json",
            stdout: """noise {"model":{"alias":"phi4-mini","contextLength":131072,"supportsToolCalling":true}} trailing""");
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act
        var result = providers.ModelContextLength(FoundryItem("Phi-4 Mini", "phi4-mini"));

        // Assert
        result.Should().Be(131072);
    }

    [TestMethod]
    public void ModelContextLength_FoundryNpuSmallContext_ReturnsSmallValue()
    {
        // Arrange: the NPU/OpenVINO failure mode - a tiny fixed context.
        var proc = new FakeProcessRunner();
        proc.WhichResults["foundry"] = @"C:\fake\foundry.exe";
        proc.AddRun(@"C:\fake\foundry.exe", "model info qwen2.5-coder-1.5b -o json",
            stdout: """{"model":{"alias":"qwen2.5-coder-1.5b","contextLength":4224}}""");
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act
        var result = providers.ModelContextLength(FoundryItem("qwen npu", "qwen2.5-coder-1.5b"));

        // Assert
        result.Should().Be(4224);
    }

    [TestMethod]
    public void ModelContextLength_FoundryModelInfoFails_ReturnsZero()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["foundry"] = @"C:\fake\foundry.exe";
        proc.AddRun(@"C:\fake\foundry.exe", "model info phi4-mini -o json", code: 1, stderr: "boom");
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act
        var result = providers.ModelContextLength(FoundryItem("Phi-4 Mini", "phi4-mini"));

        // Assert
        result.Should().Be(0);
    }

    [TestMethod]
    public void ModelContextLength_FoundryMalformedJson_ReturnsZero()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["foundry"] = @"C:\fake\foundry.exe";
        proc.AddRun(@"C:\fake\foundry.exe", "model info phi4-mini -o json", stdout: "{ not json");
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act
        var result = providers.ModelContextLength(FoundryItem("Phi-4 Mini", "phi4-mini"));

        // Assert
        result.Should().Be(0);
    }

    [TestMethod]
    public void ProbeToolCalling_NativeToolCall_ReturnsOk()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200,
            body: """{"choices":[{"message":{"content":"","tool_calls":[{"function":{"name":"get_time"}}]}}]}""");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.ProbeToolCalling(BaseUrl, "model");

        // Assert
        result.Status.Should().Be(ProviderHub.ToolStatus.Ok);
    }

    [TestMethod]
    public void ProbeToolCalling_ToolCallEmittedAsText_ReturnsNotNative()
    {
        // Arrange: the qwen2.5-coder failure mode - tool call dumped into content.
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200,
            body: Completion(content: """{"name": "get_time", "arguments": {}}"""));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.ProbeToolCalling(BaseUrl, "model");

        // Assert
        result.Status.Should().Be(ProviderHub.ToolStatus.NotNative);
    }

    [TestMethod]
    public void ProbeToolCalling_PlainAnswerNoToolCall_ReturnsInconclusive()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200,
            body: Completion(content: "I am unable to determine the current time."));
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.ProbeToolCalling(BaseUrl, "model");

        // Assert
        result.Status.Should().Be(ProviderHub.ToolStatus.Inconclusive);
    }

    [TestMethod]
    public void ProbeToolCalling_HttpError_ReturnsInconclusive()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: false, status: 500, body: "");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.ProbeToolCalling(BaseUrl, "model");

        // Assert
        result.Status.Should().Be(ProviderHub.ToolStatus.Inconclusive);
    }

    [TestMethod]
    [DataRow("""{"name": "get_time", "arguments": {}}""", true)]
    [DataRow("<tool_call>get_time</tool_call>", true)]
    [DataRow("I am ready to help.", false)]
    [DataRow("", false)]
    public void LooksLikeToolCallText_ClassifiesContent(string content, bool expected) =>
        ProviderHub.LooksLikeToolCallText(content).Should().Be(expected);

    [TestMethod]
    public void HasToolCall_EmptyToolCallsArray_ReturnsFalse() =>
        ProviderHub.HasToolCall("""{"choices":[{"message":{"content":"hi","tool_calls":[]}}]}""")
            .Should().BeFalse();

    [TestMethod]
    public void HasToolCall_PopulatedToolCallsArray_ReturnsTrue() =>
        ProviderHub.HasToolCall("""{"choices":[{"message":{"tool_calls":[{"function":{"name":"x"}}]}}]}""")
            .Should().BeTrue();

    [TestMethod]
    public void ProbeToolCalling_PostsToolsWithAutoChoice()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200,
            body: """{"choices":[{"message":{"tool_calls":[{"function":{"name":"get_time"}}]}}]}""");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        providers.ProbeToolCalling(BaseUrl, "model");

        // Assert: the probe must offer a tool and not force the call (auto, not required).
        var call = http.PostCalls.Should().ContainSingle().Subject;
        call.Json.Should().Contain("\"tools\"");
        call.Json.Should().Contain("\"tool_choice\":\"auto\"");
    }

    [TestMethod]
    public void ProbeToolCalling_UsesGenerousTokenBudget()
    {
        // Arrange: a small budget truncates a reasoning model's tool call
        // (finish_reason "length" -> undispatchable), so the probe must request >=512.
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200,
            body: """{"choices":[{"message":{"tool_calls":[{"function":{"name":"get_time"}}]}}]}""");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        providers.ProbeToolCalling(BaseUrl, "model");

        // Assert
        http.PostCalls.Should().ContainSingle().Which.Json.Should().Contain("\"max_tokens\":512");
    }

    [TestMethod]
    public void ProbeToolCalling_ReasoningContentWithToolCall_FlagsReasoning()
    {
        // Arrange: a conditional reasoner (e.g. gemma) emits reasoning_content before the
        // call on the substantive probe prompt - the trivial warm-up misses this.
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200,
            body: """{"choices":[{"message":{"content":"","reasoning_content":"Let me think...","tool_calls":[{"function":{"name":"get_time"}}]}}]}""");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.ProbeToolCalling(BaseUrl, "model");

        // Assert
        result.Status.Should().Be(ProviderHub.ToolStatus.Ok);
        result.Reasoning.Should().BeTrue();
    }

    [TestMethod]
    public void ProbeToolCalling_NoReasoningField_ReasoningFalse()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddPost(ChatUrl, ok: true, status: 200,
            body: """{"choices":[{"message":{"tool_calls":[{"function":{"name":"get_time"}}]}}]}""");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act
        var result = providers.ProbeToolCalling(BaseUrl, "model");

        // Assert
        result.Reasoning.Should().BeFalse();
    }

    [TestMethod]
    public void Unload_OllamaModel_RecordsOllamaStop()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["ollama"] = @"C:\fake\ollama.exe";
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act
        providers.Unload(new MenuItem { Kind = MenuItemKind.Model, Provider = "Ollama", Model = "llama3" });

        // Assert
        proc.RunCalls.Should().ContainSingle()
            .Which.Should().Be(new FakeProcessRunner.RunCall(@"C:\fake\ollama.exe", "stop llama3", 30_000));
    }

    [TestMethod]
    public void Unload_FoundryModel_RecordsFoundryUnload()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["foundry"] = @"C:\fake\foundry.exe";
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act
        providers.Unload(new MenuItem
        {
            Kind = MenuItemKind.Model,
            Provider = "Foundry",
            Model = "Phi-4 Mini",
            LoadAlias = "phi4-mini",
        });

        // Assert
        proc.RunCalls.Should().ContainSingle()
            .Which.Should().Be(new FakeProcessRunner.RunCall(@"C:\fake\foundry.exe", "model unload phi4-mini", 60_000));
    }

    [TestMethod]
    public void Unload_LmStudioModel_RecordsLmsUnload()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["lms"] = @"C:\fake\lms.exe";
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act
        providers.Unload(new MenuItem { Kind = MenuItemKind.Model, Provider = "LM Studio", Model = "qwen" });

        // Assert
        proc.RunCalls.Should().ContainSingle()
            .Which.Should().Be(new FakeProcessRunner.RunCall(@"C:\fake\lms.exe", "unload qwen", 30_000));
    }

    [TestMethod]
    public void EnsureServer_FoundryStatusJson_ReturnsParsedBaseUrlAndLoadsModel()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["foundry"] = @"C:\fake\foundry.exe";
        proc.AddRun(@"C:\fake\foundry.exe", "server start", timeoutMs: 60_000);
        proc.AddRun(@"C:\fake\foundry.exe", "server status -o json", stdout: """noise {"webUrls":["http://127.0.0.1:9999"]}""");
        proc.AddRun(@"C:\fake\foundry.exe", "model load phi4-mini", timeoutMs: 180_000);
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act
        var result = providers.EnsureServer(new MenuItem
        {
            Kind = MenuItemKind.Model,
            Provider = "Foundry",
            Model = "Phi-4 Mini",
            LoadAlias = "phi4-mini",
        });

        // Assert
        result.Should().Be("http://127.0.0.1:9999/v1");
        proc.RunCalls.Should().Equal(
            new FakeProcessRunner.RunCall(@"C:\fake\foundry.exe", "server start", 60_000),
            new FakeProcessRunner.RunCall(@"C:\fake\foundry.exe", "server status -o json", 30_000),
            new FakeProcessRunner.RunCall(@"C:\fake\foundry.exe", "model load phi4-mini", 180_000));
    }

    [TestMethod]
    public void EnsureServer_LmStudioWithoutBaseUrl_ReturnsDefaultUrlAndStartsServer()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["lms"] = @"C:\fake\lms.exe";
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act
        var result = providers.EnsureServer(new MenuItem { Kind = MenuItemKind.Model, Provider = "LM Studio", Model = "qwen" });

        // Assert
        result.Should().Be("http://localhost:1234/v1");
        proc.RunCalls.Should().ContainSingle()
            .Which.Should().Be(new FakeProcessRunner.RunCall(@"C:\fake\lms.exe", "server start", 60_000));
    }

    [TestMethod]
    public void EnsureServer_OllamaWithoutBaseUrl_ReturnsDefaultUrlWithoutProcessCalls()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act
        var result = providers.EnsureServer(new MenuItem { Kind = MenuItemKind.Model, Provider = "Ollama", Model = "llama3" });

        // Assert
        result.Should().Be("http://localhost:11434/v1");
        proc.RunCalls.Should().BeEmpty();
    }

    [TestMethod]
    public void HasCopilot_WhenCopilotOnPath_ReturnsTrue()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["copilot"] = @"C:\fake\copilot.exe";
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act / Assert
        providers.HasCopilot.Should().BeTrue();
    }

    [TestMethod]
    public void HasCopilot_WhenCopilotMissing_ReturnsFalse()
    {
        // Arrange
        var providers = new ProviderHub(new FakeProcessRunner(), new FakeHttpGateway());

        // Act / Assert
        providers.HasCopilot.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("\"oops\"")]
    [DataRow("123")]
    [DataRow("{\"choices\":[]}")]
    [DataRow("{\"choices\":[null]}")]
    [DataRow("{\"choices\":[\"text\"]}")]
    public void HasToolCall_ParseableButWrongShape_ReturnsFalse(string body) =>
        ProviderHub.HasToolCall(body).Should().BeFalse();

    [TestMethod]
    public void ModelContextLength_FoundryModelNotObject_ReturnsZero()
    {
        // Arrange: model-info returns valid JSON but "model" is a string, not an object.
        var proc = new FakeProcessRunner();
        proc.WhichResults["foundry"] = @"C:\fake\foundry.exe";
        proc.AddRun(@"C:\fake\foundry.exe", "model info phi4-mini -o json", stdout: """{"model":"phi"}""");
        var providers = new ProviderHub(proc, new FakeHttpGateway());

        // Act / Assert
        providers.ModelContextLength(FoundryItem("Phi-4 Mini", "phi4-mini")).Should().Be(0);
    }

    [TestMethod]
    public void ModelContextLength_LmStudioRootNotObject_ReturnsZero()
    {
        // Arrange: a different service on port 1234 answers with a JSON array.
        var http = new FakeHttpGateway();
        http.AddGet("http://localhost:1234/api/v1/models", "[]");
        var providers = new ProviderHub(new FakeProcessRunner(), http);

        // Act / Assert
        providers.ModelContextLength(LmStudioItem("target")).Should().Be(0);
    }

    private static MenuItem LmStudioItem(string model) => new()
    {
        Kind = MenuItemKind.Model,
        Provider = "LM Studio",
        BaseUrl = "http://localhost:1234/v1",
        Model = model,
    };

    private static MenuItem FoundryItem(string model, string alias) => new()
    {
        Kind = MenuItemKind.Model,
        Provider = "Foundry",
        Model = model,
        LoadAlias = alias,
    };

    private static string Completion(string content, string? reasoning = null, string? reasoningContent = null)
    {
        var fields = new List<string> { $"""
            "content":"{Json.Escape(content)}"
            """ };

        if (reasoning is not null)
            fields.Add($"""
                "reasoning":"{Json.Escape(reasoning)}"
                """);

        if (reasoningContent is not null)
            fields.Add($"""
                "reasoning_content":"{Json.Escape(reasoningContent)}"
                """);

        return "{\"choices\":[{\"message\":{" + string.Join(",", fields) + "}}]}";
    }

    private static string LmStudioModelsJson(string modelJson) => $$"""
        {
          "models": [
            {{modelJson}}
          ]
        }
        """;
}
