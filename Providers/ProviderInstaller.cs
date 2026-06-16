using System.Text.Json;

using Copilocal.Infrastructure;
using Copilocal.Launch;

namespace Copilocal.Providers;

internal sealed partial class ProviderInstaller(IProcessRunner proc, IHttpGateway http)
{
    internal const string LiteLlmModeDocker = "docker";
    internal const string LiteLlmModePython = "python";
    const string LiteLlmProcessMarker = "litellm";
    const string LocalBindHost = "127.0.0.1";
    const string DockerHostAlias = "host.docker.internal";
    const int LiteLlmReadyTimeoutMs = 90_000;
    const int LiteLlmReadyPollMs = 1_000;
    const int LiteLlmReadyProbeTimeoutMs = 3_000;
    const string OllamaDefaultBaseUrl = "http://localhost:11434/v1";
    const string LmStudioDefaultBaseUrl = "http://localhost:1234/v1";
    const string FoundryDefaultBaseUrl = "http://127.0.0.1:5273/v1";

    static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string LiteLlmDir => Path.Join(UserProfile, ".copilocal", "litellm");
    static string LiteLlmComposePath => Path.Join(LiteLlmDir, "docker-compose.yml");
    static string LiteLlmDockerEnvPath => Path.Join(LiteLlmDir, ".env");
    static string LiteLlmDockerConfigPath => Path.Join(LiteLlmDir, "config.yaml");
    static string LiteLlmPythonConfigPath => Path.Join(LiteLlmDir, "litellm-python.yaml");
    static string LiteLlmPidPath => Path.Join(LiteLlmDir, "litellm.pid");

    sealed record LiteLlmPidInfo(int Pid, string Marker);
    sealed record LiteLlmModelEntry(string ModelName, string Model, string ApiBase, string? ApiKey);

    internal bool Install(string providerName)
    {
        return providerName switch
        {
            "Ollama" => Winget("Ollama.Ollama"),
            "LM Studio" => Winget("ElementLabs.LMStudio"),
            "Foundry Local" => InstallFoundry(),
            "LiteLLM" => InstallLiteLlm(LiteLlmModeDocker),
            _ => false,
        };
    }

    /// <summary>Install the GitHub Copilot CLI (`copilot`) via winget. Windows-only; returns
    /// false when winget is unavailable (e.g. on macOS, where the user installs it themselves).</summary>
    internal bool InstallCopilot() => Winget("GitHub.Copilot");

    bool Winget(string id)
    {
        if (proc.Which("winget") is null) return false;
        var (code, _, _) = proc.Run("winget",
            $"install --id {id} -e --silent --accept-source-agreements --accept-package-agreements", 600_000);
        return code == 0;
    }

    bool InstallFoundry()
    {
        string? url = ResolveFoundryMsixUrl();
        if (url is null) return false;
        string tmp = Path.Join(Path.GetTempPath(), Path.GetFileName(url));
        try
        {
            http.DownloadToFile(url, tmp, 600_000);
            var (code, _, _) = proc.Run("powershell",
                $"-NoProfile -Command \"Add-AppxPackage -Path {PsSingleQuoted(tmp)}\"", 300_000);
            return code == 0;
        }
        catch (HttpRequestException)
        {
            // best-effort: install flow reports failure and leaves docs fallback to user.
            return false;
        }
        catch (IOException)
        {
            // best-effort: install flow reports failure and leaves docs fallback to user.
            return false;
        }
        catch (OperationCanceledException)
        {
            // best-effort: install flow reports failure and leaves docs fallback to user.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort: install flow reports failure and leaves docs fallback to user.
            return false;
        }
        finally
        {
            try { File.Delete(tmp); }
            catch (IOException)
            {
                // best-effort: downloaded installer cache can remain if locked.
            }
            catch (UnauthorizedAccessException)
            {
                // best-effort: downloaded installer cache can remain if locked.
            }
        }
    }

    /// <summary>Resolve the latest Foundry Local CLI MSIX (win-{arch}-winml) from GitHub releases.</summary>
    string? ResolveFoundryMsixUrl()
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        try
        {
            string body = http.GetString(
                "https://api.github.com/repos/microsoft/Foundry-Local/releases?per_page=40", 120_000);
            using var doc = JsonDocument.Parse(body);

            JsonElement best = default; Version bestVer = new(0, 0); bool found = false;
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (!rel.TryGetProperty("tag_name", out var tn)) continue;
                string tag = tn.GetString() ?? "";
                if (!tag.StartsWith("cli-preview-")) continue;
                if (!rel.TryGetProperty("assets", out var assets) || assets.GetArrayLength() == 0) continue;
                if (!Version.TryParse(tag.Replace("cli-preview-", ""), out var v)) continue;
                if (v > bestVer) { bestVer = v; best = rel; found = true; }
            }
            if (!found) return null;

            string? fallback = null;
            foreach (var a in best.GetProperty("assets").EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                string dl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (name.Contains($"win-{arch}-winml") && name.EndsWith(".msix")) return dl;
                if (name.Contains($"win-{arch}") && name.EndsWith(".msix")) fallback = dl;
            }
            return fallback;
        }
        catch (HttpRequestException)
        {
            // best-effort: missing release metadata means install flow reports failure.
            return null;
        }
        catch (JsonException)
        {
            // best-effort: missing release metadata means install flow reports failure.
            return null;
        }
        catch (OperationCanceledException)
        {
            // best-effort: missing release metadata means install flow reports failure.
            return null;
        }
        catch (InvalidOperationException)
        {
            // best-effort: an unexpected JSON shape from the releases API means no URL.
            return null;
        }
    }

    internal static string PsSingleQuoted(string value) =>
        "'" + (value ?? "")
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static string PsArrayLiteral(IEnumerable<string> args) =>
        "@(" + string.Join(",", args.Select(PsSingleQuoted)) + ")";

    internal static string ShArg(string value) => "'" + ShLiteral(value) + "'";

    internal static string ShLiteral(string value) =>
        (value ?? "")
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal);

    static string SingleLine(string preferred, string fallback)
    {
        string source = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
        string line = (source ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown error";
        const int max = 180;
        return line.Length <= max ? line : $"{line[..max]}…";
    }
}
