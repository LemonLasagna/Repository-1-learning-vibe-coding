using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PharmaOS.Platform.Host;

namespace PharmaOS.IntegrationTests;

/// <summary>A CAPA action that is implemented through a change control (FS §9.3.2, §9.3.3).</summary>
[Collection(PostgresFixture.Collection)]
[Trait("Req", "URS-QMS-009")]
public sealed class ChangeLinkApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid Hamburg = Guid.Parse("d0000000-0000-4000-8000-000000000011");

    private static readonly TestUser Planner = new(Guid.Parse("a1111111-0000-0000-0000-000000000001"), Tenant, "Pia Planner", "p.pia",
        Permissions.CapaRead, Permissions.CapaWrite, Permissions.ChangeRead, Permissions.ChangeWrite, Permissions.EsigSign);
    private static readonly TestUser Qa = new(Guid.Parse("a2222222-0000-0000-0000-000000000002"), Tenant, "Quirin QA", "q.quirin",
        Permissions.CapaRead, Permissions.CapaApprove, Permissions.ChangeRead, Permissions.ChangeApprove, Permissions.EsigSign);
    private static readonly TestUser ChangeOnly = new(Guid.Parse("a3333333-0000-0000-0000-000000000003"), Tenant, "Cleo Change", "c.cleo",
        Permissions.CapaRead, Permissions.ChangeRead, Permissions.ChangeWrite);
    private static readonly TestUser CapaOnly = new(Guid.Parse("a4444444-0000-0000-0000-000000000004"), Tenant, "Cal Capa", "c.cal",
        Permissions.CapaRead, Permissions.CapaWrite);

    private readonly PlatformFactory _factory;

    public ChangeLinkApiTests(PostgresFixture db) => _factory = new PlatformFactory(db);

    public void Dispose() => _factory.Dispose();

    private HttpClient As(TestUser u) => _factory.ClientFor(u);

    private static string Day(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    private async Task<JsonElement> GetAsync(string path) => await As(Planner).GetFromJsonAsync<JsonElement>(path);

    private async Task<int> VersionOf(string path) => (await GetAsync(path)).GetProperty("rowVersion").GetInt32();

    private async Task<HttpResponseMessage> SendAsync(TestUser user, HttpMethod method, string path, object? body, string versionOf)
    {
        var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) }
            .WithHeaders(ifMatch: $"\"{await VersionOf(versionOf)}\"");
        return await As(user).SendAsync(req);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() : null;

    /// <summary>A CAPA at site HAM with one open action; returns its API path.</summary>
    private async Task<string> CapaWithActionAsync(TestUser user)
    {
        var res = await As(user).PostAsJsonAsync("/api/qms/v1/capas", new
        {
            sourceType = "OTHER", siteId = Hamburg, title = "Prevent glove tears", problemStatement = "Tears found at fill line 2.",
            capaType = "CORRECTIVE", priority = "MEDIUM",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var path = $"/api/qms/v1/capas/{(await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()}";
        var action = await SendAsync(user, HttpMethod.Post, $"{path}/actions",
            new { actionType = "CORRECTIVE", description = "Change the glove supplier.", dueDate = Day(20) }, path);
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);
        return path;
    }

    private async Task<HttpResponseMessage> RaiseAsync(TestUser user, string capaPath, int actionNo = 1)
    {
        var capaId = (await GetAsync(capaPath)).GetProperty("id").GetGuid();
        return await As(user).PostAsJsonAsync("/api/qms/v1/changes", new
        {
            title = "Change the glove supplier", description = "Qualify the new supplier's gloves.", justification = "Tears with the current gloves.",
            changeType = "PLANNED", changeCategory = "MAJOR", sourceCapaId = capaId, sourceActionNo = actionNo,
        });
    }

    private async Task<string> NewChangeAsync(TestUser user)
    {
        var res = await As(user).PostAsJsonAsync("/api/qms/v1/changes", new
        {
            siteId = Hamburg, title = "Standalone change", description = "d", justification = "j", changeType = "PLANNED", changeCategory = "MINOR",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return $"/api/qms/v1/changes/{(await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()}";
    }

    private async Task<HttpResponseMessage> SignAsync(TestUser user, string entity, string path, string action, string meaning, string? reason = null)
    {
        var json = await GetAsync(path);
        var challenge = await As(user).PostAsJsonAsync("/api/esig/v1/challenges", new
        {
            entity, recordId = json.GetProperty("id").GetGuid(), recordVersion = json.GetProperty("rowVersion").GetInt32(), meaning, reason,
            acknowledge = true, action,
        });
        if (challenge.StatusCode != HttpStatusCode.Created) return challenge;
        var cid = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        return await _factory.ClientFor(user with { AuthTime = DateTimeOffset.UtcNow }).PostAsync($"/api/esig/v1/challenges/{cid}/complete", null);
    }

    /// <summary>Takes the CAPA through plan review and approval, so it is in IMPLEMENTATION.</summary>
    private async Task ApprovePlanAsync(TestUser owner, string capaPath)
    {
        var check = await SendAsync(owner, HttpMethod.Put, $"{capaPath}/effectiveness",
            new { criteria = "No tears for three months.", plannedDate = Day(100) }, capaPath);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(owner, HttpMethod.Post, $"{capaPath}/transitions", new { action = "submit_plan" }, capaPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, "qms.capa", capaPath, "approve_plan", "APPROVED")).StatusCode);
    }

    [Fact]
    public async Task A_change_raised_from_an_action_takes_the_CAPA_site_links_both_ways_and_advances_the_CAPA_version()
    {
        var capa = await CapaWithActionAsync(Planner);
        var before = await VersionOf(capa);

        var res = await RaiseAsync(Planner, capa);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var change = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("HAM", change.GetProperty("siteCode").GetString());
        var link = Assert.Single(change.GetProperty("linkedActions").EnumerateArray());
        var capaJson = await GetAsync(capa);
        Assert.Equal(capaJson.GetProperty("capaNo").GetString(), link.GetProperty("capaNo").GetString());
        Assert.Equal(1, link.GetProperty("actionNo").GetInt32());
        Assert.Equal("PLANNED", link.GetProperty("status").GetString());

        var action = Assert.Single(capaJson.GetProperty("actions").EnumerateArray());
        Assert.Equal(change.GetProperty("ccNo").GetString(), action.GetProperty("changeNo").GetString());
        Assert.Equal(change.GetProperty("id").GetGuid(), action.GetProperty("changeId").GetGuid());
        Assert.True(capaJson.GetProperty("rowVersion").GetInt32() > before, "linking changes the CAPA plan, so its version (ETag) must change");

        var history = (await GetAsync($"/api/qms/v1/changes/{change.GetProperty("id").GetGuid()}/history")).EnumerateArray().Single();
        Assert.Equal($"Created from {capaJson.GetProperty("capaNo").GetString()}, action 1", history.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Raising_from_an_action_needs_both_permissions_and_an_open_unlinked_action_and_changes_nothing_when_refused()
    {
        var capa = await CapaWithActionAsync(Planner);
        var changes = async () => (await GetAsync("/api/qms/v1/changes")).GetProperty("items").GetArrayLength();
        var count = await changes();
        var versionBefore = await VersionOf(capa);

        // Without the permission to write CAPAs.
        var forbidden = await RaiseAsync(ChangeOnly, capa);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("MISSING_PERMISSION", await CodeOf(forbidden));

        // Unknown CAPA, unknown action, only half of the pair, and no site without a CAPA.
        var unknownCapa = await As(Planner).PostAsJsonAsync("/api/qms/v1/changes", new
        {
            title = "t", description = "d", justification = "j", changeType = "PLANNED", changeCategory = "MINOR",
            sourceCapaId = Guid.NewGuid(), sourceActionNo = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownCapa.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RaiseAsync(Planner, capa, actionNo: 7)).StatusCode);
        var half = await As(Planner).PostAsJsonAsync("/api/qms/v1/changes", new
        {
            title = "t", description = "d", justification = "j", changeType = "PLANNED", changeCategory = "MINOR", sourceActionNo = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, half.StatusCode);
        var noSite = await As(Planner).PostAsJsonAsync("/api/qms/v1/changes", new
            { title = "t", description = "d", justification = "j", changeType = "PLANNED", changeCategory = "MINOR" });
        Assert.Equal(HttpStatusCode.BadRequest, noSite.StatusCode);

        Assert.Equal(count, await changes());
        Assert.Equal(versionBefore, await VersionOf(capa));

        // Once raised, the action is linked and cannot get a second change.
        Assert.Equal(HttpStatusCode.Created, (await RaiseAsync(Planner, capa)).StatusCode);
        var again = await RaiseAsync(Planner, capa);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("ACTION_ALREADY_LINKED", await CodeOf(again));
        Assert.Equal(count + 1, await changes());

        // In plan review the plan is locked.
        var review = await CapaWithActionAsync(Planner);
        var check = await SendAsync(Planner, HttpMethod.Put, $"{review}/effectiveness", new { criteria = "c", plannedDate = Day(90) }, review);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Planner, HttpMethod.Post, $"{review}/transitions", new { action = "submit_plan" }, review)).StatusCode);
        var locked = await RaiseAsync(Planner, review);
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Equal("WRONG_STATE", await CodeOf(locked));
    }

    [Fact]
    public async Task During_implementation_an_open_action_can_get_a_change_and_the_change_shows_when_the_action_is_done()
    {
        var capa = await CapaWithActionAsync(Planner);
        await ApprovePlanAsync(Planner, capa);
        var res = await RaiseAsync(Planner, capa);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var changePath = $"/api/qms/v1/changes/{(await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()}";

        var done = await SendAsync(Planner, HttpMethod.Post, $"{capa}/actions/1/complete", new { evidence = "New supplier qualified, report R-12." }, capa);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var link = Assert.Single((await GetAsync(changePath)).GetProperty("linkedActions").EnumerateArray());
        Assert.Equal("DONE", link.GetProperty("status").GetString());

        // A completed action can neither be linked nor unlinked any more.
        var unlink = await SendAsync(Planner, HttpMethod.Delete, $"{capa}/actions/1/change", null, capa);
        Assert.Equal(HttpStatusCode.Conflict, unlink.StatusCode);
        Assert.Equal("ACTION_NOT_OPEN", await CodeOf(unlink));
    }

    [Fact]
    public async Task An_open_action_can_be_linked_to_an_existing_open_change_and_unlinked()
    {
        var capa = await CapaWithActionAsync(Planner);
        var change = await NewChangeAsync(Planner);
        var changeId = (await GetAsync(change)).GetProperty("id").GetGuid();
        var link = $"{capa}/actions/1/change";
        var before = await VersionOf(capa);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(CapaOnly, HttpMethod.Put, link, new { changeId }, capa)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Planner, HttpMethod.Put, link, new { changeId = Guid.NewGuid() }, capa)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Planner, HttpMethod.Put, link, new { }, capa)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Planner, HttpMethod.Put, link, new { changeId }, capa)).StatusCode);
        Assert.True(await VersionOf(capa) > before, "linking must advance the CAPA's version");
        var linked = Assert.Single((await GetAsync(change)).GetProperty("linkedActions").EnumerateArray());
        Assert.Equal(1, linked.GetProperty("actionNo").GetInt32());
        var again = await SendAsync(Planner, HttpMethod.Put, link, new { changeId }, capa);
        Assert.Equal("ACTION_ALREADY_LINKED", await CodeOf(again));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Planner, HttpMethod.Delete, link, null, capa)).StatusCode);
        Assert.Empty((await GetAsync(change)).GetProperty("linkedActions").EnumerateArray());
        Assert.Equal("NOT_LINKED", await CodeOf(await SendAsync(Planner, HttpMethod.Delete, link, null, capa)));

        // A cancelled change cannot be linked.
        var cancel = await SignAsync(Planner, "qms.change", change, "cancel", "CANCELLED", "Not needed");
        Assert.Equal(HttpStatusCode.Created, cancel.StatusCode);
        var cancelled = await SendAsync(Planner, HttpMethod.Put, link, new { changeId }, capa);
        Assert.Equal(HttpStatusCode.BadRequest, cancelled.StatusCode);
    }
}
