# MudAI

A Windows desktop app (WPF, .NET 10) that connects to any telnet MUD and plays it with a
local LLM served by LM Studio at http://127.0.0.1:1234. It is a hand-drivable MUD client
and an AI player you can steer in real time, in one window.

Works with any telnet MUD; the default and test target is mud.arctic.org port 2700 (change it in
Settings or appsettings.json).

## What it does

- Connects to a telnet MUD with telnet (IAC) negotiation and ANSI colour rendering.
- Plays the game: the local LLM reads the recent screen and decides the next command each turn.
- Records context locally and token-efficiently: a rolling screen buffer plus distilled long-term
  memory are packed into the model's context window (default 98,304 tokens) with a budget so the
  prompt never overflows.
- Lets you steer in real time: a live steering box feeds guidance into every turn, an editable
  goal, a master on/off switch, and four autonomy modes.
- Avoids looping bad commands: each command's response is classified (success, no-effect, error);
  commands that fail repeatedly are suppressed for a cooldown and surfaced back to the model.
- Learns across sessions: durable lessons and per-command success/failure stats are saved in a
  local SQLite database and fed back into the model's context each turn.
- Categorized awareness: the model files what it learns under categories (geography, navigation,
  combat, skills, progression, economy, npcs, misc); the knowledge is recalled compactly (balanced
  per category, token-capped) and exported to a readable %LOCALAPPDATA%\MudAI\awareness.md you can
  open to see what it knows.
- Builds a persistent map: when the server provides GMCP/MSDP room data, each room (zone, exits)
  and the moves that connect rooms are recorded, and the current room's known exits are recalled
  compactly into context. Without GMCP/MSDP there is no structured room data, so the map stays empty.
- Manual play: type commands yourself at the command line at any time, like any MUD client.
- Multi-command: separate commands with ';' (for example "open door;north;look") and they are sent
  in order. Both the human command line and the AI can chain commands this way.
- Streams the model output token by token into a live pane.
- Reads structured game state (GMCP/MSDP): if the server supports it, HP/MP/room/exits are parsed
  into a compact state shown in the UI and fed to the model instead of scraping text.
- Optional scripted auto-login on connect, with the password entered in a masked field and never
  shown in the transcript or sent to the model.

## Requirements

- .NET 10 SDK (Windows; uses the WindowsDesktop runtime / WPF).
- LM Studio running with a model loaded and its server started on 127.0.0.1:1234
  (OpenAI-compatible). A model with a large context (about 96K) is ideal but not required.

## Build

    dotnet build MudAI.slnx

## Test

    dotnet test

The test project (MudAI.Tests, xUnit) covers the ANSI parser (including 256-colour, truecolor,
and CSI edge cases), the JSON decision parser, the anti-loop tracker, context-window budgeting,
the screen buffer, GMCP/MSDP parsing and GameState mapping, and the login manager.

## Run

    dotnet run --project MudAI.App

The AI master switch starts off, so you connect and log in by hand using the command line at the
bottom, then turn the AI on and pick an autonomy mode.

## Configuration

Edit MudAI.App/appsettings.json (copied next to the exe on build):

| Key | Default | Meaning |
|---|---|---|
| MudHost / MudPort | mud.arctic.org / 2700 | MUD server |
| ConnectTimeoutMs | 15000 | TCP connect timeout |
| LlmBaseUrl | http://127.0.0.1:1234 | LM Studio base URL |
| LlmModel | local-model | Model name (LM Studio uses it as a label) |
| StreamResponses | true | Stream the model output token by token (and early-stop once the JSON decision is complete) |
| StreamIdleTimeoutMs | 30000 | End a stalled stream if no token arrives in this window |
| UseJsonResponseFormat | true | Ask the server for guaranteed JSON (response_format); prevents empty/garbled decisions |
| ContextWindowTokens | 98304 | Model context window for budgeting |
| MaxResponseTokens | 512 | Max tokens per decision (tight = faster decode) |
| MaxRecentLines | 80 | Screen lines fed to the model each turn (the real prompt-size lever) |
| MinTurnDelayMs | 400 | Min pause between AI turns |
| OutputSettleMs | 300 | Quiet time before the screen is "settled" |
| ResponseTimeoutMs | 2000 | Max wait for a command's first reply before "no effect" |
| RepeatThreshold | 3 | Failures in a row before a command is suppressed |
| SuppressionCooldownSeconds | 120 | How long a suppressed command stays blocked |
| Login.AutoLogin | false | Run the expect/send login script on connect |
| Login.Username | "" | Username for the {username} placeholder |
| Login.Script | [] | Custom [{WhenContains, Send, Once}] steps; empty means name then password |
| DefaultAutonomyMode | AutoWithInterrupt | Starting autonomy mode |
| AiEnabledOnStart | false | Master AI toggle starts off (log in manually first) |
| MemoryDbPath | null | SQLite path; null means %LOCALAPPDATA%\MudAI\memory.db |

The login password is never stored in config. It is entered in the masked password field in the
UI at runtime and is masked everywhere it could otherwise surface.

## Making the AI act faster

Each turn runs: settle -> build prompt -> model inference -> send -> wait for reply -> settle ->
pause. The defaults above are tuned to cut the fixed per-turn overhead, and the app early-stops the
stream as soon as the JSON decision is complete. The remaining bottleneck is local model inference,
which is set by your hardware and LM Studio, not the app. To go faster there:

- Use a smaller/faster instruct model (a non-"thinking" model avoids empty responses) and a lighter
  quant.
- Enable full GPU offload and Flash Attention; keep the model resident (disable idle auto-unload)
  so the cached prompt prefix survives between turns.
- Set LM Studio's loaded context to what you actually use (about 8-16K) rather than 98K, to save
  KV-cache memory.

The app already orders the prompt stable-content-first so LM Studio's KV cache can reuse the
unchanged prefix across turns.

## Using it

1. Connect (top bar). The AI master switch starts off, so log in or create a character yourself
   at the command line, like a normal MUD client.
2. When you are in the game, turn on "AI may send commands".
3. Pick an autonomy mode:
   - Proposal: the AI suggests a command; nothing is sent until you approve it (you can edit it).
   - AutoWithInterrupt: the AI plays automatically; you interrupt or steer at any time.
   - HybridByRisk: auto-sends low-risk commands, asks approval for medium or high risk.
   - FullAuto: sends everything, no gating.
4. Type a goal and live steering to guide it; both are fed into every turn.
5. Watch the live model output, the reasoning feed, the suppressed-commands (anti-loop) list, the
   GMCP/MSDP game state, and the memory stats.

Memory persists at %LOCALAPPDATA%\MudAI\memory.db and grows across sessions.

## Architecture

Two projects:

- MudAI.Core (net10.0, no UI) holds the logic: Telnet/, Ansi/, Llm/, Memory/, GameData/
  (GMCP/MSDP), and Agent/. AgentOrchestrator is the hub and the single object the UI talks to.
- MudAI.App (net10.0-windows, WPF MVVM): App.xaml.cs (generic host + DI), ViewModels/MainViewModel.cs
  (marshals orchestrator events onto the dispatcher), MainWindow.xaml(.cs), and Themes/ (the
  design system used by the UI).

The non-obvious decisions are recorded in docs/adr/. The project's engineering and writing
conventions are in AGENTS.md and docs/.

## License

MIT. See [LICENSE](LICENSE).
