using System.Text.Json;

using Copilocal.Infrastructure;

namespace Copilocal.Providers;

internal sealed class ProviderInstaller(IProcessRunner proc, IHttpGateway http)
{
    internal bool Install(string providerName)
    {
        return providerName switch
        {
            "Ollama" => Winget("Ollama.Ollama"),
            "LM Studio" => Winget("ElementLabs.LMStudio"),
            "Foundry Local" => InstallFoundry(),
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
        string tmp = Path.Combine(Path.GetTempPath(), Path.GetFileName(url));
        try
        {
            http.DownloadToFile(url, tmp, 600_000);
            var (code, _, _) = proc.Run("powershell",
                $"-NoProfile -Command \"Add-AppxPackage -Path '{tmp}'\"", 300_000);
            return code == 0;
        }
        catch (Exception)
        {
            // best-effort: install flow reports failure and leaves docs fallback to user.
            return false;
        }
        finally
        {
            try { File.Delete(tmp); }
            catch (Exception)
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
                string tag = rel.GetProperty("tag_name").GetString() ?? "";
                if (!tag.StartsWith("cli-preview-")) continue;
                if (!rel.TryGetProperty("assets", out var assets) || assets.GetArrayLength() == 0) continue;
                if (!Version.TryParse(tag.Replace("cli-preview-", ""), out var v)) continue;
                if (v > bestVer) { bestVer = v; best = rel; found = true; }
            }
            if (!found) return null;

            string? fallback = null;
            foreach (var a in best.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                string dl = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.Contains($"win-{arch}-winml") && name.EndsWith(".msix")) return dl;
                if (name.Contains($"win-{arch}") && name.EndsWith(".msix")) fallback = dl;
            }
            return fallback;
        }
        catch (Exception)
        {
            // best-effort: missing release metadata means install flow reports failure.
            return null;
        }
    }
}
