---
name: copilocal-offload
description: Feasibility spike skill for local-model offload through copilocal.
---

Use this skill to evaluate whether a task can be routed to local models through copilocal.

Rules:
- Hard fail when local provider/runtime is unavailable.
- Do not route to remote fallback models.
- Keep output concise and explicit about offload status.
