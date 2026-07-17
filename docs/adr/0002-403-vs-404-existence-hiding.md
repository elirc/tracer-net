# 0002 — A foreign team is 404, not 403

**Status:** Accepted

## Context

Authorization is drawn on the team boundary. When a member addresses a resource in
a team they are not on, the status code is a signal an attacker can read: a `403`
("forbidden") confirms the id names a real team, and that single bit of leakage is
enough to map a whole workspace by probing ids and keeping the ones that answer
differently.

## Decision

Collapse existence and permission into one answer: **a team you are not on is
reported exactly like a team that never existed — `404`.** The rule lives in one
place (`TeamAccess`) rather than being re-decided per controller. The status codes
then mean:

- `401` — no key, unknown key, revoked key.
- `403` — right key, wrong *role* (you can see the thing, you lack the role), or a
  row that is not yours to edit (someone else's comment or personal view). Saying
  so discloses nothing you could not already read.
- `404` — the resource does not exist, **or** belongs to a team you are not on.
- `400` — a well-formed request pointing at another team's *reference* (a state,
  label, project, or parent that is real but not the caller's to point at from
  here).

The key routes show the same rule twice: `/users/{userId}/api-keys` answers `403`
because it checks permission *before* looking anything up (a member gets the same
answer for any id but their own, so nothing leaks), while `/api-keys/{id}` answers
`404` because a `403` there would only ever be reachable for a key that exists,
turning the status into an existence oracle for opaque ids.

## Consequences

- A member cannot distinguish "no such team" from "a team I'm not on", which is
  the point.
- The cost: a member who fat-fingers their own team id is told "not found" rather
  than "not allowed". Accepted as the right trade.
- Every controller must route team access through `TeamAccess` rather than
  inventing its own check.
