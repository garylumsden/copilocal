---
description: Offload focused work to copilocal local-model flow (feasibility spike scaffold)
argument-hint: "<task>"
---

You are running the `/copilocal` plugin command scaffold.

Run offload in-process via shell tool:

- Execute: `copilocal --offload "<user task>"`
- If user also specifies a model index, execute: `copilocal --pick <N> --offload "<user task>"`

For this implementation phase:

1. Capture the user task.
2. Run `copilocal --offload` with that task.
3. If offload fails, return explicit failure reason and do not silently fall back.
4. Return concise result and any explicit next action needed by the user.

This command routes through copilocal's non-interactive offload mode.
