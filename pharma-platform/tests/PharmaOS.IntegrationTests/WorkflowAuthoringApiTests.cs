using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using PharmaOS.Platform.Host;
using PharmaOS.Platform.Host.Qms;
using PharmaOS.Workflow;

namespace PharmaOS.IntegrationTests;

/// <summary>Request bodies and calls shared by the workflow authoring tests.</summary>
internal static class Authoring
{
    public const string Base = "/api/workflow/v1/definitions";

    /// <summary>A version number no other test in the class uses, above anything the code releases.</summary>
    public static int NewVersion() => Random.Shared.Next(100, 1_000_000);

    public static object Transition(TransitionDefinition t) => new
    {
        action = t.Action, from = t.From, to = t.To, label = t.Label, permission = t.Permission, guard = t.Guard,
        guardMessage = t.GuardMessage, signatureMeaning = t.SignatureMeaning, requireReason = t.RequireReason,
    };

    /// <summary>The shipped CAPA workflow as a draft of <paramref name="version"/>, optionally changed per transition.</summary>
    public static object Capa(int version, Func<TransitionDefinition, TransitionDefinition>? change = null) => new
    {
        code = CapaWorkflow.Definition.Code,
        version,
        initialState = CapaWorkflow.Definition.InitialState,
        transitions = CapaWorkflow.Definition.Transitions.Select(change ?? (t => t)).Select(Transition).ToArray(),
    };

    /// <summary>A shipped workflow as a draft of <paramref name="version"/>, with the given transitions (default: unchanged).</summary>
    public static object From(WorkflowDefinition release, int version, IEnumerable<TransitionDefinition>? transitions = null) => new
    {
        code = release.Code,
        version,
        initialState = release.InitialState,
        transitions = (transitions ?? release.Transitions).Select(Transition).ToArray(),
    };

    public static async Task<(Guid Id, string Etag, JsonElement Body)> CreateAsync(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync(Base, body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetGuid(), res.Headers.ETag!.Tag, json);
    }

    public static Task<HttpResponseMessage> StepAsync(HttpClient client, Guid id, string step, string etag, object? body = null) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/{id}/{step}") { Content = JsonContent.Create(body ?? new { }) }
            .WithHeaders(ifMatch: etag));

    public static int VersionOf(string etag) => int.Parse(etag.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);

    public static async Task<string> EtagAsync(HttpClient client, Guid id) =>
        (await client.GetAsync($"{Base}/{id}")).Headers.ETag!.Tag;

    /// <summary>Every step but rejecting is an electronic signature: a challenge, then the completion as if the password had just been typed.</summary>
    public static async Task<HttpResponseMessage> SignAsync(
        PlatformFactory factory, TestUser user, Guid id, string action, string meaning, int recordVersion, string? reason = null)
    {
        var challenge = await factory.ClientFor(user).PostAsJsonAsync("/api/esig/v1/challenges", new
        {
            entity = "wf.definition", recordId = id, recordVersion, meaning, reason, acknowledge = true, action,
        });
        if (challenge.StatusCode != HttpStatusCode.Created) return challenge;
        var challengeId = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeId").GetGuid();
        return await factory.ClientFor(user with { AuthTime = DateTimeOffset.UtcNow })
            .PostAsync($"/api/esig/v1/challenges/{challengeId}/complete", null);
    }

    public static Task<HttpResponseMessage> SignSubmitAsync(PlatformFactory factory, TestUser user, Guid id, int recordVersion, string? reason = null) =>
        SignAsync(factory, user, id, "submit", "SUBMITTED", recordVersion, reason);

    public static Task<HttpResponseMessage> SignApprovalAsync(PlatformFactory factory, TestUser user, Guid id, int recordVersion, string? reason = null) =>
        SignAsync(factory, user, id, "approve", "APPROVED", recordVersion, reason);

    public static Task<HttpResponseMessage> SignActivationAsync(PlatformFactory factory, TestUser user, Guid id, int recordVersion, string? reason = null) =>
        SignAsync(factory, user, id, "activate", "RELEASED", recordVersion, reason);

    /// <summary>Signs the submission (the draft is at <paramref name="etag"/>) and returns the draft's new ETag.</summary>
    public static async Task<string> SubmitAsync(PlatformFactory factory, TestUser user, Guid id, string etag)
    {
        var signed = await SignSubmitAsync(factory, user, id, VersionOf(etag));
        Assert.Equal(HttpStatusCode.Created, signed.StatusCode);
        return await EtagAsync(factory.ClientFor(user), id);
    }

    /// <summary>Submit, approve and activate, each signed, as one user who holds every permission. Returns the draft's final ETag.</summary>
    public static async Task<string> SubmitSignActivateAsync(PlatformFactory factory, TestUser user, Guid id, string etag)
    {
        var client = factory.ClientFor(user);
        etag = await SubmitAsync(factory, user, id, etag);
        var approved = await SignApprovalAsync(factory, user, id, VersionOf(etag));
        Assert.Equal(HttpStatusCode.Created, approved.StatusCode);
        var activated = await SignActivationAsync(factory, user, id, VersionOf(await EtagAsync(client, id)));
        Assert.Equal(HttpStatusCode.Created, activated.StatusCode);
        return await EtagAsync(client, id);
    }

    public static async Task<string> CodeOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
}

