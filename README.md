# tracer-net

A Linear-clone issue tracker backend built with C# / .NET 10 and ASP.NET Core.

Teams own everything: their own workflow, projects, labels, cycles, and issues.
Issues move between workflow states under a validated state machine, sit at a
fractional rank within their column, can be scheduled into a time-boxed cycle,
and can be found again through a single filterable search endpoint.

## Stack

- .NET 10, ASP.NET Core Web API (controllers)
- EF Core with SQLite
- xUnit — unit tests for domain rules, integration tests over real HTTP via `WebApplicationFactory`

## Solution layout

| Project | Purpose |
|---|---|
| `Tracer.Api` | ASP.NET Core Web API host, controllers, DTOs |
| `Tracer.Domain` | Domain entities and core business rules |
| `Tracer.Infrastructure` | EF Core `DbContext`, migrations, persistence |
| `Tracer.Tests` | Unit and integration tests |

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/Tracer.Api
```

In `Development` the app applies migrations and seeds sample data on startup
(two teams, `ENG` and `DES`, with users, projects, labels, issues, comments, and
a live cycle), so `dotnet run` gives you something to query immediately. Every
endpoint except `/api/health` needs an API key, and the seed creates three:

| Key | User | Role | Sees |
|---|---|---|---|
| `trk_dev_admin_ana` | `ana` | Admin | every team |
| `trk_dev_member_ben` | `ben` | Member | `ENG` |
| `trk_dev_member_dana` | `dana` | Member | `DES` |

```bash
curl http://localhost:5284/api/health                      # no key needed
curl -H 'X-Api-Key: trk_dev_admin_ana' http://localhost:5284/api/teams
curl -H 'X-Api-Key: trk_dev_member_dana' http://localhost:5284/api/teams  # only DES
curl -H 'X-Api-Key: trk_dev_admin_ana' \
  "http://localhost:5284/api/issues?q=rate%20limiting"
```

These keys are hard-coded precisely because the seeder only runs in
`Development`. A real deployment mints its first admin key out of band and every
one after that through the API.

## Authentication & authorization

Present a key as `X-Api-Key: trk_…`. Everything except `/api/health` requires
one — that is the *default*, not a per-controller decision: the authorization
fallback policy denies any endpoint that does not explicitly opt out, so a new
controller is protected the moment it exists rather than the moment somebody
remembers `[Authorize]`.

Two questions decide every request, and they are deliberately separate:

- **Role** — `Admin` or `Member`, a property of the *user*. Admins administer
  the workspace: teams, users, keys.
- **Membership** — which teams a user is on, a property of the *pair*. This is
  the whole of a member's reach; an admin sees every team without a membership.

Team-level configuration (workflow, labels, projects, cycles, issues) is done by
a team's own members. A workspace where only admins can add a label is a
workspace where admins are a ticket queue.

### What a denial looks like

| Situation | Status | Why |
|---|---|---|
| No key, unknown key, revoked key | `401` | |
| Right key, wrong role | `403` | You can see the thing; you lack the role. Saying so discloses nothing |
| Right key, someone else's team | `404` | |
| Right key, someone else's comment | `403` | You can already read it — a 404 would just be confusing |

**A foreign team is a 404, not a 403.** A 403 would confirm that the id names a
real team, and that difference is enough to map a workspace without reading a
single row: probe ids, keep the ones that answer differently. So existence and
permission collapse into one answer — a team you are not on is reported exactly
like a team that never existed. The cost is that a member who fat-fingers their
own team id is told "not found" rather than "not allowed", which is the right
trade and is why the rule lives in one place (`TeamAccess`) rather than being
re-decided per controller.

The same rule explains an apparent inconsistency in the key routes:
`/api/users/{userId}/api-keys` answers `403` because it checks permission
*before* looking anything up — a member gets the same answer for any id but
their own, real or not, so nothing leaks. `/api/api-keys/{id}` answers `404`,
because there a `403` would only ever be reachable for a key that exists.

Tokens are stored as SHA-256 hashes and shown exactly once, at creation. SHA-256
rather than bcrypt on purpose: slow hashes defend *low-entropy human-chosen*
secrets against brute force, and a minted token carries 192 bits from a CSPRNG.
There is no keyspace to search, so a work factor buys nothing — while costing an
expensive hash on every authenticated request, which is itself a DoS lever.

## Endpoints

### Health

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/health` | Liveness probe — the only anonymous endpoint |

