using Copilocal.Launch;

namespace Copilocal.Providers;

internal sealed partial class ProviderInstaller
{
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
}
