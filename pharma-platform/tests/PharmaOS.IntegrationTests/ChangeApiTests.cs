using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PharmaOS.Platform.Host;

namespace PharmaOS.IntegrationTests;

/// <summary>Change control lifecycle (FS §9.3.3, URS-QMS-009).</summary>
[Collection(PostgresFixture.Collection)]
[Trait("Req", "URS-QMS-009")]
public sealed class ChangeApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid Berlin = Guid.Parse("d0000000-0000-4000-8000-000000000010");

    private static readonly TestUser Owner = new(Guid.Parse("f1111111-0000-0000-0000-000000000001"), Tenant, "Olga Owner", "o.olga",
        Permissions.ChangeRead, Permissions.ChangeWrite, Permissions.EsigSign);
    private static readonly TestUser Qa = new(Guid.Parse("f2222222-0000-0000-0000-000000000002"), Tenant, "Quinn QA", "q.quinn",
        Permissions.ChangeRead, Permissions.ChangeApprove, Permissions.EsigSign);
    private static readonly TestUser Assignee = new(Guid.Parse("f3333333-0000-0000-0000-000000000003"), Tenant, "Ari Assignee", "a.ari",
        Permissions.ChangeRead, Permissions.ChangeWrite);
    /// <summary>Every change permission: segregation of duties must still stop them signing their own work.</summary>
    private static readonly TestUser Both = new(Guid.Parse("f4444444-0000-0000-0000-000000000004"), Tenant, "Bo Both", "b.both",
        Permissions.ChangeRead, Permissions.ChangeWrite, Permissions.ChangeApprove, Permissions.EsigSign);
    private static readonly TestUser Reader = new(Guid.Parse("f5555555-0000-0000-0000-000000000005"), Tenant, "Ray Reader", "r.ray",
        Permissions.ChangeRead);

    private readonly PlatformFactory _factory;

    public ChangeApiTests(PostgresFixture db) => _factory = new PlatformFactory(db);

    public void Dispose() => _factory.Dispose();

    private HttpClient As(TestUser u) => _factory.ClientFor(u);

    private static string Day(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    private async Task<JsonElement> GetAsync(string path) => await As(Reader).GetFromJsonAsync<JsonElement>(path);

    private Task<JsonElement> ChangeAsync(Guid id) => GetAsync($"/api/qms/v1/changes/{id}");

    private async Task<int> VersionOf(Guid id) => (await ChangeAsync(id)).GetProperty("rowVersion").GetInt32();

    private async Task<string> StatusOf(Guid id) => (await ChangeAsync(id)).GetProperty("status").GetString()!;

    private async Task<Guid> CreateAsync(TestUser user, object? body = null)
    {
        var res = await As(user).PostAsJsonAsync("/api/qms/v1/changes", body ?? new
        {
            siteId = Berlin, title = "Replace the labeller", description = "Swap the labeller for the new model.",
            justification = "End of life of the old model.", changeType = "PLANNED", changeCategory = "MAJOR",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> SendAsync(TestUser user, HttpMethod method, string path, object? body, Guid id, string? reason = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) }
            .WithHeaders(ifMatch: $"\"{await VersionOf(id)}\"", reason: reason);
        return await As(user).SendAsync(req);
    }

    private Task<HttpResponseMessage> TransitionAsync(TestUser user, Guid id, string action, string? reason = null) =>
        SendAsync(user, HttpMethod.Post, $"/api/qms/v1/changes/{id}/transitions", new { action, reason }, id);

    private Task<HttpResponseMessage> AssessAsync(TestUser user, Guid id, string area, bool impacted = true) =>
        SendAsync(user, HttpMethod.Put, $"/api/qms/v1/changes/{id}/impacts/{area}",
            new { isImpacted = impacted, assessment = $"{area} assessed: new labeller needs qualification.", actionRequired = impacted ? "Qualify it." : null }, id);

    private Task<HttpResponseMessage> AddTaskAsync(TestUser user, Guid id, Guid? assignee = null) =>
        SendAsync(user, HttpMethod.Post, $"/api/qms/v1/changes/{id}/tasks",
            new { phase = "IMPLEMENTATION", description = "Install and qualify the labeller.", assigneeId = assignee, dueDate = Day(30) }, id);

    private async Task<HttpResponseMessage> ChallengeAsync(TestUser user, Guid id, string action, string meaning, string? reason = null) =>
        await As(user).PostAsJsonAsync("/api/esig/v1/challenges",
            new { entity = "qms.change", recordId = id, recordVersion = await VersionOf(id), meaning, reason, acknowledge = true, action });

    private async Task<HttpResponseMessage> SignAsync(TestUser user, Guid id, string action, string meaning, string? reason = null)
    {
        var challenge = await ChallengeAsync(user, id, action, meaning, reason);
        if (challenge.StatusCode != HttpStatusCode.Created) return challenge;
        var cid = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        return await _factory.ClientFor(user with { AuthTime = DateTimeOffset.UtcNow }).PostAsync($"/api/esig/v1/challenges/{cid}/complete", null);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() : null;

    /// <summary>A change in ASSESSMENT with one impact assessment and one task, submitted for approval.</summary>
    private async Task<Guid> SubmittedAsync(TestUser owner)
    {
        var id = await CreateAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(owner, id, "submit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AssessAsync(owner, id, "QUALITY")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AddTaskAsync(owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(owner, id, "submit_for_approval")).StatusCode);
        return id;
    }

    [Fact]
    public async Task Raised_change_gets_a_number_the_site_and_defaults_and_is_validated()
    {
        var id = await CreateAsync(Owner);
        var change = await ChangeAsync(id);
        Assert.Matches(@"^CC-\d{4}-\d{5}$", change.GetProperty("ccNo").GetString());
        Assert.Equal("BER", change.GetProperty("siteCode").GetString());
        Assert.Equal("DRAFT", change.GetProperty("status").GetString());
        Assert.Equal("NONE", change.GetProperty("regulatoryImpact").GetString());
        Assert.False(change.GetProperty("isTemporary").GetBoolean());
        Assert.Equal(DateOnly.Parse(Day(90)), DateOnly.Parse(change.GetProperty("implementationDue").GetString()!));
        Assert.Equal("Olga Owner", change.GetProperty("ownerName").GetString());

        var listed = await GetAsync("/api/qms/v1/changes?status=DRAFT");
        Assert.Contains(listed.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);

        var noSite = await As(Owner).PostAsJsonAsync("/api/qms/v1/changes", new
            { title = "t", description = "d", justification = "j", changeType = "PLANNED", changeCategory = "MINOR" });
        Assert.Equal(HttpStatusCode.BadRequest, noSite.StatusCode);
        var badType = await As(Owner).PostAsJsonAsync("/api/qms/v1/changes", new
            { siteId = Berlin, title = "t", description = "d", justification = "j", changeType = "WHENEVER", changeCategory = "MINOR" });
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        var unknownSite = await As(Owner).PostAsJsonAsync("/api/qms/v1/changes", new
            { siteId = Guid.NewGuid(), title = "t", description = "d", justification = "j", changeType = "PLANNED", changeCategory = "MINOR" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownSite.StatusCode);
    }

    [Fact]
    public async Task A_temporary_change_needs_its_end_date_before_it_is_submitted()
    {
        var id = await CreateAsync(Owner, new
        {
            siteId = Berlin, title = "Temporary bypass", description = "d", justification = "j", changeType = "EMERGENCY", changeCategory = "MINOR",
            isTemporary = true,
        });
        var refused = await TransitionAsync(Owner, id, "submit");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("GUARD_FAILED", await CodeOf(refused));

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Owner, HttpMethod.Patch, $"/api/qms/v1/changes/{id}", new { isTemporary = (bool?)null }, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Owner, HttpMethod.Patch, $"/api/qms/v1/changes/{id}", new { temporaryEndDate = Day(60) }, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit")).StatusCode);
        Assert.Equal("ASSESSMENT", await StatusOf(id));
    }

    [Fact]
    public async Task Assessment_needs_an_impact_assessment_and_a_regulatory_one_for_a_major_regulatory_impact()
    {
        var id = await CreateAsync(Owner);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit")).StatusCode);
        Assert.Equal("GUARD_FAILED", await CodeOf(await TransitionAsync(Owner, id, "submit_for_approval")));

        var before = await VersionOf(id);
        Assert.Equal(HttpStatusCode.OK, (await AssessAsync(Owner, id, "QUALITY")).StatusCode);
        Assert.True(await VersionOf(id) > before, "assessing must change the version (ETag)");
        Assert.Equal(HttpStatusCode.BadRequest, (await AssessAsync(Owner, id, "CATERING")).StatusCode);

        // The same area again replaces the assessment instead of adding a second one.
        Assert.Equal(HttpStatusCode.OK, (await AssessAsync(Owner, id, "QUALITY", impacted: false)).StatusCode);
        var impacts = (await ChangeAsync(id)).GetProperty("impacts").EnumerateArray().ToList();
        var quality = Assert.Single(impacts);
        Assert.False(quality.GetProperty("isImpacted").GetBoolean());
        Assert.Equal("Olga Owner", quality.GetProperty("assessorName").GetString());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Owner, HttpMethod.Patch, $"/api/qms/v1/changes/{id}", new { regulatoryImpact = "MAJOR" }, id, "Variation needed")).StatusCode);
        var blocked = await TransitionAsync(Owner, id, "submit_for_approval");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AssessAsync(Owner, id, "REGULATORY")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit_for_approval")).StatusCode);
        Assert.Equal("APPROVAL", await StatusOf(id));

        var locked = await AssessAsync(Owner, id, "VALIDATION");
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Equal("WRONG_STATE", await CodeOf(locked));
    }

    [Fact]
    public async Task Full_lifecycle_with_return_signatures_tasks_and_the_post_implementation_review()
    {
        // The assignee must be known to PharmaOS: they have used it once.
        await CreateAsync(Assignee);
        var id = await CreateAsync(Owner);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AssessAsync(Owner, id, "QUALITY")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AssessAsync(Owner, id, "VALIDATION")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AddTaskAsync(Owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AddTaskAsync(Owner, id, Assignee.Sub)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddTaskAsync(Owner, id, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit_for_approval")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await TransitionAsync(Qa, id, "return")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Qa, id, "return", "Assess the impact on training too")).StatusCode);
        Assert.Equal("ASSESSMENT", await StatusOf(id));
        Assert.Equal(HttpStatusCode.OK, (await AssessAsync(Owner, id, "TRAINING")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit_for_approval")).StatusCode);

        Assert.Equal("MISSING_PERMISSION", await CodeOf(await ChallengeAsync(Owner, id, "approve", "APPROVED")));
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, id, "approve", "APPROVED")).StatusCode);
        Assert.Equal("IMPLEMENTATION", await StatusOf(id));

        // Tasks open and no effective date yet; with the date set, the open tasks still block it.
        Assert.Equal("GUARD_FAILED", await CodeOf(await ChallengeAsync(Qa, id, "verify_implementation", "VERIFIED")));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Owner, HttpMethod.Patch, $"/api/qms/v1/changes/{id}/implementation", new { effectiveDate = Day(10) }, id)).StatusCode);
        Assert.Equal("GUARD_FAILED", await CodeOf(await ChallengeAsync(Qa, id, "verify_implementation", "VERIFIED")));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Owner, HttpMethod.Post, $"/api/qms/v1/changes/{id}/tasks/1/complete", null, id)).StatusCode);
        Assert.Equal("GUARD_FAILED", await CodeOf(await ChallengeAsync(Qa, id, "verify_implementation", "VERIFIED")));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Assignee, HttpMethod.Post, $"/api/qms/v1/changes/{id}/tasks/2/complete", null, id)).StatusCode);
        Assert.Equal("TASK_NOT_OPEN", await CodeOf(await SendAsync(Owner, HttpMethod.Post, $"/api/qms/v1/changes/{id}/tasks/2/complete", null, id)));
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, id, "verify_implementation", "VERIFIED")).StatusCode);
        Assert.Equal("POST_REVIEW", await StatusOf(id));

        // The owner records the review (its conclusion is the reason); only then can QA close.
        Assert.Equal("ACTION_NOT_AVAILABLE", await CodeOf(await ChallengeAsync(Qa, id, "close", "VERIFIED")));
        Assert.Equal(HttpStatusCode.BadRequest, (await TransitionAsync(Owner, id, "complete_review")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "complete_review", "Labeller runs as intended; no deviations in 30 days.")).StatusCode);
        Assert.Equal("CLOSURE", await StatusOf(id));
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, id, "close", "VERIFIED")).StatusCode);

        var closed = await ChangeAsync(id);
        Assert.Equal("CLOSED", closed.GetProperty("status").GetString());
        Assert.Equal(DateOnly.Parse(Day(10)), DateOnly.Parse(closed.GetProperty("effectiveDate").GetString()!));
        Assert.Equal(["QUALITY", "TRAINING", "VALIDATION"], closed.GetProperty("impacts").EnumerateArray().Select(i => i.GetProperty("impactedArea").GetString()));
        var tasks = closed.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal("Ari Assignee", tasks[1].GetProperty("assigneeName").GetString());
        Assert.All(tasks, t => Assert.Equal("DONE", t.GetProperty("status").GetString()));

        var history = (await GetAsync($"/api/qms/v1/changes/{id}/history")).EnumerateArray().ToList();
        Assert.Equal(["DRAFT", "ASSESSMENT", "APPROVAL", "ASSESSMENT", "APPROVAL", "IMPLEMENTATION", "POST_REVIEW", "CLOSURE", "CLOSED"],
            history.Select(h => h.GetProperty("toState").GetString()));
        Assert.Equal("Labeller runs as intended; no deviations in 30 days.", history[^2].GetProperty("comment").GetString());

        // The approval covered the plan as it was; completed tasks and the effective date changed the record since.
        var sigs = (await GetAsync($"/api/esig/v1/records/qms.change/{id}/signatures")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["APPROVED", "VERIFIED", "VERIFIED"], sigs.Select(s => s.GetProperty("meaning").GetString()));
        Assert.False(sigs[0].GetProperty("appliesToCurrentVersion").GetBoolean());
        Assert.True(sigs[2].GetProperty("appliesToCurrentVersion").GetBoolean());
    }

    [Fact]
    public async Task A_change_with_no_task_cannot_be_verified_as_implemented()
    {
        var id = await CreateAsync(Owner);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AssessAsync(Owner, id, "QUALITY", impacted: false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit_for_approval")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, id, "approve", "APPROVED")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Owner, HttpMethod.Patch, $"/api/qms/v1/changes/{id}/implementation", new { effectiveDate = Day(2) }, id)).StatusCode);
        Assert.Equal("GUARD_FAILED", await CodeOf(await ChallengeAsync(Qa, id, "verify_implementation", "VERIFIED")));
    }

    [Fact]
    public async Task Nobody_approves_or_verifies_their_own_work_whatever_their_roles()
    {
        var id = await SubmittedAsync(Both);
        Assert.Equal("SEGREGATION_OF_DUTIES", await CodeOf(await ChallengeAsync(Both, id, "approve", "APPROVED")));
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, id, "approve", "APPROVED")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Both, HttpMethod.Post, $"/api/qms/v1/changes/{id}/tasks/1/complete", null, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Both, HttpMethod.Patch, $"/api/qms/v1/changes/{id}/implementation", new { effectiveDate = Day(5) }, id)).StatusCode);
        Assert.Equal("SEGREGATION_OF_DUTIES", await CodeOf(await ChallengeAsync(Both, id, "verify_implementation", "VERIFIED")));
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, id, "verify_implementation", "VERIFIED")).StatusCode);
    }

    [Fact]
    public async Task Cancelling_is_signed_needs_a_reason_and_belongs_to_QA_once_submitted()
    {
        var draft = await CreateAsync(Owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChallengeAsync(Owner, draft, "cancel", "CANCELLED")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Owner, draft, "cancel", "CANCELLED", "Raised twice")).StatusCode);
        Assert.Equal("CANCELLED", await StatusOf(draft));

        var assessing = await CreateAsync(Owner);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, assessing, "submit")).StatusCode);
        Assert.Equal("MISSING_PERMISSION", await CodeOf(await ChallengeAsync(Owner, assessing, "cancel", "CANCELLED", "No longer needed")));
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, assessing, "cancel", "CANCELLED", "No longer needed")).StatusCode);
        Assert.Equal("CANCELLED", await StatusOf(assessing));
        Assert.Equal("WRONG_STATE", await CodeOf(await AssessAsync(Owner, assessing, "QUALITY")));
    }

    [Fact]
    public async Task Changes_need_the_current_version_and_the_write_permission()
    {
        var id = await CreateAsync(Owner);
        var stale = new HttpRequestMessage(HttpMethod.Patch, $"/api/qms/v1/changes/{id}") { Content = JsonContent.Create(new { title = "x" }) }
            .WithHeaders(ifMatch: "\"99\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await As(Owner).SendAsync(stale)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(Reader, HttpMethod.Patch, $"/api/qms/v1/changes/{id}", new { title = "x" }, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Reader).PostAsJsonAsync("/api/qms/v1/changes", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Qa).PostAsJsonAsync("/api/qms/v1/changes", new { })).StatusCode);
    }
}
