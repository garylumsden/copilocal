using Copilocal;
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
            .Which.Should().Be(new FakeHttpGateway.GetCall(ReleasesUrl, 120_000));
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
