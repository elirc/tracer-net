# Getting started

## Prerequisites

- .NET 10 SDK

## Build, test, run

```bash
dotnet build
dotnet test
dotnet run --project src/Tracer.Api
```

In `Development` the app applies migrations and seeds sample data on startup, so
`dotnet run` gives you something to query immediately. It listens on
`http://localhost:5284` (and `https://localhost:7018`).

The seed creates two teams (`ENG`, `DES`), three users, and three API keys:

| Key | User | Role | Sees |
|---|---|---|---|
| `trk_dev_admin_ana` | `ana` | Admin | every team |
| `trk_dev_member_ben` | `ben` | Member | `ENG` |
| `trk_dev_member_dana` | `dana` | Member | `DES` |

These keys only exist because the seeder runs in `Development`. A real deployment
mints its first admin key out of band and every one after that through the API.

```bash
curl http://localhost:5284/api/health                      # no key needed
curl -H 'X-Api-Key: trk_dev_admin_ana' http://localhost:5284/api/me
```

`GET /api/health` returns `503` if it cannot reach the database:

```json
{ "status": "ok", "name": "tracer-net", "version": "2.0.0",
  "utcNow": "2026-07-17T17:00:14.4+00:00",
  "database": { "healthy": true, "durationMs": 0.8 } }
```

## Walkthrough

Everything below uses the admin key. Set two shell variables:

```bash
KEY='X-Api-Key: trk_dev_admin_ana'
BASE=http://localhost:5284
```

### 1. A team

```bash
curl -X POST "$BASE/api/teams" -H "$KEY" -H 'Content-Type: application/json' \
  -d '{"name":"Docs Team","key":"DOC"}'
# → { "id": "<TEAM>", "name": "Docs Team", "key": "DOC", ... }
```

Creating a team seeds the default five-state workflow — `Backlog`, `Todo`,
`In Progress`, `Done`, `Canceled`:

```bash
curl -H "$KEY" "$BASE/api/teams/<TEAM>/states"
```

### 2. A project

```bash
curl -X POST "$BASE/api/teams/<TEAM>/projects" -H "$KEY" -H 'Content-Type: application/json' \
  -d '{"name":"Docs Project","description":"Everything docs"}'
# → { "id": "<PROJECT>", ... }
```

### 3. An issue

```bash
curl -X POST "$BASE/api/teams/<TEAM>/issues" -H "$KEY" -H 'Content-Type: application/json' \
  -d '{"title":"First issue","priority":"High","projectId":"<PROJECT>","assignee":"ana"}'
# → { "id": "<ISSUE>", "identifier": "DOC-1", "state": "Backlog",
#     "version": "<VERSION>", ... }
```

The issue is auto-numbered (`DOC-1`) and lands in the first workflow state. The
`version` is its optimistic-concurrency token — send it back on a `PUT` to be sure
you are not overwriting someone else's edit.

### 4. A transition

The board list is paged; grab the `Todo` state and move the issue there:

```bash
curl -H "$KEY" "$BASE/api/teams/<TEAM>/states"          # find Todo's id
curl -X POST "$BASE/api/issues/<ISSUE>/transitions" -H "$KEY" \
  -H 'Content-Type: application/json' -d '{"stateId":"<TODO>"}'
# → { ..., "state": "Todo", ... }
```

Transitions are validated: moving `Backlog → Done` directly is refused with a
`422`.

### 5. A relation

Create a second issue and link the first as blocking it:

```bash
curl -X POST "$BASE/api/teams/<TEAM>/issues" -H "$KEY" \
  -H 'Content-Type: application/json' -d '{"title":"Second issue"}'   # → <ISSUE2>

curl -X POST "$BASE/api/issues/<ISSUE>/relations" -H "$KEY" \
  -H 'Content-Type: application/json' -d '{"kind":"Blocks","issueId":"<ISSUE2>"}'

curl -H "$KEY" "$BASE/api/issues/<ISSUE2>/relations"
# From ISSUE2's point of view the same row reads "BlockedBy" — one fact, one row.
```

