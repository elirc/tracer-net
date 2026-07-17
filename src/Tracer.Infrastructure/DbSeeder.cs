using Microsoft.EntityFrameworkCore;
using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Infrastructure;

/// <summary>
/// Fixed API keys for the seeded sample users, so <c>dotnet run</c> gives you
/// something you can immediately curl and the integration tests have a
/// credential to present.
///
/// These are safe to hard-code precisely because <see cref="DbSeeder"/> only
/// runs in Development: the keys exist only in a database built from sample
/// data. A real deployment mints its first admin key out of band and every
/// subsequent one through <c>POST /api/users/{id}/api-keys</c>, which is the
/// only other place a raw token is ever visible.
/// </summary>
public static class DevApiKeys
{
    /// <summary>Workspace admin "ana" — sees every team.</summary>
    public const string Admin = "trk_dev_admin_ana";

    /// <summary>Member "ben" — on Engineering only.</summary>
    public const string EngMember = "trk_dev_member_ben";

    /// <summary>Member "dana" — on Design only.</summary>
    public const string DesMember = "trk_dev_member_dana";
}

/// <summary>Populates a fresh local-dev database with realistic sample data.</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(TracerDbContext db, CancellationToken ct = default)
    {
        if (await db.Teams.AnyAsync(ct))
        {
            return; // already seeded
        }

        var now = DateTimeOffset.UtcNow;

        var eng = new Team { Name = "Engineering", Key = "ENG" };
        var des = new Team { Name = "Design", Key = "DES" };
        db.Teams.AddRange(eng, des);

        // Handles match the assignee/author strings used on the sample issues
        // and comments below: the same person, seen through the two conventions.
        var ana = new User { Handle = "ana", Name = "Ana Duarte", Role = WorkspaceRole.Admin };
        var ben = new User { Handle = "ben", Name = "Ben Ito", Role = WorkspaceRole.Member };
        var dana = new User { Handle = "dana", Name = "Dana Rue", Role = WorkspaceRole.Member };
        db.Users.AddRange(ana, ben, dana);

        // Ana is an admin and reaches every team without a membership row; she is
        // still put on Engineering because that is where she actually works, and
        // it keeps "my teams" meaningful for her.
        db.TeamMemberships.AddRange(
            new TeamMembership { UserId = ana.Id, TeamId = eng.Id },
            new TeamMembership { UserId = ben.Id, TeamId = eng.Id },
            new TeamMembership { UserId = dana.Id, TeamId = des.Id });

        db.ApiKeys.AddRange(
            DevKey(ana.Id, "ana's dev key", DevApiKeys.Admin),
            DevKey(ben.Id, "ben's dev key", DevApiKeys.EngMember),
            DevKey(dana.Id, "dana's dev key", DevApiKeys.DesMember));

        var engStates = DefaultWorkflow.CreateStates(eng.Id);
        var desStates = DefaultWorkflow.CreateStates(des.Id);
        db.WorkflowStates.AddRange(engStates);
        db.WorkflowStates.AddRange(desStates);

        WorkflowState EngState(WorkflowStateType type) => engStates.First(s => s.Type == type);

        var apiProject = new Project { TeamId = eng.Id, Name = "Public API", Description = "REST API surface for third-party integrations." };
        var perfProject = new Project { TeamId = eng.Id, Name = "Performance", Description = "Latency and throughput improvements." };
        var brandProject = new Project { TeamId = des.Id, Name = "Brand refresh", Description = "New visual identity rollout." };
        db.Projects.AddRange(apiProject, perfProject, brandProject);

        var bug = new Label { TeamId = eng.Id, Name = "bug", Color = "#eb5757" };
        var feature = new Label { TeamId = eng.Id, Name = "feature", Color = "#bb87fc" };
        var chore = new Label { TeamId = eng.Id, Name = "chore", Color = "#95a2b3" };
        db.Labels.AddRange(bug, feature, chore);

        var currentCycle = new Cycle
        {
            TeamId = eng.Id,
            Number = 1,
            Name = "Cycle 1",
            StartsAt = now.AddDays(-7),
            EndsAt = now.AddDays(7),
        };
        var nextCycle = new Cycle
        {
            TeamId = eng.Id,
            Number = 2,
            Name = "Cycle 2",
            StartsAt = now.AddDays(7),
            EndsAt = now.AddDays(21),
        };
        db.Cycles.AddRange(currentCycle, nextCycle);

        db.Issues.AddRange(
            new Issue
            {
                TeamId = eng.Id,
                Number = 1,
                Title = "Design authentication flow for API tokens",
                Description = "Support scoped personal access tokens with expiry.",
                Priority = IssuePriority.High,
                Estimate = 5,
                Assignee = "ana",
                StateId = EngState(WorkflowStateType.InProgress).Id,
                ProjectId = apiProject.Id,
                CycleId = currentCycle.Id,
                Position = 1.0,
                Labels = [feature],
                Comments =
                [
                    new Comment { Author = "ana", Body = "Let's follow the GitHub fine-grained token model." },
                    new Comment { Author = "ben", Body = "Agreed; expiry should default to 90 days." },
                ],
            },
            new Issue
            {
                TeamId = eng.Id,
                Number = 2,
                Title = "Rate limiting returns 500 instead of 429",
                Description = "When the bucket is exhausted the middleware throws.",
                Priority = IssuePriority.Urgent,
                Estimate = 2,
                Assignee = "ben",
                StateId = EngState(WorkflowStateType.Todo).Id,
                ProjectId = apiProject.Id,
                CycleId = currentCycle.Id,
                Position = 2.0,
                Labels = [bug],
            },
            new Issue
            {
                TeamId = eng.Id,
                Number = 3,
                Title = "Profile N+1 queries on the issue list endpoint",
                Priority = IssuePriority.Medium,
                Estimate = 3,
                StateId = EngState(WorkflowStateType.Backlog).Id,
                ProjectId = perfProject.Id,
                Position = 1.0,
                Labels = [chore],
            },
            new Issue
            {
                TeamId = eng.Id,
                Number = 4,
                Title = "Upgrade CI runners to .NET 10",
                Priority = IssuePriority.Low,
                Estimate = 1,
                Assignee = "ana",
                StateId = EngState(WorkflowStateType.Done).Id,
                CycleId = currentCycle.Id,
                Position = 1.0,
            },
            new Issue
            {
                TeamId = des.Id,
                Number = 1,
                Title = "New logo exploration",
                Priority = IssuePriority.Medium,
                Estimate = 8,
                Assignee = "dana",
                StateId = desStates.First(s => s.Type == WorkflowStateType.InProgress).Id,
                ProjectId = brandProject.Id,
                Position = 1.0,
            });

        await db.SaveChangesAsync(ct);
    }

    private static ApiKey DevKey(Guid userId, string name, string rawToken) => new()
    {
        UserId = userId,
        Name = name,
        KeyHash = ApiKeyToken.Hash(rawToken),
        Prefix = ApiKeyToken.PrefixOf(rawToken),
    };
}
