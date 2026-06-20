# MudAI

A Windows (WPF, .NET 10) MUD client that plays the **Arctic** telnet MUD using a **local LLM**
(served by **LM Studio** at `http://127.0.0.1:1234`). It is a real, hand-drivable MUD client
*and* an AI player you can steer in real time — in one window.

> Target server for testing: `mud.arctic.org` port `2700`.

---

## What it does

- **Connects to a telnet MUD** with proper telnet (IAC) negotiation and ANSI colour rendering.
- **Learns to play**: a local LLM reads the recent screen and decides the next command each turn.
- **Records context locally, token-efficiently**: a rolling screen buffer + distilled long-term
  memory are packed into the model's context window (default 98,304 tokens) with a budget so the
  prompt never overflows.
- **You steer it in real time**: a live steering box feeds guidance into every turn; an editable
  goal; a master on/off switch; and four autonomy modes.
- **Avoids looping bad commands**: every command's response is classified (success / no-effect /
  error); commands that fail repeatedly are suppressed for a cooldown and surfaced back to the
  model as "do not repeat these".
- **Learns and grows**: durable lessons, per-command success/failure stats, and room notes persist
  across sessions in a local SQLite database.
- **Manual play like any MUD client**: type commands yourself at the bottom command line at any time.
- **Live token-by-token streaming**: the model's output streams into a "live output" pane as it
  is generated (toggle with `StreamResponses`).
- **Structured game state (GMCP/MSDP)**: if the server supports it, HP/MP/room/exits are parsed
  from GMCP/MSDP side-channels into a compact, authoritative state shown in the UI and fed to the
  model (cheaper and more reliable than scraping text).
- **Scripted auto-login**: an optional expect/send script (with `{username}`/`{password}`) logs in
  on connect; the password is entered via a `PasswordBox` and masked in the transcript.

---

## How the pieces map to the requirements

| Requirement | Where it lives |
|---|---|
| Connect to telnet MUD | `MudAI.Core/Telnet/TelnetClient.cs` |
| ANSI colour + plain text for the LLM | `MudAI.Core/Ansi/AnsiParser.cs`, `MudOutputProcessor.cs` |
| Learn to play / decide commands | `MudAI.Core/Agent/AgentOrchestrator.cs` + `ContextBuilder.cs` |
| Token-efficient local context | `ContextBuilder.cs`, `ScreenBuffer.cs`, `TokenEstimator.cs` |
| Steer in real time | Steering box + master toggle + autonomy modes (`MainViewModel` / `MainWindow`) |
| Avoid looping bad commands | `MudAI.Core/Agent/CommandTracker.cs` |
| Learn and grow (persistent) | `MudAI.Core/Memory/SqliteMemoryStore.cs` |
| Manual command sending | Command line in `MainWindow.xaml` → `AgentOrchestrator.SendManualAsync` |
| LM Studio API @ 127.0.0.1:1234 (98,304 ctx) | `MudAI.Core/Llm/LmStudioClient.cs`, `appsettings.json` |
| Token-by-token streaming | `LmStudioClient.CompleteStreamAsync` → `AgentOrchestrator.GetCompletionAsync` |
| GMCP/MSDP structured state | `MudAI.Core/GameData/` (`GmcpParser`, `MsdpParser`, `GameStateMapper`, `GameState`) |
| Auto-login | `MudAI.Core/Agent/LoginManager.cs` + `Configuration/LoginOptions.cs` |

---

## Architecture

Two projects:

- **`MudAI.Core`** (`net10.0`, no UI) — all the concurrency-sensitive logic, isolated from WPF:
  - `Telnet/` — async TCP client + IAC state machine.
  - `Ansi/` — stateless SGR parser and a stream→line/prompt processor.
  - `Llm/` — OpenAI-compatible chat client for LM Studio (blocking + SSE streaming).
  - `Memory/` — SQLite-backed durable memory.
  - `GameData/` — GMCP/MSDP parsers and the `GameState` mapper.
  - `Agent/` — the orchestrator loop, anti-loop tracker, screen buffer, token-budgeted context
    builder, the JSON decision parser, and the scripted `LoginManager`.
