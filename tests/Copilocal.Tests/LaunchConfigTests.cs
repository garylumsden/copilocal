using Copilocal;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class LaunchConfigTests
{
    [TestMethod]
    public void ToArgs_EmptyConfig_ReturnsNoArgs()
    {
        // Arrange
        var config = new LaunchConfig();

        // Act
        var result = config.ToArgs();

        // Assert
        result.Should().BeEmpty();
    }

    [TestMethod]
    public void ToArgs_FlagsConfigured_EmitsSortedFlags()
    {
        // Arrange
        var config = new LaunchConfig
        {
            Flags = ["--yolo", "--banner"],
        };

        // Act
        var result = config.ToArgs();

        // Assert
        result.Should().Equal("--banner", "--yolo");
    }

    [TestMethod]
    public void ToArgs_ValidReasoningEffort_EmitsReasoningEffortArg()
    {
        // Arrange
        var config = new LaunchConfig
        {
            ReasoningEffort = "high",
        };

        // Act
        var result = config.ToArgs();

        // Assert
        result.Should().Equal("--reasoning-effort=high");
    }

    [TestMethod]
    public void ToArgs_InvalidReasoningEffort_OmitsReasoningEffortArg()
    {
        // Arrange
        var config = new LaunchConfig
        {
            ReasoningEffort = "invalid",
        };

        // Act
        var result = config.ToArgs();

        // Assert
        result.Should().BeEmpty();
    }

    [TestMethod]
    public void ToArgs_EmptyReasoningEffort_OmitsReasoningEffortArg()
    {
        // Arrange
        var config = new LaunchConfig
        {
            ReasoningEffort = "",
        };

        // Act
        var result = config.ToArgs();

        // Assert
        result.Should().BeEmpty();
    }

    [TestMethod]
    public void ToArgs_ExtraArgsWithDoubleQuotedSegments_SplitsArgsAndPreservesQuotedWhitespace()
    {
        // Arrange
        var config = new LaunchConfig
        {
            ExtraArgs = "--foo bar --name \"hello world\" \"standalone quoted\"",
        };

        // Act
        var result = config.ToArgs();

        // Assert
        result.Should().Equal("--foo", "bar", "--name", "hello world", "standalone quoted");
    }
}
