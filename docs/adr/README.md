# Architecture Decision Records

Each ADR records one decision that shaped tracer-net: the context that forced it,
the decision, and the consequences accepted with it. They are the reasoning behind
the [architecture](../architecture.md); the code comments carry the same rationale
at each call site.

| # | Decision | Status |
|---|---|---|
| [0001](0001-api-key-authentication.md) | API-key authentication, hashed with SHA-256 | Accepted |
| [0002](0002-403-vs-404-existence-hiding.md) | A foreign team is 404, not 403 | Accepted |
| [0003](0003-activity-spine-single-source.md) | The activity spine is the single source for audit, webhooks, and notifications | Accepted |
| [0004](0004-transactional-outbox-webhooks.md) | Webhooks use a transactional outbox drained by a worker | Accepted |
| [0005](0005-signed-timestamp-webhook-signatures.md) | Webhook payloads are signed with a signed timestamp | Accepted |
| [0006](0006-symmetric-relation-normalization.md) | Symmetric relations are normalized to a stable endpoint order | Accepted |
| [0007](0007-fractional-rank-ordering.md) | Issues are ordered by fractional rank with rebalance | Accepted |
| [0008](0008-half-open-cycle-intervals.md) | Cycles are half-open intervals with derived status | Accepted |
| [0009](0009-import-idempotency-external-id.md) | Import is idempotent via external id and a filtered unique index | Accepted |
| [0010](0010-datetimeoffset-utc-ticks.md) | `DateTimeOffset` is stored as UTC ticks | Accepted |
