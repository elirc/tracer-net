# Architecture

tracer-net is a Linear-style issue tracker with a C# / .NET 10 backend. This
document explains how the pieces fit together and why the load-bearing decisions
were made the way they were. Each decision also has a short [ADR](adr/README.md).

## Layering

Three projects, dependencies pointing inward:

```
Tracer.Api  ─────▶  Tracer.Infrastructure  ─────▶  Tracer.Domain
(HTTP, DI, DTOs)     (EF Core, migrations)          (entities, pure rules)
```

| Project | Holds | Depends on |
|---|---|---|
| `Tracer.Domain` | Entities and **pure** business rules — no EF, no HTTP. `IssueStateMachine`, `IssueRanker`, `IssueGraph`, `IssueRelations`, `CycleSchedule`, `SavedViewRules`, `NotificationPolicy`, `WebhookEvents`, `WebhookRetryPolicy`, `WebhookSignature`, `WebhookUrlPolicy`, `MetricMath`, `BurndownChart`, `IssueLifecycle(s)`, `MilestoneRoadmap`, `ApiKeyToken`. | nothing |
| `Tracer.Infrastructure` | `TracerDbContext`, the migrations, the `DateTimeOffset` value converters, `DefaultWorkflow`, and `DbSeeder`. | Domain |
| `Tracer.Api` | Controllers, DTOs (`Contracts/`), API-key auth, the search engine, the webhook outbox/sender/worker, notification fan-out, request-logging middleware, and the rate limiter. | Infrastructure, Domain |

The domain rules are pure so they can be tested without a database — the state
machine, the ranker, the cycle schedule, and the metric math are all just
functions over values, and the unit suite exercises them directly.

## The activity spine

The single most important structure in the system. **Every issue mutation writes
an immutable `Activity` record, in the same transaction as the change**, through
`ActivityRecorder`. That recorder never calls `SaveChanges` itself — it adds to
the `DbContext` the mutation is already sitting in, so the record and the change
commit together or not at all.

Three features hang off that one spine, and all three are populated by the
recorder inside the same transaction:

```
        issue mutation (controller)
                 │
                 ▼
        ActivityRecorder.RecordAsync
                 │
      ┌──────────┼───────────────────────┐
      ▼          ▼                        ▼
  Activity   WebhookDelivery rows    Notification fan-out
  (audit)    (transactional outbox)  (inbox, actor-excluded)
```

- **Audit log** — the `Activity` row itself, read back by the activity feed.
- **Webhooks** — `ActivityRecorder` is the only thing that enqueues webhook
  deliveries, so there is no way to record a change without announcing it and no
  way to announce something that was never recorded.
- **Notifications** — fan-out to an issue's subscribers happens off the same
  call, so a committed change never leaves its watchers untold and a notification
  never exists for a change that rolled back.

Metrics are derived from the same spine after the fact: an issue's start and
completion instants are reconstructed from its `IssueStateChanged` entries
(`IssueLifecycle`), because nothing else stores "when did this reach Done".

The log deliberately breaks the schema's usual rules: `Activity.IssueId` and
`ActorId` are plain `Guid`s with **no foreign key**, so deleting an issue or a
user cannot cascade away the record of what happened to it — which is the one
thing an audit log has to survive. Everything needed to render an entry (issue
title, actor handle) is denormalized at write time. `TeamId` is the exception
that does cascade: deleting a team is a deliberate workspace wipe, and a
per-team feed's orphaned rows would be unreachable forever.

## Transactional outbox (webhooks)

A `WebhookDelivery` row is written **in the same transaction as the change**,
then sent afterwards by a background worker (`WebhookDeliveryWorker`, draining
`WebhookSender.DeliverDueAsync`). That ordering is the whole design:

- Sending *inside* the request would hold a database transaction open across a
  stranger's network, make every write as slow as the slowest subscriber, and
  leave a retry nowhere to live.
