using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PharmaOS.Platform.Host;

namespace PharmaOS.IntegrationTests;

/// <summary>CAPA lifecycle and its link to deviations (FS §9.3.1–9.3.2, URS-QMS-006..008).</summary>
[Collection(PostgresFixture.Collection)]
[Trait("Req", "URS-QMS-006")]
public sealed class CapaApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid Berlin = Guid.Parse("d0000000-0000-4000-8000-000000000010");

    private static readonly TestUser Owner = new(Guid.Parse("c1111111-0000-0000-0000-000000000001"), Tenant, "Cara Owner", "c.owner",
        Permissions.CapaRead, Permissions.CapaWrite, Permissions.DeviationRead, Permissions.DeviationWrite, Permissions.EsigSign);
    private static readonly TestUser Qa = new(Guid.Parse("c2222222-0000-0000-0000-000000000002"), Tenant, "Quentin QA", "q.quentin",
        Permissions.CapaRead, Permissions.CapaApprove, Permissions.DeviationRead, Permissions.DeviationTriage, Permissions.DeviationApprove,
        Permissions.EsigSign);
    private static readonly TestUser Assignee = new(Guid.Parse("c3333333-0000-0000-0000-000000000003"), Tenant, "Abe Assignee", "a.assignee",
        Permissions.CapaRead, Permissions.CapaWrite);
    /// <summary>Every CAPA permission: segregation of duties must still stop them signing their own work.</summary>
    private static readonly TestUser Both = new(Guid.Parse("c4444444-0000-0000-0000-000000000004"), Tenant, "Bea Both", "b.both",
        Permissions.CapaRead, Permissions.CapaWrite, Permissions.CapaApprove, Permissions.EsigSign);
    private static readonly TestUser Reader = new(Guid.Parse("c5555555-0000-0000-0000-000000000005"), Tenant, "Rob Reader", "r.rob",
        Permissions.CapaRead, Permissions.DeviationRead);

    private readonly PlatformFactory _factory;

    public CapaApiTests(PostgresFixture db) => _factory = new PlatformFactory(db);

    public void Dispose() => _factory.Dispose();

    private HttpClient As(TestUser u) => _factory.ClientFor(u);

    private static string Day(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    private async Task<JsonElement> GetAsync(string path) => await As(Reader).GetFromJsonAsync<JsonElement>(path);

    private async Task<int> VersionOf(Guid id) => (await GetAsync($"/api/qms/v1/capas/{id}")).GetProperty("rowVersion").GetInt32();

    private async Task<string> StatusOf(Guid id) => (await GetAsync($"/api/qms/v1/capas/{id}")).GetProperty("status").GetString()!;

    private async Task<Guid> CreateCapaAsync(TestUser user, object? body = null)
    {
        var res = await As(user).PostAsJsonAsync("/api/qms/v1/capas", body ?? new
        {
            sourceType = "OTHER", siteId = Berlin, title = "Prevent label misprints", problemStatement = "Missing digits on carton labels.",
            capaType = "CORRECTIVE_AND_PREVENTIVE", priority = "HIGH",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> SendAsync(TestUser user, HttpMethod method, string path, object? body, Guid capaId)
    {
        var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) }
            .WithHeaders(ifMatch: $"\"{await VersionOf(capaId)}\"");
        return await As(user).SendAsync(req);
    }

    private Task<HttpResponseMessage> AddActionAsync(TestUser user, Guid id, Guid? assignee = null) =>
        SendAsync(user, HttpMethod.Post, $"/api/qms/v1/capas/{id}/actions",
            new { actionType = "CORRECTIVE", description = "Replace the print head and add it to preventive maintenance.", assigneeId = assignee, dueDate = Day(14) }, id);

    private Task<HttpResponseMessage> DefineCheckAsync(TestUser user, Guid id) =>
        SendAsync(user, HttpMethod.Put, $"/api/qms/v1/capas/{id}/effectiveness",
            new { criteria = "No label misprint deviations for 3 months.", method = "Trend review", plannedDate = Day(100) }, id);

    private Task<HttpResponseMessage> TransitionAsync(TestUser user, Guid id, string action, string? reason = null) =>
        SendAsync(user, HttpMethod.Post, $"/api/qms/v1/capas/{id}/transitions", new { action, reason }, id);

    private async Task<HttpResponseMessage> ChallengeAsync(TestUser user, string entity, Guid id, int version, string action, string meaning, string? reason = null) =>
        await As(user).PostAsJsonAsync("/api/esig/v1/challenges",
            new { entity, recordId = id, recordVersion = version, meaning, reason, acknowledge = true, action });

    private async Task<HttpResponseMessage> SignAsync(TestUser user, string entity, Guid id, int version, string action, string meaning, string? reason = null)
    {
        var challenge = await ChallengeAsync(user, entity, id, version, action, meaning, reason);
        if (challenge.StatusCode != HttpStatusCode.Created) return challenge;
        var cid = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        return await _factory.ClientFor(user with { AuthTime = DateTimeOffset.UtcNow }).PostAsync($"/api/esig/v1/challenges/{cid}/complete", null);
    }

    private async Task<HttpResponseMessage> SignCapaAsync(TestUser user, Guid id, string action, string meaning, string? reason = null) =>
        await SignAsync(user, "qms.capa", id, await VersionOf(id), action, meaning, reason);

    private static async Task<string?> CodeOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() : null;

    /// <summary>A CAPA with one action and the check defined, submitted for plan review.</summary>
    private async Task<Guid> PlannedCapaAsync(TestUser owner, object? createBody = null)
    {
        var id = await CreateCapaAsync(owner, createBody);
        Assert.Equal(HttpStatusCode.OK, (await AddActionAsync(owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DefineCheckAsync(owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(owner, id, "submit_plan")).StatusCode);
        return id;
    }

    [Fact]
    public async Task Raised_from_a_deviation_it_takes_the_site_and_is_listed_with_it()
    {
        var deviation = await As(Owner).PostAsJsonAsync("/api/qms/v1/deviations", new
        {
            siteId = Guid.Parse("d0000000-0000-4000-8000-000000000011"), title = "Glove tear", description = "d", classification = "MINOR",
            deviationType = "PROCESS", occurredAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
        });
        var dev = await deviation.Content.ReadFromJsonAsync<JsonElement>();
        var devId = dev.GetProperty("id").GetGuid();

        var id = await CreateCapaAsync(Owner, new
        {
            sourceType = "DEVIATION", sourceId = devId, title = "Glove integrity", problemStatement = "Tear found", capaType = "CORRECTIVE",
            priority = "MEDIUM",
        });
        var capa = await GetAsync($"/api/qms/v1/capas/{id}");
        Assert.Matches(@"^CAPA-\d{4}-\d{5}$", capa.GetProperty("capaNo").GetString());
        Assert.Equal("HAM", capa.GetProperty("siteCode").GetString());
        Assert.Equal(dev.GetProperty("deviationNo").GetString(), capa.GetProperty("sourceNo").GetString());
        Assert.Equal(DateOnly.Parse(Day(90)), DateOnly.Parse(capa.GetProperty("dueDate").GetString()!));
        Assert.Equal("DRAFT", capa.GetProperty("status").GetString());

        var linked = await GetAsync($"/api/qms/v1/capas?sourceId={devId}");
        Assert.Equal(id, Assert.Single(linked.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var noSite = await As(Owner).PostAsJsonAsync("/api/qms/v1/capas", new
            { sourceType = "OTHER", title = "t", problemStatement = "p", capaType = "CORRECTIVE", priority = "LOW" });
        Assert.Equal(HttpStatusCode.BadRequest, noSite.StatusCode);
        var unknown = await As(Owner).PostAsJsonAsync("/api/qms/v1/capas", new
            { sourceType = "DEVIATION", sourceId = Guid.NewGuid(), title = "t", problemStatement = "p", capaType = "CORRECTIVE", priority = "LOW" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Plan_needs_an_action_and_an_effectiveness_check()
    {
        var id = await CreateCapaAsync(Owner);
        var empty = await TransitionAsync(Owner, id, "submit_plan");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("GUARD_FAILED", await CodeOf(empty));

        var before = await VersionOf(id);
        Assert.Equal(HttpStatusCode.OK, (await AddActionAsync(Owner, id)).StatusCode);
        Assert.True(await VersionOf(id) > before, "planning an action must change the CAPA's version (ETag)");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await TransitionAsync(Owner, id, "submit_plan")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DefineCheckAsync(Owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit_plan")).StatusCode);
        Assert.Equal("PLAN_REVIEW", await StatusOf(id));

        var locked = await AddActionAsync(Owner, id);
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Equal("WRONG_STATE", await CodeOf(locked));
    }

    [Fact]
    public async Task Full_lifecycle_with_return_signatures_actions_and_effectiveness()
    {
        // The assignee must be known to PharmaOS: they have used it once.
        await CreateCapaAsync(Assignee);
        var id = await CreateCapaAsync(Owner);
        Assert.Equal(HttpStatusCode.OK, (await AddActionAsync(Owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AddActionAsync(Owner, id, Assignee.Sub)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddActionAsync(Owner, id, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DefineCheckAsync(Owner, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit_plan")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await TransitionAsync(Qa, id, "return_plan")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Qa, id, "return_plan", "Add a due date for the training")).StatusCode);
        Assert.Equal("DRAFT", await StatusOf(id));
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit_plan")).StatusCode);

        Assert.Equal("MISSING_PERMISSION", await CodeOf(await ChallengeAsync(Owner, "qms.capa", id, await VersionOf(id), "approve_plan", "APPROVED")));
        Assert.Equal(HttpStatusCode.Created, (await SignCapaAsync(Qa, id, "approve_plan", "APPROVED")).StatusCode);
        Assert.Equal("IMPLEMENTATION", await StatusOf(id));

        var early = await ChallengeAsync(Qa, "qms.capa", id, await VersionOf(id), "verify_implementation", "VERIFIED");
        Assert.Equal("GUARD_FAILED", await CodeOf(early));

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Owner, HttpMethod.Post, $"/api/qms/v1/capas/{id}/actions/1/complete", new { evidence = "" }, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Owner, HttpMethod.Post, $"/api/qms/v1/capas/{id}/actions/1/complete",
            new { evidence = "Print head replaced, work order WO-4711." }, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Assignee, HttpMethod.Post, $"/api/qms/v1/capas/{id}/actions/2/complete",
            new { evidence = "PM plan updated, SOP-ENG-012 rev 4." }, id)).StatusCode);
        Assert.Equal("ACTION_NOT_OPEN", await CodeOf(await SendAsync(Owner, HttpMethod.Post, $"/api/qms/v1/capas/{id}/actions/2/complete",
            new { evidence = "again" }, id)));

        Assert.Equal(HttpStatusCode.Created, (await SignCapaAsync(Qa, id, "verify_implementation", "VERIFIED")).StatusCode);
        Assert.Equal("EFFECTIVENESS_CHECK", await StatusOf(id));
        Assert.Equal("GUARD_FAILED", await CodeOf(await ChallengeAsync(Qa, "qms.capa", id, await VersionOf(id), "close", "VERIFIED")));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Owner, HttpMethod.Post, $"/api/qms/v1/capas/{id}/effectiveness/result",
            new { result = "effective", evidence = "Zero label deviations in the trend report." }, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignCapaAsync(Qa, id, "close", "VERIFIED")).StatusCode);

        var closed = await GetAsync($"/api/qms/v1/capas/{id}");
        Assert.Equal("CLOSED", closed.GetProperty("status").GetString());
        Assert.Equal("EFFECTIVE", closed.GetProperty("effectiveness").GetProperty("result").GetString());
        Assert.Equal("Cara Owner", closed.GetProperty("effectiveness").GetProperty("performedByName").GetString());
        var actions = closed.GetProperty("actions").EnumerateArray().ToList();
        Assert.Equal("Abe Assignee", actions[1].GetProperty("assigneeName").GetString());
        Assert.All(actions, a => Assert.Equal("DONE", a.GetProperty("status").GetString()));

        var history = (await GetAsync($"/api/qms/v1/capas/{id}/history")).EnumerateArray().Select(h => h.GetProperty("toState").GetString());
        Assert.Equal(["DRAFT", "PLAN_REVIEW", "DRAFT", "PLAN_REVIEW", "IMPLEMENTATION", "EFFECTIVENESS_CHECK", "CLOSED"], history);

        // The plan approval covered the plan as it was; completed actions changed the record since.
        var sigs = (await GetAsync($"/api/esig/v1/records/qms.capa/{id}/signatures")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["APPROVED", "VERIFIED", "VERIFIED"], sigs.Select(s => s.GetProperty("meaning").GetString()));
        Assert.False(sigs[0].GetProperty("appliesToCurrentVersion").GetBoolean());
        Assert.True(sigs[2].GetProperty("appliesToCurrentVersion").GetBoolean());
    }

    [Fact]
    public async Task Nobody_approves_or_verifies_their_own_work_whatever_their_roles()
    {
        var id = await PlannedCapaAsync(Both);
        var own = await ChallengeAsync(Both, "qms.capa", id, await VersionOf(id), "approve_plan", "APPROVED");
        Assert.Equal("SEGREGATION_OF_DUTIES", await CodeOf(own));
        Assert.Equal(HttpStatusCode.Created, (await SignCapaAsync(Qa, id, "approve_plan", "APPROVED")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Both, HttpMethod.Post, $"/api/qms/v1/capas/{id}/actions/1/complete",
            new { evidence = "Done." }, id)).StatusCode);
        var verifyOwn = await ChallengeAsync(Both, "qms.capa", id, await VersionOf(id), "verify_implementation", "VERIFIED");
        Assert.Equal("SEGREGATION_OF_DUTIES", await CodeOf(verifyOwn));
        Assert.Equal(HttpStatusCode.Created, (await SignCapaAsync(Qa, id, "verify_implementation", "VERIFIED")).StatusCode);
    }

    [Fact]
    public async Task Deviation_closes_only_when_every_linked_capa_plan_is_approved()
    {
        // Deviation up to QA approval.
        var dev = await (await As(Owner).PostAsJsonAsync("/api/qms/v1/deviations", new
        {
            siteId = Berlin, title = "Misprint", description = "d", classification = "MINOR", deviationType = "PROCESS",
            occurredAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
        })).Content.ReadFromJsonAsync<JsonElement>();
        var devId = dev.GetProperty("id").GetGuid();
        async Task<int> DevVersion() => (await GetAsync($"/api/qms/v1/deviations/{devId}")).GetProperty("rowVersion").GetInt32();
        async Task<HttpResponseMessage> DevTransition(TestUser u, string action)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/qms/v1/deviations/{devId}/transitions") { Content = JsonContent.Create(new { action }) }
                .WithHeaders(ifMatch: $"\"{await DevVersion()}\"");
            return await As(u).SendAsync(req);
        }
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Owner, "qms.deviation", devId, await DevVersion(), "submit", "AUTHORED")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DevTransition(Qa, "start_investigation")).StatusCode);
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/qms/v1/deviations/{devId}") { Content = JsonContent.Create(new { rootCauseSummary = "Worn print head" }) }
            .WithHeaders(ifMatch: $"\"{await DevVersion()}\"", reason: "Investigation");
        Assert.Equal(HttpStatusCode.OK, (await As(Owner).SendAsync(patch)).StatusCode);

        var capaId = await CreateCapaAsync(Owner, new
            { sourceType = "DEVIATION", sourceId = devId, title = "Fix labeller", problemStatement = "p", capaType = "CORRECTIVE", priority = "HIGH" });
        Assert.Equal(HttpStatusCode.OK, (await DevTransition(Owner, "submit_for_approval")).StatusCode);

        var blocked = await ChallengeAsync(Qa, "qms.deviation", devId, await DevVersion(), "approve", "APPROVED");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        var available = await As(Qa).GetFromJsonAsync<JsonElement>($"/api/qms/v1/deviations/{devId}/available-actions");
        Assert.Equal("Every linked CAPA needs an approved plan before the deviation can close.",
            available.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("action").GetString() == "approve").GetProperty("blockedBy").GetString());

        Assert.Equal(HttpStatusCode.OK, (await AddActionAsync(Owner, capaId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DefineCheckAsync(Owner, capaId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, capaId, "submit_plan")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignCapaAsync(Qa, capaId, "approve_plan", "APPROVED")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, "qms.deviation", devId, await DevVersion(), "approve", "APPROVED")).StatusCode);
        Assert.Equal("CLOSED", (await GetAsync($"/api/qms/v1/deviations/{devId}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Changes_need_the_current_version_and_the_write_permission()
    {
        var id = await CreateCapaAsync(Owner);
        var stale = new HttpRequestMessage(HttpMethod.Post, $"/api/qms/v1/capas/{id}/actions")
            { Content = JsonContent.Create(new { actionType = "CORRECTIVE", description = "x", dueDate = Day(5) }) }.WithHeaders(ifMatch: "\"99\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await As(Owner).SendAsync(stale)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AddActionAsync(Reader, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Reader).PostAsJsonAsync("/api/qms/v1/capas", new { })).StatusCode);
    }
}
