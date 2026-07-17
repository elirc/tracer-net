using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Tracer.Domain.Entities;

namespace Tracer.Infrastructure;

public class TracerDbContext(DbContextOptions<TracerDbContext> options) : DbContext(options)
{
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<WorkflowState> WorkflowStates => Set<WorkflowState>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Issue> Issues => Set<Issue>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Cycle> Cycles => Set<Cycle>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>(team =>
        {
            team.Property(t => t.Name).HasMaxLength(100);
            team.Property(t => t.Key).HasMaxLength(10);
            team.HasIndex(t => t.Key).IsUnique();
        });

        modelBuilder.Entity<WorkflowState>(state =>
        {
            state.Property(s => s.Name).HasMaxLength(100);
            state.Property(s => s.Color).HasMaxLength(9);
            state.HasOne(s => s.Team)
                .WithMany(t => t.WorkflowStates)
                .HasForeignKey(s => s.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            state.HasIndex(s => new { s.TeamId, s.Name }).IsUnique();
        });

        modelBuilder.Entity<Project>(project =>
        {
            project.Property(p => p.Name).HasMaxLength(200);
            project.HasOne(p => p.Team)
                .WithMany(t => t.Projects)
                .HasForeignKey(p => p.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Issue>(issue =>
        {
            issue.Property(i => i.Title).HasMaxLength(500);
            issue.HasIndex(i => new { i.TeamId, i.Number }).IsUnique();

            issue.HasOne(i => i.Team)
                .WithMany(t => t.Issues)
                .HasForeignKey(i => i.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            issue.HasOne(i => i.State)
                .WithMany(s => s.Issues)
                .HasForeignKey(i => i.StateId)
                .OnDelete(DeleteBehavior.Restrict);

            issue.HasOne(i => i.Project)
                .WithMany(p => p.Issues)
                .HasForeignKey(i => i.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            issue.HasOne(i => i.Cycle)
                .WithMany(c => c.Issues)
                .HasForeignKey(i => i.CycleId)
                .OnDelete(DeleteBehavior.SetNull);

            issue.HasMany(i => i.Labels)
                .WithMany(l => l.Issues)
                .UsingEntity(j => j.ToTable("IssueLabels"));
        });

        modelBuilder.Entity<Label>(label =>
        {
            label.Property(l => l.Name).HasMaxLength(100);
            label.Property(l => l.Color).HasMaxLength(9);
            label.HasOne(l => l.Team)
                .WithMany(t => t.Labels)
                .HasForeignKey(l => l.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            label.HasIndex(l => new { l.TeamId, l.Name }).IsUnique();
        });

        modelBuilder.Entity<Comment>(comment =>
        {
            comment.Property(c => c.Author).HasMaxLength(100);
            comment.HasOne(c => c.Issue)
                .WithMany(i => i.Comments)
                .HasForeignKey(c => c.IssueId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Cycle>(cycle =>
        {
            cycle.Property(c => c.Name).HasMaxLength(200);
            cycle.HasIndex(c => new { c.TeamId, c.Number }).IsUnique();
            cycle.HasOne(c => c.Team)
                .WithMany(t => t.Cycles)
                .HasForeignKey(c => c.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot natively order or compare DateTimeOffset columns.
        // Persist every DateTimeOffset as UTC ticks (long) so ordering,
        // filtering, and comparisons work in SQL.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToUtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>()
            .HaveConversion<NullableDateTimeOffsetToUtcTicksConverter>();
    }
}

/// <summary>Stores <see cref="DateTimeOffset"/> as UTC ticks so SQLite can order/compare it.</summary>
public sealed class DateTimeOffsetToUtcTicksConverter()
    : ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));

public sealed class NullableDateTimeOffsetToUtcTicksConverter()
    : ValueConverter<DateTimeOffset?, long?>(
        v => v.HasValue ? v.Value.UtcTicks : null,
        v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