- Sending with *no row* loses the event entirely if the process dies.

With a row, an unsent delivery is simply still due, including across a restart —
the worker polls rather than being poked, because deliveries queued before a
crash still need draining and nothing will poke anyone about them. Deliveries are
**at-least-once**; the payload's `id` is the *activity* id, stable across retries
and identical for every subscriber, so consumers deduplicate on it.

`issue.created` is rendered from the entity **in hand**, not a re-query: at the
moment the outbox row is written the issue has been `Added` to the context but a
fresh `SELECT` might not see it yet, and re-reading would also risk describing a
later state than the one that fired the event.

Signing is Stripe-style: the signed material is `{timestamp}.{payload}` and the
timestamp travels inside the signed `X-Tracer-Signature: t=…,v1=…` header, so a
captured request cannot be replayed with a refreshed timestamp. The secret is
stored in the clear (unlike an API key) because it must *sign*, and a hash cannot
sign; exposure is limited instead — returned once on create/rotate, never echoed
by a read. Failure is classified `Transient` (5xx, timeout, 429/408, connection/
DNS) vs `Permanent` (other 4xx, 3xx — redirects are not followed); transient
failures retry with exponential backoff (10s, 20s, 40s, 80s; 5 attempts).

A webhook URL is attacker-supplied input the server then fetches, so
`WebhookUrlPolicy` requires `http`/`https` and refuses loopback, private,
link-local, and carrier-grade-NAT addresses. The known, documented gap: a
hostname is only checked when it is a literal IP (DNS rebinding is not closed),
and there is a test asserting that gap exists so nobody assumes otherwise.

## Symmetric-relation normalization

Issues link five ways — `Relates`, `Blocks`, `BlockedBy`, `Duplicates`,
`DuplicatedBy` — but only **three are stored**. "Blocked by" is the same row seen
from the other end, derived on read. A request is canonicalized to one stored row
by `IssueRelations.Canonicalize`:

- **Directed** relations (`Blocks`/`Duplicates`) keep their direction — "A blocks
  B" and "B blocks A" are contradictory claims, and the cycle check exists to
  catch exactly that pair.
- **Symmetric** relations (`Relates`) have their two endpoints put in a fixed
  order first, because the unique index that enforces "one fact, one row"
  compares tuples, not meanings — without normalization "A relates to B" and "B
  relates to A" are two different tuples and the index waves the duplicate
  through.

A controller check makes the ordinary duplicate a clean `409`; the unique index
is what is actually *true*, because two concurrent requests can both pass a check
before either writes, and the loser of that race is translated back into the same
`409` rather than a 500. Blocking/duplication cycles are refused at any depth
(`IssueGraph`); `Relates` cycles are fine (no direction, nothing to deadlock).

## Fractional-rank ordering

Issues carry a `double Position` — a rank, not a row number. A move is the
midpoint between the issue's two new neighbours (`IssueRanker.Between`), so it
rewrites exactly one row instead of renumbering the column. Midpoints run out:
once neighbours are closer than `IssueRanker.MinGap` the column is renumbered
into even steps (`IssueRanker.RankAt`) **in the same transaction as the move**.
Reordering across columns is a state change, so it goes through the same
`IssueStateMachine` as an explicit transition — dragging a card is not a back
door around the workflow.

## Cycles as half-open intervals

A cycle is `[startsAt, endsAt)` — the start instant belongs to the cycle, the end
instant is the first that does not (`CycleSchedule`). Consecutive cycles share a
boundary without overlapping or gapping. Status (`Upcoming`/`Active`/`Completed`)
is always derived from the dates, never stored, so it cannot drift from the
calendar. Cycles within a team may not overlap (`409`) and must end after they
start (`422`). The activity feed's `since`/`until` window is half-open for the
same reason: consecutive windows tile without double-counting.

## Import idempotency