/// <summary>
/// Workflow authoring: drafts of a record type's workflow are checked and stored, never released until activated (URS-WF-005 keeps
/// released versions unchanged), and a draft cannot take a version number that is already released. Nothing in this class is
/// activated, so every draft is compared against the code's own release.
/// </summary>
[Collection(PostgresFixture.Collection)]
[Trait("Req", "URS-WF-005")]
public sealed class WorkflowAuthoringApiTests : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("0000000a-0000-0000-0000-000000000000");

    private static readonly TestUser Author = new(Guid.Parse("b7777777-0000-0000-0000-000000000001"), Tenant, "Wanda Author", "w.author",
        Permissions.WorkflowAuthor, Permissions.WorkflowApprove, Permissions.EsigSign);

    /// <summary>May write and sign, but not approve workflows.</summary>
    private static readonly TestUser Signer = new(Guid.Parse("b7777777-0000-0000-0000-000000000004"), Tenant, "Sia Signer", "s.signer",
        Permissions.WorkflowAuthor, Permissions.EsigSign);

    private static readonly TestUser AuthorOnly = new(Guid.Parse("b7777777-0000-0000-0000-000000000003"), Tenant, "Ari Author", "a.author",
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

    private const string Base = Authoring.Base;

    private static string NewCode() => "AUTH" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    public async Task A_complete_draft_is_stored_as_valid_with_an_etag()
    {
        var res = await As(Author).PostAsJsonAsync(Base, Authoring.Capa(Authoring.NewVersion()));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALID", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("errors").EnumerateArray());
        Assert.NotNull(res.Headers.ETag);
    }

    [Fact]
    public async Task An_incomplete_draft_is_still_saved_and_says_what_is_wrong()
    {
        var (_, _, body) = await Authoring.CreateAsync(As(Author),
            Authoring.Capa(Authoring.NewVersion(), t => t.Action == "cancel" ? t with { Permission = null } : t));
        Assert.Equal("DRAFT", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("needs a permission"));
    }

    [Fact]
    public async Task A_draft_must_fit_its_record_type()
    {
        var (_, _, body) = await Authoring.CreateAsync(As(Author), Authoring.Capa(Authoring.NewVersion(), t => t.Action == "approve_plan"
            ? t with { To = "APPROVED_SOMEWHERE", Permission = "qms.capa:anything", SignatureMeaning = "AUTHORED" }
            : t));
        var errors = body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(errors, e => e.StartsWith("State 'APPROVED_SOMEWHERE' is not a CAPA state"));
        Assert.Contains(errors, e => e.Contains("permission 'qms.capa:anything', which the platform does not define"));
        Assert.Contains(errors, e => e.Contains("signature meaning 'AUTHORED', which a CAPA cannot be signed with"));
    }

    [Fact]
    public async Task A_guard_must_read_fields_its_record_type_has()
    {
        var guard = JsonNode.Parse("""{">":[{"var":"no_such_field"},0]}""");
        var (_, _, body) = await Authoring.CreateAsync(As(Author),
            Authoring.Capa(Authoring.NewVersion(), t => t.Action == "submit_plan" ? t with { Guard = guard } : t));
        Assert.Equal("DRAFT", body.GetProperty("status").GetString());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(),
            e => e.GetString()!.StartsWith("Transition 1 ('submit_plan') has a guard that reads 'no_such_field', which a CAPA does not have"));
    }

    [Fact]
    public async Task A_guard_may_read_the_fields_the_shipped_guards_read()
    {
        var guard = JsonNode.Parse("""{"and":[{">":[{"var":"actions_planned"},1]},{"var":"effectiveness_defined"}]}""");
        var (_, _, body) = await Authoring.CreateAsync(As(Author),
            Authoring.Capa(Authoring.NewVersion(), t => t.Action == "submit_plan" ? t with { Guard = guard } : t));
        Assert.Equal("VALID", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_guard_with_a_computed_field_name_is_flagged()
    {
        var guard = JsonNode.Parse("""{"var":{"if":[true,"actions_planned","actions_open"]}}""");
        var (_, _, body) = await Authoring.CreateAsync(As(Author),
            Authoring.Capa(Authoring.NewVersion(), t => t.Action == "submit_plan" ? t with { Guard = guard } : t));
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("cannot be checked: A variable name must be written out"));
    }

    [Fact]
    public async Task A_deviation_guard_may_read_a_custom_field_because_its_field_set_is_open()
    {
        var guard = JsonNode.Parse("""{"not":{"isEmpty":{"var":"tenant_custom_field"}}}""");
        var transitions = DeviationWorkflow.Definition.Transitions.Select(t => t.Action == "start_investigation" ? t with { Guard = guard } : t);
        var (_, _, body) = await Authoring.CreateAsync(As(Author), Authoring.From(DeviationWorkflow.Definition, Authoring.NewVersion(), transitions));
        Assert.Equal("VALID", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_workflow_no_record_type_uses_cannot_become_valid()
    {
        var body = new { code = NewCode(), version = 1, initialState = "DRAFT",
            transitions = new[] { new { action = "submit", from = "DRAFT", to = "DONE", permission = Permissions.CapaWrite } } };
        var (_, _, json) = await Authoring.CreateAsync(As(Author), body);
        Assert.Equal("DRAFT", json.GetProperty("status").GetString());
        Assert.Contains(json.GetProperty("errors").EnumerateArray(), e => e.GetString()!.StartsWith("No record type uses workflow"));
    }

    [Fact]
    public async Task A_version_at_or_below_the_code_release_is_not_valid()
    {
        var (_, _, body) = await Authoring.CreateAsync(As(Author), Authoring.Capa(1));
        Assert.Contains(body.GetProperty("errors").EnumerateArray(),
            e => e.GetString() == "Version must be higher than 1, the newest released version of CAPA_STD.");
    }

    [Fact]
    public async Task A_missing_field_is_refused_with_field_errors()
    {
        var res = await As(Author).PostAsJsonAsync(Base, new { version = 1, initialState = "DRAFT", transitions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("code", out _));
    }

    [Fact]
    public async Task A_second_draft_of_the_same_version_is_refused()
    {
        var version = Authoring.NewVersion();
        await Authoring.CreateAsync(As(Author), Authoring.Capa(version));
        var res = await As(Author).PostAsJsonAsync(Base, Authoring.Capa(version));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("DRAFT_EXISTS", await Authoring.CodeOf(res));
    }

    [Fact]
    public async Task A_version_already_released_for_the_code_is_refused()
    {
        var code = NewCode();
        await ReleaseAsync(code, version: 1);
        var res = await As(Author).PostAsJsonAsync(Base, new { code, version = 1, initialState = "DRAFT", transitions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("VERSION_RELEASED", await Authoring.CodeOf(res));
    }

    [Fact]
    public async Task Replacing_a_draft_needs_the_current_etag()
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(Author), Authoring.Capa(Authoring.NewVersion()));
        var path = $"{Base}/{id}";
        var changed = Authoring.Capa(Authoring.NewVersion(), t => t with { Label = t.Label + " (v2)" });

        var missing = await As(Author).PutAsJsonAsync(path, changed);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);

        var stale = await As(Author).SendAsync(new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(changed) }
            .WithHeaders(ifMatch: "\"999\""));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var ok = await As(Author).SendAsync(new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(changed) }
            .WithHeaders(ifMatch: etag));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.EndsWith("(v2)", updated.GetProperty("transitions")[0].GetProperty("label").GetString());
        Assert.Equal(2, updated.GetProperty("rowVersion").GetInt32());
    }

    [Fact]
    public async Task Validate_reports_the_rules_without_changing_the_draft()
    {
        var (id, _, _) = await Authoring.CreateAsync(As(Author),
            Authoring.Capa(Authoring.NewVersion(), t => t.Action == "cancel" ? t with { Permission = null } : t));

        var res = await As(Author).PostAsJsonAsync($"{Base}/{id}/validate", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("valid").GetBoolean());
        Assert.Contains(body.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("needs a permission"));

        var after = await As(Author).GetFromJsonAsync<JsonElement>($"{Base}/{id}");
        Assert.Equal(1, after.GetProperty("rowVersion").GetInt32());
    }

    [Fact]
    public async Task A_draft_that_is_not_valid_cannot_be_signed_for_submission()
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(Author),
            Authoring.Capa(Authoring.NewVersion(), t => t.Action == "cancel" ? t with { Permission = null } : t));
        var res = await Authoring.SignSubmitAsync(_factory, Author, id, Authoring.VersionOf(etag));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal("NOT_VALID", await Authoring.CodeOf(res));
    }

    [Fact]
    public async Task A_submitted_draft_is_locked_until_it_is_rejected_with_a_reason()
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(Author), Authoring.Capa(Authoring.NewVersion()));
        etag = await Authoring.SubmitAsync(_factory, Author, id, etag);
        var submitted = await As(Author).GetFromJsonAsync<JsonElement>($"{Base}/{id}");
        Assert.Equal("SUBMITTED", submitted.GetProperty("status").GetString());
        Assert.Equal(Author.Sub, submitted.GetProperty("submittedBy").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, submitted.GetProperty("submissionSignatureId").ValueKind);

        var edit = await As(Author).SendAsync(new HttpRequestMessage(HttpMethod.Put, $"{Base}/{id}")
            { Content = JsonContent.Create(Authoring.Capa(Authoring.NewVersion())) }.WithHeaders(ifMatch: etag));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("WRONG_STATUS", await Authoring.CodeOf(edit));

        var submitAgain = await Authoring.SignSubmitAsync(_factory, Author, id, Authoring.VersionOf(etag));
        Assert.Equal("WRONG_STATUS", await Authoring.CodeOf(submitAgain));
        var activateEarly = await Authoring.SignActivationAsync(_factory, Author, id, Authoring.VersionOf(etag));
        Assert.Equal("WRONG_STATUS", await Authoring.CodeOf(activateEarly));

        var noReason = await Authoring.StepAsync(As(Author), id, "reject", etag);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var reject = await Authoring.StepAsync(As(Author), id, "reject", etag, new { comment = "Labels need work." });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var rejected = await reject.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALID", rejected.GetProperty("status").GetString());
        Assert.Equal("Labels need work.", rejected.GetProperty("decisionComment").GetString());
        Assert.Equal(JsonValueKind.Null, rejected.GetProperty("submittedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, rejected.GetProperty("submissionSignatureId").ValueKind);
    }

    [Fact]
    public async Task A_plain_call_to_approve_needs_the_approval_permission()
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(AuthorOnly), Authoring.Capa(Authoring.NewVersion()));
        var res = await Authoring.StepAsync(As(AuthorOnly), id, "approve", etag);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Each_record_type_offers_its_current_workflow_as_a_starting_point()
    {
        var items = (await As(Author).GetFromJsonAsync<JsonElement>("/api/workflow/v1/workflows")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["CAPA_STD", "CHANGE_STD", "DEVIATION_STD"], items.Select(i => i.GetProperty("code").GetString()));
        var capa = items.Single(i => i.GetProperty("code").GetString() == "CAPA_STD");
        Assert.Equal(CapaWorkflow.Definition.Version, capa.GetProperty("version").GetInt32());
        Assert.Equal(CapaWorkflow.Definition.Transitions.Count, capa.GetProperty("transitions").GetArrayLength());
        Assert.Equal(CapaWorkflow.Definition.Version, capa.GetProperty("releaseVersion").GetInt32());
        Assert.All(items, i => Assert.False(i.GetProperty("releaseNotApplied").GetBoolean()));
    }

    [Theory]
    [InlineData("submit", "SUBMITTED")]
    [InlineData("approve", "APPROVED")]
    [InlineData("activate", "RELEASED")]
    public async Task A_plain_call_to_a_signed_step_says_the_step_is_a_signature(string step, string meaning)
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(Author), Authoring.Capa(Authoring.NewVersion()));
        var res = await Authoring.StepAsync(As(Author), id, step, etag);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SIGNATURE_REQUIRED", problem.GetProperty("code").GetString());
        Assert.Contains($"action {step}, meaning {meaning}", problem.GetProperty("detail").GetString());
        Assert.Equal("VALID", (await As(Author).GetFromJsonAsync<JsonElement>($"{Base}/{id}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Signing_the_submission_needs_the_authoring_permission()
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(Author), Authoring.Capa(Authoring.NewVersion()));
        var reader = new TestUser(Guid.Parse("b7777777-0000-0000-0000-000000000005"), Tenant, "Rae Reader", "r.reader", Permissions.EsigSign);
        var res = await Authoring.SignSubmitAsync(_factory, reader, id, Authoring.VersionOf(etag));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("MISSING_PERMISSION", await Authoring.CodeOf(res));
    }

    [Fact]
    public async Task Signing_the_approval_needs_the_approval_permission()
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(Signer), Authoring.Capa(Authoring.NewVersion()));
        etag = await Authoring.SubmitAsync(_factory, Signer, id, etag);
        var res = await Authoring.SignApprovalAsync(_factory, Signer, id, Authoring.VersionOf(etag));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("MISSING_PERMISSION", await Authoring.CodeOf(res));
    }

    [Fact]
    public async Task A_draft_that_was_not_submitted_cannot_be_signed_as_approved()
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(Author), Authoring.Capa(Authoring.NewVersion()));
        var res = await Authoring.SignApprovalAsync(_factory, Author, id, Authoring.VersionOf(etag));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("WRONG_STATUS", await Authoring.CodeOf(res));
    }

    [Fact]
    public async Task A_step_is_signed_with_its_own_meaning_only()
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(Author), Authoring.Capa(Authoring.NewVersion()));
        var wrong = await Authoring.SignAsync(_factory, Author, id, "submit", "APPROVED", Authoring.VersionOf(etag));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("WRONG_MEANING", await Authoring.CodeOf(wrong));
        var unknown = await Authoring.SignAsync(_factory, Author, id, "reject", "APPROVED", Authoring.VersionOf(etag));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("ACTION_NOT_SUPPORTED", await Authoring.CodeOf(unknown));
    }

    [Fact]
    public async Task Submission_and_approval_are_signed_and_bound_to_the_content()
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(Author), Authoring.Capa(Authoring.NewVersion()));
        etag = await Authoring.SubmitAsync(_factory, Author, id, etag);
        var signed = await Authoring.SignApprovalAsync(_factory, Author, id, Authoring.VersionOf(etag), "Checked against the SOP.");
        Assert.Equal(HttpStatusCode.Created, signed.StatusCode);
        var signature = await signed.Content.ReadFromJsonAsync<JsonElement>();

        var draft = await As(Author).GetFromJsonAsync<JsonElement>($"{Base}/{id}");
        Assert.Equal("APPROVED", draft.GetProperty("status").GetString());
        Assert.Equal(signature.GetProperty("id").GetGuid(), draft.GetProperty("approvalSignatureId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, draft.GetProperty("submissionSignatureId").ValueKind);
        Assert.Equal(Author.Sub, draft.GetProperty("approvedBy").GetGuid());
        Assert.Equal("Checked against the SOP.", draft.GetProperty("decisionComment").GetString());
        Assert.Equal("APPROVED", signature.GetProperty("meaning").GetString());
        Assert.Matches("^[0-9a-f]{64}$", signature.GetProperty("recordHash").GetString());

        var listed = await As(Author).GetFromJsonAsync<JsonElement>($"/api/esig/v1/records/wf.definition/{id}/signatures");
        var meanings = listed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("meaning").GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["APPROVED", "SUBMITTED"], meanings);
    }

    [Fact]
    public async Task Rejecting_an_approved_draft_clears_the_links_to_its_signatures()
    {
        var (id, etag, _) = await Authoring.CreateAsync(As(Author), Authoring.Capa(Authoring.NewVersion()));
        etag = await Authoring.SubmitAsync(_factory, Author, id, etag);
        await Authoring.SignApprovalAsync(_factory, Author, id, Authoring.VersionOf(etag));
        var reject = await Authoring.StepAsync(As(Author), id, "reject", await Authoring.EtagAsync(As(Author), id), new { comment = "Changed my mind." });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var draft = await reject.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALID", draft.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("approvalSignatureId").ValueKind);
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("submissionSignatureId").ValueKind);
    }

    [Fact]
    public async Task An_approved_draft_without_a_signed_approval_cannot_be_signed_for_activation()
    {
        var (id, _, _) = await Authoring.CreateAsync(As(Author), Authoring.Capa(Authoring.NewVersion()));
        // No endpoint reaches APPROVED without a signature, so the status is set directly, as a corrupted or tampered row would be.
        await using (var conn = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await conn.OpenAsync();
            await using (var session = new NpgsqlCommand("SELECT set_config('app.user_id', @user, false), set_config('app.tenant_id', @tenant, false)", conn))
            {
                session.Parameters.AddWithValue("user", Author.Sub.ToString());
                session.Parameters.AddWithValue("tenant", Tenant.ToString());
                await session.ExecuteNonQueryAsync();
            }
            await using var update = new NpgsqlCommand("UPDATE wf.workflow_draft SET status = 'APPROVED' WHERE id = @id", conn);
            update.Parameters.AddWithValue("id", id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        var res = await Authoring.SignActivationAsync(_factory, Author, id, Authoring.VersionOf(await Authoring.EtagAsync(As(Author), id)));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("NOT_SIGNED", await Authoring.CodeOf(res));
    }

    [Fact]
    public async Task A_user_without_the_authoring_permission_cannot_create_drafts()
    {
        var res = await As(NoAccess).PostAsJsonAsync(Base, Authoring.Capa(Authoring.NewVersion()));
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
