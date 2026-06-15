using System.Text.Json;

using Copilocal.Infrastructure;
using Copilocal.Launch;

namespace Copilocal.Providers;

internal sealed class ProviderInstaller(IProcessRunner proc, IHttpGateway http)
{
    internal const string LiteLlmModeDocker = "docker";
    internal const string LiteLlmModePython = "python";

    static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string LiteLlmDir => Path.Join(UserProfile, ".copilocal", "litellm");
    static string LiteLlmComposePath => Path.Join(LiteLlmDir, "docker-compose.yml");
    static string LiteLlmDockerEnvPath => Path.Join(LiteLlmDir, ".env");
    static string LiteLlmDockerConfigPath => Path.Join(LiteLlmDir, "config.yaml");
    static string LiteLlmPythonConfigPath => Path.Join(LiteLlmDir, "litellm-python.yaml");
    static string LiteLlmPidPath => Path.Join(LiteLlmDir, "litellm.pid");

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
                $"-NoProfile -Command \"Add-AppxPackage -Path '{tmp}'\"", 300_000);
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

    internal bool InstallLiteLlm(string mode)
    {
        mode = NormalizeLiteLlmMode(mode);
        if (!EnsureLiteLlmScaffold()) return false;
        return mode == LiteLlmModeDocker ? InstallLiteLlmDocker() : InstallLiteLlmPython();
    }

    internal bool StartLiteLlm(LaunchConfig cfg)
    {
        string mode = NormalizeLiteLlmMode(cfg.LiteLlmRuntimeMode);
        if (!InstallLiteLlm(mode)) return false;
        return mode == LiteLlmModeDocker ? StartLiteLlmDocker(cfg) : StartLiteLlmPython(cfg);
    }

    internal bool StopLiteLlm(LaunchConfig cfg)
    {
        string mode = NormalizeLiteLlmMode(cfg.LiteLlmRuntimeMode);
        return mode == LiteLlmModeDocker ? StopLiteLlmDocker() : StopLiteLlmPython();
    }

    internal (bool Running, string Detail) LiteLlmStatus(LaunchConfig cfg)
    {
        string mode = NormalizeLiteLlmMode(cfg.LiteLlmRuntimeMode);
        return mode == LiteLlmModeDocker ? LiteLlmStatusDocker() : LiteLlmStatusPython();
    }

    static string NormalizeLiteLlmMode(string mode) =>
        string.Equals(mode, LiteLlmModePython, StringComparison.OrdinalIgnoreCase) ? LiteLlmModePython : LiteLlmModeDocker;

    bool InstallLiteLlmDocker()
    {
        if (proc.Which("docker") is null) return false;
        return true;
    }

    bool InstallLiteLlmPython()
    {
        if (proc.Which("uv") is not null)
        {
            var (code, _, _) = proc.Run("uv", "tool install litellm[proxy]", 600_000);
            if (code == 0) return true;
        }
        if (proc.Which("pipx") is not null)
        {
            var (code, _, _) = proc.Run("pipx", "install litellm[proxy]", 600_000);
            if (code == 0) return true;
        }
        string? python = proc.Which("python") ?? proc.Which("python3");
        if (python is null) return false;
        var (pipCode, _, _) = proc.Run(python, "-m pip install --user litellm[proxy]", 600_000);
        return pipCode == 0;
    }

    bool StartLiteLlmDocker(LaunchConfig cfg)
    {
        if (proc.Which("docker") is null) return false;
        int port = LiteLlmPort(cfg.LiteLlmBaseUrl);
        if (!WriteDockerEnv(cfg, port)) return false;
        var (code, _, _) = proc.Run("docker",
            $"compose -f \"{LiteLlmComposePath}\" --env-file \"{LiteLlmDockerEnvPath}\" up -d", 180_000);
        return code == 0;
    }

    bool StopLiteLlmDocker()
    {
        if (proc.Which("docker") is null) return false;
        var (code, _, _) = proc.Run("docker",
            $"compose -f \"{LiteLlmComposePath}\" --env-file \"{LiteLlmDockerEnvPath}\" down", 120_000);
        return code == 0;
    }

    (bool Running, string Detail) LiteLlmStatusDocker()
    {
        if (proc.Which("docker") is null) return (false, "docker not found");
        var (code, outp, err) = proc.Run("docker",
            $"compose -f \"{LiteLlmComposePath}\" --env-file \"{LiteLlmDockerEnvPath}\" ps --status running --services", 30_000);
        if (code == 0 && outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(s => s.Contains("litellm", StringComparison.OrdinalIgnoreCase)))
            return (true, "docker compose service is running");
        string detail = string.IsNullOrWhiteSpace(err) ? "service is not running" : err.Trim();
        return (false, detail);
    }

    bool StartLiteLlmPython(LaunchConfig cfg)
    {
        int port = LiteLlmPort(cfg.LiteLlmBaseUrl);
        if (OperatingSystem.IsWindows())
        {
            string script =
                "$p = Start-Process -FilePath 'litellm' " +
                $"-ArgumentList @('--config','{PsLiteral(LiteLlmPythonConfigPath)}','--host','0.0.0.0','--port','{port}') " +
                "-PassThru; $p.Id";
            var (code, outp, _) = proc.Run("powershell", $"-NoProfile -Command \"{script}\"", 30_000);
            if (code != 0) return false;
            return PersistPid(outp);
        }

        string sh = $"litellm --config '{ShLiteral(LiteLlmPythonConfigPath)}' --host 0.0.0.0 --port {port} >/dev/null 2>&1 & echo $!";
        var (shCode, shOut, _) = proc.Run("sh", $"-lc \"{sh}\"", 30_000);
        if (shCode != 0) return false;
        return PersistPid(shOut);
    }

    bool StopLiteLlmPython()
    {
        if (!TryReadPid(out int pid)) return false;

        if (OperatingSystem.IsWindows())
        {
            string script = $"$p = Get-Process -Id {pid} -ErrorAction SilentlyContinue; if ($p) {{ Stop-Process -Id {pid} -Force; exit 0 }} else {{ exit 1 }}";
            var (code, _, _) = proc.Run("powershell", $"-NoProfile -Command \"{script}\"", 20_000);
            if (code == 0) DeletePidFile();
            return code == 0;
        }

        var (killCode, _, _) = proc.Run("sh", $"-lc \"kill {pid}\"", 20_000);
        if (killCode == 0) DeletePidFile();
        return killCode == 0;
    }

    (bool Running, string Detail) LiteLlmStatusPython()
    {
        if (!TryReadPid(out int pid)) return (false, "pid file missing");
        if (OperatingSystem.IsWindows())
        {
            string script = $"$p = Get-Process -Id {pid} -ErrorAction SilentlyContinue; if ($p) {{ exit 0 }} else {{ exit 1 }}";
            var (code, _, _) = proc.Run("powershell", $"-NoProfile -Command \"{script}\"", 15_000);
            return code == 0 ? (true, $"litellm pid {pid} running") : (false, "process not running");
        }

        var (killCode, _, _) = proc.Run("sh", $"-lc \"kill -0 {pid}\"", 15_000);
        return killCode == 0 ? (true, $"litellm pid {pid} running") : (false, "process not running");
    }

    static int LiteLlmPort(string baseUrl)
    {
        string normalized = LaunchConfig.NormalizeBaseUrl(baseUrl);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Port > 0) return uri.Port;
        return 4000;
    }

    bool EnsureLiteLlmScaffold()
    {
        try
        {
            Directory.CreateDirectory(LiteLlmDir);
            WriteIfMissing(LiteLlmComposePath, DockerComposeTemplate());
            WriteIfMissing(LiteLlmDockerConfigPath, DockerConfigTemplate());
            WriteIfMissing(LiteLlmPythonConfigPath, PythonConfigTemplate());
            WriteIfMissing(LiteLlmDockerEnvPath, DefaultDockerEnvTemplate());
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    bool WriteDockerEnv(LaunchConfig cfg, int port)
    {
        string key = ResolveLiteLlmApiKey(cfg);
        if (key.Length == 0) key = "sk-local-dev-key";
        try
        {
            string env =
                $"LITELLM_MASTER_KEY={key}\n" +
                "LITELLM_SALT_KEY=sk-local-dev-salt\n" +
                "POSTGRES_PASSWORD=dbpassword9090\n" +
                $"LITELLM_PORT={port}\n" +
                "LITELLM_DATABASE_URL=postgresql://llmproxy:dbpassword9090@db:5432/litellm\n";
            File.WriteAllText(LiteLlmDockerEnvPath, env);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    static string ResolveLiteLlmApiKey(LaunchConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.LiteLlmApiKey)) return cfg.LiteLlmApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(cfg.LiteLlmApiKeyEnvVar))
        {
            string fromNamed = Environment.GetEnvironmentVariable(cfg.LiteLlmApiKeyEnvVar.Trim()) ?? "";
            if (!string.IsNullOrWhiteSpace(fromNamed)) return fromNamed.Trim();
        }
        string fallback = Environment.GetEnvironmentVariable(LaunchConfig.DefaultLiteLlmApiKeyEnvVar) ?? "";
        return fallback.Trim();
    }

    static void WriteIfMissing(string path, string content)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, content);
    }

    static string DockerComposeTemplate() =>
        """
        services:
          db:
            image: postgres:16-alpine
            restart: unless-stopped
            environment:
              POSTGRES_USER: llmproxy
              POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
              POSTGRES_DB: litellm
            volumes:
              - litellm_pgdata:/var/lib/postgresql/data

          litellm:
            image: ghcr.io/berriai/litellm:main-latest
            restart: unless-stopped
            depends_on:
              - db
            env_file:
              - .env
            ports:
              - "${LITELLM_PORT}:4000"
            volumes:
              - ./config.yaml:/app/config.yaml
            command: ["--config=/app/config.yaml", "--host", "0.0.0.0", "--port", "4000"]

        volumes:
          litellm_pgdata:
        """;

    static string DockerConfigTemplate() =>
        """
        model_list: []
        general_settings:
          master_key: os.environ/LITELLM_MASTER_KEY
          database_url: os.environ/LITELLM_DATABASE_URL
        """;

    static string PythonConfigTemplate()
    {
        string sqlite = Path.Join(LiteLlmDir, "litellm.db").Replace("\\", "/");
        return
            "model_list: []\n" +
            "general_settings:\n" +
            "  master_key: os.environ/LITELLM_MASTER_KEY\n" +
            $"  database_url: sqlite:///{sqlite}\n";
    }

    static string DefaultDockerEnvTemplate() =>
        """
        LITELLM_MASTER_KEY=sk-local-dev-key
        LITELLM_SALT_KEY=sk-local-dev-salt
        POSTGRES_PASSWORD=dbpassword9090
        LITELLM_PORT=4000
        LITELLM_DATABASE_URL=postgresql://llmproxy:dbpassword9090@db:5432/litellm
        """;

    bool PersistPid(string output)
    {
        string token = output.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!int.TryParse(token, out int pid) || pid <= 0) return false;
        try
        {
            File.WriteAllText(LiteLlmPidPath, pid.ToString());
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool TryReadPid(out int pid)
    {
        pid = 0;
        try
        {
            if (!File.Exists(LiteLlmPidPath)) return false;
            string text = File.ReadAllText(LiteLlmPidPath).Trim();
            return int.TryParse(text, out pid) && pid > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    static void DeletePidFile()
    {
        try { File.Delete(LiteLlmPidPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static string PsLiteral(string s) => s.Replace("'", "''");
    static string ShLiteral(string s) => s.Replace("'", "'\"'\"'");
}
