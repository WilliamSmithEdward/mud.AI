# 0001 - Core/UI separation with an orchestrator facade

Status: Accepted

## Context

MudAI mixes three concurrency-sensitive concerns (a telnet read loop, a background agent loop,
and a streaming HTTP client) with a WPF UI that must only be touched on the dispatcher thread.
Putting that logic in code-behind would couple thread-sensitive work to the UI and make it hard
to test.

## Decision

Split into two projects. `MudAI.Core` (target `net10.0`, no WPF reference) holds all logic:
telnet, ANSI, LLM, memory, GMCP/MSDP, and the agent loop. `MudAI.App` (target
`net10.0-windows`, WPF MVVM) holds only presentation. `AgentOrchestrator` is the single
facade the UI talks to; it raises events from background threads, and `MainViewModel` marshals
them onto the dispatcher.

## Alternatives considered

- One WPF project with logic in code-behind: simpler to start, but untestable and couples
  threading to the UI.
- A full app/domain/infrastructure layering: more ceremony than a single-window tool needs.

## Consequences

- Core is unit-testable without WPF (see `MudAI.Tests`).
- A UI rewrite does not touch domain logic; a storage change does not touch the UI.
- The cost is one marshaling boundary (`MainViewModel.OnUi`) that every event must cross.
