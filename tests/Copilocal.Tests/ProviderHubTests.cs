using Copilocal;
using Copilocal.Launch;
using Copilocal.Providers;
using Copilocal.Tests.Fakes;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class ProviderHubTests
{
    [TestMethod]
    public void LooksGarbled_CoherentProse_ReturnsFalse()
    {
        // Arrange
        const string text = "This model responds with coherent prose across several normal English words.";

        // Act
        var result = ProviderResponses.LooksGarbled(text);

        // Assert
        result.Should().BeFalse();
    }

    [TestMethod]
    public void LooksGarbled_ShortRepeatingUnits_ReturnsTrue()
    {
        // Arrange
        const string text = "UKUKUKUKUK";

        // Act
        var result = ProviderResponses.LooksGarbled(text);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    public void LooksGarbled_LowLetterRatio_ReturnsTrue()
    {
        // Arrange
        const string text = "90. 111 161 .222 33r 440 666";

        // Act
        var result = ProviderResponses.LooksGarbled(text);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    public void LooksGarbled_MostlyDigitsShortString_ReturnsTrue()
    {
        // Arrange: low letter ratio even though it is short.
        const string text = "alpha 123 456";

        // Act
        var result = ProviderResponses.LooksGarbled(text);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("Ready to assist!")]
    [DataRow("Ready to go.")]
    [DataRow("Yes, I am ready.")]
    [DataRow("I'm ready to help.")]
    public void LooksGarbled_TerseValidReply_ReturnsFalse(string text) =>
        ProviderResponses.LooksGarbled(text).Should().BeFalse();

    [TestMethod]
    public void LooksGarbled_VeryShortString_ReturnsFalse()
    {
        // Arrange
        const string text = "._.";

        // Act
        var result = ProviderResponses.LooksGarbled(text);

        // Assert
        result.Should().BeFalse();
    }

    [TestMethod]
    public void ParseFoundry_ValidJson_ReturnsModelItems()
    {
        // Arrange
        const string json = """
            prefix {
              "models": [
                {
                  "displayName": "Qwen NPU",
                  "id": "qwen2.5-coder-7b-instruct-openvino-npu:4",
                  "alias": "qwen2.5-coder-7b",
                  "supportsToolCalling": true
                },
                {
                  "displayName": "No Id Or Alias",
                  "supportsToolCalling": false
                },
                {
                  "displayName": "",
                  "alias": "ignored"
                }
              ]
            } suffix
            """;

        // Act
        var result = ProviderParsers.ParseFoundry(json).ToList();

        // Assert
        result.Should().HaveCount(2);
        result[0].Id.Should().Be("Qwen NPU");
        result[0].LoadId.Should().Be("qwen2.5-coder-7b-instruct-openvino-npu:4");   // variant id preferred
        result[0].Tools.Should().BeTrue();
        result[1].Id.Should().Be("No Id Or Alias");
        result[1].LoadId.Should().Be("No Id Or Alias");                            // falls back to display
        result[1].Tools.Should().BeFalse();
    }

    [TestMethod]
    public void ParseFoundry_MalformedOrEmptyJson_ReturnsEmpty()
    {
        // Arrange
        var inputs = new[] { "", "not json", """{ "models": [ }""" };

        // Act
        var result = inputs.Select(json => ProviderParsers.ParseFoundry(json).ToList()).ToList();

        // Assert
        result.Should().OnlyContain(items => items.Count == 0);
    }

    [TestMethod]
    public void ParseFoundry_NonArrayModelsOrNonObjectElements_DoesNotThrow()
    {
        // "models" not an array -> empty (no throw).
        ProviderParsers.ParseFoundry("""{"models":"oops"}""").ToList().Should().BeEmpty();
        // root not an object -> empty (no throw).
        ProviderParsers.ParseFoundry("""{"x":1}""").ToList().Should().BeEmpty();
        // non-object array elements are skipped, valid ones kept.
        ProviderParsers.ParseFoundry("""{"models":[123,"s",{"displayName":"Phi","alias":"phi"}]}""")
            .ToList().Should().ContainSingle().Which.Id.Should().Be("Phi");
    }

    [TestMethod]
    public void ParseLmStudio_ValidJson_ReturnsNonEmbeddingModelKeys()
    {
        // Arrange
        const string json = """
            header [
              {
                "type": "llm",
                "modelKey": "qwen/qwen3-coder"
              },
              {
                "type": "embedding",
                "modelKey": "nomic-embed-text"
              },
              {
                "type": "llm",
                "modelKey": "meta/llama"
              },
              {
                "type": "llm"
              }
            ] footer
            """;

        // Act
        var result = ProviderParsers.ParseLmStudio(json).ToList();

        // Assert
        result.Should().Equal("qwen/qwen3-coder", "meta/llama");
    }

    [TestMethod]
    public void ParseLmStudio_MalformedOrEmptyJson_ReturnsEmpty()
    {
        // Arrange
        var inputs = new[] { "", "not json", """[{ "modelKey": ]""" };

        // Act
        var result = inputs.Select(json => ProviderParsers.ParseLmStudio(json).ToList()).ToList();

        // Assert
        result.Should().OnlyContain(items => items.Count == 0);
    }

    [TestMethod]
    public void ParseLmStudio_NonObjectElements_AreSkipped()
    {
        ProviderParsers.ParseLmStudio("""[123,"s",{"type":"llm","modelKey":"a"}]""")
            .ToList().Should().Equal("a");
    }

    [TestMethod]
    public void ParseLiteLlmModels_ValidJson_ReturnsDistinctIds()
    {
        const string json = """
            {
              "object": "list",
              "data": [
                { "id": "gpt-4o", "object": "model" },
                { "id": "qwen2.5-coder:7b", "object": "model" },
                { "id": "gpt-4o", "object": "model" },
                { "object": "model" }
              ]
            }
            """;

        ProviderParsers.ParseLiteLlmModels(json)
            .ToList()
            .Should()
            .Equal("gpt-4o", "qwen2.5-coder:7b");
    }

    [TestMethod]
    public void ParseLiteLlmModels_WhitespaceIds_AreIgnored()
    {
        const string json = """
            {
              "data": [
                { "id": "   " },
                { "id": "\t" },
                { "id": "gpt-4.1-mini" }
              ]
            }
            """;

        ProviderParsers.ParseLiteLlmModels(json)
            .ToList()
            .Should()
            .Equal("gpt-4.1-mini");
    }

    [TestMethod]
    public void EnsureServer_LiteLlm_NormalizesBaseUrl()
    {
        var hub = new ProviderHub(new FakeProcessRunner(), new FakeHttpGateway());
        var item = new MenuItem
        {
            Kind = MenuItemKind.Model,
            Provider = "LiteLLM",
            BaseUrl = " https://proxy.example.com/litellm/ ",
            Model = "gpt-4o-mini",
        };

        hub.EnsureServer(item).Should().Be("https://proxy.example.com/litellm/v1");
    }

    [TestMethod]
    public void ParseLiteLlmModels_WrongShapeOrMalformed_ReturnsEmpty()
    {
        ProviderParsers.ParseLiteLlmModels("""{"data":"oops"}""").Should().BeEmpty();
        ProviderParsers.ParseLiteLlmModels("""{"x":1}""").Should().BeEmpty();
        ProviderParsers.ParseLiteLlmModels("{ not json").Should().BeEmpty();
    }

    [TestMethod]
    public void GatherModels_LiteLlm_UsesConfiguredBearerToken()
    {
        RunWithIsolatedLaunchConfig("""
            {
              "liteLlmEnabled": true,
              "liteLlmBaseUrl": "http://localhost:4000/v1",
              "liteLlmApiKey": "test_key_lllm"
            }
            """, () =>
        {
            var proc = new FakeProcessRunner();
            var http = new FakeHttpGateway();
            http.AddGet("http://localhost:4000/v1/models", """{"data":[{"id":"ollama/qwen2.5-coder:7b"}]}""");
            var hub = new ProviderHub(proc, http);

            var models = hub.GatherModels(includeLocalProviders: false, includeLiteLlm: true);

            models.Should().ContainSingle(m => m.Provider == "LiteLLM" && m.Model == "ollama/qwen2.5-coder:7b");
            http.GetCalls.Should().ContainSingle();
            http.GetCalls[0].Url.Should().Be("http://localhost:4000/v1/models");
            http.GetCalls[0].BearerToken.Should().Be("sk-test_key_lllm");
        });
    }

    [TestMethod]
    public void GatherModels_LiteLlm_RetriesWhileProxyStartsAndThenReturnsModels()
    {
        RunWithIsolatedLaunchConfig("""
            {
              "liteLlmEnabled": true,
              "liteLlmBaseUrl": "http://localhost:4000/v1",
              "liteLlmApiKey": "test_key_lllm"
            }
            """, () =>
        {
            var proc = new FakeProcessRunner();
            var http = new FakeHttpGateway();
            const string modelsUrl = "http://localhost:4000/v1/models";
            http.AddGetException(modelsUrl, new HttpRequestException("connection refused"));
            http.AddGetException(modelsUrl, new HttpRequestException("connection refused"));
            http.AddGet(modelsUrl, """{"data":[{"id":"ollama/qwen2.5-coder:7b"}]}""");
            var hub = new ProviderHub(proc, http);

            var models = hub.GatherModels(includeLocalProviders: false, includeLiteLlm: true);

            models.Should().ContainSingle(m => m.Provider == "LiteLLM" && m.Model == "ollama/qwen2.5-coder:7b");
            http.GetCalls.Should().HaveCount(3);
            http.GetCalls.Should().OnlyContain(c => c.BearerToken == "sk-test_key_lllm");
        });
    }

    private static void RunWithIsolatedLaunchConfig(string configJson, Action action)
    {
        string path = LaunchConfig.FilePath;
        string? dir = Path.GetDirectoryName(path);
        string backup = Path.Join(Path.GetTempPath(), $"copilocal-config-backup-{Guid.NewGuid():N}.json");
        bool hadFile = File.Exists(path);
        if (hadFile) File.Copy(path, backup, overwrite: true);

        try
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, configJson);
            action();
        }
        finally
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (hadFile && File.Exists(backup))
            {
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(backup, path, overwrite: true);
            }

            try { if (File.Exists(backup)) File.Delete(backup); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
