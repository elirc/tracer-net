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
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();
    public DbSet<IssueRelation> IssueRelations => Set<IssueRelation>();
    public DbSet<Activity> Activities => Set<Activity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>(team =>
        {
            team.Property(t => t.Name).HasMaxLength(100);
            team.Property(t => t.Key).HasMaxLength(10);
            team.HasIndex(t => t.Key).IsUnique();
        });

        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.Handle).HasMaxLength(100);
            user.Property(u => u.Name).HasMaxLength(200);
            user.HasIndex(u => u.Handle).IsUnique();
        });

        modelBuilder.Entity<ApiKey>(key =>
        {
            key.Property(k => k.Name).HasMaxLength(100);
            key.Property(k => k.KeyHash).HasMaxLength(64);
            key.Property(k => k.Prefix).HasMaxLength(16);

            // Authentication looks a key up by hash on every request, so this
            // index is the difference between an indexed seek and a table scan
            // of every credential in the workspace.
            key.HasIndex(k => k.KeyHash).IsUnique();

            key.HasOne(k => k.User)
                .WithMany(u => u.ApiKeys)
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamMembership>(membership =>
        {
            // One row per person per team: the unique index is what makes
            // "add to team" idempotent rather than quietly duplicating.
            membership.HasIndex(m => new { m.UserId, m.TeamId }).IsUnique();

            membership.HasOne(m => m.User)
                .WithMany(u => u.Memberships)
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            membership.HasOne(m => m.Team)
                .WithMany(t => t.Memberships)
                .HasForeignKey(m => m.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
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
            issue.Property(i => i.Assignee).HasMaxLength(100);
            issue.HasIndex(i => new { i.TeamId, i.Number }).IsUnique();

            // Search filters and board reads are always team-scoped.
            issue.HasIndex(i => new { i.TeamId, i.Assignee });
            issue.HasIndex(i => new { i.StateId, i.Position });

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

            // Deleting a parent releases its children rather than taking them
            // with it: the same call the product already makes for a deleted
            // project or cycle. Sub-issues are real work, and "I deleted the
            // umbrella ticket" should not silently mean "I deleted the six
            // tickets under it".
            issue.HasOne(i => i.Parent)
                .WithMany(i => i.Children)
                .HasForeignKey(i => i.ParentId)
                .OnDelete(DeleteBehavior.SetNull);

            issue.HasIndex(i => i.ParentId);

            issue.HasMany(i => i.Labels)
                .WithMany(l => l.Issues)
                .UsingEntity(j => j.ToTable("IssueLabels"));
        });

        modelBuilder.Entity<IssueRelation>(relation =>
        {
            // A relation only means anything while both ends exist, so it goes
            // when either does. Both sides cascade: unlike a parent, there is no
            // half of a link worth keeping.
            relation.HasOne(r => r.SourceIssue)
                .WithMany(i => i.OutgoingRelations)
                .HasForeignKey(r => r.SourceIssueId)
                .OnDelete(DeleteBehavior.Cascade);

            relation.HasOne(r => r.TargetIssue)
                .WithMany(i => i.IncomingRelations)
                .HasForeignKey(r => r.TargetIssueId)
                .OnDelete(DeleteBehavior.Cascade);

            // Reading an issue's relations asks both "what do I point at?" and
            // "what points at me?", so both directions are indexed.
            relation.HasIndex(r => new { r.SourceIssueId, r.Type });
            relation.HasIndex(r => new { r.TargetIssueId, r.Type });

            // The same pair may hold different kinds of link (A blocks B *and*
            // A duplicates B), but not the same one twice. This index — not the
            // controller's check — is what makes that true: two concurrent
            // requests can both pass a check before either writes.
            //
            // It only works because IssueRelations.Canonicalize puts symmetric
            // relations in a fixed endpoint order first. A unique index compares
            // tuples, not meanings, so without that normalization "A relates to
            // B" and "B relates to A" are two different tuples and this index
            // would wave the duplicate straight through.
            relation.HasIndex(r => new { r.SourceIssueId, r.TargetIssueId, r.Type }).IsUnique();
        });

        modelBuilder.Entity<Activity>(activity =>
        {
            activity.Property(a => a.IssueTitle).HasMaxLength(500);
            activity.Property(a => a.ActorHandle).HasMaxLength(100);
            activity.Property(a => a.Field).HasMaxLength(50);
            activity.Property(a => a.OldValue).HasMaxLength(500);
            activity.Property(a => a.NewValue).HasMaxLength(500);

            // The only relationship the log keeps. IssueId and ActorId are
            // deliberately plain columns — see the Activity docs: a cascade there
            // would erase the record of a deletion, which is the one thing an
            // audit log must survive.
            activity.HasOne(a => a.Team)
                .WithMany()
                .HasForeignKey(a => a.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            // Both feeds read newest-first within a scope, so both are indexed
            // that way: the timeline by issue, the team feed by team.
            activity.HasIndex(a => new { a.IssueId, a.CreatedAt });
            activity.HasIndex(a => new { a.TeamId, a.CreatedAt });
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
