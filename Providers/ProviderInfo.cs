namespace Copilocal.Providers;

/// <summary>Static metadata about each supported local-model provider.</summary>
internal sealed class ProviderInfo
{
    internal required string Name { get; init; }
    internal required string Key { get; init; }          // canonical id used on MenuItem.Provider
    internal required string Blurb { get; init; }
    internal required string DocsUrl { get; init; }
    internal required string InstallHow { get; init; }   // "winget" | "GitHub MSIX"
    internal required string PullCmd { get; init; }       // example command to add a model
    internal required string ModelsDocsUrl { get; init; } // where to browse models

    internal static readonly ProviderInfo Ollama = new()
    {
        Name = "Ollama",
        Key = "Ollama",
        Blurb = "Simple local model runner (ROCm/CUDA/Vulkan/Metal). Largest model library.",
        DocsUrl = "https://ollama.com/download",
        InstallHow = "winget",
        PullCmd = "ollama pull qwen2.5-coder:14b",
        ModelsDocsUrl = "https://ollama.com/library",
    };

    internal static readonly ProviderInfo Foundry = new()
    {
        Name = "Foundry Local",
        Key = "Foundry",
        Blurb = "Microsoft on-device runtime (ONNX/WinML). Uses GPU/NPU; OpenAI-compatible.",
        DocsUrl = "https://learn.microsoft.com/azure/ai-foundry/foundry-local/",
        InstallHow = "GitHub MSIX",
        PullCmd = "foundry model download qwen2.5-coder-14b",
        ModelsDocsUrl = "https://learn.microsoft.com/azure/ai-foundry/foundry-local/how-to/how-to-compile-hugging-face-models",
    };

    internal static readonly ProviderInfo LmStudio = new()
    {
        Name = "LM Studio",
        Key = "LM Studio",
        Blurb = "Polished GUI + server for GGUF models (llama.cpp). Great for browsing/A-B testing.",
        DocsUrl = "https://lmstudio.ai/docs",
        InstallHow = "winget",
        PullCmd = "lms get qwen2.5-coder-14b",
        ModelsDocsUrl = "https://lmstudio.ai/models",
    };

    internal static readonly ProviderInfo LiteLlm = new()
    {
        Name = "LiteLLM",
        Key = "LiteLLM",
        Blurb = "OpenAI-compatible proxy/router for many cloud and local model providers.",
        DocsUrl = "https://docs.litellm.ai/docs/proxy/docker_quick_start",
        InstallHow = "docker compose / uv (cross-platform)",
        PullCmd = "litellm --setup",
        ModelsDocsUrl = "https://docs.litellm.ai/docs/providers",
    };

    internal static readonly ProviderInfo[] All = [Ollama, Foundry, LmStudio, LiteLlm];

    internal static ProviderInfo ByKey(string key) => All.First(p => p.Key == key);
}
