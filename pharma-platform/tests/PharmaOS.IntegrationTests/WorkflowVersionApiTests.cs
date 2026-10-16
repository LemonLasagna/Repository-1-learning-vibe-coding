using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using PharmaOS.Platform.Host;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// A record stays on the workflow version it started on (URS-WF-005): a later version of the same workflow does not change
/// the transitions of records already in progress.
/// </summary>
[Collection(PostgresFixture.Collection)]
[Trait("Req", "URS-WF-005")]
public sealed class WorkflowVersionApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid Berlin = Guid.Parse("d0000000-0000-4000-8000-000000000010");

    private static readonly TestUser Planner = new(Guid.Parse("b1111111-0000-0000-0000-000000000001"), Tenant, "Pia Planner", "p.pia",
        Permissions.CapaRead, Permissions.CapaWrite, Permissions.EsigSign);

    private readonly PostgresFixture _db;
    private readonly PlatformFactory _factory;

    public WorkflowVersionApiTests(PostgresFixture db)
    {
        _db = db;
        _factory = new PlatformFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient As(TestUser u) => _factory.ClientFor(u);

    private static string Day(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    private async Task<string> CapaAsync()
    {
        var res = await As(Planner).PostAsJsonAsync("/api/qms/v1/capas", new
        {
            sourceType = "OTHER", siteId = Berlin, title = "Pinned workflow", problemStatement = "p", capaType = "CORRECTIVE", priority = "LOW",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var path = $"/api/qms/v1/capas/{id}";
        var action = await SendAsync(HttpMethod.Post, $"{path}/actions", path,
            new { actionType = "CORRECTIVE", description = "Fix it.", dueDate = Day(10) });
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);
        var check = await SendAsync(HttpMethod.Put, $"{path}/effectiveness", path, new { criteria = "No recurrence.", plannedDate = Day(90) });
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        return path;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string versionOf, object body)
    {
        var version = (await As(Planner).GetFromJsonAsync<JsonElement>(versionOf)).GetProperty("rowVersion").GetInt32();
        var req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) }.WithHeaders(ifMatch: $"\"{version}\"");
        return await As(Planner).SendAsync(req);
    }

    /// <summary>
    /// Stores version 2 of CAPA_STD, in which a plan cannot be submitted at all: only cancelling is possible from draft.
    /// Records in progress must not see it.
    /// </summary>
    private async Task ReleaseCapaVersionTwoAsync()
    {
        const string snapshot = """
            {"code":"CAPA_STD","version":2,"initialState":"DRAFT","transitions":[
              {"action":"cancel","from":"DRAFT","to":"CANCELLED","label":"Cancel","permission":"qms.capa:write",
               "guard":null,"guardMessage":null,"signatureMeaning":"CANCELLED","requireReason":true}]}
            """;
        await using var conn = new NpgsqlConnection(_db.AdminConnectionString);
        await conn.OpenAsync();
        // The database only accepts GxP writes with an authenticated actor in the session (as the application sets it).
        await using (var session = new NpgsqlCommand("SELECT set_config('app.user_id', @user, false), set_config('app.tenant_id', @tenant, false)", conn))
        {
            session.Parameters.AddWithValue("user", Planner.Sub.ToString());
            session.Parameters.AddWithValue("tenant", Tenant.ToString());
            await session.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO wf.process_definition_version (tenant_id, entity_id, version_no, version_label, lifecycle_status, snapshot,
                   snapshot_hash, effective_from, created_by)
            SELECT tenant_id, id, 2, '2', 'EFFECTIVE', @snapshot::jsonb, repeat('f', 64), now(), created_by
              FROM wf.process_definition WHERE code = 'CAPA_STD' AND tenant_id = @tenant
            """, conn);
        cmd.Parameters.AddWithValue("snapshot", snapshot);
        cmd.Parameters.AddWithValue("tenant", Tenant);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task A_record_in_progress_keeps_the_transitions_of_the_version_it_started_on_and_new_records_start_on_the_newest()
    {
        var path = await CapaAsync();
        await ReleaseCapaVersionTwoAsync();

        // Version 2 offers only "cancel". The record started on version 1, so planning still works and offers the same actions.
        var actions = await As(Planner).GetFromJsonAsync<JsonElement>($"{path}/available-actions");
        Assert.Contains(actions.GetProperty("actions").EnumerateArray(), a => a.GetProperty("action").GetString() == "submit_plan");
        var submit = await SendAsync(HttpMethod.Post, $"{path}/transitions", path, new { action = "submit_plan" });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal("PLAN_REVIEW", (await As(Planner).GetFromJsonAsync<JsonElement>(path)).GetProperty("status").GetString());

        // A record raised now starts on the newest released version: version 2, where only cancelling is possible.
        var next = await CapaAsync();
        var nextActions = (await As(Planner).GetFromJsonAsync<JsonElement>($"{next}/available-actions")).GetProperty("actions")
            .EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Equal(["cancel"], nextActions);
    }
}
