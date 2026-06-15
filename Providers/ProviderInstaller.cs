using System.Text.Json;

using Copilocal.Infrastructure;
using Copilocal.Launch;

namespace Copilocal.Providers;

internal sealed class ProviderInstaller(IProcessRunner proc, IHttpGateway http)
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
        return InstallLiteLlmWithDetail(mode).Ok;
    }

    internal (bool Ok, string Detail) InstallLiteLlmWithDetail(string mode)
    {
        mode = NormalizeLiteLlmMode(mode);
        if (!EnsureLiteLlmScaffold())
            return (false, $"could not create LiteLLM scaffold in {LiteLlmDir}");
        return mode == LiteLlmModeDocker
            ? InstallLiteLlmDockerWithDetail()
            : InstallLiteLlmPythonWithDetail();
    }

    internal bool StartLiteLlm(LaunchConfig cfg)
    {
        return StartLiteLlmWithDetail(cfg).Ok;
    }

    internal (bool Ok, string Detail) StartLiteLlmWithDetail(LaunchConfig cfg)
    {
        string mode = NormalizeLiteLlmMode(cfg.LiteLlmRuntimeMode);
        var install = InstallLiteLlmWithDetail(mode);
        if (!install.Ok) return (false, $"install failed: {install.Detail}");
        if (ResolveLiteLlmApiKey(cfg).Length == 0)
            return (false, "LiteLLM API key not configured (set key env var or API key in LiteLLM settings)");

        return mode == LiteLlmModeDocker
            ? StartLiteLlmDockerWithDetail(cfg)
            : StartLiteLlmPythonWithDetail(cfg);
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

    internal (bool Ok, string Detail) ResetLiteLlm()
    {
        bool ok = true;
        var notes = new List<string>();

        if (proc.Which("docker") is not null && File.Exists(LiteLlmComposePath))
        {
            string args = File.Exists(LiteLlmDockerEnvPath)
                ? $"compose -f \"{LiteLlmComposePath}\" --env-file \"{LiteLlmDockerEnvPath}\" down --remove-orphans"
                : $"compose -f \"{LiteLlmComposePath}\" down --remove-orphans";
            var (code, outp, err) = proc.Run("docker", args, 120_000);
            if (code != 0)
            {
                ok = false;
                notes.Add($"docker compose down failed: {SingleLine(err, outp)}");
            }
        }

        _ = StopLiteLlmPython(); // best-effort: no pid or already-stopped is fine during reset.

        try
        {
            if (Directory.Exists(LiteLlmDir))
                Directory.Delete(LiteLlmDir, recursive: true);
        }
        catch (IOException ex)
        {
            ok = false;
            notes.Add($"delete {LiteLlmDir}: {SingleLine(ex.Message, "")}");
        }
        catch (UnauthorizedAccessException ex)
        {
            ok = false;
            notes.Add($"delete {LiteLlmDir}: {SingleLine(ex.Message, "")}");
        }

        return ok
            ? (true, "LiteLLM local runtime and scaffold removed")
            : (false, string.Join("; ", notes));
    }

    internal (bool Ok, int Discovered, int Added, int Existing, int Skipped) AddMissingLiteLlmLocalModels(
        string mode,
        IEnumerable<MenuItem> localModels)
    {
        if (!EnsureLiteLlmScaffold()) return (false, 0, 0, 0, 0);
        string normalizedMode = NormalizeLiteLlmMode(mode);

        var mapped = new Dictionary<string, LiteLlmModelEntry>(StringComparer.Ordinal);
        int skipped = 0;
        foreach (var item in localModels)
        {
            if (item.Kind != MenuItemKind.Model) continue;
            var entry = ToLiteLlmModelEntry(item, normalizedMode);
            if (entry is null) { skipped++; continue; }
            mapped[entry.ModelName] = entry;
        }

        if (mapped.Count == 0) return (true, 0, 0, 0, skipped);

        string path = normalizedMode == LiteLlmModePython
            ? LiteLlmPythonConfigPath
            : LiteLlmDockerConfigPath;
        if (normalizedMode == LiteLlmModeDocker && !RewriteLoopbackApiBasesForDocker(path))
            return (false, mapped.Count, 0, 0, skipped);
        if (!MergeLiteLlmModelConfig(path, mapped.Values.ToList(), out int added, out int existing))
            return (false, mapped.Count, 0, 0, skipped);

        return (true, mapped.Count, added, existing, skipped);
    }

    static string NormalizeLiteLlmMode(string mode) =>
        string.Equals(mode, LiteLlmModePython, StringComparison.OrdinalIgnoreCase) ? LiteLlmModePython : LiteLlmModeDocker;

    (bool Ok, string Detail) InstallLiteLlmDockerWithDetail()
    {
        return proc.Which("docker") is null
            ? (false, "docker not found on PATH")
            : (true, "docker available");
    }

    (bool Ok, string Detail) InstallLiteLlmPythonWithDetail()
    {
        string? uvErr = null;
        if (proc.Which("uv") is not null)
        {
            var (code, outp, err) = proc.Run("uv", "tool install litellm[proxy]", 600_000);
            if (code == 0) return (true, "installed via uv");
            uvErr = $"uv install failed: {SingleLine(err, outp)}";
        }
        string? pipxErr = null;
        if (proc.Which("pipx") is not null)
        {
            var (code, outp, err) = proc.Run("pipx", "install litellm[proxy]", 600_000);
            if (code == 0) return (true, "installed via pipx");
            pipxErr = $"pipx install failed: {SingleLine(err, outp)}";
        }
        string? python = proc.Which("python") ?? proc.Which("python3");
        if (python is null)
        {
            var reasons = new List<string>();
            if (!string.IsNullOrWhiteSpace(uvErr)) reasons.Add(uvErr);
            if (!string.IsNullOrWhiteSpace(pipxErr)) reasons.Add(pipxErr);
            reasons.Add("python/python3 not found on PATH");
            return (false, string.Join("; ", reasons));
        }
        var (pipCode, pipOut, pipErr) = proc.Run(python, "-m pip install --user litellm[proxy]", 600_000);
        if (pipCode == 0) return (true, $"installed via {Path.GetFileName(python)} -m pip --user");

        var allReasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(uvErr)) allReasons.Add(uvErr);
        if (!string.IsNullOrWhiteSpace(pipxErr)) allReasons.Add(pipxErr);
        allReasons.Add($"python pip install failed: {SingleLine(pipErr, pipOut)}");
        return (false, string.Join("; ", allReasons));
    }

    (bool Ok, string Detail) StartLiteLlmDockerWithDetail(LaunchConfig cfg)
    {
        if (proc.Which("docker") is null) return (false, "docker not found on PATH");
        int port = LiteLlmPort(cfg.LiteLlmBaseUrl);
        if (!WriteDockerEnv(cfg, port))
            return (false, $"failed to write {LiteLlmDockerEnvPath}");
        if (!RewriteLoopbackApiBasesForDocker(LiteLlmDockerConfigPath))
            return (false, $"failed to update {LiteLlmDockerConfigPath}");
        if (!EnsureDockerHostGatewayAliasInCompose())
            return (false, $"failed to update {LiteLlmComposePath}");
        var (code, outp, err) = proc.Run("docker",
            $"compose -f \"{LiteLlmComposePath}\" --env-file \"{LiteLlmDockerEnvPath}\" up -d --force-recreate --remove-orphans", 180_000);
        if (code != 0)
            return (false, $"docker compose up failed: {SingleLine(err, outp)}");
        var ready = WaitForLiteLlmApiReady(cfg);
        return ready.Ok
            ? (true, "docker compose started and LiteLLM API is ready")
            : (false, $"docker compose started, but {ready.Detail}");
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

    (bool Ok, string Detail) StartLiteLlmPythonWithDetail(LaunchConfig cfg)
    {
        int port = LiteLlmPort(cfg.LiteLlmBaseUrl);
        string key = ResolveLiteLlmApiKey(cfg);
        if (key.Length == 0)
            return (false, "LiteLLM API key not configured");
        if (ResolveLiteLlmStartCommand() is not { } start)
            return (false, "litellm/python executable not found on PATH");
        var args = new List<string>(start.PrefixArgs)
        {
            "--config", LiteLlmPythonConfigPath,
            "--host", LocalBindHost,
            "--port", port.ToString(),
        };

        if (OperatingSystem.IsWindows())
        {
            string script = "$env:LITELLM_MASTER_KEY = '" + PsLiteral(key) + "'; " +
                            "$p = Start-Process -FilePath '" + PsLiteral(start.File) + "' " +
                            "-ArgumentList " + PsArrayLiteral(args) + " -PassThru; $p.Id";
            var (code, outp, err) = proc.Run("powershell", $"-NoProfile -Command \"{script}\"", 30_000);
            if (code != 0) return (false, $"failed to launch LiteLLM process: {SingleLine(err, outp)}");
            if (!PersistPid(outp, LiteLlmProcessMarker, out var info))
                return (false, $"failed to persist pid in {LiteLlmPidPath}");
            if (!WaitForLiteLlmProcess(info))
                return (false, "litellm process exited before becoming ready");
            var ready = WaitForLiteLlmApiReady(cfg);
            return ready.Ok
                ? (true, "litellm process started and API is ready")
                : (false, ready.Detail);
        }

        string sh = $"LITELLM_MASTER_KEY={ShArg(key)} {ShArg(start.File)} {string.Join(" ", args.Select(ShArg))} >/dev/null 2>&1 & echo $!";
        var (shCode, shOut, _) = proc.Run("sh", $"-lc \"{sh}\"", 30_000);
        if (shCode != 0) return (false, "failed to launch LiteLLM process");
        if (!PersistPid(shOut, LiteLlmProcessMarker, out var pidInfo))
            return (false, $"failed to persist pid in {LiteLlmPidPath}");
        if (!WaitForLiteLlmProcess(pidInfo))
            return (false, "litellm process exited before becoming ready");
        var pyReady = WaitForLiteLlmApiReady(cfg);
        return pyReady.Ok
            ? (true, "litellm process started and API is ready")
            : (false, pyReady.Detail);
    }

    bool StopLiteLlmPython()
    {
        if (!TryReadPid(out var pidInfo)) return false;
        if (!IsLiteLlmProcess(pidInfo)) return false;

        if (OperatingSystem.IsWindows())
        {
            int pid = pidInfo.Pid;
            string marker = PsLiteral(pidInfo.Marker.ToLowerInvariant());
            string script =
                $"$p = Get-CimInstance Win32_Process -Filter \"ProcessId = {pid}\" -ErrorAction SilentlyContinue; " +
                "if (-not $p) { exit 1 }; " +
                "$cmd = (($p.CommandLine ?? '') + ' ' + ($p.ExecutablePath ?? '')).ToLowerInvariant(); " +
                $"if (-not $cmd.Contains('{marker}')) {{ exit 2 }}; " +
                $"Stop-Process -Id {pid} -Force; exit 0";
            var (code, _, _) = proc.Run("powershell", $"-NoProfile -Command \"{script}\"", 20_000);
            if (code == 0) DeletePidFile();
            return code == 0;
        }

        var (killCode, _, _) = proc.Run("sh", $"-lc \"kill {pidInfo.Pid}\"", 20_000);
        if (killCode == 0) DeletePidFile();
        return killCode == 0;
    }

    (bool Running, string Detail) LiteLlmStatusPython()
    {
        if (!TryReadPid(out var pidInfo)) return (false, "pid file missing");
        return IsLiteLlmProcess(pidInfo)
            ? (true, $"litellm pid {pidInfo.Pid} running")
            : (false, "process not running");
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
        if (key.Length == 0) return false;
        const string uiUser = "admin";
        try
        {
            string env =
                $"LITELLM_MASTER_KEY={key}\n" +
                "LITELLM_SALT_KEY=sk-local-dev-salt\n" +
                $"UI_USERNAME={uiUser}\n" +
                $"UI_PASSWORD={key}\n" +
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
        if (!string.IsNullOrWhiteSpace(cfg.LiteLlmApiKey))
            return LaunchConfig.NormalizeLiteLlmApiKey(cfg.LiteLlmApiKey);
        if (!string.IsNullOrWhiteSpace(cfg.LiteLlmApiKeyEnvVar))
        {
            string fromNamed = Environment.GetEnvironmentVariable(cfg.LiteLlmApiKeyEnvVar.Trim()) ?? "";
            if (!string.IsNullOrWhiteSpace(fromNamed))
                return LaunchConfig.NormalizeLiteLlmApiKey(fromNamed);
        }
        string fallback = Environment.GetEnvironmentVariable(LaunchConfig.DefaultLiteLlmApiKeyEnvVar) ?? "";
        return LaunchConfig.NormalizeLiteLlmApiKey(fallback);
    }

    static LiteLlmModelEntry? ToLiteLlmModelEntry(MenuItem item, string mode)
    {
        string model = item.Model.Trim();
        if (model.Length == 0) return null;

        return item.Provider switch
        {
            "Ollama" => new LiteLlmModelEntry(
                $"ollama/{model}",
                $"ollama/{model}",
                NormalizeApiBaseForMode(TrimOpenAiSuffix(item.BaseUrl ?? OllamaDefaultBaseUrl), mode),
                null),
            "LM Studio" => new LiteLlmModelEntry(
                $"lmstudio/{model}",
                $"openai/{model}",
                NormalizeApiBaseForMode(LaunchConfig.NormalizeBaseUrl(item.BaseUrl ?? LmStudioDefaultBaseUrl), mode),
                "local"),
            "Foundry" => new LiteLlmModelEntry(
                $"foundry/{model}",
                $"openai/{model}",
                NormalizeApiBaseForMode(LaunchConfig.NormalizeBaseUrl(item.BaseUrl ?? FoundryDefaultBaseUrl), mode),
                "local"),
            _ => null,
        };
    }

    static string TrimOpenAiSuffix(string baseUrl)
    {
        string normalized = LaunchConfig.NormalizeBaseUrl(baseUrl);
        return normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^3]
            : normalized;
    }

    static string NormalizeApiBaseForMode(string apiBase, string mode) =>
        NormalizeLiteLlmMode(mode) == LiteLlmModeDocker
            ? RewriteLoopbackHost(apiBase)
            : apiBase;

    static bool RewriteLoopbackApiBasesForDocker(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            string current = File.ReadAllText(path);
            string rewritten = RewriteLoopbackHost(current);
            if (!string.Equals(current, rewritten, StringComparison.Ordinal))
                File.WriteAllText(path, rewritten);
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

    static string RewriteLoopbackHost(string text) =>
        (text ?? "")
            .Replace("http://localhost:", $"http://{DockerHostAlias}:", StringComparison.OrdinalIgnoreCase)
            .Replace("http://127.0.0.1:", $"http://{DockerHostAlias}:", StringComparison.OrdinalIgnoreCase)
            .Replace("http://localhost/", $"http://{DockerHostAlias}/", StringComparison.OrdinalIgnoreCase)
            .Replace("http://127.0.0.1/", $"http://{DockerHostAlias}/", StringComparison.OrdinalIgnoreCase);

    static bool EnsureDockerHostGatewayAliasInCompose()
    {
        try
        {
            if (!File.Exists(LiteLlmComposePath)) return false;
            string current = File.ReadAllText(LiteLlmComposePath);
            if (current.Contains("host.docker.internal:host-gateway", StringComparison.OrdinalIgnoreCase))
                return true;

            string updated = current
                .Replace(
                    "            env_file:\r\n              - .env\r\n            ports:",
                    "            env_file:\r\n              - .env\r\n            extra_hosts:\r\n              - \"host.docker.internal:host-gateway\"\r\n            ports:",
                    StringComparison.Ordinal)
                .Replace(
                    "            env_file:\n              - .env\n            ports:",
                    "            env_file:\n              - .env\n            extra_hosts:\n              - \"host.docker.internal:host-gateway\"\n            ports:",
                    StringComparison.Ordinal)
                .Replace(
                    "    env_file:\r\n      - .env\r\n    ports:",
                    "    env_file:\r\n      - .env\r\n    extra_hosts:\r\n      - \"host.docker.internal:host-gateway\"\r\n    ports:",
                    StringComparison.Ordinal)
                .Replace(
                    "    env_file:\n      - .env\n    ports:",
                    "    env_file:\n      - .env\n    extra_hosts:\n      - \"host.docker.internal:host-gateway\"\n    ports:",
                    StringComparison.Ordinal);
            if (!string.Equals(current, updated, StringComparison.Ordinal))
                File.WriteAllText(LiteLlmComposePath, updated);
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

    static bool MergeLiteLlmModelConfig(
        string path,
        IReadOnlyList<LiteLlmModelEntry> candidates,
        out int added,
        out int existing)
    {
        added = 0;
        existing = 0;
        try
        {
            if (!File.Exists(path)) return false;
            string text = File.ReadAllText(path);
            int generalIdx = text.IndexOf("general_settings:", StringComparison.Ordinal);
            if (generalIdx < 0) return false;

            string before = text[..generalIdx];
            if (!before.Contains("model_list:", StringComparison.Ordinal)) return false;
            string after = text[generalIdx..];
            string modelBlock = before.Replace("model_list: []", "model_list:", StringComparison.Ordinal);
            var existingNames = ParseModelNames(modelBlock);

            var toAdd = new List<LiteLlmModelEntry>();
            foreach (var entry in candidates)
            {
                if (existingNames.Contains(entry.ModelName))
                {
                    existing++;
                    continue;
                }
                existingNames.Add(entry.ModelName);
                toAdd.Add(entry);
            }

            if (toAdd.Count == 0) return true;
            var sb = new System.Text.StringBuilder();
            sb.Append(modelBlock.TrimEnd('\r', '\n'));
            sb.AppendLine();
            foreach (var entry in toAdd)
                sb.Append(RenderLiteLlmEntry(entry));
            sb.Append(after.TrimStart('\r', '\n'));
            if (!sb.ToString().EndsWith('\n')) sb.AppendLine();
            File.WriteAllText(path, sb.ToString());
            added = toAdd.Count;
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

    static HashSet<string> ParseModelNames(string modelSection)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in modelSection.Split('\n'))
        {
            string line = raw.Trim();
            const string token = "- model_name:";
            if (!line.StartsWith(token, StringComparison.Ordinal)) continue;
            string value = line[token.Length..].Trim();
            if (value.Length == 0) continue;
            if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
                value = value[1..^1];
            value = value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            if (value.Length > 0) names.Add(value);
        }
        return names;
    }

    static string RenderLiteLlmEntry(LiteLlmModelEntry entry)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  - model_name: \"{YamlEscape(entry.ModelName)}\"");
        sb.AppendLine("    litellm_params:");
        sb.AppendLine($"      model: \"{YamlEscape(entry.Model)}\"");
        sb.AppendLine($"      api_base: \"{YamlEscape(entry.ApiBase)}\"");
        if (!string.IsNullOrWhiteSpace(entry.ApiKey))
            sb.AppendLine($"      api_key: \"{YamlEscape(entry.ApiKey)}\"");
        return sb.ToString();
    }

    static string YamlEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

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
            extra_hosts:
              - "host.docker.internal:host-gateway"
            ports:
              - "127.0.0.1:${LITELLM_PORT}:4000"
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
        LITELLM_MASTER_KEY=
        LITELLM_SALT_KEY=sk-local-dev-salt
        UI_USERNAME=admin
        UI_PASSWORD=
        POSTGRES_PASSWORD=dbpassword9090
        LITELLM_PORT=4000
        LITELLM_DATABASE_URL=postgresql://llmproxy:dbpassword9090@db:5432/litellm
        """;

    bool PersistPid(string output, string marker, out LiteLlmPidInfo info)
    {
        info = new LiteLlmPidInfo(0, marker);
        string token = output.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!int.TryParse(token, out int pid) || pid <= 0) return false;
        try
        {
            info = new LiteLlmPidInfo(pid, marker);
            File.WriteAllText(LiteLlmPidPath, $"{pid}|{marker}");
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

    static bool TryReadPid(out LiteLlmPidInfo info)
    {
        info = new LiteLlmPidInfo(0, LiteLlmProcessMarker);
        try
        {
            if (!File.Exists(LiteLlmPidPath)) return false;
            string text = File.ReadAllText(LiteLlmPidPath).Trim();
            var parts = text.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var pidTagged) && pidTagged > 0)
            {
                string marker = string.IsNullOrWhiteSpace(parts[1]) ? LiteLlmProcessMarker : parts[1];
                info = new LiteLlmPidInfo(pidTagged, marker);
                return true;
            }
            if (int.TryParse(text, out var legacyPid) && legacyPid > 0)
            {
                info = new LiteLlmPidInfo(legacyPid, LiteLlmProcessMarker);
                return true;
            }
            return false;
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

    bool IsLiteLlmProcess(LiteLlmPidInfo pidInfo)
    {
        int pid = pidInfo.Pid;
        string marker = string.IsNullOrWhiteSpace(pidInfo.Marker) ? LiteLlmProcessMarker : pidInfo.Marker;
        if (OperatingSystem.IsWindows())
        {
            string script =
                $"$p = Get-CimInstance Win32_Process -Filter \"ProcessId = {pid}\" -ErrorAction SilentlyContinue; " +
                "if (-not $p) { exit 1 }; " +
                "$cmd = (($p.CommandLine ?? '') + ' ' + ($p.ExecutablePath ?? '')).ToLowerInvariant(); " +
                $"if ($cmd.Contains('{PsLiteral(marker.ToLowerInvariant())}')) {{ exit 0 }} else {{ exit 2 }}";
            var (code, _, _) = proc.Run("powershell", $"-NoProfile -Command \"{script}\"", 15_000);
            return code == 0;
        }

        string sh = $"ps -p {pid} -o command= | grep -Fqi -- {ShArg(marker)}";
        var (status, _, _) = proc.Run("sh", $"-lc \"{sh}\"", 15_000);
        return status == 0;
    }

    bool WaitForLiteLlmProcess(LiteLlmPidInfo pidInfo)
    {
        for (int i = 0; i < 5; i++)
        {
            if (IsLiteLlmProcess(pidInfo)) return true;
            Thread.Sleep(150);
        }
        DeletePidFile();
        return false;
    }

    (bool Ok, string Detail) WaitForLiteLlmApiReady(LaunchConfig cfg)
    {
        string baseUrl = LaunchConfig.NormalizeBaseUrl(cfg.LiteLlmBaseUrl);
        string apiKey = ResolveLiteLlmApiKey(cfg);
        if (apiKey.Length == 0)
            return (false, "LiteLLM API key not configured");

        int attempts = Math.Max(1, LiteLlmReadyTimeoutMs / LiteLlmReadyPollMs);
        string last = "endpoint did not become ready";
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                _ = http.GetString($"{baseUrl}/models", LiteLlmReadyProbeTimeoutMs, apiKey);
                return (true, "LiteLLM API is ready");
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                int status = (int)ex.StatusCode!.Value;
                return (false, $"authentication failed while checking LiteLLM readiness (HTTP {status})");
            }
            catch (HttpRequestException ex)
            {
                last = SingleLine((ex.InnerException ?? ex).Message, last);
            }
            catch (OperationCanceledException ex)
            {
                last = SingleLine((ex.InnerException ?? ex).Message, last);
            }
            catch (InvalidOperationException ex)
            {
                last = SingleLine(ex.Message, last);
            }

            if (i < attempts - 1)
                Thread.Sleep(LiteLlmReadyPollMs);
        }

        return (false, $"timed out waiting for LiteLLM API readiness: {last}");
    }

    (string File, string[] PrefixArgs)? ResolveLiteLlmStartCommand()
    {
        string? litellm = proc.Which("litellm");
        if (!string.IsNullOrWhiteSpace(litellm))
            return (litellm, []);
        string? python = proc.Which("python") ?? proc.Which("python3");
        if (!string.IsNullOrWhiteSpace(python))
            return (python, ["-m", "litellm"]);
        return null;
    }

    static string PsLiteral(string s) => s.Replace("'", "''");
    static string PsArrayLiteral(IEnumerable<string> args) => "@('" + string.Join("','", args.Select(PsLiteral)) + "')";
    static string ShArg(string s) => "'" + ShLiteral(s) + "'";
    static string ShLiteral(string s) => s.Replace("'", "'\"'\"'");
    static string SingleLine(string preferred, string fallback)
    {
        string source = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
        string line = (source ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown error";
        const int max = 180;
        return line.Length <= max ? line : $"{line[..max]}…";
    }
}
