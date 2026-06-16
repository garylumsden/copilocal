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
            LiteLlmEnabled = true,
            HideLocalProvidersWhenLiteLlm = true,
            LiteLlmBaseUrl = "http://localhost:4000",
            LiteLlmApiKey = "sk-test",
            LiteLlmApiKeyEnvVar = "MY_LITELLM_KEY",
            LiteLlmRuntimeMode = "python",
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
            loaded.LiteLlmEnabled.Should().BeTrue();
            loaded.HideLocalProvidersWhenLiteLlm.Should().BeTrue();
            loaded.LiteLlmBaseUrl.Should().Be("http://localhost:4000/v1");
            loaded.LiteLlmApiKey.Should().Be("sk-test");
            loaded.LiteLlmApiKeyEnvVar.Should().Be("MY_LITELLM_KEY");
            loaded.LiteLlmRuntimeMode.Should().Be("python");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void SaveThenLoad_LeavesNoTempFileAndRoundTripsContent()
    {
        string path = Path.Join(AppContext.BaseDirectory, $"copilocal-cfg-{Guid.NewGuid():N}.json");
        string tmpPath = path + ".tmp";
        var config = new LaunchConfig
        {
            Flags = ["--yolo", "--banner"],
            ReasoningEffort = "medium",
            MaxPromptTokens = 8192,
            MaxOutputTokens = 2048,
            ExtraArgs = "--model test-model",
            LiteLlmEnabled = true,
            HideLocalProvidersWhenLiteLlm = true,
            LiteLlmBaseUrl = "http://localhost:4000/v1",
            LiteLlmApiKey = "sk-test",
            LiteLlmApiKeyEnvVar = "MY_LITELLM_KEY",
            LiteLlmRuntimeMode = "python",
        };

        try
        {
            config.Save(path);
            var loaded = LaunchConfig.Load(path);

            File.Exists(tmpPath).Should().BeFalse();
            loaded.Flags.Should().BeEquivalentTo(config.Flags);
            loaded.ReasoningEffort.Should().Be(config.ReasoningEffort);
            loaded.MaxPromptTokens.Should().Be(config.MaxPromptTokens);
            loaded.MaxOutputTokens.Should().Be(config.MaxOutputTokens);
            loaded.ExtraArgs.Should().Be(config.ExtraArgs);
            loaded.LiteLlmEnabled.Should().Be(config.LiteLlmEnabled);
            loaded.HideLocalProvidersWhenLiteLlm.Should().Be(config.HideLocalProvidersWhenLiteLlm);
            loaded.LiteLlmBaseUrl.Should().Be(config.LiteLlmBaseUrl);
            loaded.LiteLlmApiKey.Should().Be(config.LiteLlmApiKey);
            loaded.LiteLlmApiKeyEnvVar.Should().Be(config.LiteLlmApiKeyEnvVar);
            loaded.LiteLlmRuntimeMode.Should().Be(config.LiteLlmRuntimeMode);
        }
        finally
        {
            File.Delete(path);
            File.Delete(tmpPath);
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
        loaded.LiteLlmEnabled.Should().BeFalse();
        loaded.HideLocalProvidersWhenLiteLlm.Should().BeFalse();
        loaded.LiteLlmBaseUrl.Should().Be(LaunchConfig.DefaultLiteLlmBaseUrl);
        loaded.LiteLlmApiKey.Should().BeEmpty();
        loaded.LiteLlmApiKeyEnvVar.Should().Be(LaunchConfig.DefaultLiteLlmApiKeyEnvVar);
        loaded.LiteLlmRuntimeMode.Should().Be("docker");
    }

    [TestMethod]
    public void Load_LiteLlmWhitespaceEnvVarAndInvalidMode_FallsBackToDefaults()
    {
        string path = Path.Join(Path.GetTempPath(), $"copilocal-litellm-{Guid.NewGuid():N}.json");
        File.WriteAllText(path,
            """
            {
              "liteLlmBaseUrl": " https://proxy.example.com/base/ ",
              "liteLlmApiKeyEnvVar": "   ",
              "liteLlmRuntimeMode": "PYTHON"
            }
            """);

        try
        {
            var loaded = LaunchConfig.Load(path);

            loaded.LiteLlmBaseUrl.Should().Be("https://proxy.example.com/base/v1");
            loaded.LiteLlmApiKeyEnvVar.Should().Be(LaunchConfig.DefaultLiteLlmApiKeyEnvVar);
            loaded.LiteLlmRuntimeMode.Should().Be("docker");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [DataRow("", "http://localhost:4000/v1")]
    [DataRow("   ", "http://localhost:4000/v1")]
    [DataRow("http://localhost:4000", "http://localhost:4000/v1")]
    [DataRow("http://localhost:4000/v1", "http://localhost:4000/v1")]
    [DataRow("  http://localhost:4000/v1/  ", "http://localhost:4000/v1")]
    [DataRow("https://proxy.example.com/base", "https://proxy.example.com/base/v1")]
    public void NormalizeBaseUrl_NormalizesExpectedShape(string input, string expected) =>
        LaunchConfig.NormalizeBaseUrl(input).Should().Be(expected);

    [TestMethod]
    [DataRow("", "")]
    [DataRow("   ", "")]
    [DataRow("sk-test", "sk-test")]
    [DataRow("Sk-Test", "Sk-Test")]
    [DataRow("test-key", "sk-test-key")]
    public void NormalizeLiteLlmApiKey_NormalizesExpectedShape(string input, string expected) =>
        LaunchConfig.NormalizeLiteLlmApiKey(input).Should().Be(expected);
}