- **`MudAI.App`** (`net10.0-windows`, WPF MVVM) — a single window:
  - `App.xaml.cs` — generic host + dependency injection + config + logging.
  - `ViewModels/MainViewModel.cs` — the only thing that talks to the orchestrator; marshals its
    background-thread events onto the UI dispatcher.
  - `MainWindow.xaml(.cs)` — unified layout; code-behind only renders coloured ANSI runs.

The **`AgentOrchestrator`** is the hub: it owns the connection, feeds telnet output through the
ANSI processor into the screen buffer, and — when the master AI toggle is on — runs the loop:
**gather context → ask the LLM → parse the decision → gate by autonomy mode → send → observe the
outcome → update anti-loop + memory.**

---

## Requirements

- **.NET 10 SDK** (Windows; uses the WindowsDesktop runtime / WPF).
- **LM Studio** running with a model loaded and its local server started on `127.0.0.1:1234`
  (OpenAI-compatible). A model with a large context (≈96K) is ideal but not required.

## Build & run

```powershell
dotnet build MudAI.slnx
dotnet run --project MudAI.App
```

## Tests

```powershell
dotnet test
```

60 unit tests (`MudAI.Tests`, xUnit) cover the ANSI parser (incl. 256-colour/truecolor and CSI
edge cases), the JSON decision parser, the anti-loop tracker, context-window budgeting, the screen
buffer, GMCP/MSDP parsing + `GameState` mapping, and the login manager.

## Configuration

Edit `MudAI.App/appsettings.json` (copied next to the exe on build):

| Key | Default | Meaning |
|---|---|---|
| `MudHost` / `MudPort` | `mud.arctic.org` / `2700` | MUD server |
| `LlmBaseUrl` | `http://127.0.0.1:1234` | LM Studio base URL |
| `LlmModel` | `local-model` | Model name (LM Studio ignores/uses as label) |
| `ContextWindowTokens` | `98304` | Model context window for budgeting |
| `MaxResponseTokens` | `1024` | Max tokens per decision |
| `StreamResponses` | `true` | Stream the model's output token-by-token into the live pane |
| `MinTurnDelayMs` | `1500` | Min pause between AI turns |
| `OutputSettleMs` | `600` | Quiet time before the screen is "settled" |
| `ResponseTimeoutMs` | `4000` | Max wait for a command's first reply before "no effect" |
| `RepeatThreshold` | `3` | Failures in a row before a command is suppressed |
| `SuppressionCooldownSeconds` | `120` | How long a suppressed command stays blocked |
| `Login.AutoLogin` | `false` | Run the expect/send login script on connect |
| `Login.Username` / `Login.Password` | `""` | Credentials for `{username}`/`{password}` |
| `Login.Script` | `[]` | Custom `[{WhenContains, Send, Once}]` steps; empty ⇒ name→password default |
| `DefaultAutonomyMode` | `AutoWithInterrupt` | Starting autonomy mode |
| `AiEnabledOnStart` | `false` | Master AI toggle starts off (log in manually first) |
| `MemoryDbPath` | `null` | SQLite path; null ⇒ `%LOCALAPPDATA%\MudAI\memory.db` |

## Using it

1. **Connect** (top bar). The AI master switch starts **off**, so you log in / create a character
   yourself using the command line at the bottom — exactly like a normal MUD client.
2. When you're in the game, flip **"AI may send commands"** on.
3. Pick an **autonomy mode**:
   - **Proposal** — the AI suggests a command; nothing is sent until you Approve (you can edit it).
   - **AutoWithInterrupt** — the AI plays automatically; you interrupt/steer any time.
   - **HybridByRisk** — auto-sends low-risk commands, asks approval for medium/high-risk ones.
   - **FullAuto** — sends everything, no gating.
4. Type a **goal** and/or **live steering** to guide it; both are fed into every turn.
5. Watch the **AI reasoning** feed, the **suppressed-commands** (anti-loop) list, and **memory** stats.

Memory persists at `%LOCALAPPDATA%\MudAI\memory.db` and grows across sessions.