### Identity

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/me` | Who you are and which teams your key reaches |

### Users & keys

Admin-only, except where noted.

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/users` | List users |
| `POST` | `/api/users` | Create a user |
| `GET` | `/api/users/{id}` | Get a user |
| `PUT` | `/api/users/{id}` | Rename a user or change their role; refuses (409) to remove the last admin |
| `DELETE` | `/api/users/{id}` | Delete a user; refuses (409) to remove the last admin |
| `PUT` | `/api/users/{userId}/teams/{teamId}` | Put a user on a team (idempotent) |
| `DELETE` | `/api/users/{userId}/teams/{teamId}` | Take a user off a team |
| `GET` | `/api/users/{userId}/api-keys` | List a user's keys — **self or admin** |
| `POST` | `/api/users/{userId}/api-keys` | Mint a key; the only response carrying a raw token — **self or admin** |
| `GET` | `/api/api-keys/{id}` | Get a key's metadata — **owner or admin** |
| `DELETE` | `/api/api-keys/{id}` | Revoke a key (idempotent); the row survives for the audit trail — **owner or admin** |

### Teams

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/teams` | List the teams you can see (all of them, for an admin) |
| `POST` | `/api/teams` | **Admin.** Create a team; seeds it with the default five-state workflow |
| `GET` | `/api/teams/{id}` | Get a team |
| `GET` | `/api/teams/{teamId}/members` | The team's roster |
| `PUT` | `/api/teams/{id}` | **Admin.** Rename a team or change its key |
| `DELETE` | `/api/teams/{id}` | **Admin.** Delete a team and everything it owns |

### Projects

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/teams/{teamId}/projects` | List a team's projects |
| `POST` | `/api/teams/{teamId}/projects` | Create a project |
| `GET` | `/api/projects/{id}` | Get a project |
| `PUT` | `/api/projects/{id}` | Update a project |
| `DELETE` | `/api/projects/{id}` | Delete a project; its issues survive, unassigned |

