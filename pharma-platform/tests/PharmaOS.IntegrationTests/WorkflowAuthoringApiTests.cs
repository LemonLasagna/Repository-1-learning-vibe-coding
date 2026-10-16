using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using PharmaOS.Platform.Host;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// Workflow authoring, step 2a: drafts of workflow definitions are checked and stored, never released (URS-WF-005 keeps released
/// versions unchanged), and a draft cannot take a version number that is already released.
/// </summary>
[Collection(PostgresFixture.Collection)]
[Trait("Req", "URS-WF-005")]
public sealed class WorkflowAuthoringApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");

    private static readonly TestUser Author = new(Guid.Parse("b7777777-0000-0000-0000-000000000001"), Tenant, "Wanda Author", "w.author",
        Permissions.WorkflowAuthor);

    private static readonly TestUser NoAccess = new(Guid.Parse("b7777777-0000-0000-0000-000000000002"), Tenant, "Nia Nobody", "n.nobody",
        Permissions.CapaRead);

    private readonly PostgresFixture _db;
    private readonly PlatformFactory _factory;

    public WorkflowAuthoringApiTests(PostgresFixture db)
    {
        _db = db;
        _factory = new PlatformFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient As(TestUser u) => _factory.ClientFor(u);

    private const string Base = "/api/workflow/v1/definitions";

    private static string NewCode() => "AUTH" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static object Draft(string code, int version = 1, string? permission = "qms.capa:write") => new
    {
        code,
        version,
        initialState = "DRAFT",
        transitions = new object[]
        {
            new { action = "submit", from = "DRAFT", to = "REVIEW", label = "Submit", permission },
            new { action = "approve", from = "REVIEW", to = "DONE", permission = "qms.capa:approve", signatureMeaning = "APPROVED" },
        },
    };

    private async Task<HttpResponseMessage> CreateAsync(object body) => await As(Author).PostAsJsonAsync(Base, body);

    private async Task<(Guid Id, string Etag)> CreateValidAsync(string code, int version = 1)
    {
        var res = await CreateAsync(Draft(code, version));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (id, res.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task A_complete_draft_is_stored_as_valid_with_an_etag()
    {
        var res = await CreateAsync(Draft(NewCode()));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALID", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("errors").EnumerateArray());
        Assert.NotNull(res.Headers.ETag);
    }

    [Fact]
    public async Task An_incomplete_draft_is_still_saved_and_says_what_is_wrong()
    {
        var res = await CreateAsync(Draft(NewCode(), permission: null));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DRAFT", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("needs a permission"));
    }

    [Fact]
    public async Task A_missing_field_is_refused_with_field_errors()
    {
        var res = await CreateAsync(new { version = 1, initialState = "DRAFT", transitions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("code", out _));
    }

    [Fact]
    public async Task A_second_draft_of_the_same_version_is_refused()
    {
        var code = NewCode();
        await CreateValidAsync(code);
        var res = await CreateAsync(Draft(code));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("DRAFT_EXISTS", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_version_already_released_for_the_code_is_refused()
    {
        var code = NewCode();
        await ReleaseAsync(code, version: 1);
        var res = await CreateAsync(Draft(code, version: 1));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("VERSION_RELEASED", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Replacing_a_draft_needs_the_current_etag()
    {
        var (id, etag) = await CreateValidAsync(NewCode());
        var path = $"{Base}/{id}";
        var body = Draft("OTHERCODE", 1);

        var missing = await As(Author).PutAsJsonAsync(path, body);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);

        var stale = await As(Author).SendAsync(new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) }
            .WithHeaders(ifMatch: "\"999\""));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var ok = await As(Author).SendAsync(new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) }
            .WithHeaders(ifMatch: etag));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OTHERCODE", updated.GetProperty("code").GetString());
        Assert.Equal(2, updated.GetProperty("rowVersion").GetInt32());
    }

    [Fact]
    public async Task Validate_reports_the_rules_without_changing_the_draft()
    {
        var code = NewCode();
        var created = await CreateAsync(Draft(code, permission: null));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var res = await As(Author).PostAsJsonAsync($"{Base}/{id}/validate", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("valid").GetBoolean());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("needs a permission"));

        var after = await As(Author).GetFromJsonAsync<JsonElement>($"{Base}/{id}");
        Assert.Equal(1, after.GetProperty("rowVersion").GetInt32());
    }

    [Fact]
    public async Task A_user_without_the_authoring_permission_cannot_create_drafts()
    {
        var res = await As(NoAccess).PostAsJsonAsync(Base, Draft(NewCode()));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    /// <summary>Makes a workflow version released for <paramref name="code"/>, as a code release would, through the admin connection.</summary>
    private async Task ReleaseAsync(string code, int version)
    {
        await using var conn = new NpgsqlConnection(_db.AdminConnectionString);
        await conn.OpenAsync();
        await using (var session = new NpgsqlCommand("SELECT set_config('app.user_id', @user, false), set_config('app.tenant_id', @tenant, false)", conn))
        {
            session.Parameters.AddWithValue("user", Author.Sub.ToString());
            session.Parameters.AddWithValue("tenant", Tenant.ToString());
            await session.ExecuteNonQueryAsync();
        }
        Guid definitionId;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO wf.process_definition (tenant_id, code, name, bpmn_xml, model_json, status, is_gxp, created_by, updated_by)
            VALUES (@tenant, @code, 'Released in test', '', '{}'::jsonb, 'ACTIVE', true, @user, @user)
            RETURNING id
            """, conn))
        {
            insert.Parameters.AddWithValue("tenant", Tenant);
            insert.Parameters.AddWithValue("code", code);
            insert.Parameters.AddWithValue("user", Author.Sub);
            definitionId = (Guid)(await insert.ExecuteScalarAsync())!;
        }
        await using var versionCmd = new NpgsqlCommand("""
            INSERT INTO wf.process_definition_version (tenant_id, entity_id, version_no, version_label, lifecycle_status, snapshot,
                   snapshot_hash, effective_from, created_by)
            VALUES (@tenant, @id, @version, @label, 'EFFECTIVE', '{}'::jsonb, repeat('a', 64), now(), @user)
            """, conn);
        versionCmd.Parameters.AddWithValue("tenant", Tenant);
        versionCmd.Parameters.AddWithValue("id", definitionId);
        versionCmd.Parameters.AddWithValue("version", version);
        versionCmd.Parameters.AddWithValue("label", version.ToString());
        versionCmd.Parameters.AddWithValue("user", Author.Sub);
        Assert.Equal(1, await versionCmd.ExecuteNonQueryAsync());
    }
}
