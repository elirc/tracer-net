# 0009 — Import is idempotent via external id and a filtered unique index

**Status:** Accepted

## Context

Bulk import is retried — a user fixes a file and sends it again — so it must be
safe to apply the same payload twice. A half-applied import is the worst outcome:
the caller cannot tell what landed, and their obvious next move (resend) is only
safe *because* import is idempotent.

## Decision

Match each import row to an existing issue by `Issue.ExternalId`, so re-sending
updates rather than duplicates. Resolution order is: an issue already imported
under this external id, then an issue whose own identifier is the string
("ENG-42") — the second rule is what makes an export re-importable into the team it
came from, since an issue created here has no external id and exports under its
identifier.

"An external id names one issue in a team, or none" is a **database guarantee**: a
filtered unique index on `(TeamId, ExternalId) WHERE ExternalId IS NOT NULL`. The
filter states explicitly that the many never-imported issues are not all "the same"
null-ided issue, rather than leaning on a provider quirk.

Import is **all-or-nothing**: every row is resolved and validated before any is
written, and one bad row rejects the payload with per-row `problem+json`. A dry run
builds the exact same plan a real import applies, so a green dry run is a promise,
not a guess. A state change on import goes through the workflow state machine like
any other. CSV export defuses formula injection (a cell beginning `= + - @` is
prefixed) so a spreadsheet cannot execute an exported title.

## Consequences

- Re-importing the same file changes nothing the second time.
- Two rows claiming the same external id in one payload are rejected, not silently
  merged.
- Import creates issues, not the team's vocabulary: an unknown label or project is
  an error, not an invitation to invent one.
