# API reference

Base URL in development: `http://localhost:5284`. Every endpoint except
`GET /api/health` requires an `X-Api-Key: trk_…` header.

**Conventions**

- **Auth** column: `anon` = no key; `any` = any authenticated user; `member` =
  a member of the addressed team (admins reach every team); `self/admin`,
  `owner/admin`, `author/admin` = the row's owner or an admin; `admin` = the
  Admin role.
- **Paged** endpoints take `page` (≥1, default 1) and `pageSize` (1–100, default
  50; 25 for `GET /api/issues`) and return
  `{ items, page, pageSize, total, totalPages }`. An out-of-range `pageSize` is
  `400`.
- Timestamps are RFC 3339 (`DateTimeOffset`). Enums serialize as their names.

## Error model

All errors are `application/problem+json` (RFC 7807) with `type`, `title`,
`status`, `detail`, `traceId`.

| Status | Means |
|---|---|
| `400` | Malformed, or points at something not the caller's to point at (another team's state/label/project/parent) |
| `401` | No key, an unknown key, or a revoked one |
| `403` | Authenticated, but needs a role you lack — or the row is not yours to edit |
| `404` | The resource does not exist, **or belongs to a team you are not on** |
| `409` | Well-formed but collides with existing data (duplicate key, overlapping cycle) **or a stale optimistic-concurrency write** |
| `422` | Well-formed and coherent, but a domain rule forbids the outcome (illegal transition, backwards dates, self/circular relation) |
| `429` | Too many write requests in the window (per API key); carries `Retry-After` |

## Health

| Method | Route | Auth | Notes |
|---|---|---|---|
| `GET` | `/api/health` | anon | `200` when the DB probe succeeds, `503` when it fails |

Response: `{ status, name, version, utcNow, database: { healthy, durationMs } }`.

## Identity

| Method | Route | Auth | Notes |
|---|---|---|---|
| `GET` | `/api/me` | any | Your id, handle, name, role, and the teams your key reaches |

## Users & API keys

Users are admin-only; keys are self-or-admin.

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/users` | admin | **Paged** `UserDto` |
| `POST` | `/api/users` | admin | `{ handle, name, role }` → `UserDto` (409 on duplicate handle) |
| `GET` | `/api/users/{id}` | admin | `UserDto` |
| `PUT` | `/api/users/{id}` | admin | `{ name, role }` → `UserDto` (409 removing the last admin) |
| `DELETE` | `/api/users/{id}` | admin | `204` (409 removing the last admin) |
| `PUT` | `/api/users/{userId}/teams/{teamId}` | admin | `204`, idempotent |
| `DELETE` | `/api/users/{userId}/teams/{teamId}` | admin | `204` |
| `GET` | `/api/users/{userId}/api-keys` | self/admin | **Paged** `ApiKeyDto` (403 before lookup) |
| `POST` | `/api/users/{userId}/api-keys` | self/admin | `{ name }` → `CreatedApiKeyDto` (the only response with the raw `token`) |
| `GET` | `/api/api-keys/{id}` | owner/admin | `ApiKeyDto` (404 for both missing and not-yours) |
| `DELETE` | `/api/api-keys/{id}` | owner/admin | `204`, idempotent; the row survives for the audit trail |

`UserDto`: `{ id, handle, name, role, createdAt }`.
`ApiKeyDto`: `{ id, userId, name, prefix, createdAt, lastUsedAt, revokedAt }`.

## Teams

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/teams` | any | **Paged** `TeamDto` (the teams you can see) |
| `POST` | `/api/teams` | admin | `{ name, key }` → `TeamDto`; seeds the default 5-state workflow (409 on duplicate key) |
| `GET` | `/api/teams/{id}` | member | `TeamDto` |
| `GET` | `/api/teams/{teamId}/members` | member | **Paged** `TeamMemberDto` |
| `PUT` | `/api/teams/{id}` | admin | `{ name, key }` → `TeamDto` (409 on duplicate key) |
| `DELETE` | `/api/teams/{id}` | admin | `204`; deletes everything the team owns |

`TeamDto`: `{ id, name, key, createdAt }`.
`TeamMemberDto`: `{ userId, handle, name, role, createdAt }`.

