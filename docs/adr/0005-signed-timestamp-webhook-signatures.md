# 0005 — Webhook payloads are signed with a signed timestamp

**Status:** Accepted

## Context

A subscriber needs to verify that a delivery genuinely came from tracer-net and
was not replayed. Signing the body alone yields a signature valid forever: capture
one request and replay it, perfectly signed, next year. The signing key also has a
storage problem the API key does not.

## Decision

Sign `{timestamp}.{payload}` and send the timestamp **inside** the signed header:

```
X-Tracer-Signature: t=<unix>,v1=<hmac-sha256 hex>
```

A receiver rejects anything outside its tolerance, and because the timestamp is
signed, an attacker cannot refresh it to make an old capture look current.
Comparison is constant-time. The scheme is Stripe's on purpose — verification is
often written by people not thinking hard about webhooks, and a familiar
`t=…,v1=…` is code they may already have.

The secret is stored **in the clear**, unlike an API key (ADR 0001). The
difference is forced, not chosen: an API key is only ever *compared*, so a hash
suffices; this one must *sign*, and a hash cannot sign. Since storage cannot
protect it, exposure is limited instead — it is returned only when a webhook is
created or its secret rotated, and never echoed by a read. Redirects are not
followed, so a signed payload is never forwarded to a host the team never
registered.

## Consequences

- Replaying a captured delivery fails once it falls outside the receiver's
  tolerance.
- The signing secret lives in the clear in the database; the mitigation is
  minimal exposure and easy rotation, not storage hardening.
- Receivers must verify over `{timestamp}.{payload}`, not the body alone, or they
  reintroduce the replay hole.
