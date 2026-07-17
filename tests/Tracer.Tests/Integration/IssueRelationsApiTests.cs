using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tracer.Domain;
using Tracer.Domain.Entities;
using Tracer.Infrastructure;

namespace Tracer.Tests.Integration;

public class IssueRelationsApiTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _client;

    public IssueRelationsApiTests(TracerApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAdminClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record IssuePayload(Guid Id, Guid TeamId, string Identifier, string Title, Guid? ParentId);
    private sealed record RelationPayload(Guid Id, string Kind, Guid IssueId, string Identifier, string Title, string State);

    private async Task<TeamPayload> CreateTeamAsync(string name, string key)
    {
        var created = await _client.PostAsJsonAsync("/api/teams", new { name, key });
        return (await created.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private async Task<IssuePayload> CreateIssueAsync(Guid teamId, string title, Guid? parentId = null)
    {
        var created = await _client.PostAsJsonAsync($"/api/teams/{teamId}/issues", new { title, parentId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private async Task<HttpResponseMessage> RelateAsync(Guid issueId, string kind, Guid otherId) =>
        await _client.PostAsJsonAsync($"/api/issues/{issueId}/relations", new { kind, issueId = otherId });

    private async Task<List<RelationPayload>> RelationsAsync(Guid issueId) =>
        (await _client.GetFromJsonAsync<List<RelationPayload>>($"/api/issues/{issueId}/relations"))!;

    // ---- Storing one row per fact ----

    [Fact]
    public async Task A_relation_reads_back_inverted_from_the_other_end()
    {
        var team = await CreateTeamAsync("Rel A", "RLA");
        var blocker = await CreateIssueAsync(team.Id, "must ship first");
        var blocked = await CreateIssueAsync(team.Id, "waits on the other");

        Assert.Equal(HttpStatusCode.Created, (await RelateAsync(blocker.Id, "Blocks", blocked.Id)).StatusCode);

        var fromBlocker = Assert.Single(await RelationsAsync(blocker.Id));
        Assert.Equal("Blocks", fromBlocker.Kind);
        Assert.Equal(blocked.Id, fromBlocker.IssueId);

        var fromBlocked = Assert.Single(await RelationsAsync(blocked.Id));
        Assert.Equal("BlockedBy", fromBlocked.Kind);
        Assert.Equal(blocker.Id, fromBlocked.IssueId);

        // The same row seen twice, not two rows.
        Assert.Equal(fromBlocker.Id, fromBlocked.Id);
    }

    /// <summary>
    /// Asking for "blocked by" stores the inverse row rather than a second kind
    /// of link, so the pair is indistinguishable from having said "blocks" at the
    /// other end.
    /// </summary>
    [Fact]
    public async Task Blocked_by_stores_the_same_row_as_blocks_from_the_far_end()
    {
        var team = await CreateTeamAsync("Rel B", "RLB");
        var blocker = await CreateIssueAsync(team.Id, "the blocker");
        var blocked = await CreateIssueAsync(team.Id, "the blocked");

        await RelateAsync(blocked.Id, "BlockedBy", blocker.Id);

        Assert.Equal("Blocks", (await RelationsAsync(blocker.Id)).Single().Kind);
        Assert.Equal("BlockedBy", (await RelationsAsync(blocked.Id)).Single().Kind);
    }

    [Fact]
    public async Task Relates_is_symmetric_and_reads_the_same_from_both_ends()
    {
        var team = await CreateTeamAsync("Rel C", "RLC");
        var first = await CreateIssueAsync(team.Id, "one");
        var second = await CreateIssueAsync(team.Id, "two");

        await RelateAsync(first.Id, "Relates", second.Id);

        Assert.Equal("Relates", (await RelationsAsync(first.Id)).Single().Kind);
        Assert.Equal("Relates", (await RelationsAsync(second.Id)).Single().Kind);
    }

    [Fact]
    public async Task Duplicates_inverts_to_duplicated_by()
    {
        var team = await CreateTeamAsync("Rel D", "RLD");
        var dupe = await CreateIssueAsync(team.Id, "the copy");
        var original = await CreateIssueAsync(team.Id, "the original");

        await RelateAsync(dupe.Id, "Duplicates", original.Id);

        Assert.Equal("Duplicates", (await RelationsAsync(dupe.Id)).Single().Kind);
        Assert.Equal("DuplicatedBy", (await RelationsAsync(original.Id)).Single().Kind);
    }

    [Fact]
    public async Task A_pair_may_hold_different_kinds_of_link_at_once()
    {
        var team = await CreateTeamAsync("Rel E", "RLE");
        var first = await CreateIssueAsync(team.Id, "one");
        var second = await CreateIssueAsync(team.Id, "two");

        Assert.Equal(HttpStatusCode.Created, (await RelateAsync(first.Id, "Blocks", second.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await RelateAsync(first.Id, "Relates", second.Id)).StatusCode);

        Assert.Equal(2, (await RelationsAsync(first.Id)).Count);
    }

    // ---- Guards ----

    [Fact]
    public async Task An_issue_cannot_be_related_to_itself()
    {
        var team = await CreateTeamAsync("Rel F", "RLF");
        var issue = await CreateIssueAsync(team.Id, "lonely");

        var response = await RelateAsync(issue.Id, "Blocks", issue.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task The_same_relation_twice_is_a_409()
    {
        var team = await CreateTeamAsync("Rel G", "RLG");
        var first = await CreateIssueAsync(team.Id, "one");
        var second = await CreateIssueAsync(team.Id, "two");

        await RelateAsync(first.Id, "Blocks", second.Id);
        var again = await RelateAsync(first.Id, "Blocks", second.Id);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    /// <summary>
    /// A relates to B and B relates to A are the same sentence, so the second is
    /// a duplicate — storing it would show the pair two identical links.
    /// </summary>
    [Fact]
    public async Task Relating_symmetrically_from_the_other_end_is_a_409()
    {
        var team = await CreateTeamAsync("Rel H", "RLH");
        var first = await CreateIssueAsync(team.Id, "one");
        var second = await CreateIssueAsync(team.Id, "two");

        await RelateAsync(first.Id, "Relates", second.Id);
        var mirrored = await RelateAsync(second.Id, "Relates", first.Id);

        Assert.Equal(HttpStatusCode.Conflict, mirrored.StatusCode);
        Assert.Single(await RelationsAsync(first.Id));
    }

    /// <summary>
    /// The 409 above is the controller being polite. This asserts the thing that
    /// is actually true: the database refuses the duplicate row on its own.
    /// Without an index behind it, "these are already linked" is only a check —
    /// and two concurrent requests both pass a check before either writes.
    /// </summary>
    [Fact]
    public async Task The_database_itself_refuses_a_duplicate_relation()
    {
        var team = await CreateTeamAsync("Rel V", "RLV");
        var first = await CreateIssueAsync(team.Id, "one");
        var second = await CreateIssueAsync(team.Id, "two");
        await RelateAsync(first.Id, "Blocks", second.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TracerDbContext>();
        db.IssueRelations.Add(new IssueRelation
        {
            SourceIssueId = first.Id,
            TargetIssueId = second.Id,
            Type = IssueRelationType.Blocks,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// And the symmetric case, which is the one that needed normalization to be
    /// protectable at all: stored naively, the mirror row is a different tuple
    /// and the index would never see it.
    /// </summary>
    [Fact]
    public async Task The_database_itself_refuses_a_mirrored_symmetric_relation()
    {
        var team = await CreateTeamAsync("Rel W", "RLW");
        var first = await CreateIssueAsync(team.Id, "one");
        var second = await CreateIssueAsync(team.Id, "two");
        await RelateAsync(first.Id, "Relates", second.Id);

        // Canonicalize the mirror spelling exactly as the controller would; it
        // must land on the row that already exists.
        var (sourceId, targetId, type) =
            IssueRelations.Canonicalize(IssueRelationKind.Relates, second.Id, first.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TracerDbContext>();
        db.IssueRelations.Add(new IssueRelation
        {
            SourceIssueId = sourceId,
            TargetIssueId = targetId,
            Type = type,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Blocking_in_a_circle_is_refused()
    {
        var team = await CreateTeamAsync("Rel I", "RLI");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");

        await RelateAsync(a.Id, "Blocks", b.Id);
        var response = await RelateAsync(b.Id, "Blocks", a.Id);

        // A deadlock stated as data: neither issue could ever start.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Blocking_in_a_longer_circle_is_refused()
    {
        var team = await CreateTeamAsync("Rel J", "RLJ");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");
        var c = await CreateIssueAsync(team.Id, "c");

        await RelateAsync(a.Id, "Blocks", b.Id);
        await RelateAsync(b.Id, "Blocks", c.Id);
        var response = await RelateAsync(c.Id, "Blocks", a.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>
    /// The inverse spelling must hit the same guard: it stores the same row, so
    /// letting it through would be a back door around the cycle check.
    /// </summary>
    [Fact]
    public async Task A_circle_spelled_as_blocked_by_is_refused_too()
    {
        var team = await CreateTeamAsync("Rel K", "RLK");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");

        await RelateAsync(a.Id, "Blocks", b.Id);
        var response = await RelateAsync(a.Id, "BlockedBy", b.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_diamond_of_blockers_is_allowed()
    {
        // a blocks b, a blocks c, b blocks d, c blocks d: no cycle, just fan-out.
        var team = await CreateTeamAsync("Rel L", "RLL");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");
        var c = await CreateIssueAsync(team.Id, "c");
        var d = await CreateIssueAsync(team.Id, "d");

        Assert.Equal(HttpStatusCode.Created, (await RelateAsync(a.Id, "Blocks", b.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await RelateAsync(a.Id, "Blocks", c.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await RelateAsync(b.Id, "Blocks", d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await RelateAsync(c.Id, "Blocks", d.Id)).StatusCode);
    }

    [Fact]
    public async Task Duplicating_in_a_circle_is_refused()
    {
        var team = await CreateTeamAsync("Rel M", "RLM");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");

        await RelateAsync(a.Id, "Duplicates", b.Id);
        var response = await RelateAsync(b.Id, "Duplicates", a.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Relating_in_a_circle_is_fine_because_relates_has_no_direction()
    {
        var team = await CreateTeamAsync("Rel N", "RLN");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");
        var c = await CreateIssueAsync(team.Id, "c");

        await RelateAsync(a.Id, "Relates", b.Id);
        await RelateAsync(b.Id, "Relates", c.Id);
        var response = await RelateAsync(c.Id, "Relates", a.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Relating_across_teams_is_refused()
    {
        var first = await CreateTeamAsync("Rel O", "RLO");
        var second = await CreateTeamAsync("Rel P", "RLP");
        var here = await CreateIssueAsync(first.Id, "ours");
        var there = await CreateIssueAsync(second.Id, "theirs");

        var response = await RelateAsync(here.Id, "Blocks", there.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Relating_to_an_unknown_issue_is_refused()
    {
        var team = await CreateTeamAsync("Rel Q", "RLQ");
        var issue = await CreateIssueAsync(team.Id, "real");

        var response = await RelateAsync(issue.Id, "Blocks", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_relation_target_is_a_400_naming_the_field()
    {
        var team = await CreateTeamAsync("Rel R", "RLR");
        var issue = await CreateIssueAsync(team.Id, "real");

        var response = await _client.PostAsJsonAsync($"/api/issues/{issue.Id}/relations", new { kind = "Blocks" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Deletion ----

    [Fact]
    public async Task A_relation_can_be_cut_from_either_end()
    {
        var team = await CreateTeamAsync("Rel S", "RLS");
        var blocker = await CreateIssueAsync(team.Id, "blocker");
        var blocked = await CreateIssueAsync(team.Id, "blocked");
        await RelateAsync(blocker.Id, "Blocks", blocked.Id);
        var relation = (await RelationsAsync(blocked.Id)).Single();

        // Cut it from the end that did not create it.
        var deleted = await _client.DeleteAsync($"/api/issues/{blocked.Id}/relations/{relation.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Empty(await RelationsAsync(blocker.Id));
        Assert.Empty(await RelationsAsync(blocked.Id));
    }

    [Fact]
    public async Task A_relation_belonging_to_other_issues_is_404_on_this_one()
    {
        var team = await CreateTeamAsync("Rel T", "RLT");
        var a = await CreateIssueAsync(team.Id, "a");
        var b = await CreateIssueAsync(team.Id, "b");
        var bystander = await CreateIssueAsync(team.Id, "uninvolved");
        await RelateAsync(a.Id, "Blocks", b.Id);
        var relation = (await RelationsAsync(a.Id)).Single();

        var response = await _client.DeleteAsync($"/api/issues/{bystander.Id}/relations/{relation.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_issue_takes_its_relations_with_it()
    {
        var team = await CreateTeamAsync("Rel U", "RLU");
        var doomed = await CreateIssueAsync(team.Id, "doomed");
        var survivor = await CreateIssueAsync(team.Id, "survivor");
        await RelateAsync(doomed.Id, "Blocks", survivor.Id);
        Assert.Single(await RelationsAsync(survivor.Id));

        await _client.DeleteAsync($"/api/issues/{doomed.Id}");

        Assert.Empty(await RelationsAsync(survivor.Id));
    }

    // ---- Authorization ----

    [Fact]
    public async Task Another_teams_issue_relations_are_out_of_reach()
    {
        var teams = await _client.GetFromJsonAsync<List<TeamPayload>>("/api/teams");
        var eng = teams!.Single(t => t.Key == "ENG");
        var issue = await CreateIssueAsync(eng.Id, "engineering only");
        var foreigner = _factory.CreateDesMemberClient();

        Assert.Equal(HttpStatusCode.NotFound, (await foreigner.GetAsync($"/api/issues/{issue.Id}/relations")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await foreigner.PostAsJsonAsync($"/api/issues/{issue.Id}/relations",
                new { kind = "Blocks", issueId = Guid.NewGuid() })).StatusCode);
    }
}
