# 0004 — Webhooks use a transactional outbox drained by a worker

**Status:** Accepted

## Context

A webhook fires on a change and calls a stranger's server, which may be slow,
hostile, or a black hole. Two naive designs both fail: sending inside the request
holds a database transaction open across the network, makes every write as slow as
the slowest subscriber, and leaves a retry nowhere to live; sending with no record
loses the event entirely if the process dies between the change and the send.

## Decision

Write a `WebhookDelivery` row **in the same transaction as the change** (via the
activity spine, ADR 0003), then send it afterwards from a background worker
(`WebhookDeliveryWorker` draining `WebhookSender.DeliverDueAsync`). An unsent
delivery is simply still *due*, including across a restart. The worker **polls**
rather than being poked, because deliveries queued before a crash still need
draining and nothing will poke anyone about them.

Deliveries are **at-least-once**. The payload's `id` is the *activity* id — stable
across retries, identical for every subscriber — so consumers deduplicate on it.
The delivery id (the envelope) is deliberately not in the body: if it were, two
teams subscribed to one event would need two payloads and two signatures for the
same fact. `issue.created` is rendered from the entity in hand, not a re-query,
because at write time the row is `Added` but not yet visible to a fresh `SELECT`.

## Consequences

- No event is lost to a crash, and none blocks a write.
- **At-least-once, not exactly-once:** consumers must dedupe on the activity id.
- **Assumes one instance.** Two workers would both claim the same due rows and
  send them twice — the claim is not atomic. Under at-least-once that is not a
  correctness bug, but it is a real limit; making it multi-instance-safe needs an
  atomic conditional claim or a row lock.
- Failure is classified `Transient` (5xx, timeout, 429/408, connection/DNS) vs
  `Permanent` (other 4xx, 3xx), with exponential backoff over five attempts.
