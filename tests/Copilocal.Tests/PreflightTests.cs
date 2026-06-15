using Copilocal;
using Copilocal.Tests.Fakes;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class PreflightTests
{
    [TestMethod]
    public void Ok_NoToolCalling_NonInteractive_Blocks()
    {
        // Arrange: a Foundry model whose catalog entry doesn't support tool calling.
        var providers = new Providers(new FakeProcessRunner(), new FakeHttpGateway());
        var item = new MenuItem { Kind = MenuItemKind.Model, Provider = "Foundry", Model = "deepseek-r1", Tools = false };

        // Act / Assert
        Preflight.Ok(item, interactive: false, providers).Should().BeFalse();
    }

    [TestMethod]
    public void Ok_LmStudioSmallContext_NonInteractive_Blocks()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddGet("http://localhost:1234/api/v1/models", LmStudioModels(maxContext: 4096));
        var providers = new Providers(new FakeProcessRunner(), http);

        // Act / Assert
        Preflight.Ok(LmStudioItem("target"), interactive: false, providers).Should().BeFalse();
    }

    [TestMethod]
    public void Ok_LmStudioLargeContext_NonInteractive_Proceeds()
    {
        // Arrange
        var http = new FakeHttpGateway();
        http.AddGet("http://localhost:1234/api/v1/models", LmStudioModels(maxContext: 32_768));
        var providers = new Providers(new FakeProcessRunner(), http);

        // Act / Assert
        Preflight.Ok(LmStudioItem("target"), interactive: false, providers).Should().BeTrue();
    }

    [TestMethod]
    public void Ok_FoundryNpuSmallContext_NonInteractive_Blocks()
    {
        // Arrange: NPU variant with a 4224-token context (too small for Copilot's prompt).
        var proc = new FakeProcessRunner();
        proc.WhichResults["foundry"] = @"C:\fake\foundry.exe";
        proc.AddRun(@"C:\fake\foundry.exe", "model info qwen2.5-coder-1.5b -o json",
            stdout: """{"model":{"contextLength":4224}}""");
        var providers = new Providers(proc, new FakeHttpGateway());
        var item = new MenuItem { Kind = MenuItemKind.Model, Provider = "Foundry", Model = "qwen npu", LoadAlias = "qwen2.5-coder-1.5b", Tools = true };

        // Act / Assert
        Preflight.Ok(item, interactive: false, providers).Should().BeFalse();
    }

    [TestMethod]
    public void Ok_UnknownContextWithTools_Proceeds()
    {
        // Arrange: model-info unavailable -> context unknown (0) -> not blocked.
        var proc = new FakeProcessRunner();
        proc.WhichResults["foundry"] = @"C:\fake\foundry.exe";
        proc.AddRun(@"C:\fake\foundry.exe", "model info phi4-mini -o json", code: 1);
        var providers = new Providers(proc, new FakeHttpGateway());
        var item = new MenuItem { Kind = MenuItemKind.Model, Provider = "Foundry", Model = "Phi-4 Mini", LoadAlias = "phi4-mini", Tools = true };

        // Act / Assert
        Preflight.Ok(item, interactive: false, providers).Should().BeTrue();
    }

    private static MenuItem LmStudioItem(string model) => new()
    {
        Kind = MenuItemKind.Model,
        Provider = "LM Studio",
        BaseUrl = "http://localhost:1234/v1",
        Model = model,
    };

    private static string LmStudioModels(int maxContext) => $$"""
        {
          "models": [
            { "key": "target", "loaded_instances": [], "max_context_length": {{maxContext}} }
          ]
        }
        """;
}
