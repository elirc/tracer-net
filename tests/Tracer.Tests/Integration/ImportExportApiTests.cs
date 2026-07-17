using System.Net;
using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// Import and export. The properties under test are that an export is the same
/// question search answers, and that an import can be run twice.
/// </summary>
public class ImportExportApiTests : IClassFixture<TracerApiFactory>
{
    private readonly TracerApiFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _foreigner;

    public ImportExportApiTests(TracerApiFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAdminClient();
        _foreigner = factory.CreateDesMemberClient();
    }

    private sealed record TeamPayload(Guid Id, string Name, string Key);
    private sealed record LabelPayload(Guid Id, string Name, string Color);
    private sealed record IssuePayload(Guid Id, string Identifier, string Title, string State, string? Assignee);
    private sealed record ExportPayload(
        string Identifier, string ExternalId, string Title, string? Description, string Priority,
        int? Estimate, string? Assignee, string State, string? Project, string? Cycle,
        List<string> Labels, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record ReportPayload(bool DryRun, int Total, int Created, int Updated);
    private sealed record ActivityPayload(string Type, string? Field, string? OldValue, string? NewValue);
    private sealed record ActivityPage(List<ActivityPayload> Items, int Total);

    private async Task<TeamPayload> CreateTeamAsync(string name, string key)
    {
        var response = await _admin.PostAsJsonAsync("/api/teams", new { name, key });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TeamPayload>())!;
    }

    private async Task<TeamPayload> SeedTeamAsync(string key)
    {
        var teams = await _admin.GetFromJsonAsync<List<TeamPayload>>("/api/teams");
        return teams!.Single(t => t.Key == key);
    }

    private async Task<IssuePayload> CreateIssueAsync(Guid teamId, object body)
    {
        var response = await _admin.PostAsJsonAsync($"/api/teams/{teamId}/issues", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuePayload>())!;
    }

