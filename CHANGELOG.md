# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.2] - 2026-06-17

### Fixed

- Non-interactive launch (piped input, CI, or the winget validation sandbox) no longer crashes
  with `STATUS_STACK_BUFFER_OVERRUN` (0xC0000409); copilocal now detects the absence of an
  interactive terminal and exits cleanly.

### Added

- `--version`/`-V` and `--help`/`-h`/`-?` flags that print and exit without entering the picker.

### Changed

- Chat mode and the GitHub Copilot CLI launch now run on the normal screen buffer so terminal
  scrollback works and the launched CLI renders cleanly; interactive menus keep the alternate screen.
- Reorganized folders/namespaces for coherence and removed all `partial class` usage (internal only).
- Hardened provider discovery, LiteLLM shell quoting/config, config save, downloads, and argument
  parsing (security/robustness review remediation).

## [0.1.1] - 2026-06-15

### Changed

- Hardened LiteLLM startup and readiness checks with auth-aware probing.
- Improved LiteLLM Docker/Python runtime handling, including startup retries and key propagation.
- Fixed interactive launch flow so failed/declined LiteLLM warm-up returns to menu and can auto-start local LiteLLM when unresolved.

## [0.1.0] - 2026-06-15

### Added

- Local-model picker for Ollama, Foundry Local, and LM Studio.
- BYOK launch flow for GitHub Copilot CLI using local OpenAI-compatible endpoints.
- Preflight guards for tool calling, streaming, context size, and tool-call shape.
- Provider install flows for Copilot CLI and local runtimes where supported.
- Native-AOT single-executable releases for Windows plus self-contained macOS binaries.
