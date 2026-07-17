using Tracer.Domain;
using Tracer.Domain.Entities;

namespace Tracer.Tests.Unit;

public class IssueRelationsTests
{
    private static readonly Guid Subject = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Fact]
    public void A_forward_kind_stores_the_subject_as_the_source()
    {
        var (source, target, type) = IssueRelations.Canonicalize(IssueRelationKind.Blocks, Subject, Other);

        Assert.Equal(Subject, source);
        Assert.Equal(Other, target);
        Assert.Equal(IssueRelationType.Blocks, type);
    }

    /// <summary>
    /// "I am blocked by that" is not a fourth kind of link, it is the same link
    /// written from the other end — so it swaps the endpoints rather than
    /// inventing a type.
    /// </summary>
    [Fact]
    public void A_reversed_kind_swaps_the_endpoints_rather_than_adding_a_type()
    {
        var (source, target, type) = IssueRelations.Canonicalize(IssueRelationKind.BlockedBy, Subject, Other);

        Assert.Equal(Other, source);
        Assert.Equal(Subject, target);
        Assert.Equal(IssueRelationType.Blocks, type);
    }

    [Fact]
    public void Duplicated_by_swaps_the_endpoints_too()
    {
        var (source, target, type) = IssueRelations.Canonicalize(IssueRelationKind.DuplicatedBy, Subject, Other);

        Assert.Equal(Other, source);
        Assert.Equal(Subject, target);
        Assert.Equal(IssueRelationType.Duplicates, type);
    }

    [Theory]
    [InlineData(IssueRelationKind.Blocks, IssueRelationKind.BlockedBy)]
    [InlineData(IssueRelationKind.BlockedBy, IssueRelationKind.Blocks)]
    [InlineData(IssueRelationKind.Duplicates, IssueRelationKind.DuplicatedBy)]
    [InlineData(IssueRelationKind.DuplicatedBy, IssueRelationKind.Duplicates)]
    [InlineData(IssueRelationKind.Relates, IssueRelationKind.Relates)]
    public void Storing_a_kind_and_reading_it_back_from_each_end_round_trips(
        IssueRelationKind requested,
        IssueRelationKind expectedFromTheOtherEnd)
    {
        var (source, _, type) = IssueRelations.Canonicalize(requested, Subject, Other);

        Assert.Equal(requested, IssueRelations.AsSeenFrom(type, fromSource: source == Subject));
        Assert.Equal(expectedFromTheOtherEnd, IssueRelations.AsSeenFrom(type, fromSource: source != Subject));
    }

    [Fact]
    public void A_symmetric_relation_reads_the_same_from_both_ends()
    {
        Assert.Equal(IssueRelationKind.Relates, IssueRelations.AsSeenFrom(IssueRelationType.Relates, true));
        Assert.Equal(IssueRelationKind.Relates, IssueRelations.AsSeenFrom(IssueRelationType.Relates, false));
    }

    // ---- Normalization: what lets a unique index see one fact as one row ----

    /// <summary>
    /// The heart of it. Said from either end, a symmetric relation must produce
    /// the identical tuple — a unique index compares tuples, not meanings, so
    /// without this the duplicate sails through and two rows record one fact.
    /// </summary>
    [Fact]
    public void A_symmetric_relation_normalizes_to_the_same_row_from_either_end()
    {
        var forward = IssueRelations.Canonicalize(IssueRelationKind.Relates, Subject, Other);
        var backward = IssueRelations.Canonicalize(IssueRelationKind.Relates, Other, Subject);

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Symmetric_normalization_holds_whichever_way_the_ids_sort()
    {
        // Not a fluke of these two particular Guids: try both orderings.
        var low = new Guid("00000000-0000-0000-0000-00000000000a");
        var high = new Guid("00000000-0000-0000-0000-00000000000b");

        Assert.Equal(
            IssueRelations.Canonicalize(IssueRelationKind.Relates, low, high),
            IssueRelations.Canonicalize(IssueRelationKind.Relates, high, low));
    }

    /// <summary>
    /// Directed relations must NOT be normalized: "A blocks B" and "B blocks A"
    /// are contradictory claims, not one fact said twice. Collapsing them would
    /// silently discard one of two different statements — and would quietly
    /// destroy the cycle detection, which exists precisely to notice that pair.
    /// </summary>
    [Fact]
    public void A_directed_relation_keeps_its_direction()
    {
        var forward = IssueRelations.Canonicalize(IssueRelationKind.Blocks, Subject, Other);
        var backward = IssueRelations.Canonicalize(IssueRelationKind.Blocks, Other, Subject);

        Assert.NotEqual(forward, backward);
    }

    [Fact]
    public void A_directed_relation_and_its_inverse_spelling_are_one_row()
    {
        // "Subject blocks Other" and "Other is blocked by Subject": same fact.
        Assert.Equal(
            IssueRelations.Canonicalize(IssueRelationKind.Blocks, Subject, Other),
            IssueRelations.Canonicalize(IssueRelationKind.BlockedBy, Other, Subject));
    }

    [Fact]
    public void Duplicates_and_its_inverse_spelling_are_one_row()
    {
        Assert.Equal(
            IssueRelations.Canonicalize(IssueRelationKind.Duplicates, Subject, Other),
            IssueRelations.Canonicalize(IssueRelationKind.DuplicatedBy, Other, Subject));
    }
}
