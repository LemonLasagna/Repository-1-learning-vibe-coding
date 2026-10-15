using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PharmaOS.Platform.Host;

namespace PharmaOS.IntegrationTests;

/// <summary>"My work": what is waiting on each user, following the deviation and CAPA workflows.</summary>
[Collection(PostgresFixture.Collection)]
public sealed class InboxApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid Berlin = Guid.Parse("d0000000-0000-4000-8000-000000000010");

    private static readonly TestUser Owner = new(Guid.Parse("e1111111-0000-0000-0000-000000000001"), Tenant, "Oona Owner", "i.owner",
        Permissions.DeviationRead, Permissions.DeviationWrite, Permissions.CapaRead, Permissions.CapaWrite, Permissions.EsigSign);
    private static readonly TestUser Qa = new(Guid.Parse("e2222222-0000-0000-0000-000000000002"), Tenant, "Quill QA", "i.qa",
        Permissions.DeviationRead, Permissions.DeviationTriage, Permissions.DeviationApprove, Permissions.CapaRead, Permissions.CapaApprove,
        Permissions.EsigSign);
    private static readonly TestUser Both = new(Guid.Parse("e3333333-0000-0000-0000-000000000003"), Tenant, "Bo Both", "i.both",
        Permissions.DeviationRead, Permissions.DeviationWrite, Permissions.DeviationTriage, Permissions.DeviationApprove, Permissions.CapaRead,
        Permissions.CapaWrite, Permissions.EsigSign);
    private static readonly TestUser Assignee = new(Guid.Parse("e4444444-0000-0000-0000-000000000004"), Tenant, "Ari Assignee", "i.assignee",
        Permissions.CapaRead, Permissions.CapaWrite);
    private static readonly TestUser Reader = new(Guid.Parse("e5555555-0000-0000-0000-000000000005"), Tenant, "Ray Reader", "i.reader",
        Permissions.DeviationRead, Permissions.CapaRead);

    private readonly PostgresFixture _db;
    private readonly PlatformFactory _factory;

    public InboxApiTests(PostgresFixture db)
    {
        _db = db;
        _factory = new PlatformFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient As(TestUser u, PlatformFactory? f = null) => (f ?? _factory).ClientFor(u);

    private async Task<List<JsonElement>> InboxAsync(TestUser user, Guid? record = null, PlatformFactory? f = null)
    {
        var body = await As(user, f).GetFromJsonAsync<JsonElement>("/api/work/v1/inbox");
        return body.GetProperty("items").EnumerateArray().Where(i => record is null || i.GetProperty("recordId").GetGuid() == record).ToList();
    }

    private static string Task(JsonElement item) => item.GetProperty("task").GetString()!;

    private async Task<int> Version(string path) => (await As(Reader).GetFromJsonAsync<JsonElement>(path)).GetProperty("rowVersion").GetInt32();

    private async Task<Guid> RaiseDeviationAsync(TestUser user)
    {
        var res = await As(user).PostAsJsonAsync("/api/qms/v1/deviations", new
        {
            siteId = Berlin, title = "Inbox deviation", description = "d", classification = "MINOR", deviationType = "PROCESS",
            occurredAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task SignAsync(TestUser user, string entity, Guid id, string action, string meaning)
    {
        var path = entity == "qms.capa" ? $"/api/qms/v1/capas/{id}" : $"/api/qms/v1/deviations/{id}";
        var challenge = await As(user).PostAsJsonAsync("/api/esig/v1/challenges",
            new { entity, recordId = id, recordVersion = await Version(path), meaning, acknowledge = true, action });
        Assert.Equal(HttpStatusCode.Created, challenge.StatusCode);
        var cid = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        var done = await _factory.ClientFor(user with { AuthTime = DateTimeOffset.UtcNow }).PostAsync($"/api/esig/v1/challenges/{cid}/complete", null);
        Assert.Equal(HttpStatusCode.Created, done.StatusCode);
    }

    private async Task SendAsync(TestUser user, HttpMethod method, string path, string versionOf, object? body, string? reason = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) }
            .WithHeaders(ifMatch: $"\"{await Version(versionOf)}\"", reason: reason);
        var res = await As(user).SendAsync(req);
        Assert.True(res.IsSuccessStatusCode, $"{method} {path}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Owners_see_their_drafts_QA_sees_triage_readers_see_nothing()
    {
        var id = await RaiseDeviationAsync(Owner);
        var draft = Assert.Single(await InboxAsync(Owner, id));
        Assert.Equal("Complete and submit", Task(draft));
        Assert.Equal("SIGN", draft.GetProperty("kind").GetString());
        Assert.Empty(await InboxAsync(Qa, id));

        await SignAsync(Owner, "qms.deviation", id, "submit", "AUTHORED");
        Assert.Empty(await InboxAsync(Owner, id));
        Assert.Equal("Triage", Task(Assert.Single(await InboxAsync(Qa, id))));
        Assert.Empty(await InboxAsync(Reader));
    }

    [Fact]
    public async Task Approval_is_offered_only_to_those_who_may_sign_it_and_waits_for_capa_plans()
    {
        var id = await RaiseDeviationAsync(Both);
        await SignAsync(Both, "qms.deviation", id, "submit", "AUTHORED");
        await SendAsync(Qa, HttpMethod.Post, $"/api/qms/v1/deviations/{id}/transitions", $"/api/qms/v1/deviations/{id}", new { action = "start_investigation" });
        await SendAsync(Both, HttpMethod.Patch, $"/api/qms/v1/deviations/{id}", $"/api/qms/v1/deviations/{id}", new { rootCauseSummary = "Worn part" }, "Investigation");
        var capa = await As(Both).PostAsJsonAsync("/api/qms/v1/capas", new
            { sourceType = "DEVIATION", sourceId = id, title = "Fix", problemStatement = "p", capaType = "CORRECTIVE", priority = "HIGH" });
        Assert.Equal(HttpStatusCode.Created, capa.StatusCode);
        await SendAsync(Both, HttpMethod.Post, $"/api/qms/v1/deviations/{id}/transitions", $"/api/qms/v1/deviations/{id}", new { action = "submit_for_approval" });

        // Both authored the deviation and edited it last: they may not approve it, so it is not in their inbox.
        Assert.Empty(await InboxAsync(Both, id));
        var waiting = Assert.Single(await InboxAsync(Qa, id));
        Assert.Equal("WAITING", waiting.GetProperty("kind").GetString());
        Assert.Equal("Waiting for 1 linked CAPA plan(s) to be approved", waiting.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Capa_items_follow_the_plan_actions_and_effectiveness_check()
    {
        await As(Assignee).PostAsJsonAsync("/api/qms/v1/capas", new
            { sourceType = "OTHER", siteId = Berlin, title = "Known user", problemStatement = "p", capaType = "CORRECTIVE", priority = "LOW" });
        var res = await As(Owner).PostAsJsonAsync("/api/qms/v1/capas", new
            { sourceType = "OTHER", siteId = Berlin, title = "Inbox CAPA", problemStatement = "p", capaType = "CORRECTIVE", priority = "HIGH" });
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var path = $"/api/qms/v1/capas/{id}";
        Assert.Equal("Plan the actions and effectiveness check, then submit", Task(Assert.Single(await InboxAsync(Owner, id))));

        await SendAsync(Owner, HttpMethod.Post, $"{path}/actions", path,
            new { actionType = "CORRECTIVE", description = "Replace the seal", assigneeId = Assignee.Sub, dueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10).ToString("yyyy-MM-dd") });
        var checkDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60);
        await SendAsync(Owner, HttpMethod.Put, $"{path}/effectiveness", path, new { criteria = "No recurrence", plannedDate = checkDate.ToString("yyyy-MM-dd") });
        await SendAsync(Owner, HttpMethod.Post, $"{path}/transitions", path, new { action = "submit_plan" });
        Assert.Empty(await InboxAsync(Owner, id));
        Assert.Equal("Review and approve the plan", Task(Assert.Single(await InboxAsync(Qa, id))));

        await SignAsync(Qa, "qms.capa", id, "approve_plan", "APPROVED");
        Assert.Empty(await InboxAsync(Qa, id));
        var action = Assert.Single(await InboxAsync(Assignee, id));
        Assert.Equal("Complete action 1", Task(action));
        Assert.Equal("Replace the seal", action.GetProperty("detail").GetString());

        await SendAsync(Assignee, HttpMethod.Post, $"{path}/actions/1/complete", path, new { evidence = "Seal replaced, WO-1" });
        Assert.Empty(await InboxAsync(Assignee, id));
        Assert.Equal("Verify implementation", Task(Assert.Single(await InboxAsync(Qa, id))));

        await SignAsync(Qa, "qms.capa", id, "verify_implementation", "VERIFIED");
        var record = Assert.Single(await InboxAsync(Owner, id));
        Assert.Equal("Record the effectiveness check result", Task(record));
        Assert.Equal(checkDate, DateOnly.Parse(record.GetProperty("dueDate").GetString()!));

        await SendAsync(Owner, HttpMethod.Post, $"{path}/effectiveness/result", path, new { result = "EFFECTIVE", evidence = "Trend clean" });
        Assert.Empty(await InboxAsync(Owner, id));
        Assert.Equal("Verify effectiveness and close", Task(Assert.Single(await InboxAsync(Qa, id))));
        await SignAsync(Qa, "qms.capa", id, "close", "VERIFIED");
        Assert.Empty(await InboxAsync(Qa, id));
    }

    [Fact]
    public async Task Overdue_items_come_first()
    {
        var id = await RaiseDeviationAsync(Owner);
        var clock = new TestClock { Offset = TimeSpan.FromDays(40) };
        using var later = new PlatformFactory(_db, clock);
        var body = await As(Owner, later).GetFromJsonAsync<JsonElement>("/api/work/v1/inbox");
        var first = body.GetProperty("items").EnumerateArray().First();
        Assert.True(first.GetProperty("overdue").GetBoolean());
        Assert.True(body.GetProperty("counts").GetProperty("overdue").GetInt32() >= 1);
        Assert.Contains(body.GetProperty("items").EnumerateArray(), i => i.GetProperty("recordId").GetGuid() == id && i.GetProperty("overdue").GetBoolean());
    }
}
