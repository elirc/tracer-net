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
| `GET` | `/api/health` | Liveness **and** readiness probe — the only anonymous endpoint |

The probe actually touches the database rather than returning `200` the moment
the process is up: a host that has lost its database is not healthy, so the check
returns `503` when the probe fails and an orchestrator can pull the instance from
rotation. The body is structured for exactly that consumer:

```json
{
  "status": "ok",
  "name": "tracer-net",
  "version": "2.0.0",
  "utcNow": "2026-07-17T16:15:00.123+00:00",
  "database": { "healthy": true, "durationMs": 0.8 }
}
```

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
| `PUT` | `/api/issues/{id}` | Update an issue's fields, project, cycle, and assignee; pass the `version` you read for optimistic concurrency (409 on a stale write) |
| `POST` | `/api/issues/{id}/transitions` | Move to another state; validated, appends to the target column |
| `POST` | `/api/issues/{id}/reorder` | Reposition within a column, or move columns — see [Ordering](#ordering) |
| `GET` | `/api/issues/{id}/children` | Sub-issues plus a progress roll-up — see [Sub-issues](#sub-issues) |
| `GET` | `/api/issues/{id}/activity` | This issue's timeline — see [Activity](#activity) |
| `GET` | `/api/issues/{id}/subscription` | Whether you're watching it, and why |
| `PUT` | `/api/issues/{id}/subscription` | Watch it (idempotent) |
| `DELETE` | `/api/issues/{id}/subscription` | Stop watching |
| `GET` | `/api/issues/{id}/subscribers` | Everyone watching it, and why |
| `DELETE` | `/api/issues/{id}` | Delete an issue; its children survive, un-nested |

### Activity

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/issues/{issueId}/activity` | One issue's history, newest first |
| `GET` | `/api/teams/{teamId}/activity` | The team's feed; filterable — see [Activity](#activity) |

### Webhooks

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/teams/{teamId}/webhooks` | List a team's webhooks |
| `POST` | `/api/teams/{teamId}/webhooks` | Subscribe; the only response carrying the signing secret |
| `GET` | `/api/webhooks/{id}` | Get a webhook (never the secret) |
| `PUT` | `/api/webhooks/{id}` | Change its URL, events, or active flag |
| `POST` | `/api/webhooks/{id}/rotate-secret` | Replace the secret; the old one stops working immediately |
| `DELETE` | `/api/webhooks/{id}` | Delete a webhook and its delivery log |
| `GET` | `/api/webhooks/{id}/deliveries` | Delivery log; `?status=Pending\|Delivered\|Failed` |
| `GET` | `/api/webhooks/{id}/deliveries/{deliveryId}` | One delivery, with the exact bytes that were sent |
| `POST` | `/api/webhooks/{id}/deliveries/{deliveryId}/redeliver` | Queue a finished delivery to be tried again |

### Notifications

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/notifications` | Your inbox, newest first; `?unread=true` |
| `GET` | `/api/notifications/unread-count` | The badge |
| `POST` | `/api/notifications/{id}/read` | Mark one read |
| `POST` | `/api/notifications/{id}/unread` | Mark one unread again |
| `POST` | `/api/notifications/read-all` | Clear the badge |

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

### Saved views

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/teams/{teamId}/views` | The team's shared views, plus your own personal ones |
| `POST` | `/api/teams/{teamId}/views` | Create a view |
| `GET` | `/api/teams/{teamId}/views/default` | The team's default view (404 if it has none) |
| `GET` | `/api/views/{id}` | Get a view |
| `PUT` | `/api/views/{id}` | Rename a view, replace its rules, change its scope, or make it the default |
| `DELETE` | `/api/views/{id}` | Delete a view |
| `GET` | `/api/views/{id}/issues` | **Run** the view; `?page=&pageSize=` — see [Saved views](#saved-views-1) |

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

## Activity

Every issue mutation writes an immutable record: creation, field edits, state
changes, assignment, labels, relations, re-parenting, comments, deletion. Each
carries the actor and the before/after values.

```bash
curl -H "$KEY" .../api/issues/$ID/activity
curl -H "$KEY" ".../api/teams/$TEAM/activity?type=IssueStateChanged&actor=ben&since=2026-07-01T00:00:00Z"
```

| Filter | Notes |
|---|---|
| `issueId` | Including an issue since deleted |
| `type` | `IssueCreated`, `IssueUpdated`, `IssueStateChanged`, `IssueAssigned`, `IssueLabelAdded`/`Removed`, `IssueRelationAdded`/`Removed`, `IssueParentChanged`, `IssueDeleted`, `CommentCreated`/`Updated`/`Deleted` |
| `actor` | Handle, compared case-insensitively |
| `since` / `until` | Half-open `[since, until)`, as cycles are, so windows tile without double-counting |
| `page` / `pageSize` | Default 1 / 50, max 100 |

**Read-only, and not by omission.** There is no route to write, edit, or delete
an entry. A log with a `DELETE` endpoint answers "what happened, unless someone
preferred otherwise", which is not the question.

**The log deliberately breaks the rules the rest of the schema follows.**
`Activity.IssueId` and `ActorId` are plain Guids with *no foreign key*. A foreign
key would have to cascade — and then deleting an issue would erase the record of
who deleted it, which is the single question an audit log exists to answer. The
log outlives the rows it describes; that is what makes it a log and not a join
table. Because those rows can vanish, everything needed to render an entry is
copied in at write time: the issue title and actor handle are denormalized on
purpose, since a feed that renders "(deleted issue) was deleted by (deleted
user)" is not a feed.

`TeamId` is the exception that proves the rule: it *does* cascade. Deleting a
team is an admin nuking a whole workspace, feed included — and as the feed is
only ever read per team, orphaned rows would be unreachable forever while still
taking space. Keeping the team also means `ENG-42` is rendered from the live team
key, so a renamed team renames its history, exactly as identifiers behave
everywhere else here.

**Records commit in the same transaction as the change.** `ActivityRecorder`
never calls `SaveChanges`; it adds to the DbContext the mutation is already
sitting in. A separate save, a queue, or an outbox drain would each buy a window
where the change lands and the record doesn't. An audit log that disagrees with
the data is worse than no audit log, because it is trusted.

**One save is one instant.** Entries from a single request share one timestamp
rather than reading the clock per row — editing a title and a priority together
did not happen twice, microseconds apart. That makes the id tiebreak on both
feeds load-bearing: those entries collide exactly, and paging an unstable order
silently repeats and skips rows.

**Events, not column diffs.** Recording is an explicit call per controller rather
than a `SaveChanges` interceptor. An interceptor would be impossible to forget,
which is tempting — but it sees columns, not intent: it can't tell a re-parent
from an assignment, can't name the actor without ambient request state, and would
report a rank rebalance (a move of *other people's* cards) as a dozen edits
nobody made. The trade is real — this can be forgotten — and the tests are where
that's caught. Two consequences worth knowing:

- **A no-op writes nothing.** Re-saving an issue unchanged, or re-attaching a
  label it already has, records nothing. A form re-sends every field on every
  save; if that wrote history, one edit would bury the feed.
- **Big text isn't copied.** A description change records *that* it changed, not
  two copies of it; comment bodies are excerpted. An append-only table holding
  every version of every 10k-character body stops being an audit log and becomes
  a second, worse copy of the comments table.

## Webhooks

A team subscribes an endpoint to `issue.created`, `issue.updated`,
`issue.state_changed`, or `comment.created`. Events come from the same activity
spine the feed does — `ActivityRecorder` is the only thing that queues them, so
there is no way to record a change without announcing it, and no way to announce
something that isn't a recorded change.

```json
{
  "id": "3f2a…",                     // the activity id: dedupe on this
  "event": "issue.state_changed",
  "createdAt": "2026-07-17T13:26:59Z",
  "actor": "ana",
  "team": { "id": "…", "key": "ENG" },
  "issue": { "id": "…", "identifier": "ENG-42", "title": "…", "state": "Todo",
             "priority": "High", "assignee": "ben" },
  "change": { "type": "IssueStateChanged", "field": null, "from": "Backlog", "to": "Todo" }
}
```

| Header | Meaning |
|---|---|
| `X-Tracer-Event` | `issue.state_changed` |
| `X-Tracer-Delivery` | This envelope. Differs per subscriber and per redelivery |
| `X-Tracer-Attempt` | 1, 2, 3… |
| `X-Tracer-Signature` | `t=<unix>,v1=<hmac-sha256 hex>` |

**Deliveries are at-least-once.** That's a promise to send, not to send once. The
body's `id` is the *activity* id — stable across retries, identical for every
subscriber — so it's what you deduplicate on. The delivery id is the envelope and
is deliberately not in the body: if it were, two teams subscribed to one event
would need two payloads and two signatures for the same fact.

### The outbox

The delivery row is written **in the same transaction as the change**, then sent
afterwards by a background worker. That order is the whole design:

- Sending *inside* the request would hold a database transaction open across a
  stranger's network, make every write as slow as the slowest subscriber, and
  leave a retry nowhere to live — a second attempt can't outlive the process that
  died during the first.
- Sending with *no row* loses the event entirely if the process dies.

With a row, a delivery that hasn't succeeded is simply still due, including
across a restart. The worker polls rather than being poked, because deliveries
queued before a crash still need draining on the way back up and nothing is going
to poke anyone about them.

**This assumes one instance.** Two workers would both claim the same due rows and
send them twice — the claim isn't atomic. At-least-once means that's not a
correctness bug, but it's a real limit, and making it safe needs an atomic
conditional claim or a row lock.

### Signing

The signed material is `{timestamp}.{payload}`, and the timestamp travels *inside*
the signed header. Signing the body alone yields a signature valid forever:
capture one request and replay it, perfectly signed, next year. A receiver
rejects anything outside its tolerance — and because the timestamp is signed, an
attacker can't just refresh it. The scheme is Stripe's on purpose; verification
is written by people not thinking about webhooks, and a familiar `t=…,v1=…` is
one they may already have code for. Compare in constant time.

A webhook secret is stored **in the clear**, unlike an API key — and that
difference is forced, not chosen. An API key is only ever *compared*, so a hash
suffices; this one must *sign*, and a hash cannot sign. Since storage can't
protect it, exposure is limited instead: it's returned when created or rotated,
and never echoed by a read.

### Retry and failure classification

| Response | Class | Retried? |
|---|---|---|
| 2xx | — | Delivered |
| 5xx, timeout, refused connection, DNS failure | `Transient` | Yes |
| 429, 408 | `Transient` | Yes — "busy", not "no" |
| 4xx | `Permanent` | No |
| 3xx | `Permanent` | No — redirects are not followed |

Backoff is exponential (10s, 20s, 40s, 80s; 5 attempts). Exponential rather than
fixed because an endpoint failing under load doesn't need our retries arriving at
a constant rate on top of it. And the transient/permanent split is the point:
retrying a 404 a thousand times won't conjure the route into existence — it just
turns one team's typo into load everyone pays for, and buries the real failures.
Redirects aren't followed because a signed payload must not be forwarded to a
host the team never registered.

### SSRF

A webhook URL is attacker-supplied input that **this server then fetches**, from
inside the network. `http://169.254.169.254/…` is where cloud providers serve
instance credentials; an internal admin service is a request nobody at the edge
ever sees — and the delivery log obligingly reports the response back. So URLs
must be `http`/`https` and must not resolve to loopback, private, link-local, or
carrier-grade-NAT addresses.

**The known gap, stated plainly:** a hostname is only checked when it's a literal
IP. `evil.example.com` resolving to `127.0.0.1` passes, because the answer depends
on DNS at request time, not check time — and those can differ (DNS rebinding).
Closing it properly means resolving at send time and pinning the connection to
the address that was checked, via a custom `SocketsHttpHandler.ConnectCallback`.
There's a test asserting this gap exists, so nobody reads the policy and assumes
otherwise.

## Notifications

Watch an issue and its notable changes land in your inbox. You are auto-subscribed
when you create an issue (`Author`), comment on it (`Commenter`), or are assigned
it (`Assignee`), and can watch or unwatch anything you can see (`Manual`).
Subscribers are *accounts*, not assignee strings — assignment resolves the handle
to a user and quietly does nothing when no account owns it, because you cannot
route an inbox item to a name that is only a label.

Notifications fan out from the **same activity spine** the feed and webhooks use:
`ActivityRecorder` queues them in the transaction that records the change, so a
committed change never leaves its watchers untold and a notification never exists
for a change that rolled back.

Two rules shape what lands:

- **You are never notified of your own action.** The one thing you reliably
  already know is the thing you just did. The actor is excluded from fan-out — so
  the assignee just added a moment ago (and every other watcher) hears about the
  assignment, but the person who *made* it does not.
- **The inbox is curated; the audit log is exhaustive.** Only notable changes
  notify — comments, state changes, assignment, a new relation, a re-parenting, a
  deletion. A title or estimate edit is in the feed but is not a reason to
  interrupt anyone. This is the same split `WebhookEvents` makes: the internal
  record and the thing you push at people are different sizes on purpose, because
  an inbox that pings on "estimate 3 → 5" is one people stop reading.

An inbox item points at the activity rather than copying it, so it renders
exactly as a feed row and survives the deletion of its issue just as the audit
entry does. `read` is a timestamp, not a flag, so "how long did this sit unseen"
stays answerable; `read-all` is one set-based update, not a load-everything loop.

One honest limitation: unwatching removes the row, and a later comment or
assignment will auto-subscribe you again — auto-subscribe cannot tell "never
wanted this" from "not added yet", since both are the absence of a row. A durable
mute would need a row that says *no*, which is a deliberate feature rather than
something to smuggle in.

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

## Saved views

A view is a **named filter set**, stored as rules and run on demand. Its rules
are exactly the filter half of [Search](#search) — every filter above except
`teamId`, `page`, and `pageSize` — and running one goes through the same query
builder, so a view cannot escape wildcards differently or order ties differently
than search does. It is not a second implementation.

```bash
curl -X POST http://localhost:5284/api/teams/$TEAM/views \
  -H 'X-Api-Key: trk_dev_member_ben' -H 'Content-Type: application/json' \
  -d '{"name":"My urgent bugs","scope":"Personal",
       "rules":{"assignee":"ben","priority":"Urgent","sort":"Created","order":"Asc"}}'

curl "http://localhost:5284/api/views/$VIEW/issues?page=1&pageSize=25" \
  -H 'X-Api-Key: trk_dev_member_ben'
```

Three rules define the shape:

- **The rules cannot name a team.** A view already belongs to one, so a `teamId`
  rule would give "whose issues does this show?" two answers that can disagree —
  and a way to aim a view on your team at someone else's. The field simply isn't
  in the rule set, so running a view is always scoped to its own team.
- **Paging is a property of the request, not the view.** The same view is page 1
  for one caller and page 3 for the next, so `page`/`pageSize` are supplied when
  it runs.
- **A rule that points at another team's project, state, cycle, or label is
  rejected** (`400`) when the view is saved. Stored as-is, such a view would
  match nothing forever, and the caller would be told it had worked.

| Scope | Owner | Who sees it |
|---|---|---|
| `Team` | none — the team's | anyone who can reach the team |
| `Personal` | you | you, and nobody else |

A team view has **no owner**: it is team property, so it survives its creator's
account being deleted rather than being cascade-deleted out from under the team.
A personal view is the opposite — it belongs to one person and goes when they do.
Personal really does mean personal: an admin reaches every team, but a workspace
role is a licence to administer the workspace, not to read over someone's
shoulder, so another user's personal view is `404` even for an admin.

Each team may have **one default view** — the one to land on when nobody has
chosen. Promoting a view demotes the outgoing default in the same transaction,
and the invariant is held by a filtered unique index rather than by the handler
remembering to. Only a team view may be the default (`422` otherwise): the
default is what everyone sees, and a personal view is visible to exactly one
person.

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
| `409` | The request is well-formed but collides with existing data (a duplicate team key, an overlapping cycle) **or the row it edits changed underneath it** (optimistic-concurrency conflict) |
| `422` | The request is well-formed and coherent, but a domain rule forbids the outcome (an illegal transition, a backwards date range) |
| `429` | Too many write requests in a short window — see [Rate limiting](#rate-limiting) |

```json
{
  "type": "https://tools.ietf.org/html/rfc4918#section-11.2",
  "title": "Invalid state transition.",
  "status": 422,
  "detail": "Cannot move an issue from 'Backlog' (Backlog) to 'Done' (Done).",
  "traceId": "00-482031f9e1173765b2f925a2ccc1509b-e9a71d739ab27c1f-00"
}
```

## Pagination

Every list endpoint that grows with usage returns the same envelope and takes the
same two query parameters, so no single request can ask the database for an
unbounded number of rows:

```json
{ "items": [ ... ], "page": 1, "pageSize": 50, "total": 137, "totalPages": 3 }
```

| Parameter | Type | Notes |
|---|---|---|
| `page` | int ≥ 1 | Default 1 |
| `pageSize` | int 1–100 | Default 50 (25 for `GET /api/issues`, whose paging predates this) |

The paged endpoints are the issue search and board, activity feeds, notifications,
webhook deliveries, saved-view execution, and the team/project/label/comment/
relation/subscriber/webhook/saved-view/user/api-key lists. A handful of reads are
deliberately **not** paged because they are bounded by the domain rather than by
usage — a team's workflow states (its board columns), its cycles and milestones
(whose derived status is filtered in memory), and an issue's direct children
(whose roll-up is over the whole set). Each says so at its call site.

## Concurrency

An issue carries a `version` token that rotates on every update. It is an EF
concurrency token, so a write is `UPDATE … WHERE Version = <the value that was
read>`: two edits that raced both loaded the same version, the first wins, and the
second updates zero rows and comes back as a `409` instead of silently clobbering
a change nobody saw. Read the `version` on an issue and send it back on `PUT
/api/issues/{id}` to hold your update to it; omit it and you are still protected
against a concurrent in-flight edit, because the token that was read is checked
either way.

## Rate limiting

Write requests draw from a per-credential fixed window; reads are never throttled.
A burst of `GET`s is an eager client, but a burst of `POST`/`PUT`/`DELETE`s is a
runaway script or retry storm, and each write costs a transaction, an audit entry,
and — through the activity spine — webhook deliveries and notifications. Over the
limit is a `429` `problem+json` with a `Retry-After` header. The limit is a fixed
window read from configuration (`RateLimiting:PermitLimit`, `RateLimiting:WindowSeconds`;
120 per 60s by default), partitioned by API key so one team's integration cannot
spend everyone else's budget.

## Request logging

Every request logs one structured line — method, path, status, elapsed
milliseconds — from the very front of the pipeline, so the timing covers auth,
model binding, the handler, and the error middleware, and the line is still
written when a request throws.

## Notes on the data model

SQLite has no native `DateTimeOffset`, and storing one as text makes SQL
ordering and comparison compare *wall-clock strings* rather than instants — so
`09:00+09:00` sorts after `10:00-05:00` despite being six hours earlier. Every
`DateTimeOffset` is therefore persisted as UTC ticks through a value converter
(`TracerDbContext.ConfigureConventions`), which keeps ordering, filtering, and
cycle-status checks correct in the database rather than only in memory.