### Workflow states

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/teams/{teamId}/states` | List a team's workflow, in order |
| `POST` | `/api/teams/{teamId}/states` | Add a state at a position |
| `GET` | `/api/states/{id}` | Get a state |
| `PUT` | `/api/states/{id}` | Rename, recolor, or reorder a state |
| `DELETE` | `/api/states/{id}` | Delete a state; refused (409) if it holds issues or is the team's last |

### Issues

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/issues` | **Search** across teams — see [Search](#search) |
| `GET` | `/api/teams/{teamId}/issues` | List a team's issues in board order |
| `POST` | `/api/teams/{teamId}/issues` | Create an issue; auto-numbered (`ENG-42`), appended to its column |
| `GET` | `/api/issues/{id}` | Get an issue |
| `PUT` | `/api/issues/{id}` | Update an issue's fields, project, cycle, and assignee |
| `POST` | `/api/issues/{id}/transitions` | Move to another state; validated, appends to the target column |
| `POST` | `/api/issues/{id}/reorder` | Reposition within a column, or move columns — see [Ordering](#ordering) |
| `GET` | `/api/issues/{id}/children` | Sub-issues plus a progress roll-up — see [Sub-issues](#sub-issues) |
| `DELETE` | `/api/issues/{id}` | Delete an issue; its children survive, un-nested |

### Relations

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/issues/{issueId}/relations` | Links involving this issue, named from its point of view |
| `POST` | `/api/issues/{issueId}/relations` | Link two issues — see [Relations](#relations-1) |
| `DELETE` | `/api/issues/{issueId}/relations/{relationId}` | Cut a link; reachable from either end |

### Labels

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/teams/{teamId}/labels` | List a team's labels |
| `POST` | `/api/teams/{teamId}/labels` | Create a label |
| `GET` | `/api/labels/{id}` | Get a label |
| `PUT` | `/api/labels/{id}` | Update a label |
| `DELETE` | `/api/labels/{id}` | Delete a label |
| `PUT` | `/api/issues/{issueId}/labels/{labelId}` | Attach a label to an issue (idempotent) |
| `DELETE` | `/api/issues/{issueId}/labels/{labelId}` | Detach a label from an issue |

### Comments

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/issues/{issueId}/comments` | List an issue's comments, oldest first |
| `POST` | `/api/issues/{issueId}/comments` | Comment on an issue; the author is your credential, not a field |
| `GET` | `/api/comments/{id}` | Get a comment |
| `PUT` | `/api/comments/{id}` | Edit a comment's body — **author or admin** |
| `DELETE` | `/api/comments/{id}` | Delete a comment — **author or admin** |

A comment's `author` is taken from the authenticated key. It used to be a field
on the request, which meant anyone could post as anyone — before there was a
caller to ask, the API had no way to know better. The field is now *gone* from
the contract rather than ignored: silently overwriting it would be worse, since
the client would be told its impersonation had worked.

### Cycles

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/teams/{teamId}/cycles` | List a team's cycles; `?status=Upcoming\|Active\|Completed` |
| `POST` | `/api/teams/{teamId}/cycles` | Create a cycle; auto-numbered per team |
| `GET` | `/api/cycles/{id}` | Get a cycle |
| `PUT` | `/api/cycles/{id}` | Move or rename a cycle |
| `DELETE` | `/api/cycles/{id}` | Delete a cycle; its issues survive, unscheduled |
| `GET` | `/api/cycles/{id}/summary` | Progress roll-up — see [Cycles](#cycles-1) |

## Relations

Issues link to each other five ways — `Relates`, `Blocks`, `BlockedBy`,
`Duplicates`, `DuplicatedBy` — but only **three are stored**. "Blocked by" is not
a fourth kind of link; it is the same link seen from the other end.

```bash
# these two calls produce the identical row
curl -X POST .../api/issues/$BLOCKER/relations -d '{"kind":"Blocks","issueId":"'$BLOCKED'"}'
curl -X POST .../api/issues/$BLOCKED/relations -d '{"kind":"BlockedBy","issueId":"'$BLOCKER'"}'
```

**One fact, one row.** The alternative — a row per direction — makes every link
two writes that can disagree: delete one side, or fail between them, and the
graph claims A blocks B while B is blocked by nothing. So a request is
canonicalized to a single stored row and the inverse is derived on read. `kind`
in a response is therefore *relative to the issue you asked about*: the same row
reads `Blocks` to one end and `BlockedBy` to the other, and carries the same
relation `id` both times.

**Symmetry needs normalizing, direction must not be.** `Relates` has no
direction, so "A relates to B" and "B relates to A" are one sentence — but stored
as-is they are two different tuples, and the unique index that enforces "one
fact, one row" compares tuples, not meanings. Symmetric relations therefore get
their endpoints put in a fixed order first (which order is arbitrary; that it is
*stable* is the point). Directed relations keep their direction: "A blocks B" and
"B blocks A" are contradictory claims, not one fact said twice — and collapsing
them would destroy the cycle check that exists to catch exactly that pair.

That check-plus-index pairing is deliberate. The check makes the ordinary
duplicate a clean `409` without an exception; the index is what is actually
*true*, because two concurrent requests both pass a check before either writes.
The loser of that race is translated back into the same `409` rather than a 500.

| Guard | Status |
|---|---|
| Relating an issue to itself | `422` |
| The same link twice (including the mirror of a symmetric one) | `409` |
| A blocking or duplication **cycle**, at any depth | `422` |
| An issue in another team, or one that doesn't exist | `400` |

A blocking cycle is a deadlock stated as data — A waits on B, B waits on A, and
no sequence of work finishes either — so it is refused at any depth. `Relates`
cycles are fine: with no direction there is nothing to deadlock. Fan-out and
diamonds (A blocks B and C, both block D) are not cycles and are allowed.

**Links stay inside a team**, because the team is the boundary authorization is
drawn on. A cross-team link would either leak the far issue's title into a
response the caller can't see, or need per-row redaction on every read — so it is
a `400`, the same answer the API already gives for another team's label or state.

## Sub-issues

An issue may have a `parentId`, forming a hierarchy of any depth. Set it on
create or update; as with `projectId` and `cycleId`, a `PUT` that omits it
un-nests the issue.

`GET /api/issues/{id}/children` returns the sub-issues and a roll-up:

```json
{
  "rollup": {
    "totalIssues": 2, "scopeIssues": 1, "completedIssues": 1,
    "canceledIssues": 1, "scopeEstimate": 2, "progressPercent": 100
  },
  "items": [ ... ]
}
```

Canceled children leave the scope but stay reported — the same rule the cycle
roll-up applies, because a product that answers "how far along is this?" two
different ways in two places is worse than either answer.

The roll-up lives on this endpoint rather than on every `IssueDto` so that
listing a board stays one query: hanging it off each issue would mean counting
children per row to answer a question only an issue's own page asks.

Two guards:

- **A parent chain cannot loop.** Not merely untidy — a loop hangs every "walk up
  to the root" the product does. Nothing in a self-referencing foreign key
  prevents it, so `422`.
- **A parent must be in the same team** (`400`), for the same reason relations are.

Deleting a parent **releases** its children rather than deleting them — the call
this product already makes for a deleted project or cycle. "I deleted the
umbrella ticket" must not silently mean "I deleted the six tickets under it".

## Search

`GET /api/issues` — every filter is optional and they combine with AND.

| Parameter | Type | Notes |
|---|---|---|
| `teamId` | guid | |
| `projectId` | guid | |
| `stateId` | guid | |
| `cycleId` | guid | |
| `labelId` | guid | Issues carrying that label |
| `assignee` | string | Exact handle, compared case-insensitively |
| `priority` | `None`/`Urgent`/`High`/`Medium`/`Low` | |
| `q` | string | Substring of title or description; LIKE wildcards are treated literally |
| `sort` | `Updated`/`Created`/`Priority`/`Number`/`Title`/`Position` | Default `Updated` |
| `order` | `Asc`/`Desc` | Default `Desc` |
| `page` | int ≥ 1 | Default 1 |
| `pageSize` | int 1–100 | Default 25 |

Returns `{ items, page, pageSize, total, totalPages }`.

Two details worth knowing:

- **`sort=Priority` ranks by urgency, not by enum value.** `None` means *no
  priority set*, which is an absence rather than the lowest urgency, so it sorts
  last ascending: `Urgent, High, Medium, Low, None`.
- **Ties break by id.** Without a deterministic tiebreak, paging a result set
  whose rows share a sort key silently repeats and skips rows between pages.

```bash
curl "http://localhost:5284/api/issues?assignee=ana&priority=High&sort=Created&order=Asc"
```

## Ordering

Issues carry a fractional `position` — a rank, not a row number. A move is
computed as the midpoint between the issue's two new neighbours, so it rewrites
exactly one row instead of renumbering the column.

```bash
# put issue C between A and B
curl -X POST http://localhost:5284/api/issues/$C/reorder \
  -H 'Content-Type: application/json' \
  -d '{"afterIssueId":"'$A'","beforeIssueId":"'$B'"}'
```

`stateId` moves the issue to another column; omit both neighbours to append to
the end. Two rules fall out of the design:

- **Reordering across columns is a state change**, so it is validated by the same
  state machine as an explicit transition. Dragging a card must not become a back
  door around the workflow.
- **Midpoints run out.** Each split halves the gap, so once neighbours get closer
  than `IssueRanker.MinGap` the column is renumbered into even steps — in the
  same transaction as the move — rather than letting two ranks collide.

## Cycles

A cycle is the half-open interval `[startsAt, endsAt)`: the start instant belongs
to the cycle, the end instant is the first that does not. That lets consecutive
cycles share a boundary without overlapping and without leaving a gap. Cycles
within a team may not overlap (409), and must end after they start (422).

`status` is always derived from the dates (`Upcoming`/`Active`/`Completed`) and
never stored, so a cycle cannot drift out of sync with the calendar.

`GET /api/cycles/{id}/summary` rolls up progress. Canceled issues are dropped
from the scope — work that was called off shouldn't count against a team's
completion rate — but are still reported separately:

```json
{
  "number": 1, "status": "Active",
  "totalIssues": 3, "scopeIssues": 3, "completedIssues": 1,
  "inProgressIssues": 1, "canceledIssues": 0,
  "scopeEstimate": 8, "completedEstimate": 1, "progressPercent": 33.3
}
```

## Workflow rules

States are per-team and fully customizable, but each one has a *category*
(`Backlog`, `Todo`, `InProgress`, `Done`, `Canceled`) and transitions are
validated between categories, so a team can rename and reorder its workflow
without inventing new rules. Moves within a category are always allowed.

| From | May move to |
|---|---|
| `Backlog` | `Todo`, `InProgress`, `Canceled` |
| `Todo` | `Backlog`, `InProgress`, `Canceled` |
| `InProgress` | `Todo`, `Done`, `Canceled` |
| `Done` | `Todo`, `InProgress` (reopen) |
| `Canceled` | `Backlog`, `Todo` (reopen) |

Notably forbidden: skipping straight from `Backlog`/`Todo` to `Done`, canceling
an already-`Done` issue, and resurrecting a `Canceled` issue directly into
`InProgress` or `Done`.

## Errors

Every failure — from a controller, from model validation, or from routing before
a controller ever runs — is [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807)
`application/problem+json`, so a client only ever parses one error shape.

| Status | Means |
|---|---|
| `400` | The request is malformed, or points at something that isn't the caller's to point at (another team's state, a label from another team) |
| `401` | No key, an unknown key, or a revoked one |
| `403` | Authenticated, but this needs a role you don't hold — or it isn't yours to edit |
| `404` | The addressed resource does not exist, **or belongs to a team you're not on** |
| `409` | The request is well-formed but collides with existing data (a duplicate team key, an overlapping cycle) |
| `422` | The request is well-formed and coherent, but a domain rule forbids the outcome (an illegal transition, a backwards date range) |

```json
{
  "type": "https://tools.ietf.org/html/rfc4918#section-11.2",
  "title": "Invalid state transition.",
  "status": 422,
  "detail": "Cannot move an issue from 'Backlog' (Backlog) to 'Done' (Done).",
  "traceId": "00-482031f9e1173765b2f925a2ccc1509b-e9a71d739ab27c1f-00"
}
```

## Notes on the data model

SQLite has no native `DateTimeOffset`, and storing one as text makes SQL
ordering and comparison compare *wall-clock strings* rather than instants — so
`09:00+09:00` sorts after `10:00-05:00` despite being six hours earlier. Every
`DateTimeOffset` is therefore persisted as UTC ticks through a value converter
(`TracerDbContext.ConfigureConventions`), which keeps ordering, filtering, and
cycle-status checks correct in the database rather than only in memory.
