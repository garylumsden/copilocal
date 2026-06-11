# copilocal

Pick a **local LLM** from a fast terminal UI and launch **GitHub Copilot CLI** against it.

`copilocal` discovers the models you already have in **Ollama**, **Foundry Local**, and
**LM Studio**, lets you choose one in an arrow-key menu, makes sure that provider's
OpenAI-compatible server is running, then starts `copilot` with the right BYOK
environment variables — **set only on the Copilot child process**, never persisted to
your shell.

If a provider isn't installed, copilocal offers to install it (checkbox opt-in, with
links to each tool's docs so you can decide).

> ⚠️ **Not affiliated with GitHub or Microsoft.** copilocal is an independent,
> community-built tool. It is **not** affiliated with, endorsed by, or sponsored by
> GitHub, Microsoft, OpenAI, Ollama, or LM Studio. It simply launches the official
> **GitHub Copilot CLI** that *you* install separately. See
> [Disclaimer & trademarks](#disclaimer--trademarks).

```
 ██████╗ ██████╗ ██████╗ ██╗██╗      ██████╗  ██████╗ █████╗ ██╗
██╔════╝██╔═══██╗██╔══██╗██║██║     ██╔═══██╗██╔════╝██╔══██╗██║
██║     ██║   ██║██████╔╝██║██║     ██║   ██║██║     ███████║██║
██║     ██║   ██║██╔═══╝ ██║██║     ██║   ██║██║     ██╔══██║██║
╚██████╗╚██████╔╝██║     ██║███████╗╚██████╔╝╚██████╗██║  ██║███████╗
 ╚═════╝ ╚═════╝ ╚═╝     ╚═╝╚══════╝ ╚═════╝  ╚═════╝╚═╝  ╚═╝╚══════╝
     Pick a local model · launch GitHub Copilot CLI against it

Discovering local models…
  ✓ Ollama — 2 models
  ✓ LM Studio — 1 model

Select a local model  (↑/↓, Enter to launch):
  Ollama
>   Ollama     qwen2.5-coder:7b
    Ollama     llama3.2:3b
  LM Studio
    LM Studio  qwen3-0.6b
  ⚙  Configure launch options
  ✖  Quit
```

## Quickstart

```powershell
# 1. Install copilocal
winget install Gjlumsden.Copilocal

# 2. Prerequisites (install separately):
#    - GitHub Copilot CLI on PATH:   copilot --version
#    - A local runtime + a model, e.g. Ollama:
ollama pull qwen2.5-coder:7b

# 3. Ollama only — give Copilot's prompt room (default 4096 is too small):
setx OLLAMA_CONTEXT_LENGTH 32768          # then restart Ollama

# 4. Launch
copilocal
```

Pick a model with **↑/↓** and **Enter**. copilocal starts the provider if needed, warms
the model up, sets the BYOK env vars, and launches `copilot` against it. When Copilot
exits you can pick a **different model to continue the same session**, or **Exit**.

## Why

GitHub Copilot CLI supports BYOK (bring-your-own-key/model) via environment variables,
but that's **one model per session**, set by hand. copilocal turns it into a picker over
**all** your local runtimes — no proxy, no config files, no shell pollution.

## Requirements

- **GitHub Copilot CLI** (`copilot` on `PATH`) — https://docs.github.com/copilot/how-tos/copilot-cli
- At least one of: [Ollama](https://ollama.com), [Foundry Local](https://learn.microsoft.com/azure/ai-foundry/foundry-local/), [LM Studio](https://lmstudio.ai)
  (copilocal can install these for you)
- Windows x64 or ARM64 (single self-contained exe; no .NET runtime required)

## Install

### winget
```powershell
winget install Gjlumsden.Copilocal
```

### Manual
Download `copilocal-<arch>.exe` from [Releases](https://github.com/garylumsden/copilocal/releases),
put it on your `PATH`.

## Usage

```powershell
copilocal                      # interactive picker -> air-gap prompt -> launches copilot
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

### How models are discovered

| Provider      | Discovery command          | Endpoint                       |
| ------------- | -------------------------- | ------------------------------ |
| Ollama        | `ollama list`              | `http://localhost:11434/v1`    |
| Foundry Local | `foundry cache list -o json` | `http://127.0.0.1:<port>/v1` (resolved at runtime) |
| LM Studio     | `lms ls --json`            | `http://localhost:1234/v1`     |

copilocal sets these on the **child** `copilot` process only:

```
COPILOT_PROVIDER_BASE_URL   the chosen provider's OpenAI base URL
COPILOT_PROVIDER_TYPE       openai
COPILOT_MODEL               the chosen model id
COPILOT_PROVIDER_API_KEY    local  (placeholder; local servers ignore it)
COPILOT_PROVIDER_WIRE_API   responses  (only for reasoning models, see below)
```

> Models must support **tool calling** and **streaming** to work well with Copilot CLI.
> copilocal flags models that don't advertise tool calling, and—at launch—probes the chosen
> model to catch ones that emit tool calls as plain text instead of native `tool_calls`
> (e.g. Ollama's `qwen2.5-coder`), which silently breaks Copilot's agentic loop.

### Reasoning models & Ollama context

Two gotchas copilocal now handles for you:

- **Reasoning models** (e.g. `gpt-oss`) reply in a `reasoning` field and leave `content`
  empty on the chat/completions wire — Copilot then loops and Ollama returns
  `400 invalid message content type: <nil>`. When the warm-up detects a reasoning model
  and the endpoint exposes `/v1/responses` (Ollama, LM Studio do), copilocal switches it
  to the OpenAI **Responses** wire API (`COPILOT_PROVIDER_WIRE_API=responses`).
- **Ollama's default context is 4096 tokens** (when `OLLAMA_CONTEXT_LENGTH` isn't set).
  Copilot's prompt + tools don't fit, giving blank replies / loops / 400. If the env var
  is unset **or below 16384**, copilocal warns when you pick an Ollama model and asks
  before launching (default **No**, returns you to the picker). Fix it with
  `setx OLLAMA_CONTEXT_LENGTH 131072` (clamped per model's max) then restart Ollama.

## Recommended models

Models need **tool calling** (for Copilot's agentic loop) and ideally fit your VRAM.
Small, fast, non-reasoning coders are the safest start; reasoning models work too
(copilocal routes them via the Responses API automatically).

| Use | Ollama | LM Studio |
| --- | --- | --- |
| Best small coder | `qwen2.5-coder:7b` | `qwen2.5-coder-7b-instruct` |
| Lighter / faster | `qwen2.5-coder:3b`, `llama3.2:3b` | `llama-3.2-3b-instruct` |
| Tiny (quick tests) | `llama3.2:1b` | `qwen3-0.6b` |
| Recent / agentic | `granite4:3b`, `qwen3:4b` | `granite-4.0-h-tiny`, `phi-4-mini-instruct` |

> Tags change fast — check the latest live: Ollama
> [`ollama.com/library?sort=newest`](https://ollama.com/library?sort=newest), and
> LM Studio's in-app **Discover** catalog.

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
from the model's actual context — Ollama via `OLLAMA_CONTEXT_LENGTH`, LM Studio via its
loaded context (`/api/v1/models`) — reserving room for the reply
(`prompt ≈ context − output − buffer`, `output ≈ context/4`, capped at 8192). Override
either with the **Max prompt tokens** / **Max output tokens** fields in *Configure launch
options* (blank = auto).

## Installing providers

If a runtime is missing, copilocal shows it in the menu. Choosing **Install / manage
providers** opens a checkbox list (space to toggle) with a docs link for each:

- **Ollama** — winget (`Ollama.Ollama`)
- **LM Studio** — winget (`ElementLabs.LMStudio`)
- **Foundry Local** — latest CLI MSIX from [microsoft/Foundry-Local](https://github.com/microsoft/Foundry-Local) releases (matches your CPU architecture)

## Build from source

```powershell
git clone https://github.com/garylumsden/copilocal.git
cd copilocal
dotnet run                                   # debug
dotnet publish -c Release -r win-x64         # single AOT exe -> bin/Release/net10.0/win-x64/publish/
dotnet publish -c Release -r win-arm64
```

Requires the .NET 10 SDK. Output is a self-contained, Native-AOT single executable.

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
