using Copilocal;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class ProvidersTests
{
    [TestMethod]
    public void LooksGarbled_CoherentProse_ReturnsFalse()
    {
        // Arrange
        const string text = "This model responds with coherent prose across several normal English words.";

        // Act
        var result = Providers.LooksGarbled(text);

        // Assert
        result.Should().BeFalse();
    }

    [TestMethod]
    public void LooksGarbled_ShortRepeatingUnits_ReturnsTrue()
    {
        // Arrange
        const string text = "UKUKUKUKUK";

        // Act
        var result = Providers.LooksGarbled(text);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    public void LooksGarbled_LowLetterRatio_ReturnsTrue()
    {
        // Arrange
        const string text = "90. 111 161 .222 33r 440 666";

        // Act
        var result = Providers.LooksGarbled(text);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    public void LooksGarbled_MostlyDigitsShortString_ReturnsTrue()
    {
        // Arrange: low letter ratio even though it is short.
        const string text = "alpha 123 456";

        // Act
        var result = Providers.LooksGarbled(text);

        // Assert
        result.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("Ready to assist!")]
    [DataRow("Ready to go.")]
    [DataRow("Yes, I am ready.")]
    [DataRow("I'm ready to help.")]
    public void LooksGarbled_TerseValidReply_ReturnsFalse(string text) =>
        Providers.LooksGarbled(text).Should().BeFalse();

    [TestMethod]
    public void LooksGarbled_VeryShortString_ReturnsFalse()
    {
        // Arrange
        const string text = "._.";

        // Act
        var result = Providers.LooksGarbled(text);

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
                  "displayName": "Phi-4 Mini",
                  "alias": "phi4-mini",
                  "supportsToolCalling": true
                },
                {
                  "displayName": "No Alias",
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
        var result = Providers.ParseFoundry(json).ToList();

        // Assert
        result.Should().HaveCount(2);
        result[0].Id.Should().Be("Phi-4 Mini");
        result[0].Alias.Should().Be("phi4-mini");
        result[0].Tools.Should().BeTrue();
        result[1].Id.Should().Be("No Alias");
        result[1].Alias.Should().Be("No Alias");
        result[1].Tools.Should().BeFalse();
    }

    [TestMethod]
    public void ParseFoundry_MalformedOrEmptyJson_ReturnsEmpty()
    {
        // Arrange
        var inputs = new[] { "", "not json", """{ "models": [ }""" };

        // Act
        var result = inputs.Select(json => Providers.ParseFoundry(json).ToList()).ToList();

        // Assert
        result.Should().OnlyContain(items => items.Count == 0);
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
        var result = Providers.ParseLmStudio(json).ToList();

        // Assert
        result.Should().Equal("qwen/qwen3-coder", "meta/llama");
    }

    [TestMethod]
    public void ParseLmStudio_MalformedOrEmptyJson_ReturnsEmpty()
    {
        // Arrange
        var inputs = new[] { "", "not json", """[{ "modelKey": ]""" };

        // Act
        var result = inputs.Select(json => Providers.ParseLmStudio(json).ToList()).ToList();

        // Assert
        result.Should().OnlyContain(items => items.Count == 0);
    }
}