    private async Task<List<ExportPayload>> ExportJsonAsync(Guid teamId, string query = "")
    {
        var response = await _admin.GetAsync($"/api/teams/{teamId}/issues/export{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<ExportPayload>>())!;
    }

    private async Task<string> ExportCsvAsync(Guid teamId, string query = "")
    {
        var separator = query.Length == 0 ? "?" : "&";
        var response = await _admin.GetAsync($"/api/teams/{teamId}/issues/export{query}{separator}format=Csv");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<HttpResponseMessage> ImportAsync(Guid teamId, object body) =>
        await _admin.PostAsJsonAsync($"/api/teams/{teamId}/issues/import", body);

    private async Task<ReportPayload> ImportOkAsync(Guid teamId, object body)
    {
        var response = await ImportAsync(teamId, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReportPayload>())!;
    }

    private async Task<List<IssuePayload>> IssuesAsync(Guid teamId) =>
        (await _admin.GetFromJsonAsync<List<IssuePayload>>($"/api/teams/{teamId}/issues"))!;

    // ---- Export ----

    [Fact]
    public async Task Export_returns_a_teams_issues_as_json()
    {
        var team = await CreateTeamAsync("Export Json", "XJS");
        await CreateIssueAsync(team.Id, new { title = "exported", assignee = "ana", estimate = 3, priority = "High" });

        var rows = await ExportJsonAsync(team.Id);

        var only = Assert.Single(rows);
        Assert.Equal("XJS-1", only.Identifier);
        Assert.Equal("exported", only.Title);
        Assert.Equal("High", only.Priority);
        Assert.Equal(3, only.Estimate);
        Assert.Equal("ana", only.Assignee);
        Assert.Equal("Backlog", only.State);
    }

    [Fact]
    public async Task Export_names_references_rather_than_numbering_them()
    {
        var eng = await SeedTeamAsync("ENG");

        var rows = await ExportJsonAsync(eng.Id, "?q=authentication");

        var only = Assert.Single(rows);
        // A guid would mean nothing to a spreadsheet or another tracker — and
        // would mean something wrong if this payload were imported elsewhere.
        Assert.Equal("Public API", only.Project);
        Assert.Equal("In Progress", only.State);
        Assert.Contains("feature", only.Labels);
    }

    [Fact]
    public async Task Export_is_downloadable_with_a_filename()
    {
        var team = await CreateTeamAsync("Export File", "XFL");
        await CreateIssueAsync(team.Id, new { title = "anything" });

        var response = await _admin.GetAsync($"/api/teams/{team.Id}/issues/export?format=Csv");

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("XFL-issues-", response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task Csv_export_has_a_header_row_and_one_row_per_issue()
    {
        var team = await CreateTeamAsync("Export Csv", "XCS");
        await CreateIssueAsync(team.Id, new { title = "first" });
        await CreateIssueAsync(team.Id, new { title = "second" });

        var csv = await ExportCsvAsync(team.Id);
        var lines = csv.TrimEnd('\r', '\n').Split("\r\n");

        Assert.StartsWith("identifier,externalId,title,", lines[0].TrimStart('﻿'));
        Assert.Equal(3, lines.Length);
        Assert.Contains("first", csv);
        Assert.Contains("second", csv);
    }

    [Fact]
    public async Task Csv_export_survives_a_title_that_would_break_the_format()
    {
        var team = await CreateTeamAsync("Export Quoting", "XQU");
        await CreateIssueAsync(team.Id, new { title = "a title, with a comma and \"quotes\"" });

        var csv = await ExportCsvAsync(team.Id);

        Assert.Contains("\"a title, with a comma and \"\"quotes\"\"\"", csv);
    }

    [Fact]
    public async Task Csv_export_defuses_a_title_a_spreadsheet_would_execute()
    {
        var team = await CreateTeamAsync("Export Injection", "XIN");
        await CreateIssueAsync(team.Id, new { title = "=cmd|' /C calc'!A0" });

        var csv = await ExportCsvAsync(team.Id);

        // The cell must not reach Excel starting with '='.
        Assert.Contains("'=cmd|' /C calc'!A0", csv);
        Assert.DoesNotContain(",=cmd", csv);
    }

    /// <summary>
    /// Export does not have its own filters — it has search's. This is the test
    /// that would fail if someone forked the filter chain.
    /// </summary>
    [Fact]
    public async Task Export_reuses_the_search_filters()
    {
        var team = await CreateTeamAsync("Export Filters", "XFI");
        await CreateIssueAsync(team.Id, new { title = "ana's", assignee = "ana", priority = "Urgent" });
        await CreateIssueAsync(team.Id, new { title = "ben's", assignee = "ben", priority = "Low" });

        Assert.Equal("ana's", Assert.Single(await ExportJsonAsync(team.Id, "?assignee=ana")).Title);
        Assert.Equal("ben's", Assert.Single(await ExportJsonAsync(team.Id, "?priority=Low")).Title);
        Assert.Empty(await ExportJsonAsync(team.Id, "?assignee=ana&priority=Low"));
        // Including the wildcard escaping, which a hand-rolled filter would miss.
        Assert.Empty(await ExportJsonAsync(team.Id, "?q=%25"));
    }

    [Fact]
    public async Task Export_honours_the_sort_the_filters_ask_for()
    {
        var team = await CreateTeamAsync("Export Sort", "XSO");
        foreach (var title in new[] { "cherry", "apple", "banana" })
        {
            await CreateIssueAsync(team.Id, new { title });
        }

        var rows = await ExportJsonAsync(team.Id, "?sort=Title&order=Asc");

        Assert.Equal(["apple", "banana", "cherry"], rows.Select(r => r.Title).ToArray());
    }

    [Fact]
    public async Task Export_of_a_foreign_team_is_404()
    {
        var eng = await SeedTeamAsync("ENG");

        var response = await _foreigner.GetAsync($"/api/teams/{eng.Id}/issues/export");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Export_requires_a_credential()
    {
        var eng = await SeedTeamAsync("ENG");
        var anonymous = _factory.CreateAnonymousClient();

        var response = await anonymous.GetAsync($"/api/teams/{eng.Id}/issues/export");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Import ----

    [Fact]
    public async Task Import_creates_issues()
    {
        var team = await CreateTeamAsync("Import Create", "MCR");

        var report = await ImportOkAsync(team.Id, new
        {
            issues = new[]
            {
                new { externalId = "JIRA-1", title = "imported one", assignee = "ana" },
                new { externalId = "JIRA-2", title = "imported two", assignee = "ben" },
            },
        });

        Assert.Equal(2, report.Created);
        Assert.Equal(0, report.Updated);
        Assert.Equal(2, report.Total);
        Assert.False(report.DryRun);

        var issues = await IssuesAsync(team.Id);
        Assert.Equal(["imported one", "imported two"], issues.Select(i => i.Title).OrderBy(t => t).ToArray());
    }

    /// <summary>The headline property: the same payload twice is not twice the issues.</summary>
    [Fact]
    public async Task Importing_the_same_payload_twice_updates_rather_than_duplicates()
    {
        var team = await CreateTeamAsync("Import Idempotent", "MID");
        var payload = new
        {
            issues = new[] { new { externalId = "JIRA-100", title = "from jira" } },
        };

        var first = await ImportOkAsync(team.Id, payload);
        var second = await ImportOkAsync(team.Id, payload);

        Assert.Equal(1, first.Created);
        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Updated);
        Assert.Single(await IssuesAsync(team.Id));
    }

    [Fact]
    public async Task A_re_import_applies_changed_fields_to_the_mapped_issue()
    {
        var team = await CreateTeamAsync("Import Update", "MUP");
        await ImportOkAsync(team.Id, new
        {
            issues = new[] { new { externalId = "JIRA-7", title = "old title", assignee = "ana" } },
        });

        await ImportOkAsync(team.Id, new
        {
            issues = new[] { new { externalId = "JIRA-7", title = "new title", assignee = "ben" } },
        });

        var issue = Assert.Single(await IssuesAsync(team.Id));
        Assert.Equal("new title", issue.Title);
        Assert.Equal("ben", issue.Assignee);
    }

    /// <summary>
    /// An export carries each issue's identifier as its external id, so an export
    /// fed straight back in must recognise its own issues rather than clone them.
    /// </summary>
    [Fact]
    public async Task An_export_can_be_imported_back_into_the_team_it_came_from()
    {
        var team = await CreateTeamAsync("Import Round Trip", "MRT");
        await CreateIssueAsync(team.Id, new { title = "born here", assignee = "ana" });
        await CreateIssueAsync(team.Id, new { title = "also born here" });

        var exported = await ExportJsonAsync(team.Id);
        var report = await ImportOkAsync(team.Id, new
        {
            issues = exported.Select(e => new
            {
                externalId = e.ExternalId,
                title = e.Title,
                description = e.Description,
                priority = e.Priority,
                estimate = e.Estimate,
                assignee = e.Assignee,
                state = e.State,
                project = e.Project,
                labels = e.Labels,
            }).ToArray(),
        });

        Assert.Equal(0, report.Created);
        Assert.Equal(2, report.Updated);
        Assert.Equal(2, (await IssuesAsync(team.Id)).Count);
    }

    [Fact]
    public async Task Import_places_issues_in_the_state_they_name()
    {
        var team = await CreateTeamAsync("Import State", "MST");

        await ImportOkAsync(team.Id, new
        {
            issues = new[] { new { externalId = "JIRA-9", title = "in flight", state = "In Progress" } },
        });

        Assert.Equal("In Progress", Assert.Single(await IssuesAsync(team.Id)).State);
    }

    [Fact]
    public async Task Import_attaches_labels_by_name()
    {
        var eng = await SeedTeamAsync("ENG");

        await ImportOkAsync(eng.Id, new
        {
            issues = new[] { new { externalId = "JIRA-LBL", title = "a labelled import", labels = new[] { "bug" } } },
        });

        var rows = await ExportJsonAsync(eng.Id, "?q=a labelled import");
        Assert.Contains("bug", Assert.Single(rows).Labels);
    }

    // ---- Dry run ----

    [Fact]
    public async Task A_dry_run_reports_what_it_would_do_and_writes_nothing()
    {
        var team = await CreateTeamAsync("Import Dry", "MDR");

        var report = await ImportOkAsync(team.Id, new
        {
            dryRun = true,
            issues = new[] { new { externalId = "JIRA-3", title = "not yet" } },
        });

        Assert.True(report.DryRun);
        Assert.Equal(1, report.Created);
        Assert.Empty(await IssuesAsync(team.Id));
    }

    /// <summary>
    /// A green dry run is a promise about the run that follows, not a separate
    /// estimate of it — both build the same plan from the same code.
    /// </summary>
    [Fact]
    public async Task A_dry_run_predicts_the_real_run_exactly()
    {
        var team = await CreateTeamAsync("Import Dry Match", "MDM");
        await ImportOkAsync(team.Id, new { issues = new[] { new { externalId = "A", title = "already here" } } });

        var payload = new[]
        {
            new { externalId = "A", title = "updated" },
            new { externalId = "B", title = "created" },
        };

        var dry = await ImportOkAsync(team.Id, new { dryRun = true, issues = payload });
        var real = await ImportOkAsync(team.Id, new { dryRun = false, issues = payload });

        Assert.Equal((dry.Total, dry.Created, dry.Updated), (real.Total, real.Created, real.Updated));
        Assert.Equal(1, real.Created);
        Assert.Equal(1, real.Updated);
    }

    [Fact]
    public async Task A_dry_run_reports_the_same_errors_the_real_run_would()
    {
        var team = await CreateTeamAsync("Import Dry Errors", "MDE");
        var payload = new { issues = new[] { new { externalId = "X", title = "bad", state = "Nowhere" } } };

        var dry = await ImportAsync(team.Id, new { dryRun = true, payload.issues });
        var real = await ImportAsync(team.Id, new { dryRun = false, payload.issues });

        Assert.Equal(HttpStatusCode.BadRequest, dry.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, real.StatusCode);
        Assert.Empty(await IssuesAsync(team.Id));
    }

    // ---- Validation ----

    [Fact]
    public async Task An_unknown_state_is_rejected_and_names_the_row()
    {
        var team = await CreateTeamAsync("Import Bad State", "MBS");

        var response = await ImportAsync(team.Id, new
        {
            issues = new[]
            {
                new { externalId = "OK", title = "fine", state = "Backlog" },
                new { externalId = "BAD", title = "broken", state = "Atlantis" },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("issues[1].state", body);
        Assert.Contains("Atlantis", body);
        // All or nothing: the valid row must not have landed either.
        Assert.Empty(await IssuesAsync(team.Id));
    }

    [Fact]
    public async Task An_unknown_label_is_rejected()
    {
        var team = await CreateTeamAsync("Import Bad Label", "MBL");

        var response = await ImportAsync(team.Id, new
        {
            issues = new[] { new { externalId = "L", title = "labelled", labels = new[] { "nonexistent" } } },
        });

        // Import creates issues, not the team's vocabulary — a typo must not
        // quietly add a label to a list the whole team shares.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("issues[0].labels", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_project_is_rejected()
    {
        var team = await CreateTeamAsync("Import Bad Project", "MBP");

        var response = await ImportAsync(team.Id, new
        {
            issues = new[] { new { externalId = "P", title = "projected", project = "Ghost" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("issues[0].project", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Two_rows_claiming_the_same_external_id_are_rejected()
    {
        var team = await CreateTeamAsync("Import Dupe", "MDU");

        var response = await ImportAsync(team.Id, new
        {
            issues = new[]
            {
                new { externalId = "SAME", title = "first" },
                new { externalId = "SAME", title = "second" },
            },
        });

        // Applied in order, one would silently overwrite the other and the report
        // would call it two successes.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("issues[1].externalId", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_row_without_an_external_id_or_title_is_rejected()
    {
        var team = await CreateTeamAsync("Import Missing", "MMI");

        var noId = await ImportAsync(team.Id, new { issues = new[] { new { title = "anonymous" } } });
        var noTitle = await ImportAsync(team.Id, new { issues = new[] { new { externalId = "T" } } });

        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noTitle.StatusCode);
    }

    /// <summary>
    /// Import is not a back door around the workflow, for the same reason
    /// dragging a card between columns is not one.
    /// </summary>
    [Fact]
    public async Task An_import_may_not_make_a_transition_the_workflow_forbids()
    {
        var team = await CreateTeamAsync("Import Transition", "MTR");
        await ImportOkAsync(team.Id, new
        {
            issues = new[] { new { externalId = "W-1", title = "backlogged", state = "Backlog" } },
        });

        var response = await ImportAsync(team.Id, new
        {
            issues = new[] { new { externalId = "W-1", title = "backlogged", state = "Done" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("issues[0].state", await response.Content.ReadAsStringAsync());
        Assert.Equal("Backlog", Assert.Single(await IssuesAsync(team.Id)).State);
    }

    [Fact]
    public async Task A_new_issue_may_be_imported_straight_into_any_state()
    {
        var team = await CreateTeamAsync("Import New State", "MNS");

        // Creation is not a transition: there is no state it is moving from.
        var report = await ImportOkAsync(team.Id, new
        {
            issues = new[] { new { externalId = "N-1", title = "arrived finished", state = "Done" } },
        });

        Assert.Equal(1, report.Created);
        Assert.Equal("Done", Assert.Single(await IssuesAsync(team.Id)).State);
    }

    [Fact]
    public async Task Import_into_a_foreign_team_is_404()
    {
        var eng = await SeedTeamAsync("ENG");

        var response = await _foreigner.PostAsJsonAsync($"/api/teams/{eng.Id}/issues/import", new
        {
            issues = new[] { new { externalId = "X", title = "trespassing" } },
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_external_id_is_only_unique_within_a_team()
    {
        var first = await CreateTeamAsync("Import Team A", "MTA");
        var second = await CreateTeamAsync("Import Team B", "MTB");
        var payload = new { issues = new[] { new { externalId = "PROJ-1", title = "same id, different trackers" } } };

        var into_first = await ImportOkAsync(first.Id, payload);
        var into_second = await ImportOkAsync(second.Id, payload);

        // Two teams importing from two different trackers may both receive a
        // "PROJ-1", and they are not the same issue.
        Assert.Equal(1, into_first.Created);
        Assert.Equal(1, into_second.Created);
    }

    // ---- Audit trail ----

    [Fact]
    public async Task An_import_is_written_to_the_activity_log()
    {
        var team = await CreateTeamAsync("Import Activity", "MAC");
        await ImportOkAsync(team.Id, new
        {
            issues = new[] { new { externalId = "AUD-1", title = "created by import" } },
        });

        await ImportOkAsync(team.Id, new
        {
            issues = new[] { new { externalId = "AUD-1", title = "renamed by import", assignee = "ana" } },
        });

        var feed = await _admin.GetFromJsonAsync<ActivityPage>($"/api/teams/{team.Id}/activity");

        // A bulk import that quietly rewrites issues with no trail would leave
        // the audit log lying about what happened to them.
        Assert.Contains(feed!.Items, a => a.Type == "IssueCreated");
        Assert.Contains(feed.Items, a => a.Type == "IssueUpdated" && a.Field == "title"
            && a.OldValue == "created by import" && a.NewValue == "renamed by import");
        Assert.Contains(feed.Items, a => a.Type == "IssueAssigned" && a.NewValue == "ana");
    }
}
