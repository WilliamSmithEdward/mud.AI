# Architecture Decision Records

Short records of the non-obvious decisions in MudAI. Each ADR uses the same headings:
Status, Context, Decision, Alternatives considered, Consequences. Add a new numbered file
when a decision is significant enough that a future maintainer would otherwise have to
reverse-engineer the reasoning.

- [0001 - Core/UI separation with an orchestrator facade](0001-core-ui-separation.md)
- [0002 - Pin SQLitePCLRaw to 3.0.3](0002-sqlitepclraw-pin.md)
- [0003 - Latin-1 telnet wire encoding](0003-latin1-telnet-encoding.md)
- [0004 - Single-connection, serialized SQLite memory](0004-single-connection-sqlite-memory.md)
- [0005 - Treat all network input as untrusted](0005-untrusted-network-input.md)
- [0006 - Categorized awareness knowledge base](0006-categorized-awareness-knowledge-base.md)
