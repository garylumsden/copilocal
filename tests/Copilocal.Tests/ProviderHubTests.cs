using Copilocal;
using Copilocal.Providers;
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
        var result = ProviderHub.LooksGarbled(text);

        // Assert
        result.Should().BeFalse();
    }

    [TestMethod]
    public void LooksGarbled_ShortRepeatingUnits_ReturnsTrue()
    {
        // Arrange
        const string text = "UKUKUKUKUK";

        // Act
        var result = ProviderHub.LooksGarbled(text);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    public void LooksGarbled_LowLetterRatio_ReturnsTrue()
    {
        // Arrange
        const string text = "90. 111 161 .222 33r 440 666";

        // Act
        var result = ProviderHub.LooksGarbled(text);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    public void LooksGarbled_MostlyDigitsShortString_ReturnsTrue()
    {
        // Arrange: low letter ratio even though it is short.
        const string text = "alpha 123 456";

        // Act
        var result = ProviderHub.LooksGarbled(text);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("Ready to assist!")]
    [DataRow("Ready to go.")]
    [DataRow("Yes, I am ready.")]
    [DataRow("I'm ready to help.")]
    public void LooksGarbled_TerseValidReply_ReturnsFalse(string text) =>
        ProviderHub.LooksGarbled(text).Should().BeFalse();

    [TestMethod]
    public void LooksGarbled_VeryShortString_ReturnsFalse()
    {
        // Arrange
        const string text = "._.";

        // Act
        var result = ProviderHub.LooksGarbled(text);

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
        var result = ProviderHub.ParseFoundry(json).ToList();

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
        var result = inputs.Select(json => ProviderHub.ParseFoundry(json).ToList()).ToList();

        // Assert
        result.Should().OnlyContain(items => items.Count == 0);
    }

    [TestMethod]
    public void ParseFoundry_NonArrayModelsOrNonObjectElements_DoesNotThrow()
    {
        // "models" not an array -> empty (no throw).
        ProviderHub.ParseFoundry("""{"models":"oops"}""").ToList().Should().BeEmpty();
        // root not an object -> empty (no throw).
        ProviderHub.ParseFoundry("""{"x":1}""").ToList().Should().BeEmpty();
        // non-object array elements are skipped, valid ones kept.
        ProviderHub.ParseFoundry("""{"models":[123,"s",{"displayName":"Phi","alias":"phi"}]}""")
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
        var result = ProviderHub.ParseLmStudio(json).ToList();

        // Assert
        result.Should().Equal("qwen/qwen3-coder", "meta/llama");
    }

    [TestMethod]
    public void ParseLmStudio_MalformedOrEmptyJson_ReturnsEmpty()
    {
        // Arrange
        var inputs = new[] { "", "not json", """[{ "modelKey": ]""" };

        // Act
        var result = inputs.Select(json => ProviderHub.ParseLmStudio(json).ToList()).ToList();

        // Assert
        result.Should().OnlyContain(items => items.Count == 0);
    }

    [TestMethod]
    public void ParseLmStudio_NonObjectElements_AreSkipped()
    {
        ProviderHub.ParseLmStudio("""[123,"s",{"type":"llm","modelKey":"a"}]""")
            .ToList().Should().Equal("a");
    }
}
