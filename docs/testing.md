# Testing

The suite is **591 tests** and runs with:

```bash
dotnet test
```

## Taxonomy

Two kinds of test, in `tests/Tracer.Tests`:

- **Unit** (`Unit/`) — the pure domain rules, exercised directly as functions over
  values, no database and no HTTP: the state machine (`IssueStateMachineTests`),
  the fractional ranker (`IssueRankerTests`), the relation/parent graph
  (`IssueGraphTests`, `IssueRelationsTests`), cycle scheduling
  (`CycleScheduleTests`), saved-view rules (`SavedViewRulesTests`), notification
  policy (`NotificationPolicyTests`), webhook policy and signing
  (`WebhookPolicyTests`), CSV rendering and formula-defusing (`CsvTests`), the
  metric math and burndown (`MetricMathTests`, `BurndownChartTests`,
  `IssueLifecycleTests`, `MilestoneRoadmapTests`), and the model configuration
  (`DbContextTests`). Plus the **migration-drift guard** (`MigrationDriftTests`,
  below).
- **Integration** (`Integration/`) — the API over real HTTP through
  `WebApplicationFactory`, one file per surface (issues, search, ordering,
  transitions, relations, sub-issues, labels/comments, cycles, saved views,
  activity, notifications, webhooks and deliveries, import/export, metrics,
  milestones, auth/authz, problem-details, the full-journey smoke test, and the
  Sprint 15 production-readiness + concurrency + transition-matrix tests).

## The WebApplicationFactory harness

`TracerApiFactory` boots the real application and swaps only the database for a
**shared in-memory SQLite connection**, held open for the factory's lifetime so
the schema and seed data survive across requests. What it deliberately does *not*
swap:

- **Authentication is real.** There is no test-only auth stub — requests go
  through the actual `ApiKeyAuthenticationHandler`, hashing and all. A fake
  handler would make every authorization test a test of the fake, and the denial
  matrix would keep passing even if key lookup were broken outright. Clients come
  pre-credentialed from the seeded dev keys (`CreateAdminClient`,
  `CreateEngMemberClient`, `CreateDesMemberClient`, `CreateAnonymousClient`).
- **The webhook worker is replaced by an explicit drain.** The background polling
  worker is removed; tests call `DrainWebhooksAsync()` to deliver due webhooks
  synchronously, and every webhook request goes to an in-process
  `StubWebhookEndpoint` whose responses tests script and read back. A test that
  waits for a five-second poll is slow and flaky; the thing worth asserting is
  what a drain *does*, not that a timer fires.

The factory also runs with a very high rate limit by default
(`RateLimitPermitLimit`), so the suite's write bursts — several classes hammering
the same admin key in parallel — never trip the limiter. The one test that means
to exercise throttling constructs its own factory with a small limit.

Paged list endpoints return the `{ items, ... }` envelope; the `GetListAsync<T>`
test helper reads one and hands back just the rows, so assertions that predate
pagination read a list exactly as before.

## The EnsureCreated-vs-Migrate caveat, and the drift guard

There are two ways to get a schema onto a database, and they do not agree:

- `EnsureCreated()` stamps the **current model** straight onto the database and
  never looks at the migrations.
- `Migrate()` replays the **migrations**, which is what production does.

If a test harness builds its schema with `EnsureCreated` while production runs
`Migrate`, a property added to the model *without a migration* works in every
test and fails only in production, where the column the migrations never added is
simply not there. A sibling project shipped exactly that.

This repo's harness already migrates (its factory boots the app in `Development`,
which runs `Migrate` + seed), and `MigrationDriftTests` closes the gap from both
ends so it cannot regress:

- `The_model_has_no_changes_that_a_migration_has_not_captured` asserts
  `db.Database.HasPendingModelChanges()` is false — EF compares the live model to
  the last migration's snapshot, so a model change without a migration fails here
  with a message telling you to run `dotnet ef migrations add`.
- `The_seeder_runs_against_a_migrated_database` migrates a fresh database and runs
  the seeder against it, so a migration missing a column the seeder writes throws
  instead of passing on a model-stamped schema.

The guard was verified by adding an unmapped property and confirming both tests
fail, then removing the probe. Keep it green: whenever you change an entity or the
`DbContext` configuration, add a migration in the same change.

```bash
dotnet ef migrations add <Name> \
  --project src/Tracer.Infrastructure --startup-project src/Tracer.Api
```

## Running subsets

```bash
dotnet test --filter "FullyQualifiedName~MigrationDriftTests"
dotnet test --filter "FullyQualifiedName~IssueConcurrencyTests"
dotnet test --filter "FullyQualifiedName~ProductionReadinessApiTests"
```
