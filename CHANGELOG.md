# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
