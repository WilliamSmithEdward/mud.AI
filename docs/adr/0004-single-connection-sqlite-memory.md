# 0004 - Single-connection, serialized SQLite memory

Status: Accepted

## Context

The agent reads and writes memory (lessons, command stats, rooms) from the background agent
loop, while the UI reads counts for the stats panel. SQLite connections are not safe for
concurrent use, and the access pattern is low-volume (a few writes per turn).

## Decision

`SqliteMemoryStore` keeps one open `SqliteConnection` and serializes every operation through a
`SemaphoreSlim`. Disposal acquires the same gate before closing the connection. WAL journal mode
is enabled. The DB lives at `%LOCALAPPDATA%\MudAI\memory.db` by default, outside the repo.

## Alternatives considered

- A connection per operation: simpler concurrency story but more open/close churn and still
  needs care around the shared file; unnecessary for this volume.
- A connection pool: overkill for a single-user desktop app with light write traffic.

## Consequences

- No data races on the connection; the contract is documented on the type.
- All memory access is one `await` behind a semaphore, which is fine at this volume but would
  need revisiting if access became high-frequency or multi-writer.
