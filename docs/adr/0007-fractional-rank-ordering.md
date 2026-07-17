# 0007 — Issues are ordered by fractional rank with rebalance

**Status:** Accepted

## Context

Issues sit in an explicit order within a state column, and that order is edited by
dragging cards. Storing order as consecutive integer row numbers means every move
renumbers everything after the insertion point — a single drag becomes a write
across the whole column.

## Decision

Give each issue a `double Position` — a rank, not a row number. A move is computed
as the **midpoint** between the issue's two new neighbours (`IssueRanker.Between`),
so it rewrites exactly one row. Because each split halves the remaining gap,
midpoints eventually run out; once neighbours are closer than `IssueRanker.MinGap`,
the column is renumbered into even steps (`IssueRanker.RankAt`) **in the same
transaction as the move**, rather than letting two ranks collide.

Moving a card to another column is a **state change**, validated by the same
`IssueStateMachine` as an explicit transition — dragging must not become a back
door around the workflow. Moving *within* a column is not logged (rank is a view
preference); moving *between* columns logs the same `IssueStateChanged` an explicit
transition produces.

## Consequences

- A reorder is normally a single-row write.
- Occasionally a move also renumbers its column — accepted, and bounded to that one
  column and that one transaction.
- Board reads order by `state.Position` then `Position`, with an id tiebreak so
  paging a board that shares ranks cannot repeat or skip rows.
