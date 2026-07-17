using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Tests.Unit;

public class NotificationPolicyTests
{
    /// <summary>The things a watcher would want to act on.</summary>
    [Theory]
    [InlineData(ActivityType.CommentCreated)]
    [InlineData(ActivityType.IssueStateChanged)]
    [InlineData(ActivityType.IssueAssigned)]
    [InlineData(ActivityType.IssueRelationAdded)]
    [InlineData(ActivityType.IssueParentChanged)]
    [InlineData(ActivityType.IssueDeleted)]
    public void Notable_changes_notify(ActivityType type)
    {
        Assert.True(NotificationPolicy.IsNotable(type));
    }

    /// <summary>
    /// Everything here is in the audit log, and none of it is a reason to
    /// interrupt someone. An inbox that pings on "estimate 3 to 5" is an inbox
    /// people stop reading — and then it fails even for what mattered.
    /// </summary>
    [Theory]
    [InlineData(ActivityType.IssueCreated)]
    [InlineData(ActivityType.IssueUpdated)]
    [InlineData(ActivityType.IssueLabelAdded)]
    [InlineData(ActivityType.IssueLabelRemoved)]
    [InlineData(ActivityType.IssueRelationRemoved)]
    [InlineData(ActivityType.CommentUpdated)]
    [InlineData(ActivityType.CommentDeleted)]
    public void Unremarkable_changes_do_not_notify(ActivityType type)
    {
        Assert.False(NotificationPolicy.IsNotable(type));
    }
}
