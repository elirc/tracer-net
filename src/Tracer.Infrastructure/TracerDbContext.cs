using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();
    public DbSet<IssueRelation> IssueRelations => Set<IssueRelation>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<IssueSubscription> IssueSubscriptions => Set<IssueSubscription>();
    public DbSet<Notification> Notifications => Set<Notification>();

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
            issue.Property(i => i.ExternalId).HasMaxLength(200);
            issue.HasIndex(i => new { i.TeamId, i.Number }).IsUnique();

            // Rotated by the update paths and checked by EF on every write, so two
            // concurrent edits cannot silently overwrite one another — the loser
            // updates zero rows and surfaces as a 409 rather than a lost change.
            issue.Property(i => i.Version).IsConcurrencyToken();

            // An external id names one issue in a team, or none. The filter is
            // what makes that true without also claiming that the many issues
            // never imported from anywhere are all "the same" null-ided issue —
            // and it says so explicitly rather than leaning on SQLite's habit of
            // treating NULLs as distinct in a unique index, which is a provider
            // quirk to depend on rather than a rule to state.
            issue.HasIndex(i => new { i.TeamId, i.ExternalId })
                .IsUnique()
                .HasFilter("\"ExternalId\" IS NOT NULL");

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

            // Deleting a milestone releases its issues rather than deleting them,
            // exactly as a deleted project or cycle does: the milestone was a
            // grouping, and the work grouped under it outlives the target.
            issue.HasOne(i => i.Milestone)
                .WithMany(m => m.Issues)
                .HasForeignKey(i => i.MilestoneId)
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

        modelBuilder.Entity<Webhook>(webhook =>
        {
            webhook.Property(w => w.Name).HasMaxLength(100);
            webhook.Property(w => w.Url).HasMaxLength(2000);
            webhook.Property(w => w.Secret).HasMaxLength(100);

            // Stored as a sorted, comma-joined list rather than a join table.
            // The only question ever asked of it is "does this webhook want this
            // event?", answered for the handful of webhooks one team has, so a
            // table to make it queryable would buy nothing and cost a join on
            // every dispatch. The comparer is not optional: without one EF
            // compares the List by reference, decides it never changes, and
            // silently drops every edit to a subscription.
            webhook.Property(w => w.Events)
                .HasMaxLength(500)
                .HasConversion(
                    events => string.Join(',', events.Select(e => e.ToString())),
                    stored => stored.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(Enum.Parse<WebhookEvent>)
                        .ToList(),
                    new ValueComparer<List<WebhookEvent>>(
                        (left, right) => left!.SequenceEqual(right!),
                        events => events.Aggregate(0, (hash, e) => HashCode.Combine(hash, e.GetHashCode())),
                        events => events.ToList()));

            webhook.HasOne(w => w.Team)
                .WithMany()
                .HasForeignKey(w => w.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            webhook.HasIndex(w => w.TeamId);
        });

        modelBuilder.Entity<WebhookDelivery>(delivery =>
        {
            delivery.Property(d => d.Error).HasMaxLength(500);

            delivery.HasOne(d => d.Webhook)
                .WithMany(w => w.Deliveries)
                .HasForeignKey(d => d.WebhookId)
                .OnDelete(DeleteBehavior.Cascade);

            // The worker's only query: what is owed, oldest first. Without this
            // index, draining the outbox scans every delivery ever made.
            delivery.HasIndex(d => new { d.Status, d.NextAttemptAt });

            // The team's view of their own log, newest first.
            delivery.HasIndex(d => new { d.WebhookId, d.CreatedAt });
        });

        modelBuilder.Entity<IssueSubscription>(subscription =>
        {
            // One row per person per issue: the unique index is what makes
            // auto-subscribe idempotent under concurrency rather than piling up
            // duplicate watchers that each notify separately.
            subscription.HasIndex(s => new { s.UserId, s.IssueId }).IsUnique();

            subscription.HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            subscription.HasOne(s => s.Issue)
                .WithMany()
                .HasForeignKey(s => s.IssueId)
                .OnDelete(DeleteBehavior.Cascade);

            // Fan-out's only query: who watches this issue.
            subscription.HasIndex(s => s.IssueId);
        });

        modelBuilder.Entity<Notification>(notification =>
        {
            // A change fans out to a watcher exactly once. The unique index makes
            // that a guarantee rather than a hope, so a retried or double-fired
            // fan-out cannot stack the same event in one inbox twice.
            notification.HasIndex(n => new { n.UserId, n.ActivityId }).IsUnique();

            notification.HasOne(n => n.User)
                .WithMany()
                .HasForeignKey(n => n.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // The notification points at the activity rather than copying it. The
            // activity is immutable and cascades only with its team, so an inbox
            // item survives the deletion of its issue exactly as the audit entry
            // does.
            notification.HasOne(n => n.Activity)
                .WithMany()
                .HasForeignKey(n => n.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);

            // The inbox reads one user's notifications newest-first, and the badge
            // counts their unread ones; this index serves both.
            notification.HasIndex(n => new { n.UserId, n.ReadAt, n.CreatedAt });
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

        modelBuilder.Entity<SavedView>(view =>
        {
            view.Property(v => v.Name).HasMaxLength(100);

            view.HasOne(v => v.Team)
                .WithMany(t => t.SavedViews)
                .HasForeignKey(v => v.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            // A personal view belongs to its owner and goes when they do. A team
            // view has no owner (OwnerUserId is null), so deleting a user cannot
            // take the team's shared views with them.
            view.HasOne(v => v.Owner)
                .WithMany(u => u.SavedViews)
                .HasForeignKey(v => v.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);

            // "At most one default per team" is a data invariant, so the database
            // enforces it. A filtered unique index constrains only the rows that
            // claim to be the default and leaves every other view unconstrained.
            view.HasIndex(v => v.TeamId)
                .IsUnique()
                .HasFilter("\"IsDefault\" = 1");

            view.HasIndex(v => new { v.TeamId, v.Scope, v.OwnerUserId });
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

        modelBuilder.Entity<Milestone>(milestone =>
        {
            milestone.Property(m => m.Name).HasMaxLength(200);

            milestone.HasOne(m => m.Team)
                .WithMany()
                .HasForeignKey(m => m.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            // A milestone belongs to its project and goes when the project does —
            // a roadmap target with no project left to land on is nothing.
            milestone.HasOne(m => m.Project)
                .WithMany()
                .HasForeignKey(m => m.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            // The roadmap reads a team's milestones in target-date order.
            milestone.HasIndex(m => new { m.TeamId, m.TargetDate });
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
