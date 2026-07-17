# 0008 — Cycles are half-open intervals with derived status

**Status:** Accepted

## Context

Cycles (sprints) are time boxes that run back to back. If the interval were closed
on both ends, two consecutive cycles would either share their boundary instant
(overlap) or leave a one-tick gap. And a stored `status` field is a second copy of
something the calendar already knows, free to drift out of sync with it.

## Decision

Model a cycle as the **half-open** interval `[startsAt, endsAt)`: the start instant
belongs to the cycle, the end instant is the first that does not
(`CycleSchedule`). Consecutive cycles share a boundary with no overlap and no gap.
`status` (`Upcoming`/`Active`/`Completed`) is **always derived** from the dates and
`now`, never stored, so it cannot drift. Cycles within a team may not overlap
(`409`) and must end after they start (`422`).

The same half-open convention is reused elsewhere for the same reason: the activity
feed's `since`/`until` window and the metrics `from`/`to` window are both
`[lower, upper)`, so consecutive windows tile without double-counting an entry.

## Consequences

- Adjacent cycles are exactly contiguous.
- `status` is computed on every read (cheap; a team's cycle list is small), which
  is also why the cycle and milestone list endpoints filter status in memory
  rather than in SQL and are not paged.
- Required date fields are modelled nullable in the DTO so a missing one yields a
  `400`, not a bogus `422`.
