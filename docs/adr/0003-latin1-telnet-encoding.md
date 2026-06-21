# 0003 - Latin-1 telnet wire encoding

Status: Accepted

## Context

Many MUD servers are byte-oriented and predate UTF-8. Bytes 0x80-0xFF
appear in ANSI art and high-bit text. Decoding the socket as UTF-8 would throw or insert
replacement characters on those bytes.

## Decision

Decode and encode the telnet byte stream as Latin-1 (`Encoding.Latin1`), which maps every byte
0x00-0xFF to exactly one char and back with no loss. ANSI colour and telnet IAC handling run on
top of this 1:1 mapping. GMCP payloads, which are UTF-8 by spec, are decoded as UTF-8 at the
point they are parsed.

## Alternatives considered

- UTF-8 for the whole stream: throws or corrupts on high-bit bytes; rejected.
- CP437: closer to classic DOS art but not a clean 1:1 round-trip and not what these servers assume.

## Consequences

- The reader never throws on "invalid" bytes.
- A server that genuinely sends UTF-8 in the main stream would render mojibake; acceptable for
  the target server, and revisited only if a UTF-8 MUD is added.