## Projects

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/teams/{teamId}/projects` | member | **Paged** `ProjectDto` |
| `POST` | `/api/teams/{teamId}/projects` | member | `{ name, description? }` → `ProjectDto` |
| `GET` | `/api/projects/{id}` | member | `ProjectDto` |
| `PUT` | `/api/projects/{id}` | member | `{ name, description? }` → `ProjectDto` |
| `DELETE` | `/api/projects/{id}` | member | `204`; its issues survive, unassigned |

`ProjectDto`: `{ id, teamId, name, description, createdAt }`.

## Workflow states

Not paged — a team's states are its board columns, a bounded set.

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/teams/{teamId}/states` | member | `WorkflowStateDto[]`, in order |
| `POST` | `/api/teams/{teamId}/states` | member | `{ name, type, position?, color? }` → `WorkflowStateDto` (409 on duplicate name) |
| `GET` | `/api/states/{id}` | member | `WorkflowStateDto` |
| `PUT` | `/api/states/{id}` | member | `{ name, position, color }` → `WorkflowStateDto` |
| `DELETE` | `/api/states/{id}` | member | `204` (409 if it holds issues or is the team's last) |

`type` ∈ `Backlog`, `Todo`, `InProgress`, `Done`, `Canceled`.

## Issues

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/issues` | any | **Search**, **paged** `IssueDto` — see [Search](#search) |
| `GET` | `/api/teams/{teamId}/issues` | member | **Paged** `IssueDto`, board order |
| `POST` | `/api/teams/{teamId}/issues` | member | `CreateIssueRequest` → `IssueDto`; auto-numbered, appended to its column |
| `GET` | `/api/issues/{id}` | member | `IssueDto` |
| `PUT` | `/api/issues/{id}` | member | `UpdateIssueRequest` → `IssueDto`; pass `version` for optimistic concurrency (409 on a stale write) |
| `POST` | `/api/issues/{id}/transitions` | member | `{ stateId }` → `IssueDto` (422 on an illegal transition, 400 on another team's state) |
| `POST` | `/api/issues/{id}/reorder` | member | `{ stateId?, afterIssueId?, beforeIssueId? }` → `IssueDto` |
| `GET` | `/api/issues/{id}/children` | member | `SubIssuesDto` — roll-up + direct children (not paged) |
| `DELETE` | `/api/issues/{id}` | member | `204`; children survive, un-nested |

`CreateIssueRequest`: `{ title, description?, priority?, estimate?, stateId?,
projectId?, cycleId?, milestoneId?, assignee?, parentId? }`.
`UpdateIssueRequest`: same minus `stateId`, plus `version?` (a `Guid`).
`IssueDto`: `{ id, teamId, identifier, number, title, description, priority,
estimate, assignee, stateId, state, projectId, cycleId, milestoneId, parentId,
position, labels, createdAt, updatedAt, version }`.
`priority` ∈ `None`, `Urgent`, `High`, `Medium`, `Low`.

Unknown reference (project/cycle/milestone/parent/state from another team) → `400`.
A parent chain or blocking/duplication cycle → `422`.

## Relations

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/issues/{issueId}/relations` | member | **Paged** `IssueRelationDto`, named from this issue's point of view |
| `POST` | `/api/issues/{issueId}/relations` | member | `{ kind, issueId }` → `IssueRelationDto` |
| `DELETE` | `/api/issues/{issueId}/relations/{relationId}` | member | `204`; reachable from either end |

`kind` ∈ `Relates`, `Blocks`, `BlockedBy`, `Duplicates`, `DuplicatedBy`.
`IssueRelationDto`: `{ id, kind, issueId, identifier, title, state, createdAt }`.
Self-relation → `422`; the same link twice (incl. a symmetric mirror) → `409`; a
blocking/duplication cycle → `422`; another team's issue or a non-existent one →
`400`.

## Labels

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/teams/{teamId}/labels` | member | **Paged** `TeamLabelDto` |
| `POST` | `/api/teams/{teamId}/labels` | member | `{ name, color? }` → `TeamLabelDto` (409 on duplicate name) |
| `GET` | `/api/labels/{id}` | member | `TeamLabelDto` |
| `PUT` | `/api/labels/{id}` | member | `{ name, color }` → `TeamLabelDto` |
| `DELETE` | `/api/labels/{id}` | member | `204` |
| `PUT` | `/api/issues/{issueId}/labels/{labelId}` | member | `204`, idempotent (422 if the label is another team's) |
| `DELETE` | `/api/issues/{issueId}/labels/{labelId}` | member | `204` |

`TeamLabelDto`: `{ id, teamId, name, color }`.

## Comments

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/issues/{issueId}/comments` | member | **Paged** `CommentDto`, oldest first |
| `POST` | `/api/issues/{issueId}/comments` | member | `{ body }` → `CommentDto`; author is your credential, not a field |
| `GET` | `/api/comments/{id}` | member | `CommentDto` |
| `PUT` | `/api/comments/{id}` | author/admin | `{ body }` → `CommentDto` (403 if not yours) |
| `DELETE` | `/api/comments/{id}` | author/admin | `204` (403 if not yours) |

`CommentDto`: `{ id, issueId, author, body, createdAt }`.

## Cycles

Not paged — bounded by a team's cycle count; `status` is derived and filtered in
memory.

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/teams/{teamId}/cycles` | member | `CycleDto[]`; `?status=Upcoming\|Active\|Completed` |
| `POST` | `/api/teams/{teamId}/cycles` | member | `{ name, startsAt, endsAt }` → `CycleDto`; auto-numbered (409 overlap, 422 backwards) |
| `GET` | `/api/cycles/{id}` | member | `CycleDto` |
| `PUT` | `/api/cycles/{id}` | member | `{ name, startsAt, endsAt }` → `CycleDto` |
| `DELETE` | `/api/cycles/{id}` | member | `204`; its issues survive, unscheduled |
| `GET` | `/api/cycles/{id}/summary` | member | `CycleSummaryDto` — progress roll-up |
| `GET` | `/api/cycles/{id}/burndown` | member | `BurndownDto` — see [Metrics](#metrics) |

`CycleDto` includes derived `status`. `startsAt`/`endsAt` are required (a missing
one is a `400`, not a bogus `422`).

## Saved views

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/teams/{teamId}/views` | member | **Paged** `SavedViewDto` — team views + your personal ones |
| `POST` | `/api/teams/{teamId}/views` | member | `{ name, scope, rules, isDefault? }` → `SavedViewDto` |
| `GET` | `/api/teams/{teamId}/views/default` | member | `SavedViewDto` (404 if none) |
| `GET` | `/api/views/{id}` | member/owner | `SavedViewDto` (another member's personal view → 404) |
| `PUT` | `/api/views/{id}` | member/owner | `{ name, scope, rules, isDefault? }` → `SavedViewDto` |
| `DELETE` | `/api/views/{id}` | member/owner | `204` |
| `GET` | `/api/views/{id}/issues` | member/owner | **Paged** `IssueDto` — runs the view |

`scope` ∈ `Team`, `Personal`. `rules` is the filter half of [Search](#search)
(no `teamId`, `page`, `pageSize`). A rule pointing at another team's
project/state/cycle/label → `400`. Only a team view may be the default (`422`
otherwise).

## Activity

Both feeds are **paged** (default `pageSize` 50), newest first, id tiebreak.

| Method | Route | Auth | Notes |
|---|---|---|---|
| `GET` | `/api/issues/{issueId}/activity` | member | One issue's timeline |
| `GET` | `/api/teams/{teamId}/activity` | member | Team feed; `?issueId=&type=&actor=&since=&until=` |

`type` ∈ `IssueCreated`, `IssueUpdated`, `IssueStateChanged`, `IssueAssigned`,
`IssueLabelAdded`/`Removed`, `IssueRelationAdded`/`Removed`, `IssueParentChanged`,
`IssueDeleted`, `CommentCreated`/`Updated`/`Deleted`. `since`/`until` are
half-open `[since, until)`.

## Notifications

| Method | Route | Auth | Notes |
|---|---|---|---|
| `GET` | `/api/notifications` | any | **Paged** inbox, newest first; `?unread=true` |
| `GET` | `/api/notifications/unread-count` | any | `{ unread }` |
| `POST` | `/api/notifications/{id}/read` | owner | `NotificationDto` |
| `POST` | `/api/notifications/{id}/unread` | owner | `NotificationDto` |
| `POST` | `/api/notifications/read-all` | any | `{ unread: 0 }` |

You auto-subscribe by creating (`Author`), commenting (`Commenter`), or being
assigned (`Assignee`); you are never notified of your own action.

## Subscriptions

| Method | Route | Auth | Notes |
|---|---|---|---|
| `GET` | `/api/issues/{issueId}/subscription` | member | `{ subscribed, reason }` |
| `PUT` | `/api/issues/{issueId}/subscription` | member | Watch (idempotent, keeps an existing reason) |
| `DELETE` | `/api/issues/{issueId}/subscription` | member | Unwatch |
| `GET` | `/api/issues/{issueId}/subscribers` | member | **Paged** `IssueSubscriberDto` |

`reason` ∈ `Author`, `Commenter`, `Assignee`, `Manual`.

## Webhooks

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/teams/{teamId}/webhooks` | member | **Paged** `WebhookDto` |
| `POST` | `/api/teams/{teamId}/webhooks` | member | `{ name, url, events }` → `CreatedWebhookDto` (the only response with the `secret`) |
| `GET` | `/api/webhooks/{id}` | member | `WebhookDto` (never the secret) |
| `PUT` | `/api/webhooks/{id}` | member | `{ name, url, events, isActive }` → `WebhookDto` |
| `POST` | `/api/webhooks/{id}/rotate-secret` | member | `WebhookSecretDto` — the new secret; the old stops working |
| `DELETE` | `/api/webhooks/{id}` | member | `204`; deletes the delivery log |
| `GET` | `/api/webhooks/{id}/deliveries` | member | **Paged** `WebhookDeliveryDto`; `?status=Pending\|Delivered\|Failed` |
| `GET` | `/api/webhooks/{id}/deliveries/{deliveryId}` | member | `WebhookDeliveryDetailDto` — the exact bytes sent |
| `POST` | `/api/webhooks/{id}/deliveries/{deliveryId}/redeliver` | member | `WebhookDeliveryDto` (409 if still pending) |

`events` are the **enum names** `IssueCreated`, `IssueUpdated`,
`IssueStateChanged`, `CommentCreated`. On the wire they become
`issue.created`, `issue.updated`, `issue.state_changed`, `comment.created` (the
`X-Tracer-Event` header and the payload `event`). A URL that is non-http or
resolves to a private/loopback address → `400`.

## Import / export

| Method | Route | Auth | Notes |
|---|---|---|---|
| `GET` | `/api/teams/{teamId}/issues/export` | member | JSON or CSV file; `?format=Json\|Csv` plus any search filter. Not paged — an export is the whole selection |
| `POST` | `/api/teams/{teamId}/issues/import` | member | `{ dryRun, issues: [...] }` → `ImportReportDto` `{ dryRun, total, created, updated }` |

Each import row: `{ externalId?, title, description?, priority?, estimate?,
assignee?, state?, project?, labels? }`. Matched by `externalId`, then by the
issue's own identifier. All-or-nothing: any bad row rejects the whole payload with
per-row `problem+json` errors.

## Roadmap (milestones)

Not paged — bounded by a team's milestone count; `status` derived, filtered in
memory.

| Method | Route | Auth | Request → Response |
|---|---|---|---|
| `GET` | `/api/teams/{teamId}/milestones` | member | `MilestoneDto[]`; `?projectId=&status=` |
| `POST` | `/api/projects/{projectId}/milestones` | member | `{ name, description?, targetDate }` → `MilestoneDto` |
| `GET` | `/api/milestones/{id}` | member | `MilestoneDto` |
| `PUT` | `/api/milestones/{id}` | member | `{ name, description?, targetDate }` → `MilestoneDto` |
| `DELETE` | `/api/milestones/{id}` | member | `204`; its issues survive, released |

`MilestoneDto` includes a progress roll-up recomputed on read and a derived
`status`. `targetDate` is required.

## Metrics

Derived from the activity spine and cycles.

| Method | Route | Auth | Notes |
|---|---|---|---|
| `GET` | `/api/teams/{teamId}/metrics/velocity` | member | `?take=` — completed points per completed cycle + trailing average |
| `GET` | `/api/cycles/{id}/burndown` | member | Remaining vs ideal per day, with the scope line |
| `GET` | `/api/teams/{teamId}/metrics/throughput` | member | `?from=&to=&interval=Day\|Week&projectId=&assignee=` |
| `GET` | `/api/teams/{teamId}/metrics/cycle-time` | member | `?from=&to=&projectId=&assignee=` — p50/p75/p90 hours (null when nothing completed) |

Windows default to the last 30 days and are half-open `[from, to)`.