Import matches each row to an existing issue by `Issue.ExternalId`, so sending the
same payload twice updates rather than duplicates. A filtered unique index
(`(TeamId, ExternalId) WHERE ExternalId IS NOT NULL`) makes "an external id names
one issue or none" a database guarantee without claiming every never-imported
issue shares a null id. An issue created here has no external id, so it exports
under its own identifier (`ENG-42`) and re-imports as itself. Import is
**all-or-nothing**: every row is resolved and validated before any is written, a
dry run builds the exact same plan a real import applies, and CSV export defuses
formula injection (a cell starting `= + - @` is prefixed) so a spreadsheet cannot
execute an exported title.

## The DateTimeOffset converter

SQLite has no native `DateTimeOffset`, and storing one as text makes SQL ordering
compare wall-clock strings rather than instants — `09:00+09:00` would sort after
`10:00-05:00` despite being six hours earlier. Every `DateTimeOffset` is persisted
as **UTC ticks** (`long`) through value converters in
`TracerDbContext.ConfigureConventions`, so ordering, filtering, and cycle-status
checks are correct in the database and not only in memory.

## Authentication, authorization, and 403-vs-404

Present a key as `X-Api-Key: trk_…`. A fallback authorization policy in
`Program.cs` requires an authenticated user on **every** endpoint, so a new
controller is protected the moment it exists; opening one up is a deliberate
`[AllowAnonymous]` (only `/api/health`). Keys are stored as SHA-256 hashes —
not bcrypt, because a 192-bit CSPRNG token has no keyspace to brute-force, so a
work factor buys nothing while costing a hash on every request.

Two orthogonal questions, decided in one place (`TeamAccess`):

- **Role** — `Admin` or `Member`, a property of the *user*. Admins administer the
  workspace (teams, users, keys).
- **Membership** — which teams a user is on, a property of the *pair*. An admin
  reaches every team without a membership row.

The status codes carry meaning:

| Situation | Status | Why |
|---|---|---|
| No / unknown / revoked key | `401` | |
| Right key, wrong role | `403` | You can see the thing; you lack the role — saying so discloses nothing |
| A team you are not on | `404` | A `403` would confirm the id names a real team, letting an attacker map the workspace by probing ids |
| Someone else's comment/view | `403` | You can already read it; a 404 would only confuse |

**A foreign team is a 404, not a 403**, so existence and permission collapse into
one answer. Unknown-*reference* validation (another team's state, label, project,
parent) is a **400**, not a 422 — the id names something real, just not the
caller's to point at from here.

## Pagination and concurrency (Sprint 15)

Every list endpoint that grows with usage returns one envelope and takes
`page`/`pageSize` (1–100), so no request can ask for an unbounded number of rows:

```json
{ "items": [ ... ], "page": 1, "pageSize": 50, "total": 137, "totalPages": 3 }
```

A handful of reads are **not** paged because the domain bounds them, not usage: a
team's workflow states (its board columns), its cycles and milestones (whose
derived status is filtered in memory), and an issue's direct children (whose
roll-up is over the whole set). Each says so at its call site.

An issue carries a `Version` token configured as an EF concurrency token and
rotated on every update / transition / reorder. A write is `UPDATE … WHERE Version
= <the value that was read>`: two edits that raced both loaded the same version,
the first wins, and the second updates zero rows and comes back a `409` instead of
silently clobbering a change nobody saw. Send the `version` you read on `PUT
/api/issues/{id}` to hold the update to it; omit it and you are still protected
against a concurrent in-flight edit.

Write-heavy endpoints draw from a per-API-key fixed window (`RateLimiting:*`
config, 120/60s default); reads are never throttled. Over the limit is a `429`
`problem+json` with `Retry-After`. Every request is logged as one structured line
(method, path, status, elapsed ms) from the front of the pipeline.

## Errors

Every failure — controller, model validation, or routing before a controller runs
— is [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807) `application/problem+json`,
so a client parses one shape. See the [API reference](api-reference.md#error-model)
for the status-code table.
