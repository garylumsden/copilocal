using Copilocal;
using Copilocal.Launch;
using Copilocal.Providers;
using Copilocal.Tests.Fakes;
using FluentAssertions;

namespace Copilocal.Tests;

[TestClass]
public sealed class ProviderInstallerTests
{
    private const string ReleasesUrl = "https://api.github.com/repos/microsoft/Foundry-Local/releases?per_page=40";
    private const string FoundryMsixUrl = "https://example.test/foundry-cli-win-x64-winml.msix";

    [TestMethod]
    public void Install_OllamaWingetSuccess_ReturnsTrueAndRecordsCommand()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["winget"] = @"C:\fake\winget.exe";
        var installer = new ProviderInstaller(proc, new FakeHttpGateway());

        // Act
        var result = installer.Install("Ollama");

        // Assert
        result.Should().BeTrue();
        proc.RunCalls.Should().ContainSingle()
            .Which.Should().Be(new FakeProcessRunner.RunCall(
                "winget",
                "install --id Ollama.Ollama -e --silent --accept-source-agreements --accept-package-agreements",
                600_000));
    }

    [TestMethod]
    public void Install_LmStudioWingetFailure_ReturnsFalseAndRecordsCommand()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["winget"] = @"C:\fake\winget.exe";
        proc.AddRun(
            "winget",
            "install --id ElementLabs.LMStudio -e --silent --accept-source-agreements --accept-package-agreements",
            code: 1,
            stderr: "install failed");
        var installer = new ProviderInstaller(proc, new FakeHttpGateway());

        // Act
        var result = installer.Install("LM Studio");

        // Assert
        result.Should().BeFalse();
        proc.RunCalls.Should().ContainSingle()
            .Which.Should().Be(new FakeProcessRunner.RunCall(
                "winget",
                "install --id ElementLabs.LMStudio -e --silent --accept-source-agreements --accept-package-agreements",
                600_000));
    }

    [TestMethod]
    public void Install_WingetMissing_ReturnsFalseWithoutRun()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        var installer = new ProviderInstaller(proc, new FakeHttpGateway());

        // Act
        var result = installer.Install("Ollama");

        // Assert
        result.Should().BeFalse();
        proc.RunCalls.Should().BeEmpty();
        proc.WhichCalls.Should().ContainSingle().Which.Should().Be("winget");
    }

    [TestMethod]
    public void InstallCopilot_WingetSuccess_RunsGitHubCopilotInstall()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.WhichResults["winget"] = @"C:\fake\winget.exe";
        var installer = new ProviderInstaller(proc, new FakeHttpGateway());

        // Act
        var result = installer.InstallCopilot();

        // Assert
        result.Should().BeTrue();
        proc.RunCalls.Should().ContainSingle()
            .Which.Should().Be(new FakeProcessRunner.RunCall(
                "winget",
                "install --id GitHub.Copilot -e --silent --accept-source-agreements --accept-package-agreements",
                600_000));
    }

    [TestMethod]
    public void InstallCopilot_WingetMissing_ReturnsFalseWithoutRun()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        var installer = new ProviderInstaller(proc, new FakeHttpGateway());

        // Act
        var result = installer.InstallCopilot();

        // Assert
        result.Should().BeFalse();
        proc.RunCalls.Should().BeEmpty();
    }

    [TestMethod]
    public void Install_FoundrySuccess_DownloadsMsixAndRunsAddAppxPackage()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        var http = new FakeHttpGateway();
        http.AddGet(ReleasesUrl, FoundryReleaseJson());
        var installer = new ProviderInstaller(proc, http);

        // Act
        var result = installer.Install("Foundry Local");

        // Assert
        result.Should().BeTrue();
        http.GetCalls.Should().ContainSingle()
            .Which.Should().Be(new FakeHttpGateway.GetCall(ReleasesUrl, 120_000, null));
        http.DownloadCalls.Should().ContainSingle()
            .Which.Should().Match<FakeHttpGateway.DownloadCall>(c =>
                c.Url == FoundryMsixUrl &&
                c.Path.EndsWith("foundry-cli-win-x64-winml.msix", StringComparison.Ordinal) &&
                c.TimeoutMs == 600_000);
        proc.RunCalls.Should().ContainSingle()
            .Which.Should().Match<FakeProcessRunner.RunCall>(c =>
                c.File == "powershell" &&
                c.Args.Contains("Add-AppxPackage -Path", StringComparison.Ordinal) &&
                c.Args.Contains("foundry-cli-win-x64-winml.msix", StringComparison.Ordinal) &&
                c.TimeoutMs == 300_000);
    }

    [TestMethod]
    public void Install_FoundryAddAppxPackageFailure_ReturnsFalse()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        proc.QueueRun(code: 1, stderr: "Add-AppxPackage failed");
        var http = new FakeHttpGateway();
        http.AddGet(ReleasesUrl, FoundryReleaseJson());
        var installer = new ProviderInstaller(proc, http);

        // Act
        var result = installer.Install("Foundry Local");

        // Assert
        result.Should().BeFalse();
        http.DownloadCalls.Should().ContainSingle()
            .Which.Url.Should().Be(FoundryMsixUrl);
        proc.RunCalls.Should().ContainSingle()
            .Which.File.Should().Be("powershell");
    }

    [TestMethod]
    public void Install_FoundryDownloadThrows_ReturnsFalseAndRecordsDownload()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        var http = new FakeHttpGateway();
        http.AddGet(ReleasesUrl, FoundryReleaseJson());
        http.AddDownloadException(FoundryMsixUrl, new HttpRequestException("download failed"));
        var installer = new ProviderInstaller(proc, http);

        // Act
        var result = installer.Install("Foundry Local");

        // Assert
        result.Should().BeFalse();
        http.DownloadCalls.Should().ContainSingle()
            .Which.Url.Should().Be(FoundryMsixUrl);
        proc.RunCalls.Should().BeEmpty();
    }

    [TestMethod]
    public void Install_FoundryReleaseJsonHasNoMsix_ReturnsFalse()
    {
        // Arrange
        var proc = new FakeProcessRunner();
        var http = new FakeHttpGateway();
        http.AddGet(ReleasesUrl, """[{"tag_name":"cli-preview-1.0.0","assets":[]}]""");
        var installer = new ProviderInstaller(proc, http);

        // Act
        var result = installer.Install("Foundry Local");

        // Assert
        result.Should().BeFalse();
        http.DownloadCalls.Should().BeEmpty();
        proc.RunCalls.Should().BeEmpty();
    }

    [TestMethod]
    public void InstallLiteLlm_Python_UvFails_PipxSucceeds()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var proc = new FakeProcessRunner();
            proc.WhichResults["uv"] = @"C:\fake\uv.exe";
            proc.WhichResults["pipx"] = @"C:\fake\pipx.exe";
            proc.AddRun("uv", "tool install litellm[proxy]", code: 1, stderr: "uv failed");
            proc.AddRun("pipx", "install litellm[proxy]", code: 0);
            var installer = new ProviderInstaller(proc, new FakeHttpGateway());

            var result = installer.InstallLiteLlm("python");

            result.Should().BeTrue();
            proc.RunCalls.Should().HaveCount(2);
            proc.RunCalls[0].Should().Be(new FakeProcessRunner.RunCall("uv", "tool install litellm[proxy]", 600_000));
            proc.RunCalls[1].Should().Be(new FakeProcessRunner.RunCall("pipx", "install litellm[proxy]", 600_000));
        });
    }

    [TestMethod]
    public void InstallLiteLlmWithDetail_DockerMissing_ReturnsReason()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var installer = new ProviderInstaller(new FakeProcessRunner(), new FakeHttpGateway());

            var result = installer.InstallLiteLlmWithDetail("docker");

            result.Ok.Should().BeFalse();
            result.Detail.ToLowerInvariant().Should().Contain("docker not found");
        });
    }

    [TestMethod]
    public void InstallLiteLlmWithDetail_PythonNoTooling_ReturnsReason()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var installer = new ProviderInstaller(new FakeProcessRunner(), new FakeHttpGateway());

            var result = installer.InstallLiteLlmWithDetail("python");

            result.Ok.Should().BeFalse();
            result.Detail.Should().Contain("python/python3 not found on PATH");
        });
    }

    [TestMethod]
    public void StartLiteLlm_Docker_WritesEnvAndRunsComposeUp()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var proc = new FakeProcessRunner();
            proc.WhichResults["docker"] = @"C:\fake\docker.exe";
            var http = new FakeHttpGateway();
            http.AddGet("http://localhost:4010/v1/models", """{"data":[]}""");
            var installer = new ProviderInstaller(proc, http);
            var cfg = new LaunchConfig
            {
                LiteLlmRuntimeMode = "docker",
                LiteLlmBaseUrl = "http://localhost:4010",
                LiteLlmApiKey = "sk-test-key",
            };

            var result = installer.StartLiteLlm(cfg);

            result.Should().BeTrue();
            string env = File.ReadAllText(LiteLlmDockerEnvPath());
            env.Should().Contain("LITELLM_MASTER_KEY=sk-test-key");
            env.Should().Contain("UI_USERNAME=admin");
            env.Should().Contain("UI_PASSWORD=sk-test-key");
            env.Should().Contain("LITELLM_PORT=4010");
            string compose = File.ReadAllText(Path.Join(LiteLlmDir(), "docker-compose.yml"));
            compose.Should().Contain("127.0.0.1:${LITELLM_PORT}:4000");
            compose.Should().Contain("host.docker.internal:host-gateway");
            proc.RunCalls.Should().ContainSingle()
                .Which.Should().Match<FakeProcessRunner.RunCall>(c =>
                    c.File == "docker"
                    && c.Args.Contains("compose -f", StringComparison.Ordinal)
                    && c.Args.Contains("up -d", StringComparison.Ordinal)
                    && c.Args.Contains("--force-recreate", StringComparison.Ordinal)
                    && c.Args.Contains("--remove-orphans", StringComparison.Ordinal)
                    && c.TimeoutMs == 180_000);
        });
    }

    [TestMethod]
    public void StartLiteLlm_Docker_PlainKey_NormalizesToSkPrefix()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var proc = new FakeProcessRunner();
            proc.WhichResults["docker"] = @"C:\fake\docker.exe";
            var http = new FakeHttpGateway();
            http.AddGet("http://localhost:4010/v1/models", """{"data":[]}""");
            var installer = new ProviderInstaller(proc, http);
            var cfg = new LaunchConfig
            {
                LiteLlmRuntimeMode = "docker",
                LiteLlmBaseUrl = "http://localhost:4010",
                LiteLlmApiKey = "plain-key",
            };

            var result = installer.StartLiteLlm(cfg);

            result.Should().BeTrue();
            string env = File.ReadAllText(LiteLlmDockerEnvPath());
            env.Should().Contain("LITELLM_MASTER_KEY=sk-plain-key");
            env.Should().Contain("UI_PASSWORD=sk-plain-key");
        });
    }

    [TestMethod]
    public void AddMissingLiteLlmLocalModels_Docker_MapsLoopbackHostsToHostDockerInternal()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var installer = new ProviderInstaller(new FakeProcessRunner(), new FakeHttpGateway());
            var local = new List<MenuItem>
            {
                new() { Kind = MenuItemKind.Model, Provider = "Ollama", Model = "qwen2.5:7b", BaseUrl = "http://localhost:11434/v1" },
                new() { Kind = MenuItemKind.Model, Provider = "LM Studio", Model = "qwen2.5-coder-1.5b-instruct", BaseUrl = "http://localhost:1234/v1" },
                new() { Kind = MenuItemKind.Model, Provider = "Foundry", Model = "qwen2.5-coder-7b-instruct-openvino-npu", BaseUrl = "http://127.0.0.1:5273/v1" },
            };

            var result = installer.AddMissingLiteLlmLocalModels("docker", local);

            result.Ok.Should().BeTrue();
            string config = File.ReadAllText(LiteLlmDockerConfigPath());
            config.Should().Contain("api_base: \"http://host.docker.internal:11434\"");
            config.Should().Contain("api_base: \"http://host.docker.internal:1234/v1\"");
            config.Should().Contain("api_base: \"http://host.docker.internal:5273/v1\"");
        });
    }

    [TestMethod]
    public void StartLiteLlm_Docker_RewritesExistingLoopbackApiBases()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            Directory.CreateDirectory(LiteLlmDir());
            File.WriteAllText(LiteLlmDockerConfigPath(), """
                model_list:
                  - model_name: "lmstudio/qwen2.5-coder-1.5b-instruct"
                    litellm_params:
                      model: "openai/qwen2.5-coder-1.5b-instruct"
                      api_base: "http://localhost:1234/v1"
                      api_key: "local"
                  - model_name: "foundry/qwen2.5-coder-7b-instruct-openvino-npu"
                    litellm_params:
                      model: "openai/qwen2.5-coder-7b-instruct-openvino-npu"
                      api_base: "http://127.0.0.1:5273/v1"
                      api_key: "local"
                general_settings:
                  master_key: os.environ/LITELLM_MASTER_KEY
                  database_url: os.environ/LITELLM_DATABASE_URL
                """);

            var proc = new FakeProcessRunner();
            proc.WhichResults["docker"] = @"C:\fake\docker.exe";
            var http = new FakeHttpGateway();
            http.AddGet("http://localhost:4010/v1/models", """{"data":[]}""");
            var installer = new ProviderInstaller(proc, http);
            var cfg = new LaunchConfig
            {
                LiteLlmRuntimeMode = "docker",
                LiteLlmBaseUrl = "http://localhost:4010",
                LiteLlmApiKey = "sk-test-key",
            };

            var result = installer.StartLiteLlm(cfg);

            result.Should().BeTrue();
            string config = File.ReadAllText(LiteLlmDockerConfigPath());
            config.Should().Contain("http://host.docker.internal:1234/v1");
            config.Should().Contain("http://host.docker.internal:5273/v1");
            config.Should().NotContain("http://localhost:1234/v1");
            config.Should().NotContain("http://127.0.0.1:5273/v1");
        });
    }

    [TestMethod]
    public void StartLiteLlm_Docker_ExistingCompose_GetsHostGatewayAlias()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            Directory.CreateDirectory(LiteLlmDir());
            File.WriteAllText(Path.Join(LiteLlmDir(), "docker-compose.yml"), """
                services:
                  litellm:
                    image: ghcr.io/berriai/litellm:main-latest
                    env_file:
                      - .env
                    ports:
                      - "127.0.0.1:${LITELLM_PORT}:4000"
                    command: ["--config=/app/config.yaml", "--host", "0.0.0.0", "--port", "4000"]
                """);
            File.WriteAllText(LiteLlmDockerConfigPath(), """
                model_list: []
                general_settings:
                  master_key: os.environ/LITELLM_MASTER_KEY
                  database_url: os.environ/LITELLM_DATABASE_URL
                """);

            var proc = new FakeProcessRunner();
            proc.WhichResults["docker"] = @"C:\fake\docker.exe";
            var http = new FakeHttpGateway();
            http.AddGet("http://localhost:4010/v1/models", """{"data":[]}""");
            var installer = new ProviderInstaller(proc, http);
            var cfg = new LaunchConfig
            {
                LiteLlmRuntimeMode = "docker",
                LiteLlmBaseUrl = "http://localhost:4010",
                LiteLlmApiKey = "sk-test-key",
            };

            installer.StartLiteLlm(cfg).Should().BeTrue();

            string compose = File.ReadAllText(Path.Join(LiteLlmDir(), "docker-compose.yml"));
            compose.Should().Contain("extra_hosts:");
            compose.Should().Contain("host.docker.internal:host-gateway");
        });
    }

    [TestMethod]
    public void StartLiteLlm_Docker_WaitsForApiReadinessBeforeSuccess()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var proc = new FakeProcessRunner();
            proc.WhichResults["docker"] = @"C:\fake\docker.exe";
            var http = new FakeHttpGateway();
            const string modelsUrl = "http://localhost:4010/v1/models";
            http.AddGetException(modelsUrl, new HttpRequestException("connection refused"));
            http.AddGet(modelsUrl, """{"data":[]}""");
            var installer = new ProviderInstaller(proc, http);
            var cfg = new LaunchConfig
            {
                LiteLlmRuntimeMode = "docker",
                LiteLlmBaseUrl = "http://localhost:4010",
                LiteLlmApiKey = "sk-test-key",
            };

            var result = installer.StartLiteLlm(cfg);

            result.Should().BeTrue();
            http.GetCalls.Should().HaveCount(2);
        });
    }

    [TestMethod]
    public void StartLiteLlm_Docker_UnauthorizedReadiness_FailsFast()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var proc = new FakeProcessRunner();
            proc.WhichResults["docker"] = @"C:\fake\docker.exe";
            var http = new FakeHttpGateway();
            http.AddGetException("http://localhost:4010/v1/models",
                new HttpRequestException("unauthorized", null, System.Net.HttpStatusCode.Unauthorized));
            var installer = new ProviderInstaller(proc, http);
            var cfg = new LaunchConfig
            {
                LiteLlmRuntimeMode = "docker",
                LiteLlmBaseUrl = "http://localhost:4010",
                LiteLlmApiKey = "sk-test-key",
            };

            var result = installer.StartLiteLlmWithDetail(cfg);

            result.Ok.Should().BeFalse();
            result.Detail.Should().Contain("authentication failed");
            http.GetCalls.Should().ContainSingle();
        });
    }

    [TestMethod]
    public void StartLiteLlmWithDetail_Docker_MissingApiKey_ReturnsReason()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            const string MissingVar = "__COPILOCAL_TEST_MISSING_KEY__";
            string? previousDefault = Environment.GetEnvironmentVariable(LaunchConfig.DefaultLiteLlmApiKeyEnvVar);
            string? previousNamed = Environment.GetEnvironmentVariable(MissingVar);
            Environment.SetEnvironmentVariable(LaunchConfig.DefaultLiteLlmApiKeyEnvVar, null);
            Environment.SetEnvironmentVariable(MissingVar, null);
            try
            {
                var proc = new FakeProcessRunner();
                proc.WhichResults["docker"] = @"C:\fake\docker.exe";
                var installer = new ProviderInstaller(proc, new FakeHttpGateway());
                var cfg = new LaunchConfig
                {
                    LiteLlmRuntimeMode = "docker",
                    LiteLlmBaseUrl = "http://localhost:4010",
                    LiteLlmApiKey = "",
                    LiteLlmApiKeyEnvVar = MissingVar,
                };

                var result = installer.StartLiteLlmWithDetail(cfg);

                result.Ok.Should().BeFalse();
                result.Detail.Should().Contain("API key not configured");
                proc.RunCalls.Should().BeEmpty();
            }
            finally
            {
                Environment.SetEnvironmentVariable(LaunchConfig.DefaultLiteLlmApiKeyEnvVar, previousDefault);
                Environment.SetEnvironmentVariable(MissingVar, previousNamed);
            }
        });
    }

    [TestMethod]
    public void StartLiteLlm_Docker_MissingApiKey_ReturnsFalseWithoutComposeRun()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            const string MissingVar = "__COPILOCAL_TEST_MISSING_KEY__";
            string? previousDefault = Environment.GetEnvironmentVariable(LaunchConfig.DefaultLiteLlmApiKeyEnvVar);
            string? previousNamed = Environment.GetEnvironmentVariable(MissingVar);
            Environment.SetEnvironmentVariable(LaunchConfig.DefaultLiteLlmApiKeyEnvVar, null);
            Environment.SetEnvironmentVariable(MissingVar, null);
            try
            {
                var proc = new FakeProcessRunner();
                proc.WhichResults["docker"] = @"C:\fake\docker.exe";
                var installer = new ProviderInstaller(proc, new FakeHttpGateway());
                var cfg = new LaunchConfig
                {
                    LiteLlmRuntimeMode = "docker",
                    LiteLlmBaseUrl = "http://localhost:4010",
                    LiteLlmApiKey = "",
                    LiteLlmApiKeyEnvVar = MissingVar,
                };

                var result = installer.StartLiteLlm(cfg);

                result.Should().BeFalse();
                proc.RunCalls.Should().BeEmpty();
            }
            finally
            {
                Environment.SetEnvironmentVariable(LaunchConfig.DefaultLiteLlmApiKeyEnvVar, previousDefault);
                Environment.SetEnvironmentVariable(MissingVar, previousNamed);
            }
        });
    }

    [TestMethod]
    public void StartLiteLlm_Python_ProcessDiesImmediately_ReturnsFalseAndDeletesPid()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var proc = new FakeProcessRunner();
            proc.WhichResults["litellm"] = @"C:\fake\litellm.exe";
            proc.QueueRun(code: 0, stdout: "12345\n");
            for (int i = 0; i < 5; i++) proc.QueueRun(code: 1);
            var installer = new ProviderInstaller(proc, new FakeHttpGateway());
            var cfg = new LaunchConfig { LiteLlmRuntimeMode = "python" };

            var result = installer.StartLiteLlm(cfg);

            result.Should().BeFalse();
            File.Exists(LiteLlmPidPath()).Should().BeFalse();
        });
    }

    [TestMethod]
    public void StartLiteLlm_Python_PassesMasterKeyIntoChildEnvironment()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var proc = new FakeProcessRunner();
            proc.WhichResults["uv"] = @"C:\fake\uv.exe";
            proc.WhichResults["litellm"] = @"C:\fake\litellm.exe";
            proc.AddRun("uv", "tool install litellm[proxy]", code: 0);
            proc.QueueRun(code: 0, stdout: "12345\n");
            proc.QueueRun(code: 0);
            var http = new FakeHttpGateway();
            http.AddGet("http://localhost:4000/v1/models", """{"data":[]}""");
            var installer = new ProviderInstaller(proc, http);
            var cfg = new LaunchConfig
            {
                LiteLlmRuntimeMode = "python",
                LiteLlmBaseUrl = "http://localhost:4000",
                LiteLlmApiKey = "plain-key",
            };

            var result = installer.StartLiteLlm(cfg);

            result.Should().BeTrue();
            proc.RunCalls.Should().NotBeEmpty();
            proc.RunCalls.Should().Contain(c => c.Args.Contains("LITELLM_MASTER_KEY", StringComparison.Ordinal));
            proc.RunCalls.Should().Contain(c => c.Args.Contains("sk-plain-key", StringComparison.Ordinal));
            http.GetCalls.Should().ContainSingle();
            http.GetCalls[0].BearerToken.Should().Be("sk-plain-key");
        });
    }

    [TestMethod]
    public void AddMissingLiteLlmLocalModels_Docker_AddsDiscoveredProviderEntries()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var installer = new ProviderInstaller(new FakeProcessRunner(), new FakeHttpGateway());
            var local = new List<MenuItem>
            {
                new() { Kind = MenuItemKind.Model, Provider = "Ollama", Model = "qwen2.5-coder:7b", BaseUrl = "http://localhost:11434/v1" },
                new() { Kind = MenuItemKind.Model, Provider = "LM Studio", Model = "qwen2.5-coder-7b", BaseUrl = "http://localhost:1234/v1" },
                new() { Kind = MenuItemKind.Model, Provider = "Foundry", Model = "qwen2.5-coder-14b", BaseUrl = null },
                new() { Kind = MenuItemKind.Model, Provider = "LiteLLM", Model = "gpt-4o-mini", BaseUrl = "http://localhost:4000/v1" },
            };

            var result = installer.AddMissingLiteLlmLocalModels("docker", local);

            result.Ok.Should().BeTrue();
            result.Discovered.Should().Be(3);
            result.Added.Should().Be(3);
            result.Existing.Should().Be(0);
            result.Skipped.Should().Be(1);
            string config = File.ReadAllText(LiteLlmDockerConfigPath());
            config.Should().Contain("model_name: \"ollama/qwen2.5-coder:7b\"");
            config.Should().Contain("model: \"ollama/qwen2.5-coder:7b\"");
            config.Should().Contain("api_base: \"http://host.docker.internal:11434\"");
            config.Should().Contain("model_name: \"lmstudio/qwen2.5-coder-7b\"");
            config.Should().Contain("model: \"openai/qwen2.5-coder-7b\"");
            config.Should().Contain("api_base: \"http://host.docker.internal:1234/v1\"");
            config.Should().Contain("model_name: \"foundry/qwen2.5-coder-14b\"");
            config.Should().Contain("api_base: \"http://host.docker.internal:5273/v1\"");
        });
    }

    [TestMethod]
    public void AddMissingLiteLlmLocalModels_Python_DoesNotDuplicateExistingEntries()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var installer = new ProviderInstaller(new FakeProcessRunner(), new FakeHttpGateway());
            var local = new List<MenuItem>
            {
                new() { Kind = MenuItemKind.Model, Provider = "Ollama", Model = "qwen2.5:7b", BaseUrl = "http://localhost:11434/v1" },
            };

            var first = installer.AddMissingLiteLlmLocalModels("python", local);
            var second = installer.AddMissingLiteLlmLocalModels("python", local);

            first.Ok.Should().BeTrue();
            first.Added.Should().Be(1);
            second.Ok.Should().BeTrue();
            second.Added.Should().Be(0);
            second.Existing.Should().Be(1);
            string config = File.ReadAllText(LiteLlmPythonConfigPath());
            config.Split("model_name: \"ollama/qwen2.5:7b\"", StringSplitOptions.None).Length.Should().Be(2);
        });
    }

    [TestMethod]
    public void ResetLiteLlm_DockerPresent_RemovesLocalScaffoldAndRunsComposeDown()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            Directory.CreateDirectory(LiteLlmDir());
            File.WriteAllText(Path.Join(LiteLlmDir(), "docker-compose.yml"), "services: {}");
            File.WriteAllText(Path.Join(LiteLlmDir(), ".env"), "LITELLM_MASTER_KEY=sk-test");
            File.WriteAllText(LiteLlmPidPath(), "12345");
            var proc = new FakeProcessRunner();
            proc.WhichResults["docker"] = @"C:\fake\docker.exe";
            var installer = new ProviderInstaller(proc, new FakeHttpGateway());

            var result = installer.ResetLiteLlm();

            result.Ok.Should().BeTrue();
            Directory.Exists(LiteLlmDir()).Should().BeFalse();
            proc.RunCalls.Should().Contain(c => c.File == "docker" && c.Args.Contains("down --remove-orphans", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void StopLiteLlm_Python_PidFilePresent_StopsAndDeletesPid()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            Directory.CreateDirectory(LiteLlmDir());
            File.WriteAllText(LiteLlmPidPath(), "12345");
            var proc = new FakeProcessRunner();
            var installer = new ProviderInstaller(proc, new FakeHttpGateway());
            var cfg = new LaunchConfig { LiteLlmRuntimeMode = "python" };

            var result = installer.StopLiteLlm(cfg);

            result.Should().BeTrue();
            File.Exists(LiteLlmPidPath()).Should().BeFalse();
            proc.RunCalls.Should().HaveCount(2);
            if (OperatingSystem.IsWindows())
            {
                proc.RunCalls[0].File.Should().Be("powershell");
                proc.RunCalls[1].File.Should().Be("powershell");
            }
            else
            {
                proc.RunCalls[0].File.Should().Be("sh");
                proc.RunCalls[1].File.Should().Be("sh");
            }
        });
    }

    [TestMethod]
    public void StopLiteLlm_Python_PidFileForOtherProcess_DoesNotStop()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            Directory.CreateDirectory(LiteLlmDir());
            File.WriteAllText(LiteLlmPidPath(), "12345|other-process");
            var proc = new FakeProcessRunner();
            proc.QueueRun(code: 1);
            var installer = new ProviderInstaller(proc, new FakeHttpGateway());
            var cfg = new LaunchConfig { LiteLlmRuntimeMode = "python" };

            var result = installer.StopLiteLlm(cfg);

            result.Should().BeFalse();
            File.Exists(LiteLlmPidPath()).Should().BeTrue();
            proc.RunCalls.Should().ContainSingle();
        });
    }

    [TestMethod]
    public void LiteLlmStatus_Docker_ServiceRunning_ReturnsRunning()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var proc = new FakeProcessRunner();
            proc.WhichResults["docker"] = @"C:\fake\docker.exe";
            proc.QueueRun(code: 0, stdout: "litellm\n");
            var installer = new ProviderInstaller(proc, new FakeHttpGateway());

            var status = installer.LiteLlmStatus(new LaunchConfig { LiteLlmRuntimeMode = "docker" });

            status.Running.Should().BeTrue();
            status.Detail.Should().Be("docker compose service is running");
            proc.RunCalls.Should().ContainSingle()
                .Which.Args.Should().Contain("ps --status running --services");
        });
    }

    [TestMethod]
    public void ParseFoundry_NonStringFields_SkipsBadEntriesAndKeepsGoodEntry()
    {
        string json = """
            {
              "models": [
                { "displayName": 123, "id": "bad-number", "alias": "bad" },
                { "displayName": { "value": "bad" }, "id": "bad-object", "alias": "bad" },
                { "displayName": "Good model", "id": { "value": "not-string" }, "alias": "good-alias", "supportsToolCalling": true }
              ]
            }
            """;

        var result = ProviderHub.ParseFoundry(json).ToList();

        result.Should().ContainSingle();
        result[0].Id.Should().Be("Good model");
        result[0].LoadId.Should().Be("good-alias");
        result[0].Tools.Should().BeTrue();
    }

    [TestMethod]
    public void ParseLmStudio_NonStringFields_SkipsBadEntriesAndKeepsGoodEntry()
    {
        string json = """
            [
              { "type": 7, "modelKey": 123 },
              { "type": { "kind": "llm" }, "modelKey": { "value": "bad" } },
              { "type": "llm", "modelKey": "good-model" }
            ]
            """;

        ProviderHub.ParseLmStudio(json).ToList().Should().Equal(["good-model"]);
    }

    [TestMethod]
    public void StartLiteLlm_Docker_KeyWithNewline_ReturnsFalseAndDoesNotInjectEnvLine()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            var proc = new FakeProcessRunner();
            proc.WhichResults["docker"] = @"C:\fake\docker.exe";
            var installer = new ProviderInstaller(proc, new FakeHttpGateway());
            var cfg = new LaunchConfig
            {
                LiteLlmRuntimeMode = "docker",
                LiteLlmBaseUrl = "http://localhost:4010",
                LiteLlmApiKey = "plain-key\nEVIL=1",
            };

            var result = installer.StartLiteLlmWithDetail(cfg);

            result.Ok.Should().BeFalse();
            result.Detail.Should().Contain("failed to write");
            File.ReadAllText(LiteLlmDockerEnvPath()).Should().NotContain("EVIL=1");
            proc.RunCalls.Should().BeEmpty();
        });
    }

    [TestMethod]
    public void StartLiteLlm_Docker_ComposeWithoutAliasAnchor_ReturnsFalseWithoutComposeUp()
    {
        RunWithIsolatedLiteLlmDir(() =>
        {
            Directory.CreateDirectory(LiteLlmDir());
            string composePath = Path.Join(LiteLlmDir(), "docker-compose.yml");
            File.WriteAllText(composePath, """
                services:
                  litellm:
                    image: ghcr.io/berriai/litellm:main-latest
                    command: ["--config=/app/config.yaml"]
                """);
            File.WriteAllText(LiteLlmDockerConfigPath(), """
                model_list: []
                general_settings:
                  master_key: os.environ/LITELLM_MASTER_KEY
                  database_url: os.environ/LITELLM_DATABASE_URL
                """);
            var proc = new FakeProcessRunner();
            proc.WhichResults["docker"] = @"C:\fake\docker.exe";
            var installer = new ProviderInstaller(proc, new FakeHttpGateway());
            var cfg = new LaunchConfig
            {
                LiteLlmRuntimeMode = "docker",
                LiteLlmBaseUrl = "http://localhost:4010",
                LiteLlmApiKey = "sk-test-key",
            };

            var result = installer.StartLiteLlmWithDetail(cfg);

            result.Ok.Should().BeFalse();
            result.Detail.Should().Contain("failed to update");
            File.ReadAllText(composePath).Should().NotContain("host.docker.internal:host-gateway");
            proc.RunCalls.Should().BeEmpty();
        });
    }

    private static string LiteLlmDir() =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilocal", "litellm");

    private static string LiteLlmDockerEnvPath() => Path.Join(LiteLlmDir(), ".env");
    private static string LiteLlmDockerConfigPath() => Path.Join(LiteLlmDir(), "config.yaml");
    private static string LiteLlmPythonConfigPath() => Path.Join(LiteLlmDir(), "litellm-python.yaml");

    private static string LiteLlmPidPath() => Path.Join(LiteLlmDir(), "litellm.pid");

    private static void RunWithIsolatedLiteLlmDir(Action action)
    {
        string liteLlmDir = LiteLlmDir();
        string? backupPath = null;
        bool hadExisting = Directory.Exists(liteLlmDir);
        if (hadExisting)
        {
            backupPath = Path.Join(Path.GetTempPath(), $"copilocal-litellm-backup-{Guid.NewGuid():N}");
            Directory.Move(liteLlmDir, backupPath);
        }

        try
        {
            action();
        }
        finally
        {
            try
            {
                if (Directory.Exists(liteLlmDir))
                    Directory.Delete(liteLlmDir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (hadExisting && backupPath is not null && Directory.Exists(backupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(liteLlmDir)!);
                Directory.Move(backupPath, liteLlmDir);
            }
        }
    }

    private static string FoundryReleaseJson() => $$"""
        [
          {
            "tag_name": "cli-preview-1.2.3",
            "assets": [
              {
                "name": "foundry-cli-win-x64-winml.msix",
                "browser_download_url": "{{FoundryMsixUrl}}"
              }
            ]
          },
          {
            "tag_name": "cli-preview-1.0.0",
            "assets": [
              {
                "name": "older-win-x64-winml.msix",
                "browser_download_url": "https://example.test/older.msix"
              }
            ]
          }
        ]
        """;
}
