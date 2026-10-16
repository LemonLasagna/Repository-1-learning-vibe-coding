using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using PharmaOS.ESig;
using PharmaOS.Platform.Host;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// Electronic signatures through HTTP → PostgreSQL. The Keycloak re-authentication is represented by the
/// auth_time claim the test user carries when completing the signature.
/// </summary>
[Collection(PostgresFixture.Collection)]
[Trait("Req", "URS-SEC-011")]
public sealed class EsigApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");

    private static readonly TestUser Author = new(Guid.Parse("a1111111-0000-0000-0000-000000000001"), Tenant, "Ana Author", "a.author",
        Permissions.UomRead, Permissions.UomWrite, Permissions.AuditTrailRead, Permissions.EsigSign);
    private static readonly TestUser Approver = new(Guid.Parse("a2222222-0000-0000-0000-000000000002"), Tenant, "Avery Approver", "a.approver",
        Permissions.UomRead, Permissions.EsigSign);
    private static readonly TestUser Reviewer = new(Guid.Parse("a3333333-0000-0000-0000-000000000003"), Tenant, "Riley Reviewer", "r.reviewer",
        Permissions.UomRead, Permissions.EsigSign);
    private static readonly TestUser Reader = new(Guid.Parse("a4444444-0000-0000-0000-000000000004"), Tenant, "Rea Reader", "r.reader",
        Permissions.UomRead);

    private readonly PostgresFixture _db;
    private readonly PlatformFactory _factory;

    public EsigApiTests(PostgresFixture db)
    {
        _db = db;
        _factory = new PlatformFactory(db);
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>The user as they are right after typing their password at Keycloak.</summary>
    private static TestUser Fresh(TestUser u, TimeProvider? clock = null) => u with { AuthTime = (clock ?? TimeProvider.System).GetUtcNow() };

    private async Task<(Guid Id, string ETag)> CreateUomAsync()
    {
        var code = "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var res = await _factory.ClientFor(Author).PostAsJsonAsync("/api/mdm/v1/uoms", new { code, name = "Kilogram", dimension = "mass" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), res.Headers.ETag!.Tag);
    }

    private async Task EditUomAsync(Guid id, string etag)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/mdm/v1/uoms/{id}") { Content = JsonContent.Create(new { name = "Kilogramme" }) }
            .WithHeaders(ifMatch: etag, reason: "Spelling");
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientFor(Author).SendAsync(req)).StatusCode);
    }

    private static Task<HttpResponseMessage> ChallengeAsync(HttpClient client, Guid id, string meaning, int? version = 1, bool ack = true, string? reason = null) =>
        client.PostAsJsonAsync("/api/esig/v1/challenges",
            new { entity = "mdm.uom", recordId = id, recordVersion = version, meaning, reason, acknowledge = ack });

    private static async Task<Guid> ChallengeIdAsync(HttpClient client, Guid id, string meaning, int version = 1)
    {
        var res = await ChallengeAsync(client, id, meaning, version);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
    }

    private Task<HttpResponseMessage> CompleteAsync(TestUser user, Guid challengeId, PlatformFactory? factory = null) =>
        (factory ?? _factory).ClientFor(user).PostAsync($"/api/esig/v1/challenges/{challengeId}/complete", null);

    private async Task<HttpResponseMessage> SignAsync(TestUser user, Guid id, string meaning, int version = 1)
    {
        var challenge = await ChallengeIdAsync(_factory.ClientFor(user), id, meaning, version);
        return await CompleteAsync(Fresh(user), challenge);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() : null;

    private async Task<JsonElement> SignaturesAsync(Guid id)
    {
        var res = await _factory.ClientFor(Reader).GetAsync($"/api/esig/v1/records/mdm.uom/{id}/signatures");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Signing_needs_the_esig_permission()
    {
        var (id, _) = await CreateUomAsync();
        var reader = _factory.ClientFor(Reader);
        Assert.Equal(HttpStatusCode.Forbidden, (await ChallengeAsync(reader, id, "REVIEWED")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/esig/v1/meanings?entity=mdm.uom")).StatusCode);
    }

    [Fact]
    [Trait("Req", "URS-SEC-010")]
    public async Task Approval_writes_a_bound_signature_with_manifest_acknowledgement_and_audit_entry()
    {
        var (id, _) = await CreateUomAsync();
        var approver = _factory.ClientFor(Approver);

        var meanings = await approver.GetFromJsonAsync<JsonElement>("/api/esig/v1/meanings?entity=mdm.uom");
        Assert.Equal(["REVIEWED", "APPROVED"], meanings.GetProperty("meanings").EnumerateArray().Select(m => m.GetProperty("code").GetString()));

        // First signature: the legally-binding statement must be acknowledged.
        if (!meanings.GetProperty("statement").GetProperty("acknowledged").GetBoolean())
        {
            var noAck = await ChallengeAsync(approver, id, "APPROVED", ack: false);
            Assert.Equal(HttpStatusCode.BadRequest, noAck.StatusCode);
            Assert.Equal("ACKNOWLEDGEMENT_REQUIRED", await CodeOf(noAck));
        }

        var challenge = await ChallengeAsync(approver, id, "APPROVED", reason: "Released for use");
        Assert.Equal(HttpStatusCode.Created, challenge.StatusCode);
        var challengeId = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();

        var done = await CompleteAsync(Fresh(Approver), challengeId);
        Assert.Equal(HttpStatusCode.Created, done.StatusCode);
        var sig = await done.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Avery Approver", sig.GetProperty("signerName").GetString());
        Assert.Equal("APPROVED", sig.GetProperty("meaning").GetString());
        Assert.Equal(1, sig.GetProperty("recordVersion").GetInt32());
        Assert.Contains("Meaning: Approved", sig.GetProperty("manifest").GetString());
        var sigId = sig.GetProperty("id").GetGuid();

        var list = await SignaturesAsync(id);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("appliesToCurrentVersion").GetBoolean());
        Assert.Equal("a.approver", item.GetProperty("signerLogin").GetString());

        var after = await approver.GetFromJsonAsync<JsonElement>("/api/esig/v1/meanings?entity=mdm.uom");
        Assert.True(after.GetProperty("statement").GetProperty("acknowledged").GetBoolean());

        // The stored row recomputes to its signature_hash (DS §10.5), and the insert is in the audit trail.
        await using (var conn = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                SELECT s.signer_id, m.code, s.signed_at, s.record_id, s.record_version, s.record_hash, s.reason, s.signature_hash
                  FROM esig.signature s JOIN esig.signature_meaning m ON m.id = s.meaning_id WHERE s.id = @id
                """, conn);
            cmd.Parameters.AddWithValue("id", sigId);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            var evidence = new SignatureEvidence(sigId, r.GetGuid(0), r.GetString(1), "mdm.uom", r.GetGuid(3),
                int.Parse(r.GetString(4), CultureInfo.InvariantCulture), r.GetString(5), r.GetFieldValue<DateTimeOffset>(2),
                r.GetString(6), PolicyId: null);
            Assert.Equal(r.GetString(7), SignatureBinding.ComputeSignatureHash(evidence));
        }
        var audit = await _factory.ClientFor(Author).GetFromJsonAsync<JsonElement>($"/api/audit/v1/records/esig/signature/{sigId}");
        var entry = Assert.Single(audit.EnumerateArray());
        Assert.Equal("INSERT", entry.GetProperty("operation").GetString());
        Assert.Equal("Avery Approver (a.approver)", entry.GetProperty("actorDisplay").GetString());
    }

    [Fact]
    [Trait("Req", "URS-SEC-013")]
    public async Task Last_editor_and_reviewer_cannot_approve_the_same_version()
    {
        var (id, _) = await CreateUomAsync();

        var own = await ChallengeAsync(_factory.ClientFor(Author), id, "APPROVED");
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal("SEGREGATION_OF_DUTIES", await CodeOf(own));
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Author, id, "REVIEWED")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Reviewer, id, "REVIEWED")).StatusCode);
        var reviewerApproves = await ChallengeAsync(_factory.ClientFor(Reviewer), id, "APPROVED");
        Assert.Equal(HttpStatusCode.Forbidden, reviewerApproves.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Approver, id, "APPROVED")).StatusCode);
    }

    [Fact]
    [Trait("Req", "URS-SEC-014")]
    public async Task Password_entered_before_the_challenge_is_refused_and_the_challenge_cannot_be_reused()
    {
        var (id, _) = await CreateUomAsync();
        var challenge = await ChallengeIdAsync(_factory.ClientFor(Approver), id, "APPROVED");

        var stale = await CompleteAsync(Approver with { AuthTime = DateTimeOffset.UtcNow.AddMinutes(-10) }, challenge);
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Equal("AUTHENTICATION_NOT_FRESH", await CodeOf(stale));

        var retry = await CompleteAsync(Fresh(Approver), challenge);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal("CHALLENGE_USED", await CodeOf(retry));

        await using var conn = new NpgsqlConnection(_db.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT array_agg(reason ORDER BY attempted_at) FROM esig.signature_failure WHERE record_id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        Assert.Equal(["AUTHENTICATION_NOT_FRESH", "CHALLENGE_USED"], (string[])(await cmd.ExecuteScalarAsync())!);
        Assert.Empty((await SignaturesAsync(id)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Record_changed_while_signing_is_refused()
    {
        var (id, etag) = await CreateUomAsync();
        var challenge = await ChallengeIdAsync(_factory.ClientFor(Approver), id, "APPROVED");
        await EditUomAsync(id, etag);

        var res = await CompleteAsync(Fresh(Approver), challenge);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("RECORD_CHANGED", await CodeOf(res));
    }

    [Fact]
    public async Task Signing_a_version_the_user_is_not_looking_at_is_refused()
    {
        var (id, etag) = await CreateUomAsync();
        await EditUomAsync(id, etag);
        var res = await ChallengeAsync(_factory.ClientFor(Approver), id, "APPROVED", version: 1);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("RECORD_CHANGED", await CodeOf(res));
    }

    [Fact]
    public async Task Signature_on_an_older_version_is_shown_as_not_current()
    {
        var (id, etag) = await CreateUomAsync();
        Assert.Equal(HttpStatusCode.Created, (await SignAsync(Approver, id, "APPROVED")).StatusCode);
        await EditUomAsync(id, etag);

        var list = await SignaturesAsync(id);
        Assert.Equal(2, list.GetProperty("currentVersion").GetInt32());
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(1, item.GetProperty("recordVersion").GetInt32());
        Assert.False(item.GetProperty("appliesToCurrentVersion").GetBoolean());
    }

    [Fact]
    public async Task Another_users_challenge_is_not_found_and_stays_usable_by_its_owner()
    {
        var (id, _) = await CreateUomAsync();
        var challenge = await ChallengeIdAsync(_factory.ClientFor(Approver), id, "APPROVED");
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(Fresh(Reviewer), challenge)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CompleteAsync(Fresh(Approver), challenge)).StatusCode);
    }

    [Fact]
    public async Task Expired_challenge_is_refused()
    {
        var clock = new TestClock();
        using var factory = new PlatformFactory(_db, clock);
        var (id, _) = await CreateUomAsync();
        var res = await ChallengeAsync(factory.ClientFor(Approver), id, "APPROVED");
        var challenge = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();

        clock.Offset = TimeSpan.FromSeconds(121);
        var late = await CompleteAsync(Fresh(Approver, clock), challenge, factory);
        Assert.Equal(HttpStatusCode.Gone, late.StatusCode);
        Assert.Equal("CHALLENGE_EXPIRED", await CodeOf(late));
    }
}
