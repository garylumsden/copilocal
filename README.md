# copilocal

Pick a **local LLM** from an arrow-key terminal menu and launch **GitHub Copilot CLI** against it.

`copilocal` discovers the models you already have in **Ollama**, **Foundry Local**,
**LM Studio**, and **LiteLLM**, lets you choose one in an arrow-key menu, makes sure that provider's
OpenAI-compatible server is running, then starts `copilot` with the right BYOK
environment variables — **set only on the Copilot child process**, never persisted to
your shell.

If a provider isn't installed, copilocal offers to install it (checkbox opt-in, with
links to each tool's docs so you can decide).

[![ci](https://github.com/garylumsden/copilocal/actions/workflows/ci.yml/badge.svg)](https://github.com/garylumsden/copilocal/actions/workflows/ci.yml)
[![release](https://github.com/garylumsden/copilocal/actions/workflows/release.yml/badge.svg)](https://github.com/garylumsden/copilocal/actions/workflows/release.yml)
[![winget](https://img.shields.io/badge/winget-Gjlumsden.Copilocal-blue)](https://github.com/garylumsden/copilocal/releases)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![platform: Windows · macOS](https://img.shields.io/badge/platform-Windows%20%C2%B7%20macOS-0078D6)

<p align="center">
  <img src="assets/copilocal-hero.svg" alt="copilocal hero image showing local runtimes routing into Copilot CLI" width="920" />
</p>

<p align="center">
  <img src="assets/copilocal-icon.svg" alt="copilocal app icon" width="104" />
</p>

> ⚠️ **Not affiliated with GitHub or Microsoft.** copilocal is an independent,
> community-built tool. It is **not** affiliated with, endorsed by, or sponsored by
> GitHub, Microsoft, OpenAI, Ollama, or LM Studio. It simply launches the official
> **GitHub Copilot CLI** that *you* install separately. See
> [Disclaimer & trademarks](#disclaimer--trademarks).

```text
(startup animation: icon flies in left→right, reveals the wordmark, then settles on the right)

 ██████╗ ██████╗ ██████╗ ██╗██╗      ██████╗  ██████╗ █████╗ ██╗        ╭─════════════─╮
██╔════╝██╔═══██╗██╔══██╗██║██║     ██╔═══██╗██╔════╝██╔══██╗██║        │  ┄┄┄┄┄┄┄┄┄┄  ├─●
██║     ██║   ██║██████╔╝██║██║     ██║   ██║██║     ███████║██║      ●─┤   >_         │
██║     ██║   ██║██╔═══╝ ██║██║     ██║   ██║██║     ██╔══██║██║        │  ┄┄┄┄┄┄┄┄┄┄  ├─●
╚██████╗╚██████╔╝██║     ██║███████╗╚██████╔╝╚██████╗██║  ██║███████╗   ╰─════════════─╯
 ╚═════╝ ╚═════╝ ╚═╝     ╚═╝╚══════╝ ╚═════╝  ╚═════╝╚═╝  ╚═╝╚══════╝
      Pick a local model · launch GitHub Copilot CLI against it

Discovering local models…
  ✓ Ollama — 2 models
  ✓ Foundry Local — 2 models
  ✓ LM Studio — 1 model

Select a local model  (↑/↓, Enter to launch):
  Ollama
>   Ollama        qwen2.5-coder:7b
    Ollama        llama3.2:3b
  Foundry Local
    Foundry Local qwen2.5-coder-7b-generic-gpu
    Foundry Local phi-4-mini
  LM Studio
    LM Studio     qwen3-0.6b
  ⚙  Configure launch options
  ✖  Quit
```

## Quickstart

```powershell
# 1. Install copilocal
winget install Gjlumsden.Copilocal

# 2. Prerequisites (install separately):
#    - GitHub Copilot CLI on PATH:   copilot --version
#    - Optional now: local runtime + model (you can also install a runtime from inside copilocal on launch)
#      e.g. Ollama:
ollama pull qwen2.5-coder:7b

# 3. Ollama only — set at least 64k context for coding tools when memory allows:
setx OLLAMA_CONTEXT_LENGTH 131072         # then restart Ollama
# After the model loads, verify the active allocation with: ollama ps

# 4. Launch
copilocal
```

No local runtime installed yet? Run `copilocal` and choose **⚙ Install / manage providers**
to install local runtimes (Ollama / Foundry Local / LM Studio), or enable LiteLLM.
When setting up LiteLLM, copilocal can import all currently discovered local-provider
models into the LiteLLM config in one step.

Pick a model with **↑/↓** and **Enter**, then choose how to run it:
- **Launch GitHub Copilot CLI** (current behavior)
- **Chat-only mode** (local model chat loop; no tool execution)
- **Back to picker**

For Copilot launch mode, copilocal starts the provider if needed, warms the model up,
sets BYOK env vars, and launches `copilot`. When Copilot exits you can pick a **different
model to continue the same session**, or **Exit**.

## Why

GitHub Copilot CLI supports BYOK (bring-your-own-key/model) via environment variables,
but that's **one model per session**, set by hand. copilocal turns it into a picker over
**all** your local runtimes — no proxy, no config files, no shell pollution.

## Requirements

- **GitHub Copilot CLI** (`copilot` on `PATH`) — https://github.com/github/copilot-cli
  (on Windows copilocal can install it for you at startup via winget; on macOS install it
  yourself, e.g. with Homebrew)
- At least one provider path: [Ollama](https://ollama.com), [Foundry Local](https://learn.microsoft.com/azure/foundry-local/), [LM Studio](https://lmstudio.ai), or [LiteLLM](https://docs.litellm.ai/docs/proxy/docker_quick_start)
  (copilocal can install/manage local runtimes and LiteLLM runtime flows from the UI)
  - **Foundry Local note:** copilocal uses the current preview CLI surface:
    `foundry cache list -o json`, `foundry model ...`, and `foundry server ...`.
- Windows x64 / ARM64, or macOS arm64 / x64 — a single self-contained binary; no .NET runtime required

### Validated with

copilocal talks to each runtime's CLI/REST surface, which shifts over time. The current
behaviour and documentation were checked against these versions on Windows 11 x64 in
September 2026:

| Component | Version |
| --- | --- |
| Ollama | 0.34.2 |
| LM Studio | 0.4.23+1 |
| Foundry Local (CLI) | 0.10.3 |
| LiteLLM | 1.101.0 |
| GitHub Copilot CLI | 1.0.88 |
| .NET SDK (build) | 10.0 |

Newer releases usually work too; if discovery or launch misbehaves after a runtime update,
please [open an issue](https://github.com/garylumsden/copilocal/issues) noting the version.

## Install

### winget (Windows)
```powershell
winget install Gjlumsden.Copilocal
```

### Manual (Windows / macOS)
Download the binary for your platform from [Releases](https://github.com/garylumsden/copilocal/releases)
and put it on your `PATH`:

- **Windows:** `copilocal-win-x64.exe` / `copilocal-win-arm64.exe`
- **macOS:** `copilocal-osx-arm64` / `copilocal-osx-x64` — then make it runnable:
  ```bash
  chmod +x copilocal-osx-arm64
  xattr -d com.apple.quarantine copilocal-osx-arm64   # clear Gatekeeper (unsigned binary)
  ```

> On **macOS**, copilocal discovers and launches your local providers and Copilot CLI, but
> the one-click **install** flows (winget / Add-AppxPackage) are Windows-only — install
> Copilot CLI and the runtimes yourself there.

## Usage

```powershell
copilocal                      # interactive picker -> choose Copilot launch or chat-only mode
copilocal -- --resume          # everything after -- is forwarded to copilot
copilocal --name "my feature"  # name the managed session (resume it later by name)
copilocal --pick 1             # non-interactive: pick model #1
copilocal --pick 1 --offline   # non-interactive: pick #1 and run air-gapped
copilocal --dry-run            # show what it would set, don't launch
```

> **Continue with a different model:** in the interactive flow copilocal assigns a
> stable `--session-id` to the Copilot session it launches. When Copilot exits, it shows
> the captured resume id/name (and the `copilot --resume=<id>` command to reopen it
> later), then drops you back to the model picker. Pick another local model to **continue
> the same conversation** with it, or choose **Exit**. This is skipped when you drive
> sessions yourself (e.g. `--resume`, `--continue`, `--session-id`).

> **Air-gapped mode:** after you pick a model, copilocal asks whether to run
> air-gapped (default **No**). Choosing yes sets `COPILOT_OFFLINE=true`, so Copilot CLI
> never contacts GitHub's servers and disables telemetry. Pass `--offline` to default
> the prompt to yes (or to enable it directly on the `--pick` path).

### Chat-only mode

Choose **Chat-only mode** after selecting a model to stay inside copilocal and chat
directly with that provider endpoint (no GitHub Copilot CLI launch, no tool execution).

Commands inside chat:
- `/help` show commands
- `/clear` reset conversation history
- `/multi` compose multiline input (finish with `/send`, cancel with `/cancel`)
- `/exit` return to model picker
- `Ctrl+C` also returns to model picker (same behavior as `/exit`). Note: while a model
  response is in flight (the `Thinking…` spinner), `Ctrl+C` is honored only after the
  request returns or the request timeout elapses — the HTTP call itself is not cancelled mid-flight.
- slash command autocomplete: type `/` then Enter to pick a command, or use unique prefixes like `/h`
- bottom-right token tracker showing active model, cumulative tokens, and last-turn token usage (when provider returns `usage` fields)
- assistant replies wrap to terminal width and render common markdown (headings, emphasis, inline code, links) plus markdown tables as native terminal tables
- interactive menus (picker/options/install) use the terminal's alternate screen so each is a
  distinct, cleared page; chat mode and the GitHub Copilot CLI launch run on the normal buffer
  so the terminal's native scrollback works and the launched CLI's own UI renders cleanly

### How models are discovered

| Provider      | Discovery command / source        | Endpoint                       |
| ------------- | --------------------------------- | ------------------------------ |
| Ollama        | `ollama list`                     | `http://localhost:11434/v1`    |
| Foundry Local | `foundry cache list -o json`      | `http://127.0.0.1:<port>/v1` (resolved at runtime) |
| LM Studio     | `lms ls --json`                   | `http://localhost:1234/v1`     |
| LiteLLM       | `GET /v1/models`                  | configurable (default `http://localhost:4000/v1`) |

copilocal sets these on the **child** `copilot` process only:

| Environment variable | Value / when set |
| --- | --- |
| `COPILOT_PROVIDER_BASE_URL` | the chosen provider's OpenAI base URL |
| `COPILOT_PROVIDER_TYPE` | `openai` |
| `COPILOT_MODEL` | the chosen model id |
| `COPILOT_PROVIDER_API_KEY` | `local` for local runtimes; LiteLLM uses your configured key/env var |
| `COPILOT_PROVIDER_WIRE_API` | `responses` when a reasoning model needs the Responses API |
| `COPILOT_OFFLINE` | `true` in air-gapped mode |
| `COPILOT_PROVIDER_MAX_PROMPT_TOKENS` | auto-derived from model context, or your launch-options override |
| `COPILOT_PROVIDER_MAX_OUTPUT_TOKENS` | auto-derived from model context, or your launch-options override |

> Models must support **tool calling** and **streaming** to work well with Copilot CLI.
> Before launch copilocal runs **preflight guards**: it flags models that don't advertise
> tool calling, checks the model's **context window** is big enough for Copilot's large
> prompt, and—at launch—probes the chosen model to catch ones that emit tool calls as plain
> text instead of native `tool_calls` (e.g. Ollama's `qwen2.5-coder`), which silently breaks
> Copilot's agentic loop. Interactively you can override a warning; a non-interactive
> `--pick` is blocked so it fails fast with a clear reason.

### Reasoning models & context windows

A few gotchas copilocal now handles for you:

- **Reasoning models** (e.g. `gpt-oss`) reply in a `reasoning` field and leave `content`
  empty on the chat/completions wire — Copilot then loops and Ollama returns
  `400 invalid message content type: <nil>`. When the warm-up detects a reasoning model
  and the endpoint exposes `/v1/responses` (Ollama, LM Studio do), copilocal switches it
  to the OpenAI **Responses** wire API (`COPILOT_PROVIDER_WIRE_API=responses`).
- **Context too small for Copilot's prompt.** Copilot's static prompt, tool schemas, history,
  input, and output share one window. GitHub recommends at least **128k tokens** for best
  results. copilocal blocks scripted launches below **32768**, warns below **131072**, and
  checks the active context again after model warm-up:
  - **Ollama** auto-sizes context from available VRAM when `OLLAMA_CONTEXT_LENGTH` is unset
    (4k below 24 GiB VRAM, 32k at 24–48 GiB, and 256k at 48 GiB or more). Ollama recommends
    at least **64000** for coding tools. Set `64000` or `131072` if memory allows. copilocal
    reads `/api/ps` after warm-up, so token limits use the active allocation, not an assumed default.
  - **Foundry Local** compiles context into each device variant. Run
    `foundry model list --variants`, then inspect a candidate with
    `foundry model info <model> -o json`. The tested Qwen 2.5 Coder NPU variants on Foundry
    Local 0.10.3 report **4224** tokens and are too small for Copilot CLI. Choose another
    tool-capable variant when the context warning appears.
  - **LM Studio** exposes `trainedForToolUse` and `maxContextLength` through `lms ls --json`.
    copilocal now uses both fields. It reads the loaded allocation from `/api/v1/models`
    after warm-up. Load a larger context with
    `lms load <model> --context-length 131072` when memory allows.

## Recommended models

Models need **tool calling**, **streaming**, and enough context for Copilot's tool schemas.
Prefer a current tool-trained model. Reasoning models can work because copilocal detects
them and uses the Responses API when the provider supports it.

| Use | Ollama | Foundry Local | LM Studio |
| --- | --- | --- | --- |
| Current tool-trained examples | `gemma4`, `qwen3`, `granite4` | Use `foundry model list` and select a row with the `tools` task | `ibm/granite-4-h-tiny`, `mistralai/ministral-3-3b` |
| Reasoning examples | `gpt-oss` | Use a catalog model that reports tool calling | Use a model that supports `/v1/responses` and tool use |
| Compatibility checks | `ollama ps`, then copilocal's tool probe | `foundry model info <model> -o json` | `lms ls --json`, then copilocal's tool probe |

> Tags change fast — check the latest live: Ollama
> [`ollama.com/library?sort=newest`](https://ollama.com/library?sort=newest),
> Foundry with `foundry model list`, and LM Studio's in-app **Discover** catalog.

> **Foundry variant note:** device variants can have different compiled contexts and tool
> support. Do not select by alias alone. Inspect the exact variant before use.

### Example: tuning to your machine

There's no single "best" model — it depends on your **VRAM** and **RAM**. A model's
weights must fit in **VRAM + RAM**; what fits in **VRAM alone runs fastest**; and a
**MoE** model (high total params, few *active* per token) lets you punch above your
VRAM *if* you have plenty of RAM — because memory is set by *total* params while speed
is set by *active* params.

As an **example only**, on a *Ryzen 9 5900X · 128 GB RAM · Radeon RX 6800 XT (16 GB VRAM)*:

- **Fits in VRAM (fastest):** a 7–14B dense model at Q4 — e.g. `qwen2.5-coder:14b`
  (~9 GB) as an everyday coder, with headroom for context.
- **Lean / snappy:** `qwen2.5-coder:7b`, `llama3.2:3b`.
- **More capability via RAM:** a low-active **MoE** such as `qwen3-coder:30b` (~3B active)
  — its ~18 GB of weights spill into the ample 128 GB RAM while only ~3B params compute
  per token, so it stays usable.
- **Context:** 16 GB VRAM comfortably handles `OLLAMA_CONTEXT_LENGTH=32768`; `131072`
  is heavy (large KV cache).
- **Avoid:** big *dense* 24B+ models — they overflow 16 GB VRAM and crawl.

## Configure launch options

The menu's **⚙ Configure launch options** item opens a page to set the flags copilocal
passes to `copilot` on every launch. Choices are saved to `~/.copilocal/config.json` and
applied automatically. Toggle (multi-select), pick a **reasoning effort**, and add any
**extra raw args**. Available toggles include:

- **MCP/skills:** disable built-in MCP (`--disable-builtin-mcps`), disable user MCP servers
  (`--disable-mcp-server=<name>` for each in `~/.copilot/mcp-config.json`), disable skills
  (`--excluded-tools=skill`)
- **Permissions:** `--allow-all-tools`, `--allow-all-paths`, `--allow-all-urls`,
  `--yolo` (allow everything)
- **Modes:** `--autopilot`, `--plan`, `--experimental`
- **Behaviour:** `--no-custom-instructions`, `--no-ask-user`, `--enable-memory`,
  `--no-remote`, `--disallow-temp-dir`, `--enable-reasoning-summaries`
- **UI/maintenance:** `--banner`, `--screen-reader`, `--no-color`, `--no-auto-update`
- **Reasoning effort:** `--reasoning-effort` = `none` / `low` / `medium` / `high` / `xhigh` / `max`

Plus a free-text **extra args** field for any other `copilot` flag. The page shows a
preview of the resulting launch command. Disabling MCP servers and skills shrinks
Copilot's prompt a lot — useful for local models with limited context.

### Token budget

Unknown local models aren't in Copilot's catalog, so Copilot falls back to generic
`COPILOT_PROVIDER_MAX_PROMPT_TOKENS` / `MAX_OUTPUT_TOKENS` defaults that can overshoot the
model's real context and truncate to empty/garbled output. copilocal **auto-derives** them
from the model's active context. Ollama uses `/api/ps` after warm-up, and LM Studio uses
`/api/v1/models` with `lms ls --json` metadata as a fallback. copilocal reserves room for
provider framing and the reply (`output ≈ context/4`, capped at 8192). Override
either with the **Max prompt tokens** / **Max output tokens** fields in *Configure launch
options* (blank = auto).

## Installing providers

If a runtime is missing, copilocal shows it in the menu. Choosing **Install / manage
providers** opens a checkbox list (space to toggle) with a docs link for each:

- **Ollama** — winget (`Ollama.Ollama`)
- **LM Studio** — winget (`ElementLabs.LMStudio`)
- **Foundry Local** — winget (`Microsoft.FoundryLocal`)
- **LiteLLM** — choose runtime mode in UI:
  - setup mode can either **install local LiteLLM runtime** or **skip install and configure an existing LiteLLM instance**
  - if local install fails, copilocal shows the failure reason and can immediately switch to existing-instance setup
  - start failures now include the concrete reason (e.g., missing Docker or missing LiteLLM key)
  - start now waits until LiteLLM is actually reachable (`/v1/models`) before reporting success
  - if LiteLLM is enabled on a local endpoint and discovery can't resolve it at startup, copilocal attempts one automatic LiteLLM start, then re-discovers models
  - **docker**: scaffolds a compose stack with a pinned LiteLLM image, Postgres, and the Admin UI
  - **python**: installs the pinned `litellm[proxy]` version, uses SQLite, and manages start/stop/status
  - after a successful LiteLLM start, copilocal prints the login key + endpoint + clickable UI link so you can sign in and manage models/config
  - you can print the clickable UI link any time via **Manage LiteLLM runtime → Show runtime status**
  - key hint: keys are normalized to `sk-...` format; docker UI login uses `admin` + the same LiteLLM key
  - new Docker setups generate and retain random database and encryption secrets in `.env`
  - setup prompt: optionally add all discovered local-provider models into LiteLLM config
  - manage action: add any missing local-provider models later (no duplicate entries)

### LiteLLM troubleshooting

- **UI says invalid credentials**  
  For local docker mode, sign in with:
  - username: `admin`
  - password: your LiteLLM key (normalized to `sk-...`)
  
  Credentials are written to `~/.copilocal/litellm/.env` as `UI_USERNAME` / `UI_PASSWORD`.

- **Copilot launch gets 401 on LiteLLM model**  
  Make sure the same LiteLLM key is configured in copilocal (**Set endpoint + auth**).  
  copilocal now uses that key for:
  - LiteLLM `/v1/models` discovery
  - launch auth (`COPILOT_PROVIDER_API_KEY`)

- **Changed key or auth settings?**  
  Run **Manage LiteLLM runtime → Stop LiteLLM runtime**, then **Start LiteLLM runtime** to rewrite `.env`.

- **LiteLLM can’t call local routed models (500 connection error)**  
  In docker mode, local provider routes must use container-reachable hostnames.  
  copilocal now rewrites `api_base` loopback routes (`localhost` / `127.0.0.1`) to
  `host.docker.internal` on LiteLLM start, and when syncing local models into config.

- **Need a full clean reset?**  
  Use **Manage LiteLLM runtime → Reset LiteLLM local setup**. This:
  - runs docker compose down (if present)
  - removes `~/.copilocal/litellm/`
  - resets LiteLLM settings in `~/.copilocal/config.json` to defaults (LiteLLM disabled, default base URL/env var, docker mode)

### Where settings are stored

- Global app settings: `~/.copilocal/config.json`
- LiteLLM local runtime files (compose/config/.env/pid): `~/.copilocal/litellm/`

### Provider compatibility references

- [GitHub Copilot CLI BYOK model requirements](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-byok-models)
- [Ollama context length](https://docs.ollama.com/context-length)
- [Ollama OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility)
- [Foundry Local CLI reference](https://learn.microsoft.com/azure/foundry-local/reference/reference-cli)
- [LM Studio model loading](https://lmstudio.ai/docs/cli/local-models/load)
- [LM Studio tool use](https://lmstudio.ai/docs/developer/openai-compat/tools)
- [LiteLLM Docker quick start](https://docs.litellm.ai/docs/proxy/docker_quick_start)

## Build from source

```powershell
git clone https://github.com/garylumsden/copilocal.git
cd copilocal
dotnet run                                   # debug
dotnet publish -c Release -r win-x64         # single AOT exe -> bin/Release/net10.0/win-x64/publish/
dotnet publish -c Release -r win-arm64
```

Requires the .NET 10 SDK. Output is a self-contained, Native-AOT single executable.

> **Native AOT on Windows** needs the Visual Studio C++ toolchain (Desktop development
> with C++) for the linker; `dotnet run`/`dotnet build` work without it.

Run the test suite:

```powershell
dotnet test tests/Copilocal.Tests/Copilocal.Tests.csproj
```

### Project layout

Source is grouped by responsibility, one namespace per folder:

| Folder / namespace | What's in it |
| --- | --- |
| `Copilocal` (root) | `Program` — entry point and interactive orchestration |
| `Cli` → `Copilocal.Cli` | `CommandLineArgs` — argv parsing |
| `Providers` → `Copilocal.Providers` | `ProviderHub` (discovery + lifecycle), `ProviderInfo`, `ProviderInstaller`, `MenuItem` |
| `Launch` → `Copilocal.Launch` | `Launcher`, `Preflight` (guards), `LaunchConfig` |
| `Ui` → `Copilocal.Ui` | `Banner`, `InstallFlow`, `LaunchOptionsPage` (Spectre.Console pages) |
| `Infrastructure` → `Copilocal.Infrastructure` | `ProcessRunner`/`HttpGateway` (+ interfaces), `Json` |

Dependency direction stays one-way: `Infrastructure ← Providers ← Launch ← Ui`, with `Cli`
standalone and `Program` wiring everything together.

## Contributing

Issues and PRs are welcome. Please keep changes small and focused, run
`dotnet build -c Debug` and `dotnet test` before opening a PR (both must be green), and
match the existing style (interface-backed `IProcessRunner`/`IHttpGateway` for testability,
hand-built JSON helpers so the build stays Native-AOT friendly). Report bugs and request
features on the [issue tracker](https://github.com/garylumsden/copilocal/issues).

## License

[MIT](LICENSE)

## Disclaimer & trademarks

copilocal is an independent, unofficial project and is **not affiliated with, endorsed
by, or sponsored by** GitHub, Microsoft, OpenAI, Ollama, or LM Studio. It launches the
official **GitHub Copilot CLI** that you install and authenticate separately, and it
talks to local runtimes you install yourself.

"GitHub", "GitHub Copilot", "Microsoft", "Foundry Local", "Ollama", and "LM Studio" are
trademarks of their respective owners and are used here for identification only. Use of
GitHub Copilot remains subject to GitHub's terms; bringing your own model does not change
your obligations under those terms.
