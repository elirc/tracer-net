# 0001 — API-key authentication, hashed with SHA-256

**Status:** Accepted

## Context

Every endpoint except the health probe needs a caller identity, and the product
is a backend with no interactive login flow. The credential store is itself an
attack surface: whatever protects the tokens has to hold up if the database
leaks, and it runs on every authenticated request, so its cost is paid constantly.

## Decision

Authenticate with a bearer API key presented as `X-Api-Key: trk_…`. Tokens are
minted from a CSPRNG (192 bits), shown exactly once at creation, and stored only
as **SHA-256 hashes**. A fallback authorization policy requires an authenticated
user on every endpoint, so opting out is a deliberate `[AllowAnonymous]`.

SHA-256, not bcrypt/argon2: a slow hash defends *low-entropy, human-chosen*
secrets against brute force. A 192-bit random token has no keyspace to search, so
a work factor buys nothing — while costing an expensive hash on every request,
which is itself a denial-of-service lever. The key hash is uniquely indexed so
lookup is a seek, not a scan of every credential.

## Consequences

- A leaked database yields hashes, not usable tokens.
- A lost token is replaced, never recovered — only its hash exists server-side.
- Revocation keeps the row (revoked, not deleted) so the audit trail survives.
- The scheme is not suitable for user-chosen passwords; it is correct only
  *because* tokens are high-entropy and machine-generated.
