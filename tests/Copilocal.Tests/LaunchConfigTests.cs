using Copilocal;
using Copilocal.Launch;
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

    [TestMethod]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        // Arrange: persist to a temp path so the real ~/.copilocal/config.json is untouched.
        string path = Path.Join(Path.GetTempPath(), $"copilocal-cfg-{Guid.NewGuid():N}.json");
        var config = new LaunchConfig
        {
            Flags = ["--yolo", "--banner"],
            ReasoningEffort = "high",
            MaxPromptTokens = 4096,
            MaxOutputTokens = 1024,
            // A value containing a quote and control char exercises correct JSON escaping.
            ExtraArgs = "--name \"a\tb\"",
        };

        try
        {
            // Act
            config.Save(path);
            var loaded = LaunchConfig.Load(path);

            // Assert
            loaded.Flags.Should().BeEquivalentTo(config.Flags);
            loaded.ReasoningEffort.Should().Be("high");
            loaded.MaxPromptTokens.Should().Be(4096);
            loaded.MaxOutputTokens.Should().Be(1024);
            loaded.ExtraArgs.Should().Be("--name \"a\tb\"");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_NonObjectRoot_ReturnsDefaults()
    {
        // Arrange: a hand-edited config that parses but isn't a JSON object.
        string path = Path.Join(Path.GetTempPath(), $"copilocal-arr-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "[]");

        try
        {
            // Act
            var loaded = LaunchConfig.Load(path);

            // Assert
            loaded.Flags.Should().BeEmpty();
            loaded.ExtraArgs.Should().BeEmpty();
            loaded.MaxPromptTokens.Should().Be(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Load_MissingFile_ReturnsDefaults()
    {
        // Arrange
        string path = Path.Join(Path.GetTempPath(), $"copilocal-missing-{Guid.NewGuid():N}.json");

        // Act
        var loaded = LaunchConfig.Load(path);

        // Assert
        loaded.Flags.Should().BeEmpty();
        loaded.ReasoningEffort.Should().BeEmpty();
        loaded.MaxPromptTokens.Should().Be(0);
        loaded.MaxOutputTokens.Should().Be(0);
        loaded.ExtraArgs.Should().BeEmpty();
    }
}
