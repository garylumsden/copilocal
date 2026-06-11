using Copilocal;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class ProgramTests
{
    [TestMethod]
    public void TokenLimits_ConfigOverridesWin_ReturnsConfiguredLimits()
    {
        // Arrange
        var config = new LaunchConfig
        {
            MaxPromptTokens = 123,
            MaxOutputTokens = 456,
        };

        // Act
        var result = Program.TokenLimits(config, ctx: 32_768);

        // Assert
        result.Should().Be((123, 456));
    }

    [TestMethod]
    public void TokenLimits_KnownContext_DerivesPromptAndOutputLimits()
    {
        // Arrange
        var config = new LaunchConfig();

        // Act
        var result = Program.TokenLimits(config, ctx: 32_768);

        // Assert
        result.Should().Be((24_064, 8_192));
    }

    [TestMethod]
    public void TokenLimits_UnknownContext_ReturnsZeroLimits()
    {
        // Arrange
        var config = new LaunchConfig();

        // Act
        var result = Program.TokenLimits(config, ctx: 0);

        // Assert
        result.Should().Be((0, 0));
    }
}
