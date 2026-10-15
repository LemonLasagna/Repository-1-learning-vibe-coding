using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PharmaOS.Platform.Host;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// End-to-end through HTTP → GxP context → EF Core → PostgreSQL triggers/RLS → audit API.
/// Each test uses its own codes so tests are independent within the shared fixture database.
/// </summary>
public sealed class UomApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("0000000a-0000-0000-0000-000000000000");
    private static readonly Guid TenantB = Guid.Parse("0000000b-0000-0000-0000-000000000000");

    private static readonly TestUser Writer = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), TenantA,
        "Test Analyst", "tanalyst", Permissions.UomRead, Permissions.UomWrite, Permissions.AuditTrailRead);
    private static readonly TestUser ReadOnly = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), TenantA,
        "Read Only", "ronly", Permissions.UomRead);
    private static readonly TestUser OtherTenant = new(Guid.Parse("33333333-3333-3333-3333-333333333333"), TenantB,
        "Other Tenant", "other", Permissions.UomRead, Permissions.UomWrite);

    private readonly PlatformFactory _factory;

    public UomApiTests(PostgresFixture db) => _factory = new PlatformFactory(db);

    public void Dispose() => _factory.Dispose();

    private static string Code() => "U" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<(Guid Id, string ETag)> CreateAsync(HttpClient client, string code)
    {
        var res = await client.PostAsJsonAsync("/api/mdm/v1/uoms", new { code, name = "Kilogram", dimension = "mass" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), res.Headers.ETag!.Tag);
    }

    private static HttpRequestMessage Patch(Guid id, object body) =>
        new(HttpMethod.Patch, $"/api/mdm/v1/uoms/{id}") { Content = JsonContent.Create(body) };

    [Fact]
    [Trait("Req", "URS-DI-001")]
    public async Task Create_is_audited_with_actor_and_display_name()
    {
        var client = _factory.ClientFor(Writer);
        var (id, etag) = await CreateAsync(client, Code());
        Assert.Equal("\"1\"", etag);

        var trail = await client.GetFromJsonAsync<JsonElement[]>($"/api/audit/v1/records/mdm/uom/{id}");
        var insert = Assert.Single(trail!);
        Assert.Equal("INSERT", insert.GetProperty("operation").GetString());
        Assert.Equal(Writer.Sub, insert.GetProperty("actorUserId").GetGuid());
        Assert.Equal("Test Analyst (tanalyst)", insert.GetProperty("actorDisplay").GetString());
        Assert.Equal("MASS", insert.GetProperty("newValues").GetProperty("dimension").GetString());
    }

    [Fact]
    [Trait("Req", "URS-DI-004")]
    public async Task Update_records_changed_field_old_new_and_reason()
    {
        var client = _factory.ClientFor(Writer);
        var (id, etag) = await CreateAsync(client, Code());

        var res = await client.SendAsync(Patch(id, new { name = "Kilogramme" })
            .WithHeaders(ifMatch: etag, reason: "Align spelling with EU labelling (CC-2026-0042)"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("\"2\"", res.Headers.ETag!.Tag);

        var trail = await client.GetFromJsonAsync<JsonElement[]>($"/api/audit/v1/records/mdm/uom/{id}");
        var update = trail!.Single(e => e.GetProperty("operation").GetString() == "UPDATE");
        Assert.Equal(["name"], update.GetProperty("changedFields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal("Kilogram", update.GetProperty("oldValues").GetProperty("name").GetString());
        Assert.Equal("Kilogramme", update.GetProperty("newValues").GetProperty("name").GetString());
        Assert.Equal("Align spelling with EU labelling (CC-2026-0042)", update.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("Req", "URS-DI-009")]
    public async Task Update_with_stale_version_is_rejected_and_not_applied()
    {
        var client = _factory.ClientFor(Writer);
        var (id, etag) = await CreateAsync(client, Code());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Patch(id, new { name = "First" })
            .WithHeaders(ifMatch: etag, reason: "first edit"))).StatusCode);

        var stale = await client.SendAsync(Patch(id, new { name = "Second" }).WithHeaders(ifMatch: etag, reason: "lost update"));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var current = await client.GetFromJsonAsync<JsonElement>($"/api/mdm/v1/uoms/{id}");
        Assert.Equal("First", current.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Update_without_if_match_requires_precondition()
    {
        var client = _factory.ClientFor(Writer);
        var (id, _) = await CreateAsync(client, Code());
        var res = await client.SendAsync(Patch(id, new { name = "X" }).WithHeaders(reason: "r"));
        Assert.Equal((HttpStatusCode)428, res.StatusCode);
    }

    [Fact]
    [Trait("Req", "URS-DI-004")]
    public async Task Update_without_reason_is_rejected()
    {
        var client = _factory.ClientFor(Writer);
        var (id, etag) = await CreateAsync(client, Code());
        var res = await client.SendAsync(Patch(id, new { name = "X" }).WithHeaders(ifMatch: etag));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("X-Change-Reason", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait("Req", "URS-DI-003")]
    public async Task Delete_is_not_offered()
    {
        var client = _factory.ClientFor(Writer);
        var (id, _) = await CreateAsync(client, Code());
        var res = await client.DeleteAsync($"/api/mdm/v1/uoms/{id}");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }

    [Fact]
    [Trait("Req", "URS-SEC-003")]
    public async Task Other_tenant_cannot_see_or_change_the_record()
    {
        var (id, etag) = await CreateAsync(_factory.ClientFor(Writer), Code());
        var other = _factory.ClientFor(OtherTenant);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/mdm/v1/uoms/{id}")).StatusCode);
        var patch = await other.SendAsync(Patch(id, new { name = "Hijacked" }).WithHeaders(ifMatch: etag, reason: "x"));
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
    }

    [Fact]
    public async Task Same_code_in_another_tenant_is_allowed_but_duplicate_in_same_tenant_conflicts()
    {
        var code = Code();
        await CreateAsync(_factory.ClientFor(Writer), code);
        await CreateAsync(_factory.ClientFor(OtherTenant), code);

        var dup = await _factory.ClientFor(Writer).PostAsJsonAsync("/api/mdm/v1/uoms",
            new { code, name = "Again", dimension = "MASS" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    [Trait("Req", "URS-SEC-003")]
    public async Task Missing_permission_is_forbidden()
    {
        var res = await _factory.ClientFor(ReadOnly).PostAsJsonAsync("/api/mdm/v1/uoms",
            new { code = Code(), name = "x", dimension = "MASS" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var (id, _) = await CreateAsync(_factory.ClientFor(Writer), Code());
        var audit = await _factory.ClientFor(ReadOnly).GetAsync($"/api/audit/v1/records/mdm/uom/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
    }

    [Fact]
    [Trait("Req", "URS-SEC-003")]
    public async Task List_returns_only_own_tenant_records_sorted_by_code()
    {
        var mine = Code();
        var theirs = Code();
        await CreateAsync(_factory.ClientFor(Writer), mine);
        await CreateAsync(_factory.ClientFor(OtherTenant), theirs);

        var list = await _factory.ClientFor(ReadOnly).GetFromJsonAsync<JsonElement>("/api/mdm/v1/uoms");
        var codes = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToList();
        Assert.Contains(mine, codes);
        Assert.DoesNotContain(theirs, codes);
        Assert.Equal(codes.Order(StringComparer.Ordinal), codes);
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized_but_health_is_open()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/mdm/v1/uoms/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Validation_errors_are_problem_details()
    {
        var res = await _factory.ClientFor(Writer).PostAsJsonAsync("/api/mdm/v1/uoms", new { code = "", name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty("code", out _));
        Assert.True(body.GetProperty("errors").TryGetProperty("dimension", out _));
    }
}