### 6. A cycle

Cycles are half-open `[startsAt, endsAt)`; `status` is derived from the dates:

```bash
curl -X POST "$BASE/api/teams/<TEAM>/cycles" -H "$KEY" -H 'Content-Type: application/json' \
  -d '{"name":"Sprint 1","startsAt":"2026-07-20T00:00:00Z","endsAt":"2026-08-03T00:00:00Z"}'
# → { "number": 1, "status": "Upcoming", ... }
```

### 7. A saved view

A view stores rules (the filter half of search), run on demand:

```bash
curl -X POST "$BASE/api/teams/<TEAM>/views" -H "$KEY" -H 'Content-Type: application/json' \
  -d '{"name":"High priority","scope":"Team",
       "rules":{"priority":"High","sort":"Created","order":"Asc"}}'
# → { "id": "<VIEW>", ... }

curl -H "$KEY" "$BASE/api/views/<VIEW>/issues?page=1&pageSize=25"
# → { "items": [ ... ], "page": 1, "pageSize": 25, "total": 1, "totalPages": 1 }
```

### 8. Export

Export is the whole of what the filters select — not paged. CSV or JSON:

```bash
curl -H "$KEY" "$BASE/api/teams/<TEAM>/issues/export?format=Csv"
# identifier,externalId,title,description,priority,estimate,assignee,state,project,cycle,labels,createdAt,updatedAt
```

Re-importing that file updates rather than duplicates, because each issue exports
under its identifier and import matches on it.

## A webhook

Subscribe an endpoint to events. The `events` are the **enum names**, and the
response is the only place the signing `secret` appears:

```bash
curl -X POST "$BASE/api/teams/<TEAM>/webhooks" -H "$KEY" -H 'Content-Type: application/json' \
  -d '{"name":"CI hook","url":"https://example.com/hook",
       "events":["IssueCreated","IssueStateChanged"]}'
# → { "id": "<WEBHOOK>", "events": ["IssueCreated","IssueStateChanged"],
#     "secret": "trk_...", ... }
```

On the wire the event name is the dotted form (`issue.created`,
`issue.state_changed`) in the `X-Tracer-Event` header and the payload. Each
delivery is signed `X-Tracer-Signature: t=<unix>,v1=<hmac-sha256 hex>` over
`{timestamp}.{payload}`. Inspect what was sent:

```bash
curl -H "$KEY" "$BASE/api/webhooks/<WEBHOOK>/deliveries"
curl -H "$KEY" "$BASE/api/webhooks/<WEBHOOK>/deliveries/<DELIVERY>"   # exact bytes
```

## Metrics

After some issues move through `Done` inside cycles, the metrics endpoints report
delivery data derived from the activity history:

```bash
curl -H "$KEY" "$BASE/api/teams/<TEAM>/metrics/velocity"
# → { "teamId": "...", "cycles": [ ... ], "averageVelocity": 0 }

curl -H "$KEY" "$BASE/api/teams/<TEAM>/metrics/cycle-time?from=2026-07-01T00:00:00Z"
# → { "teamId": "...", "completedIssues": 0, "p50Hours": null, "p75Hours": null, "p90Hours": null }

curl -H "$KEY" "$BASE/api/cycles/<CYCLE>/burndown"
```

## Optimistic concurrency in practice

Read an issue, keep its `version`, and send it back on the `PUT`. If someone else
edited the issue in between, your write is refused with a `409` instead of
silently clobbering their change:

```bash
curl -X PUT "$BASE/api/issues/<ISSUE>" -H "$KEY" -H 'Content-Type: application/json' \
  -d '{"title":"Renamed","priority":"High","version":"<VERSION>"}'
# 200 if <VERSION> is current; 409 problem+json if the issue moved underneath you.
```

## Rate limiting

Write requests (`POST`/`PUT`/`PATCH`/`DELETE`) draw from a per-API-key window
(120/60s by default, `RateLimiting:PermitLimit` / `RateLimiting:WindowSeconds`);
reads are never throttled. Over the limit is a `429` with a `Retry-After` header.
