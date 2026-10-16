using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PharmaOS.Platform.Host;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// Activating an authored workflow (URS-WF-005): it becomes the newest version of the record type's workflow, new records start on
/// it, and records already in progress stay on the version they started on. Kept apart from the draft tests because activation
/// changes how every later CAPA in the database starts.
/// </summary>
[Collection(PostgresFixture.Collection)]
[Trait("Req", "URS-WF-005")]
public sealed class WorkflowActivationApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid Berlin = Guid.Parse("d0000000-0000-4000-8000-000000000010");

    /// <summary>One person with every permission, as in a single-user installation.</summary>
    private static readonly TestUser Owner = new(Guid.Parse("b8888888-0000-0000-0000-000000000001"), Tenant, "Olu Owner", "o.owner",
        Permissions.WorkflowAuthor, Permissions.WorkflowApprove, Permissions.CapaRead, Permissions.CapaWrite, Permissions.EsigSign);

    private static readonly TestUser Approver = new(Guid.Parse("b8888888-0000-0000-0000-000000000002"), Tenant, "Ama Approver", "a.approver",
        Permissions.WorkflowApprove);

    private const string NewLabel = "Send plan to QA";

    private readonly PostgresFixture _db;
    private readonly PlatformFactory _factory;

    public WorkflowActivationApiTests(PostgresFixture db)
    {
        _db = db;
        _factory = new PlatformFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient As(TestUser u) => _factory.ClientFor(u);

    private static string Day(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    /// <summary>A CAPA with a planned action and an effectiveness check, so its plan can be submitted.</summary>
    private async Task<string> CapaAsync()
    {
        var res = await As(Owner).PostAsJsonAsync("/api/qms/v1/capas", new
        {
            sourceType = "OTHER", siteId = Berlin, title = "Authored workflow", problemStatement = "p", capaType = "CORRECTIVE", priority = "LOW",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var path = $"/api/qms/v1/capas/{(await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()}";
        Assert.Equal(HttpStatusCode.OK, (await CapaChangeAsync(HttpMethod.Post, $"{path}/actions", path,
            new { actionType = "CORRECTIVE", description = "Fix it.", dueDate = Day(10) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CapaChangeAsync(HttpMethod.Put, $"{path}/effectiveness", path,
            new { criteria = "No recurrence.", plannedDate = Day(90) })).StatusCode);
        return path;
    }

    private async Task<HttpResponseMessage> CapaChangeAsync(HttpMethod method, string path, string versionOf, object body)
    {
        var version = (await As(Owner).GetFromJsonAsync<JsonElement>(versionOf)).GetProperty("rowVersion").GetInt32();
        return await As(Owner).SendAsync(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) }
            .WithHeaders(ifMatch: $"\"{version}\""));
    }

    private async Task<string?> SubmitPlanLabelAsync(string capa)
    {
        var actions = (await As(Owner).GetFromJsonAsync<JsonElement>($"{capa}/available-actions")).GetProperty("actions").EnumerateArray();
        return actions.Where(a => a.GetProperty("action").GetString() == "submit_plan").Select(a => a.GetProperty("label").GetString()).SingleOrDefault();
    }

    [Fact]
    public async Task One_person_can_activate_by_default_and_only_new_records_start_on_the_new_version()
    {
        var before = await CapaAsync();
        Assert.Equal("Submit plan for review", await SubmitPlanLabelAsync(before));

        var (id, etag, _) = await Authoring.CreateAsync(As(Owner),
            Authoring.Capa(2, t => t.Action == "submit_plan" ? t with { Label = NewLabel } : t));
        foreach (var step in new[] { "submit", "approve", "activate" })
        {
            var res = await Authoring.StepAsync(As(Owner), id, step, etag);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            etag = res.Headers.ETag!.Tag;
        }
        var active = await As(Owner).GetFromJsonAsync<JsonElement>($"{Authoring.Base}/{id}");
        Assert.Equal("ACTIVE", active.GetProperty("status").GetString());
        Assert.Equal(Owner.Sub, active.GetProperty("approvedBy").GetGuid());

        var after = await CapaAsync();
        Assert.Equal(NewLabel, await SubmitPlanLabelAsync(after));
        Assert.Equal("Submit plan for review", await SubmitPlanLabelAsync(before));

        // The activated version is released now, so it cannot be drafted again.
        var again = await As(Owner).PostAsJsonAsync(Authoring.Base, Authoring.Capa(2));
        Assert.Equal("VERSION_RELEASED", await Authoring.CodeOf(again));
    }

    [Fact]
    public async Task With_the_second_person_rule_on_the_submitter_cannot_approve()
    {
        using var strict = new PlatformFactory(_db, settings: new Dictionary<string, string> { ["WorkflowAuthoring:RequireSecondPerson"] = "true" });
        var (id, etag, _) = await Authoring.CreateAsync(strict.ClientFor(Owner), Authoring.Capa(Authoring.NewVersion()));
        var submit = await Authoring.StepAsync(strict.ClientFor(Owner), id, "submit", etag);
        etag = submit.Headers.ETag!.Tag;

        var self = await Authoring.StepAsync(strict.ClientFor(Owner), id, "approve", etag);
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("SAME_PERSON", await Authoring.CodeOf(self));

        var other = await Authoring.StepAsync(strict.ClientFor(Approver), id, "approve", etag, new { comment = "Checked against the SOP." });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        var approved = await other.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Approver.Sub, approved.GetProperty("approvedBy").GetGuid());
        Assert.Equal("Checked against the SOP.", approved.GetProperty("decisionComment").GetString());
    }
}
