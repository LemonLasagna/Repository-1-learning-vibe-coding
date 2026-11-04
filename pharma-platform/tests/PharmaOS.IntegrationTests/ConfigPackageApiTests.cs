using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PharmaOS.Platform.Host;
using PharmaOS.Platform.Host.ConfigPackages;
using PharmaOS.Platform.Host.Keys;
using PharmaOS.Platform.Host.Qms;
using PharmaOS.Workflow;

namespace PharmaOS.IntegrationTests;

/// <summary>An in-process stand-in for OpenBao: signs in the same form and verifies like the real transit engine.</summary>
internal sealed class FakeSigner : IPackageSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public bool Available { get; set; } = true;
    public int Version { get; set; } = 1;
    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();
    public int Signs { get; private set; }

    public Task<PackageSignature> SignAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!Available) throw new SigningUnavailableException("OpenBao is sealed.");
        Signs++;
        var raw = _key.SignData(data.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var text = $"vault:v{Version}:" + Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Task.FromResult(new PackageSignature(text, "pharmaos-packages", Version));
    }

    public Task<bool> VerifyAsync(ReadOnlyMemory<byte> data, string signature, CancellationToken ct) =>
        Task.FromResult(PackageSignatureVerifier.Verify(data.Span, signature, PublicKeyPem));

    public Task<SigningKeyInfo> KeyInfoAsync(CancellationToken ct) => Available
        ? Task.FromResult(new SigningKeyInfo("pharmaos-packages", Version, new Dictionary<int, string> { [Version] = PublicKeyPem }))
        : throw new SigningUnavailableException("OpenBao is sealed.");

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Configuration packages end to end between two installations (two databases): build from released workflow versions, approve with a
/// signature, export, and import where the signing key is trusted. Every refusal on import leaves nothing behind.
/// </summary>
[Collection(PostgresFixture.Collection)]
public sealed class ConfigPackageApiTests : IClassFixture<PostgresFixture>, IAsyncLifetime, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");

    private static readonly TestUser Admin = new(Guid.Parse("b8888888-0000-0000-0000-000000000001"), Tenant, "Pia Packager", "p.packager",
        Permissions.ConfigPackageAuthor, Permissions.ConfigPackageApprove, Permissions.WorkflowAuthor, Permissions.WorkflowApprove, Permissions.EsigSign);

    private static readonly TestUser AuthorOnly = new(Guid.Parse("b8888888-0000-0000-0000-000000000002"), Tenant, "Ari Author", "a.author",
        Permissions.ConfigPackageAuthor, Permissions.EsigSign);

    private static readonly TestUser NoAccess = new(Guid.Parse("b8888888-0000-0000-0000-000000000003"), Tenant, "Nia Nobody", "n.nobody",
        Permissions.CapaRead);

    private readonly PostgresFixture _sourceDb;
    private readonly PostgresFixture _targetDb = new();
    private readonly FakeSigner _sourceSigner = new();
    private readonly FakeSigner _targetSigner = new();
    private PlatformFactory _source = null!;
    private PlatformFactory _target = null!;

    public ConfigPackageApiTests(PostgresFixture db) => _sourceDb = db;

    public async Task InitializeAsync()
    {
        await _targetDb.InitializeAsync();
        _source = new PlatformFactory(_sourceDb, settings: new Dictionary<string, string> { ["ConfigPackages:EnvironmentName"] = "CONFIG" },
            configure: s => s.AddSingleton<IPackageSigner>(_sourceSigner));
        _target = TargetTrusting(_sourceSigner);
    }

    private PlatformFactory TargetTrusting(FakeSigner? trusted) => new(_targetDb, settings: new Dictionary<string, string>
    {
        ["ConfigPackages:EnvironmentName"] = "VAL",
        ["ConfigPackages:TrustedKeys:0:Name"] = trusted is null ? "nobody" : "config-env",
        ["ConfigPackages:TrustedKeys:0:Version"] = "1",
        ["ConfigPackages:TrustedKeys:0:PublicKeyPem"] = (trusted ?? new FakeSigner()).PublicKeyPem,
    }, configure: s => s.AddSingleton<IPackageSigner>(_targetSigner));

    public async Task DisposeAsync() => await _targetDb.DisposeAsync();

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
        _sourceSigner.Dispose();
        _targetSigner.Dispose();
    }

    private static int _version = 1000;

    /// <summary>Each release is newer than the last in its database, which the rules require; one counter serves both installations.</summary>
    private static int NextVersion() => Interlocked.Increment(ref _version);

    private static string NewCode() => "PKG-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>Releases a new CAPA workflow version through the authoring API (submit, approve, activate) and returns its number.</summary>
    private static async Task<int> ReleaseCapaAsync(PlatformFactory factory, Func<TransitionDefinition, TransitionDefinition>? change = null, int? version = null)
    {
        var v = version ?? NextVersion();
        var (id, etag, _) = await Authoring.CreateAsync(factory.ClientFor(Admin), Authoring.Capa(v, change));
        await Authoring.SubmitSignActivateAsync(factory, Admin, id, etag);
        return v;
    }

    private static object Body(string code, params int[] capaVersions) => new
    {
        code, name = "Workflow release", versionLabel = "1.0",
        items = capaVersions.Select(v => new { artifactType = "WORKFLOW", code = CapaWorkflow.Definition.Code, version = v }).ToArray(),
    };

    private static async Task<(Guid Id, JsonElement Body)> BuildAsync(PlatformFactory factory, string code, params int[] versions)
    {
        var res = await factory.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages", Body(code, versions));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetGuid(), json);
    }

    private static async Task<HttpResponseMessage> ApproveAsync(PlatformFactory factory, TestUser user, Guid id)
    {
        var current = await factory.ClientFor(user).GetAsync($"/api/config/v1/packages/{id}");
        var rowVersion = Authoring.VersionOf(current.Headers.ETag!.Tag);
        var challenge = await factory.ClientFor(user).PostAsJsonAsync("/api/esig/v1/challenges", new
        {
            entity = "meta.config_package", recordId = id, recordVersion = rowVersion, meaning = "APPROVED", acknowledge = true, action = "approve",
        });
        if (challenge.StatusCode != HttpStatusCode.Created) return challenge;
        var challengeId = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        return await factory.ClientFor(user with { AuthTime = DateTimeOffset.UtcNow }).PostAsync($"/api/esig/v1/challenges/{challengeId}/complete", null);
    }

    private static async Task<JsonNode> ExportAsync(PlatformFactory factory, Guid id)
    {
        var res = await factory.ClientFor(Admin).PostAsync($"/api/config/v1/packages/{id}/export", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    private async Task<JsonNode> ApprovedFileAsync(params int[] versions)
    {
        var (id, _) = await BuildAsync(_source, NewCode(), versions);
        Assert.Equal(HttpStatusCode.Created, (await ApproveAsync(_source, Admin, id)).StatusCode);
        return await ExportAsync(_source, id);
    }

    private static async Task<JsonElement> ProblemOf(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static IEnumerable<string> Problems(JsonElement problem) => problem.GetProperty("problems").EnumerateArray().Select(p => p.GetString()!);

    private static async Task<long> CountAsync(PostgresFixture db, string table)
    {
        await using var conn = new NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ---- build -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_package_of_released_workflow_versions_is_built_with_a_manifest_and_a_hash()
    {
        var version = await ReleaseCapaAsync(_source);
        var code = NewCode();

        var (_, body) = await BuildAsync(_source, code, version);

        Assert.Equal("DRAFT", body.GetProperty("status").GetString());
        Assert.Equal("CONFIG", body.GetProperty("sourceEnvironment").GetString());
        Assert.Matches("^[0-9a-f]{64}$", body.GetProperty("contentHash").GetString()!);
        var item = Assert.Single(body.GetProperty("manifest").GetProperty("items").EnumerateArray());
        Assert.Equal(CapaWorkflow.Definition.Code, item.GetProperty("code").GetString());
        Assert.Equal(version, item.GetProperty("version").GetInt32());
        Assert.False(item.TryGetProperty("definition", out _));
    }

    [Fact]
    public async Task Only_released_versions_can_be_packaged_and_a_code_is_used_once()
    {
        var version = await ReleaseCapaAsync(_source);
        var client = _source.ClientFor(Admin);

        var unreleased = await client.PostAsJsonAsync("/api/config/v1/packages", Body(NewCode(), 999_999_999));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unreleased.StatusCode);
        Assert.Equal("NOT_RELEASED", await Authoring.CodeOf(unreleased));

        var code = NewCode();
        await BuildAsync(_source, code, version);
        var again = await client.PostAsJsonAsync("/api/config/v1/packages", Body(code, version));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("PACKAGE_EXISTS", await Authoring.CodeOf(again));
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("A")]
    [InlineData("HAS SPACE")]
    public async Task A_badly_formed_code_is_refused(string code)
    {
        var res = await _source.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages", Body(code, 100));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_package_needs_items_each_listed_once()
    {
        var client = _source.ClientFor(Admin);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/config/v1/packages", new { code = NewCode(), name = "x", versionLabel = "1", items = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/config/v1/packages", Body(NewCode(), 100, 100))).StatusCode);
    }

    [Fact]
    public async Task Packages_need_the_author_permission_and_approval_needs_the_approve_permission()
    {
        var version = await ReleaseCapaAsync(_source);
        Assert.Equal(HttpStatusCode.Forbidden, (await _source.ClientFor(NoAccess).GetAsync("/api/config/v1/packages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _source.ClientFor(NoAccess).PostAsJsonAsync("/api/config/v1/packages", Body(NewCode(), version))).StatusCode);

        var res = await _source.ClientFor(AuthorOnly).PostAsJsonAsync("/api/config/v1/packages", Body(NewCode(), version));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var approval = await ApproveAsync(_source, AuthorOnly, id);
        Assert.Equal(HttpStatusCode.Forbidden, approval.StatusCode);
        Assert.Equal("MISSING_PERMISSION", await Authoring.CodeOf(approval));
    }

    // ---- approval and export ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approval_is_a_signature_that_also_has_the_key_sign_the_hash()
    {
        var version = await ReleaseCapaAsync(_source);
        var (id, built) = await BuildAsync(_source, NewCode(), version);
        var signsBefore = _sourceSigner.Signs;

        var plain = await _source.ClientFor(Admin).PostAsync($"/api/config/v1/packages/{id}/approve", null);
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Equal("SIGNATURE_REQUIRED", await Authoring.CodeOf(plain));

        var signed = await ApproveAsync(_source, Admin, id);
        Assert.Equal(HttpStatusCode.Created, signed.StatusCode);

        var after = await (await _source.ClientFor(Admin).GetAsync($"/api/config/v1/packages/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("APPROVED", after.GetProperty("status").GetString());
        Assert.Equal(1, after.GetProperty("signingKeyVersion").GetInt32());
        Assert.Equal(Admin.Sub, after.GetProperty("approvedBy").GetGuid());
        Assert.NotEqual(Guid.Empty, after.GetProperty("approvalSignatureId").GetGuid());
        Assert.Equal(built.GetProperty("contentHash").GetString(), after.GetProperty("contentHash").GetString());
        Assert.Equal(signsBefore + 1, _sourceSigner.Signs);

        var twice = await ApproveAsync(_source, Admin, id);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("WRONG_STATUS", await Authoring.CodeOf(twice));
    }

    [Fact]
    public async Task When_the_key_cannot_be_reached_approval_is_refused_before_a_password_is_asked_and_nothing_changes()
    {
        var version = await ReleaseCapaAsync(_source);
        var (id, _) = await BuildAsync(_source, NewCode(), version);
        _sourceSigner.Available = false;
        try
        {
            var res = await ApproveAsync(_source, Admin, id);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.Equal("SIGNING_UNAVAILABLE", await Authoring.CodeOf(res));
            var after = await (await _source.ClientFor(Admin).GetAsync($"/api/config/v1/packages/{id}")).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("DRAFT", after.GetProperty("status").GetString());
        }
        finally
        {
            _sourceSigner.Available = true;
        }
    }

    [Fact]
    public async Task Only_an_approved_package_can_be_exported_and_the_file_carries_hash_signature_and_content()
    {
        var version = await ReleaseCapaAsync(_source);
        var (id, built) = await BuildAsync(_source, NewCode(), version);
        var early = await _source.ClientFor(Admin).PostAsync($"/api/config/v1/packages/{id}/export", null);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);

        await ApproveAsync(_source, Admin, id);
        var file = await ExportAsync(_source, id);

        Assert.Equal("pharmaos-config-package/1", (string?)file["format"]);
        Assert.Equal(built.GetProperty("contentHash").GetString(), (string?)file["contentHash"]);
        Assert.StartsWith("vault:v1:", (string?)file["signature"]!["value"]);
        Assert.Equal(CapaWorkflow.Definition.Code, (string?)file["package"]!["items"]![0]!["code"]);
        Assert.NotNull(file["package"]!["items"]![0]!["definition"]!["transitions"]);
        var after = await (await _source.ClientFor(Admin).GetAsync($"/api/config/v1/packages/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("exportedAt").ValueKind);
    }

    // ---- import ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_exported_package_imports_where_its_key_is_trusted_and_every_item_passes_the_dry_run()
    {
        var version = await ReleaseCapaAsync(_source);
        var file = await ApprovedFileAsync(version);

        var res = await _target.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IMPORTED", body.GetProperty("status").GetString());
        Assert.Equal("CONFIG", body.GetProperty("sourceEnvironment").GetString());
        Assert.Equal((string?)file["contentHash"], body.GetProperty("contentHash").GetString());
        var log = body.GetProperty("importLog");
        Assert.Equal("config-env", log.GetProperty("trustedAs").GetString());
        Assert.Equal("ADD", Assert.Single(log.GetProperty("items").EnumerateArray()).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Importing_stores_the_package_and_its_items_but_deploys_nothing()
    {
        var version = await ReleaseCapaAsync(_source);
        var file = await ApprovedFileAsync(version);
        var versionsBefore = await CountAsync(_targetDb, "wf.process_definition_version");

        var res = await _target.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(versionsBefore, await CountAsync(_targetDb, "wf.process_definition_version"));
        Assert.True(await CountAsync(_targetDb, "meta.config_package_item") >= 1);
        var list = await (await _target.ClientFor(Admin).GetAsync("/api/config/v1/packages")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list.GetProperty("items").EnumerateArray(), p => p.GetProperty("status").GetString() == "IMPORTED");
    }

    [Fact]
    public async Task A_package_whose_content_changed_after_signing_is_refused_with_a_hash_mismatch_and_nothing_is_stored()
    {
        var version = await ReleaseCapaAsync(_source);
        var file = await ApprovedFileAsync(version);
        var before = await CountAsync(_targetDb, "meta.config_package");
        file["package"]!["items"]![0]!["definition"]!["transitions"]![0]!["permission"] = "capa:everything";

        var res = await _target.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains(Problems(await ProblemOf(res)), p => p.StartsWith("HASH_MISMATCH"));
        Assert.Equal(before, await CountAsync(_targetDb, "meta.config_package"));
    }

    [Fact]
    public async Task A_package_re_signed_by_someone_else_is_refused_even_with_a_matching_hash_and_their_key_in_the_file()
    {
        var version = await ReleaseCapaAsync(_source);
        var file = await ApprovedFileAsync(version);
        using var attacker = new FakeSigner();
        file["package"]!["items"]![0]!["definition"]!["transitions"]![0]!["permission"] = "capa:everything";
        var forged = PackageDocument.Parse(file["package"], []);
        Assert.NotNull(forged);
        file["contentHash"] = forged!.ContentHash;
        file["signature"]!["value"] = (await attacker.SignAsync(PackageDocument.SignedBytes(forged.ContentHash), default)).Signature;
        file["publicKeyPem"] = attacker.PublicKeyPem;

        var res = await _target.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains(Problems(await ProblemOf(res)), p => p.StartsWith("UNTRUSTED_SIGNER"));
    }

    [Fact]
    public async Task A_package_from_an_installation_this_one_does_not_trust_is_refused()
    {
        var version = await ReleaseCapaAsync(_source);
        var file = await ApprovedFileAsync(version);
        using var untrusting = TargetTrusting(null);

        var res = await untrusting.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains(Problems(await ProblemOf(res)), p => p.StartsWith("UNTRUSTED_SIGNER"));
    }

    [Fact]
    public async Task An_unsigned_package_is_refused()
    {
        var version = await ReleaseCapaAsync(_source);
        var file = await ApprovedFileAsync(version);
        file.AsObject().Remove("signature");

        var res = await _target.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains(Problems(await ProblemOf(res)), p => p.StartsWith("SIGNATURE_MISSING"));
    }

    [Fact]
    public async Task The_same_package_cannot_be_imported_twice()
    {
        var version = await ReleaseCapaAsync(_source);
        var file = await ApprovedFileAsync(version);
        Assert.Equal(HttpStatusCode.Created, (await _target.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file)).StatusCode);

        var again = await _target.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("PACKAGE_EXISTS", await Authoring.CodeOf(again));
    }

    [Fact]
    public async Task The_dry_run_refuses_a_version_the_target_already_released_with_different_content()
    {
        var version = NextVersion();
        await ReleaseCapaAsync(_source, version: version);
        await ReleaseCapaAsync(_target, t => t.Action == "close" ? t with { Label = "Different label" } : t, version);
        var file = await ApprovedFileAsync(version);
        var before = await CountAsync(_targetDb, "meta.config_package");

        var res = await _target.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var problem = await ProblemOf(res);
        Assert.Contains(Problems(problem), p => p.Contains("already released here with different content"));
        Assert.Equal("REFUSED", problem.GetProperty("items")[0].GetProperty("outcome").GetString());
        Assert.Equal(before, await CountAsync(_targetDb, "meta.config_package"));
    }

    [Fact]
    public async Task The_dry_run_accepts_a_version_the_target_already_released_identically()
    {
        var version = NextVersion();
        await ReleaseCapaAsync(_source, version: version);
        await ReleaseCapaAsync(_target, version: version);
        var file = await ApprovedFileAsync(version);

        var res = await _target.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var log = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("importLog");
        Assert.Equal("ALREADY_RELEASED", log.GetProperty("items")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task The_dry_run_refuses_content_this_installation_cannot_run_and_a_version_that_is_too_old()
    {
        var version = await ReleaseCapaAsync(_source);
        var file = await ApprovedFileAsync(version);
        // Re-build a validly hashed and signed package whose workflow is not one this installation can run, and one whose version
        // is not newer than the code's own release.
        async Task<HttpResponseMessage> ImportAltered(Action<JsonNode> alter)
        {
            var copy = JsonNode.Parse(file.ToJsonString())!;
            alter(copy["package"]!["items"]![0]!);
            copy["package"]!["code"] = NewCode();
            var doc = PackageDocument.Parse(copy["package"], [])!;
            copy["contentHash"] = doc.ContentHash;
            copy["signature"]!["value"] = (await _sourceSigner.SignAsync(PackageDocument.SignedBytes(doc.ContentHash), default)).Signature;
            return await _target.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", copy);
        }

        var unknownState = await ImportAltered(item => item["definition"]!["transitions"]![0]!["to"] = "NOT_A_STATE");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownState.StatusCode);
        Assert.NotEmpty(Problems(await ProblemOf(unknownState)));

        var tooOld = await ImportAltered(item =>
        {
            item["version"] = 1;
            item["definition"]!["version"] = 1;
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooOld.StatusCode);
        Assert.Contains(Problems(await ProblemOf(tooOld)), p => p.Contains("Version must be higher"));
    }

    [Fact]
    public async Task A_package_built_and_signed_here_imports_here_because_this_installations_own_key_is_trusted()
    {
        var version = await ReleaseCapaAsync(_target);
        var (id, _) = await BuildAsync(_target, NewCode(), version);
        await ApproveAsync(_target, Admin, id);
        var file = await ExportAsync(_target, id);
        // Importing into a database where the code is free: a second target, trusting nobody but itself.
        await using var other = new PostgresFixture();
        await other.InitializeAsync();
        using var factory = new PlatformFactory(other, configure: s => s.AddSingleton<IPackageSigner>(_targetSigner));

        var res = await factory.ClientFor(Admin).PostAsJsonAsync("/api/config/v1/packages/import", file);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var log = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("importLog");
        Assert.Contains("this installation", log.GetProperty("trustedAs").GetString());
    }
}
