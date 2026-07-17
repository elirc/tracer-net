using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Tests.Unit;

public class DbContextTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private TracerDbContext CreateContext()
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
            using var setup = new TracerDbContext(Options());
            setup.Database.EnsureCreated();
        }

        return new TracerDbContext(Options());
    }

    private DbContextOptions<TracerDbContext> Options() =>
        new DbContextOptionsBuilder<TracerDbContext>().UseSqlite(_connection).Options;

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task DateTimeOffset_roundtrips_and_orders_correctly_on_sqlite()
    {
        var early = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(9)); // 03:00 UTC
        var late = new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.FromHours(-5)); // 10:00 UTC

        await using (var db = CreateContext())
        {
            db.Teams.AddRange(
                new Team { Name = "Later", Key = "LAT", CreatedAt = late },
                new Team { Name = "Earlier", Key = "EAR", CreatedAt = early });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            // Ordering must happen in SQL, comparing instants (UTC), not raw local times.
            var ordered = await db.Teams.OrderBy(t => t.CreatedAt).Select(t => t.Key).ToListAsync();
            Assert.Equal(["EAR", "LAT"], ordered);

            var earlier = await db.Teams.SingleAsync(t => t.Key == "EAR");
            Assert.Equal(early.ToUniversalTime(), earlier.CreatedAt);
        }
    }

    [Fact]
    public async Task Team_key_must_be_unique()
    {
        await using var db = CreateContext();
        db.Teams.Add(new Team { Name = "One", Key = "DUP" });
        await db.SaveChangesAsync();

        db.Teams.Add(new Team { Name = "Two", Key = "DUP" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_team_cascades_to_states_projects_labels_and_cycles()
    {
        Guid teamId;
        await using (var db = CreateContext())
        {
            var team = new Team { Name = "Doomed", Key = "DOOM" };
            teamId = team.Id;
            db.Teams.Add(team);
            db.WorkflowStates.AddRange(DefaultWorkflow.CreateStates(team.Id));
            db.Projects.Add(new Project { TeamId = team.Id, Name = "P" });
            db.Labels.Add(new Label { TeamId = team.Id, Name = "l" });
            db.Cycles.Add(new Cycle { TeamId = team.Id, Number = 1, StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddDays(14) });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            db.Teams.Remove(await db.Teams.SingleAsync(t => t.Id == teamId));
            await db.SaveChangesAsync();

            Assert.False(await db.WorkflowStates.AnyAsync(s => s.TeamId == teamId));
            Assert.False(await db.Projects.AnyAsync(p => p.TeamId == teamId));
            Assert.False(await db.Labels.AnyAsync(l => l.TeamId == teamId));
            Assert.False(await db.Cycles.AnyAsync(c => c.TeamId == teamId));
        }
    }

    [Fact]
    public async Task Seeder_populates_teams_states_issues_and_is_idempotent()
    {
        await using var db = CreateContext();

        await DbSeeder.SeedAsync(db);
        await DbSeeder.SeedAsync(db); // second run must be a no-op

        Assert.Equal(2, await db.Teams.CountAsync());
        Assert.Equal(10, await db.WorkflowStates.CountAsync()); // 5 per team
        Assert.Equal(5, await db.Issues.CountAsync());
        Assert.True(await db.Comments.AnyAsync());
        Assert.True(await db.Cycles.AnyAsync());

        var engIssues = await db.Issues
            .Where(i => i.Team!.Key == "ENG")
            .Select(i => i.Number)
            .OrderBy(n => n)
            .ToListAsync();
        Assert.Equal([1, 2, 3, 4], engIssues);
    }

    [Fact]
    public void Default_workflow_has_five_ordered_states()
    {
        var states = DefaultWorkflow.CreateStates(Guid.NewGuid());

        Assert.Equal(5, states.Count);
        Assert.Equal(
            [WorkflowStateType.Backlog, WorkflowStateType.Todo, WorkflowStateType.InProgress, WorkflowStateType.Done, WorkflowStateType.Canceled],
            states.OrderBy(s => s.Position).Select(s => s.Type).ToArray());
    }
}
