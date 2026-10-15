using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PharmaOS.Platform.Host;

namespace PharmaOS.IntegrationTests;

/// <summary>Deviation lifecycle through HTTP → workflow → e-signature → PostgreSQL (FS §9.3.1, URS-QMS-003).</summary>
[Collection(PostgresFixture.Collection)]
[Trait("Req", "URS-QMS-003")]
public sealed class DeviationApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid Berlin = Guid.Parse("d0000000-0000-4000-8000-000000000010");

    private static readonly TestUser Owner = new(Guid.Parse("d1111111-0000-0000-0000-000000000001"), Tenant, "Olive Owner", "o.owner",
        Permissions.DeviationRead, Permissions.DeviationWrite, Permissions.EsigSign, Permissions.AuditTrailRead);
    private static readonly TestUser Qa = new(Guid.Parse("d2222222-0000-0000-0000-000000000002"), Tenant, "Quinn QA", "q.qa",
        Permissions.DeviationRead, Permissions.DeviationTriage, Permissions.DeviationApprove, Permissions.EsigSign);
    /// <summary>Holds every deviation permission, to show that segregation of duties does not depend on roles.</summary>
    private static readonly TestUser Both = new(Guid.Parse("d3333333-0000-0000-0000-000000000003"), Tenant, "Ada Both", "a.both",
        Permissions.DeviationRead, Permissions.DeviationWrite, Permissions.DeviationTriage, Permissions.DeviationApprove, Permissions.EsigSign);
    private static readonly TestUser Reader = new(Guid.Parse("d4444444-0000-0000-0000-000000000004"), Tenant, "Rea Reader", "r.reader",
        Permissions.DeviationRead);
    private static readonly TestUser OtherTenant = new(Guid.Parse("d5555555-0000-0000-0000-000000000005"), Guid.Parse("0000000b-0000-0000-0000-000000000000"),
        "Otto Other", "o.other", Permissions.DeviationRead, Permissions.DeviationWrite);

    private readonly PlatformFactory _factory;

    public DeviationApiTests(PostgresFixture db) => _factory = new PlatformFactory(db);

    public void Dispose() => _factory.Dispose();

    private HttpClient As(TestUser u) => _factory.ClientFor(u);

    private static object NewDeviation(string classification = "MINOR", string? title = null) => new
    {
        siteId = Berlin,
        title = title ?? "Label printer misprint on line 3",
        description = "Lot numbers printed with a missing digit on 40 cartons.",
        classification,
        deviationType = "PROCESS",
        occurredAt = DateTimeOffset.UtcNow.AddHours(-2).ToString("O"),
        shift = "EARLY",
    };

    private async Task<JsonElement> CreateAsync(TestUser user, object? body = null)
    {
        var res = await As(user).PostAsJsonAsync("/api/qms/v1/deviations", body ?? NewDeviation());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetAsync(Guid id) =>
        await As(Reader).GetFromJsonAsync<JsonElement>($"/api/qms/v1/deviations/{id}");

    private static int Version(JsonElement d) => d.GetProperty("rowVersion").GetInt32();

    private async Task<HttpResponseMessage> TransitionAsync(TestUser user, Guid id, string action, string? reason = null, int? version = null)
    {
        var current = version ?? Version(await GetAsync(id));
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/qms/v1/deviations/{id}/transitions")
            { Content = JsonContent.Create(new { action, reason }) }.WithHeaders(ifMatch: $"\"{current}\"");
        return await As(user).SendAsync(req);
    }

    private async Task<HttpResponseMessage> PatchAsync(TestUser user, Guid id, object body, string? reason)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/qms/v1/deviations/{id}") { Content = JsonContent.Create(body) }
            .WithHeaders(ifMatch: $"\"{Version(await GetAsync(id))}\"", reason: reason);
        return await As(user).SendAsync(req);
    }

    private async Task<HttpResponseMessage> ChallengeAsync(TestUser user, Guid id, string? action, string meaning, string? reason = null) =>
        await As(user).PostAsJsonAsync("/api/esig/v1/challenges", new
        {
            entity = "qms.deviation", recordId = id, recordVersion = Version(await GetAsync(id)), meaning, reason, acknowledge = true, action,
        });

    /// <summary>Challenge, then complete as if the user had just typed the password at Keycloak.</summary>
    private async Task<HttpResponseMessage> SignAsync(TestUser user, Guid id, string action, string meaning, string? reason = null)
    {
        var challenge = await ChallengeAsync(user, id, action, meaning, reason);
        if (challenge.StatusCode != HttpStatusCode.Created) return challenge;
        var challengeId = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        return await _factory.ClientFor(user with { AuthTime = DateTimeOffset.UtcNow })
            .PostAsync($"/api/esig/v1/challenges/{challengeId}/complete", null);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() : null;

    private async Task<string> StatusOf(Guid id) => (await GetAsync(id)).GetProperty("status").GetString()!;

    [Fact]
    public async Task Created_deviation_is_numbered_owned_due_in_30_days_and_in_draft()
    {
        var a = await CreateAsync(Owner);
        var b = await CreateAsync(Owner);
        Assert.Matches(@"^DEV-\d{4}-\d{5}$", a.GetProperty("deviationNo").GetString());
        var n = int.Parse(a.GetProperty("deviationNo").GetString()![9..]);
        Assert.Equal($"DEV-{DateTime.UtcNow.Year}-{n + 1:D5}", b.GetProperty("deviationNo").GetString());
        Assert.Equal("DRAFT", a.GetProperty("status").GetString());
        Assert.Equal("Olive Owner", a.GetProperty("ownerName").GetString());
        Assert.Equal("BER", a.GetProperty("siteCode").GetString());
        Assert.Equal("EARLY", a.GetProperty("shift").GetString());
        var discovered = a.GetProperty("discoveredAt").GetDateTimeOffset();
        Assert.Equal(DateOnly.FromDateTime(discovered.UtcDateTime).AddDays(30), DateOnly.Parse(a.GetProperty("dueDate").GetString()!));

        var history = await As(Reader).GetFromJsonAsync<JsonElement>($"/api/qms/v1/deviations/{a.GetProperty("id").GetGuid()}/history");
        var entry = Assert.Single(history.EnumerateArray());
        Assert.Equal("DRAFT", entry.GetProperty("toState").GetString());
        Assert.Equal("Olive Owner", entry.GetProperty("actorName").GetString());
    }

    [Fact]
    public async Task Invalid_input_is_refused_with_field_errors()
    {
        var client = As(Owner);
        async Task<JsonElement> Errors(object body)
        {
            var res = await client.PostAsJsonAsync("/api/qms/v1/deviations", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        }
        Assert.True((await Errors(new { siteId = Berlin })).TryGetProperty("title", out _));
        Assert.True((await Errors(new { siteId = Berlin, title = "t", description = "d", classification = "MINOR", deviationType = "PROCESS",
            occurredAt = "2026-10-15T08:00:00" })).TryGetProperty("occurredAt", out _));
        Assert.True((await Errors(new { siteId = Berlin, title = "t", description = "d", classification = "SEVERE", deviationType = "PROCESS",
            occurredAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("O") })).TryGetProperty("classification", out _));
        Assert.True((await Errors(new { siteId = Berlin, title = "t", description = "d", classification = "MINOR", deviationType = "PROCESS",
            occurredAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"), discoveredAt = DateTimeOffset.UtcNow.AddHours(-3).ToString("O") }))
            .TryGetProperty("occurredAt", out _));
        Assert.True((await Errors(new { colour = "red" })).TryGetProperty("colour", out _));
        Assert.True((await Errors(new { siteId = Guid.NewGuid(), title = "t", description = "d", classification = "MINOR", deviationType = "PROCESS",
            occurredAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("O") })).TryGetProperty("siteId", out _));
    }

    [Fact]
    public async Task Full_lifecycle_with_signatures_guards_rejection_and_history()
    {
        var id = (await CreateAsync(Owner)).GetProperty("id").GetGuid();

        // Submitting is signed: the plain transition endpoint says so.
        var plain = await TransitionAsync(Owner, id, "submit");
        Assert.Equal(HttpStatusCode.PreconditionRequired, plain.StatusCode);
        Assert.Equal("SIGNATURE_REQUIRED", await CodeOf(plain));

        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Owner, id, "submit", "AUTHORED")).StatusCode);
        Assert.Equal("TRIAGE", await StatusOf(id));

        var triage = await TransitionAsync(Qa, id, "start_investigation");
        Assert.Equal(HttpStatusCode.OK, triage.StatusCode);
        Assert.Equal("Quinn QA", (await triage.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("qaOwnerName").GetString());

        var notReady = await TransitionAsync(Owner, id, "submit_for_approval");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notReady.StatusCode);
        Assert.Equal("GUARD_FAILED", await CodeOf(notReady));

        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(Owner, id, new { rootCauseSummary = "Worn print head" }, reason: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(Owner, id, new { rootCauseSummary = "Worn print head" }, "Investigation result")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit_for_approval")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await TransitionAsync(Qa, id, "reject")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Qa, id, "reject", "Add the print-head maintenance record")).StatusCode);
        Assert.Equal("INVESTIGATION", await StatusOf(id));
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit_for_approval")).StatusCode);

        var ownerApproves = await ChallengeAsync(Owner, id, "approve", "APPROVED");
        Assert.Equal(HttpStatusCode.Forbidden, ownerApproves.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, id, "approve", "APPROVED")).StatusCode);
        var closed = await GetAsync(id);
        Assert.Equal("CLOSED", closed.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, closed.GetProperty("closedAt").ValueKind);

        var readOnly = await PatchAsync(Owner, id, new { rootCauseSummary = "Changed later" }, "Late edit");
        Assert.Equal(HttpStatusCode.Conflict, readOnly.StatusCode);
        Assert.Equal("READ_ONLY", await CodeOf(readOnly));

        var history = (await As(Reader).GetFromJsonAsync<JsonElement>($"/api/qms/v1/deviations/{id}/history")).EnumerateArray().ToList();
        Assert.Equal(["DRAFT", "TRIAGE", "INVESTIGATION", "QA_APPROVAL", "INVESTIGATION", "QA_APPROVAL", "CLOSED"],
            history.Select(h => h.GetProperty("toState").GetString()));
        Assert.Equal("Authored", history[1].GetProperty("signatureMeaning").GetString());
        Assert.Equal("Add the print-head maintenance record", history[4].GetProperty("comment").GetString());
        Assert.Equal("Approved", history[6].GetProperty("signatureMeaning").GetString());
        Assert.Equal("Quinn QA", history[6].GetProperty("actorName").GetString());

        // The Authored signature covered the draft content; the root cause added later makes it older than the record.
        var sigs = await As(Reader).GetFromJsonAsync<JsonElement>($"/api/esig/v1/records/qms.deviation/{id}/signatures");
        var items = sigs.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["AUTHORED", "APPROVED"], items.Select(s => s.GetProperty("meaning").GetString()));
        Assert.False(items[0].GetProperty("appliesToCurrentVersion").GetBoolean());
        Assert.True(items[1].GetProperty("appliesToCurrentVersion").GetBoolean());

        var audit = await As(Owner).GetFromJsonAsync<JsonElement>($"/api/audit/v1/records/qms/deviation/{id}");
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("reason").GetString() == "Approve and close (signed: Approved)");
    }

    [Fact]
    public async Task Author_cannot_approve_even_with_every_permission()
    {
        var id = (await CreateAsync(Both)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Both, id, "submit", "AUTHORED")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Qa, id, "start_investigation")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(Owner, id, new { rootCauseSummary = "Operator error" }, "Investigation")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TransitionAsync(Owner, id, "submit_for_approval")).StatusCode);

        var res = await ChallengeAsync(Both, id, "approve", "APPROVED");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("SEGREGATION_OF_DUTIES", await CodeOf(res));
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Qa, id, "approve", "APPROVED")).StatusCode);
    }

    [Fact]
    public async Task Signatures_and_actions_must_match()
    {
        var id = (await CreateAsync(Owner)).GetProperty("id").GetGuid();
        Assert.Equal("WRONG_MEANING", await CodeOf(await ChallengeAsync(Owner, id, "submit", "APPROVED")));
        Assert.Equal(HttpStatusCode.BadRequest, (await ChallengeAsync(Owner, id, null, "AUTHORED")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Owner, id, "submit", "AUTHORED")).StatusCode);
        var unsignedAction = await ChallengeAsync(Qa, id, "start_investigation", "AUTHORED");
        Assert.Equal("NO_SIGNATURE_NEEDED", await CodeOf(unsignedAction));
        var notAvailable = await ChallengeAsync(Owner, id, "submit", "AUTHORED");
        Assert.Equal("ACTION_NOT_AVAILABLE", await CodeOf(notAvailable));
    }

    [Fact]
    public async Task Cancel_needs_a_reason_and_a_signature_and_ends_the_workflow()
    {
        var id = (await CreateAsync(Owner)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await ChallengeAsync(Owner, id, "cancel", "CANCELLED")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Owner, id, "cancel", "CANCELLED", "Raised twice; see the other deviation")).StatusCode);
        Assert.Equal("CANCELLED", await StatusOf(id));

        var actions = await As(Owner).GetFromJsonAsync<JsonElement>($"/api/qms/v1/deviations/{id}/actions");
        Assert.Empty(actions.GetProperty("actions").EnumerateArray());
        Assert.Empty(actions.GetProperty("editableFields").EnumerateArray());
    }

    [Fact]
    public async Task Actions_show_what_the_user_may_do_and_why_not()
    {
        var id = (await CreateAsync(Owner, new
        {
            siteId = Berlin, title = "Planned change of supplier", description = "d", classification = "MINOR", deviationType = "MATERIAL",
            occurredAt = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"), isPlanned = true,
        })).GetProperty("id").GetGuid();

        var owner = await As(Owner).GetFromJsonAsync<JsonElement>($"/api/qms/v1/deviations/{id}/actions");
        var submit = owner.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("action").GetString() == "submit");
        Assert.Equal("AUTHORED", submit.GetProperty("signatureMeaning").GetString());
        Assert.Equal("A planned deviation needs a justification.", submit.GetProperty("blockedBy").GetString());
        Assert.Contains("title", owner.GetProperty("editableFields").EnumerateArray().Select(f => f.GetString()));
        Assert.False(owner.GetProperty("reasonForChangeRequired").GetBoolean());

        var reader = await As(Reader).GetFromJsonAsync<JsonElement>($"/api/qms/v1/deviations/{id}/actions");
        Assert.Empty(reader.GetProperty("actions").EnumerateArray());
        Assert.Empty(reader.GetProperty("editableFields").EnumerateArray());

        // In draft, no reason for change is needed; the guard then lets submission through.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(Owner, id, new { plannedJustification = "Approved change CC-2026-0042" }, reason: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Owner, id, "submit", "AUTHORED")).StatusCode);
    }

    [Fact]
    public async Task Transitions_need_the_current_version()
    {
        var created = await CreateAsync(Owner);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Owner, id, "submit", "AUTHORED")).StatusCode);
        var stale = await TransitionAsync(Qa, id, "start_investigation", version: Version(created));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var noIfMatch = await As(Qa).PostAsJsonAsync($"/api/qms/v1/deviations/{id}/transitions", new { action = "start_investigation" });
        Assert.Equal(HttpStatusCode.PreconditionRequired, noIfMatch.StatusCode);
    }

    [Fact]
    public async Task Other_tenants_cannot_see_deviations_or_sites()
    {
        var id = (await CreateAsync(Owner)).GetProperty("id").GetGuid();
        var other = As(OtherTenant);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/qms/v1/deviations/{id}")).StatusCode);
        var list = await other.GetFromJsonAsync<JsonElement>("/api/qms/v1/deviations");
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/org/v1/sites")).EnumerateArray());
        var create = await other.PostAsJsonAsync("/api/qms/v1/deviations", NewDeviation());
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task Login_reused_by_a_new_identity_is_a_conflict_not_a_server_error()
    {
        await CreateAsync(Owner);
        var res = await As(Owner with { Sub = Guid.NewGuid() }).PostAsJsonAsync("/api/qms/v1/deviations", NewDeviation());
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("IDENTITY_CONFLICT", await CodeOf(res));
    }

    [Fact]
    public async Task Readers_cannot_raise_or_change_deviations()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Reader).PostAsJsonAsync("/api/qms/v1/deviations", NewDeviation())).StatusCode);
        var id = (await CreateAsync(Owner)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(Reader, id, new { title = "x" }, null)).StatusCode);
    }
}
