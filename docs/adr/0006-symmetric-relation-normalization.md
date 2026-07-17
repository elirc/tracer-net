# 0006 — Symmetric relations are normalized to a stable endpoint order

**Status:** Accepted

## Context

Issues link five ways — `Relates`, `Blocks`, `BlockedBy`, `Duplicates`,
`DuplicatedBy` — but "blocked by" is not a fourth kind of link; it is the same
link seen from the other end. Storing a row per direction makes every link two
writes that can disagree: delete one side, or fail between them, and the graph
claims A blocks B while B is blocked by nothing. And a uniqueness constraint that
enforces "one fact, one row" compares *tuples*, not *meanings* — so "A relates to
B" and "B relates to A" slip through as two different tuples.

## Decision

Store **three** relation types, canonicalized on write (`IssueRelations.Canonicalize`)
and deriving the inverse on read:

- **Directed** relations (`Blocks`, `Duplicates`) keep their direction — "A blocks
  B" and "B blocks A" are contradictory claims, and collapsing them would destroy
  the cycle check that catches exactly that pair.
- **Symmetric** relations (`Relates`) get their two endpoints put in a fixed,
  arbitrary-but-stable order first, so the mirror spelling becomes the same tuple.

Uniqueness is enforced by a **database unique index** on
`(SourceIssueId, TargetIssueId, Type)`, not just a controller check. The check
makes the ordinary duplicate a clean `409` without an exception; the index is what
is actually true, because two concurrent requests can both pass a check before
either writes — and the loser of that race is translated back into the same `409`
rather than a 500. Blocking/duplication cycles are refused at any depth
(`IssueGraph`); `Relates` cycles are allowed (no direction, nothing to deadlock).
Links stay inside a team (`400` otherwise), so a response never leaks a far issue's
title.

## Consequences

- One row per fact; the inverse is always consistent because it is derived.
- The unique index only works *because* symmetric relations are normalized first —
  the two must ship together.
- `kind` in a response is relative to the issue you asked about: the same row
  reads `Blocks` to one end and `BlockedBy` to the other, carrying the same id.
