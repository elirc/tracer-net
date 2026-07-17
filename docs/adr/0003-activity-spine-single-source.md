# 0003 — The activity spine is the single source for audit, webhooks, and notifications

**Status:** Accepted

## Context

Three features need to know when an issue changes: the audit feed, outbound
webhooks, and in-app notifications. Metrics need it too, after the fact. If each
subscribed to changes independently, they would drift — a change could be audited
but not announced, or announced but not notified — and every controller would have
three things to remember instead of one.

## Decision

Route every issue mutation through one recorder (`ActivityRecorder`) that writes
the immutable `Activity` record **and** enqueues the webhook outbox rows **and**
fans out notifications, all inside the transaction the mutation is already in. The
recorder never calls `SaveChanges`; it adds to the caller's `DbContext`, so the
record and the change commit together or not at all. Metrics are derived from the
same `Activity` history later (state-change entries reconstruct each issue's
lifecycle).

Recording is an explicit call per controller, **not** a `SaveChanges`
interceptor. An interceptor sees columns, not intent: it cannot tell a re-parent
from an assignment, cannot name the actor without ambient request state, and would
report a rank rebalance (a move of *other people's* cards) as a dozen phantom
edits.

## Consequences

- A committed change cannot exist without its audit entry, its webhook delivery
  rows, and its notifications; a rolled-back change leaves none of them.
- The audit log breaks the schema's usual rules on purpose: `IssueId`/`ActorId`
  are plain `Guid`s with no foreign key, so deleting an issue or user cannot
  cascade away the record of what happened. Render data (issue title, actor
  handle) is denormalized at write time. `TeamId` is the one relationship that
  cascades.
- Two consequences worth knowing: a no-op writes nothing (re-saving unchanged, or
  re-attaching an existing label, records nothing), and big text is not copied (a
  description change records *that* it changed; comment bodies are excerpted).
- The trade is real — an explicit call can be forgotten. The tests are where that
  is caught.
