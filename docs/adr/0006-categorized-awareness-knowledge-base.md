# 0006 - Categorized awareness knowledge base

Status: Accepted

## Context

The agent already persists flat tactical lessons, per-command success/failure stats, and a room
map. That is useful but not organized: there was no way to accumulate durable, categorized
knowledge ("what I know about geography, combat, skills, this zone") that grows across sessions and
can be reviewed by a human. The model also had only a free-text "lesson" channel, which conflates
one-off tactical notes with strategic facts about a subject.

## Decision

Add a single `awareness` table keyed by `(category, subject)` with a fact, confidence, and
reinforcement count. The model files facts through a new optional `awareness` object in its
decision JSON (category, subject, fact); the category is normalized to a closed taxonomy of eight
(geography, navigation, combat, skills, progression, economy, npcs, misc) and subject/fact are
length-clamped. Re-filing the same subject refreshes the fact and bumps confidence.

Recall is balanced and token-capped: the top N entries per category (default 2) are rendered as a
compact "WHAT YOU KNOW" block, trimmed to a hard token budget. Geographic awareness (rooms per
zone, exploration frontier) is derived on read from the existing `rooms`/`room_exits` tables rather
than re-stored. A human-readable `awareness.md` is regenerated from the DB (atomic write) on
disconnect and every N awareness writes.

## Alternatives considered

- Extend the existing `lessons` table with a category column: loses the "refine a subject"
  semantics and conflates tactical anti-loop lessons with strategic facts. Rejected.
- A free-form (domain, subject, attribute, value) 4-tuple: too fiddly for a small local model to
  populate reliably; fragments badly. Rejected in favor of the simpler (category, subject) -> fact.
- Storing derived geography/zone facts in the table: duplicates data already in `rooms`. Derived on
  read instead.

## Consequences

- Lessons and awareness are distinct channels (flat tactical vs. categorized strategic); both are
  recalled, neither duplicates the other.
- Recall stays token-stable regardless of DB size (per-category quota + hard token cap).
- One new table (created by `CREATE TABLE IF NOT EXISTS`, no migration needed), one new decision
  field, read-time aggregation over existing tables, and one markdown exporter. Existing
  lessons/rooms/command behavior is unchanged.
- Deferred (v1): row-cap pruning, recency decay beyond the tie-breaker, and subject-name entity
  resolution.
