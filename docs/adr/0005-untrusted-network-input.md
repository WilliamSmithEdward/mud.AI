# 0005 - Treat all network input as untrusted

Status: Accepted

## Context

MudAI connects to a remote MUD server and a local LLM server. Both feed data into parsers and,
ultimately, into the LLM context. A malicious or buggy server should not be able to crash the
app, desync the protocol, or leak the user's password.

## Decision

Harden every network-input boundary:

- MSDP parsing caps recursion depth (a deeply nested payload is skipped iteratively rather than
  recursing), so a crafted packet cannot overflow the stack.
- Outbound telnet data escapes the IAC byte (0xFF doubled) so user/AI text cannot be read as a
  telnet command.
- LLM stream reads have a per-token idle timeout; telnet connect has a timeout and TCP keepalive.
- The login password is masked everywhere it could surface (transcript, prompt, LLM context)
  for the whole session once it has been sent, not just for a time window, and the password is
  never stored in `appsettings.json`.

## Alternatives considered

- Trust the target server and skip hardening: a single misbehaving or hostile endpoint
  could crash or mislead the app; rejected per RG-19 and RG-12.

## Consequences

- The parsers and telnet client carry small guards and timeouts that are covered by unit tests
  (for example the MSDP deep-nesting regression test).
- New network inputs must follow the same rule: validate and bound before use.
