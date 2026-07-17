# 0010 — `DateTimeOffset` is stored as UTC ticks

**Status:** Accepted

## Context

The application reasons about instants constantly — cycle status, activity
windows, ordering feeds newest-first. SQLite has no native `DateTimeOffset` type.
The default EF mapping stores it as text, and text ordering compares *wall-clock
strings*, not instants: `09:00+09:00` sorts after `10:00-05:00` even though it is
six hours earlier. Any `ORDER BY`, range filter, or status check done in SQL would
then be quietly wrong.

## Decision

Persist every `DateTimeOffset` as **UTC ticks** (`long`) through value converters
registered in `TracerDbContext.ConfigureConventions` (one for
`DateTimeOffset`, one for `DateTimeOffset?`). Ticks are a monotonic integer in a
single reference frame, so ordering, filtering, and cycle-status comparisons are
correct **in the database**, not only after everything is pulled into memory.

## Consequences

- SQL ordering and range queries over instants are correct.
- The stored value is normalized to UTC; the original offset is not preserved
  (every read comes back as `+00:00`). Acceptable — the system reasons in instants,
  and the offset carries no business meaning here.
- The conversion is central and automatic (a convention), so no entity or query
  has to remember it, and a new `DateTimeOffset` property is handled the moment it
  is added.
