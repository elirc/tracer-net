using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tracer.Infrastructure;

namespace Tracer.Tests.Unit;

/// <summary>
/// Guards against migration drift — the model changing without a migration to
/// carry the change to a real database.
///
/// <para>
/// This is the gap a sibling project shipped through: its integration harness
/// built the schema with <c>EnsureCreated</c>, which stamps the current model
/// straight onto the database and never looks at the migrations at all. So a
/// property added without <c>dotnet ef migrations add</c> worked in every test
/// and failed only in production, where <c>Migrate</c> runs and the column is
/// simply not there. These tests close it from both ends: one asserts the model
/// and the migrations agree, the other asserts the seeder runs against a database
/// built by <c>Migrate</c> rather than <c>EnsureCreated</c>.
/// </para>
/// </summary>
public class MigrationDriftTests
{
    [Fact]
    public void The_model_has_no_changes_that_a_migration_has_not_captured()
    {
        using var db = NewContext();

        // EF compares the live model against the last migration's snapshot. True
        // here means someone changed an entity or the DbContext configuration and
        // did not add a migration for it — the exact drift that EnsureCreated-based
        // tests wave through and Migrate later rejects in production.
        Assert.False(
            db.Database.HasPendingModelChanges(),
            "The EF model has changes not captured in a migration. Run: " +
            "dotnet ef migrations add <Name> --project src/Tracer.Infrastructure --startup-project src/Tracer.Api");
    }

    [Fact]
    public async Task The_seeder_runs_against_a_migrated_database()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        using var db = NewContext(connection);

        // Migrate, not EnsureCreated: the schema is whatever the migrations
        // actually produce. A migration missing a column the seeder writes would
        // throw right here rather than passing on a schema stamped from the model.
        await db.Database.MigrateAsync();
        await DbSeeder.SeedAsync(db);

        Assert.True(await db.Teams.AnyAsync(t => t.Key == "ENG"));
        Assert.True(await db.Teams.AnyAsync(t => t.Key == "DES"));
        Assert.Equal(3, await db.Users.CountAsync());
        // The seed creates sample issues, comments, and a cycle; if any column the
        // migrations forgot were involved, one of these inserts would have failed.
        Assert.True(await db.Issues.AnyAsync());
        Assert.True(await db.Comments.AnyAsync());
        Assert.True(await db.Cycles.AnyAsync());
    }

    private static TracerDbContext NewContext(SqliteConnection? connection = null)
    {
        var builder = new DbContextOptionsBuilder<TracerDbContext>();
        if (connection is null)
        {
            builder.UseSqlite("Data Source=:memory:");
        }
        else
        {
            builder.UseSqlite(connection);
        }

        return new TracerDbContext(builder.Options);
    }
}
