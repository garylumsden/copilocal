# Copilot instructions for this repository

## Build, test, and lint commands

Use .NET 10 SDK.

```powershell
# Build (local dev baseline)
dotnet build -c Debug

# Build (CI/release baseline)
dotnet build -c Release --nologo

# Run full test suite
dotnet test tests\Copilocal.Tests\Copilocal.Tests.csproj --nologo --verbosity minimal

# Run a single test method
dotnet test tests\Copilocal.Tests\Copilocal.Tests.csproj --filter "FullyQualifiedName~Copilocal.Tests.LauncherTests.TokenLimits_KnownContext_DerivesPromptAndOutputLimits"

# Publish Windows Native AOT artifact
dotnet publish copilocal.csproj -c Release -r win-x64 -o out
```

No separate lint command is configured in this repo or CI workflow; quality gates are `dotnet build` + `dotnet test`, plus a Windows AOT publish check.

## High-level architecture

- `Program.cs` is the orchestrator: parse CLI args, discover models, render picker, run preflight checks, then either:
  - launch GitHub Copilot CLI via `Launch/Launcher.cs`, or
  - run local chat loop via `Chat/LocalChatRunner.cs`.
- `Providers/ProviderHub.cs` handles provider discovery/lifecycle and capability probes across Ollama, Foundry Local, LM Studio, and LiteLLM.
- `Launch/Preflight.cs` blocks/warns on models that fail tool-calling or context-window requirements.
- `Configuration/LaunchConfig.cs` persists launch settings in `~/.copilocal/config.json`, including MCP/flag toggles passed through to `copilot`.
- `Ui/*` uses Spectre.Console for menus/pages and alternate-screen management.
- `Infrastructure/*` provides side-effect boundaries (`IProcessRunner`, `IHttpGateway`) used by production code and replaced by fakes in tests.

Dependency direction is intentional: `Infrastructure <- Providers <- Launch <- Ui`, with `Cli` standalone and `Program` wiring everything.

## Key conventions in this codebase

- **Native AOT-safe JSON handling:** avoid reflection-based `JsonSerializer`; build JSON payloads manually and parse with `JsonDocument`, checking `JsonElement.ValueKind` before getters.
- **Testability boundary first:** shell/process/network access stays behind `IProcessRunner` / `IHttpGateway`; tests use `tests/Copilocal.Tests/Fakes/*`.
- **BYOK env vars are child-process-only:** set Copilot provider env vars only for the launched `copilot` process; do not persist them globally.
- **Terminal UX pattern:** menus run in alternate screen; chat mode and Copilot launch temporarily suspend alt-screen so normal scrollback and child TUI rendering work.
- **Repo organization:** one namespace per top-level folder (`Copilocal.Cli`, `.Configuration`, `.Infrastructure`, `.Providers`, `.Launch`, `.Ui`, `.Chat`).
