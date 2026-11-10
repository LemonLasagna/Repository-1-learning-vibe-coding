using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ConfigPackages;
using PharmaOS.Platform.Host.ESignature;

namespace PharmaOS.Platform.Host.Workflow;

/// <summary>
/// A workflow draft signed at each step that changes what the platform will run: submitting it (SUBMITTED), approving it (APPROVED)
/// and activating it (RELEASED). Each signature is bound to the draft's content (code, version, initial state and every transition),
/// so it holds only for exactly what was signed, and the step it authorises commits in the same transaction as the signature.
/// Rejecting signs nothing and stays an ordinary call.
/// </summary>
/// <remarks>
/// The e-signature rules for APPROVED (the last editor cannot approve) enforce the <c>WorkflowAuthoring:RequireSecondPerson</c>
/// setting: with it off, no one counts as the last editor, so a single-user installation can approve its own draft. SUBMITTED and
/// RELEASED are separate meanings because AUTHORED would forbid the same person from approving.
/// </remarks>
public sealed class WorkflowDraftSignable(IOptions<WorkflowAuthoringOptions> options, IOptions<ConfigPackageOptions> packages) : ISignableSource
{
    public const string Entity = "wf.definition";
    public const string Submit = "submit";
    public const string Approve = "approve";
    public const string Activate = "activate";

    /// <summary>The signature meaning each step is signed with.</summary>
    public static readonly IReadOnlyDictionary<string, string> MeaningOf = new Dictionary<string, string>
    {
        [Submit] = EsigCatalog.Submitted.Code,
        [Approve] = EsigCatalog.Approved.Code,
        [Activate] = EsigCatalog.Released.Code,
    };

    public string EntityCode => Entity;
    public string EntityName => "Workflow definition";
    public string ModuleCode => "wf";
    public string ModuleName => "Workflow";
    public string PhysicalTable => "wf.workflow_draft";
    public string ReadPermission => Permissions.WorkflowAuthor;
    public IReadOnlyList<string> Meanings { get; } = [.. MeaningOf.Values];
    public bool SignaturesRequireAction => true;

    public async Task<SignableRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool lockRow, CancellationToken ct) =>
        await new WorkflowDraftStore(conn, tx).GetAsync(id, forUpdate: lockRow, ct) is { } draft
            ? new SignableRecord(draft.Id, $"{draft.Code} v{draft.Version}", draft.RowVersion, ContentHash(draft),
                options.Value.RequireSecondPerson ? draft.UpdatedBy : Guid.Empty)
            : null;

    public async Task<ActionRefusal?> CheckActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct) =>
        (await CheckAsync(conn, tx, a, ct)).Refusal;

    public async Task<ActionRefusal?> ApplyActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, Guid signatureId, CancellationToken ct)
    {
        var (refusal, draft) = await CheckAsync(conn, tx, a, ct);
        if (refusal is not null) return refusal;
        var columns = a.Action switch
        {
            Submit => new Dictionary<string, object?>
            {
                ["status"] = WorkflowAuthoringEndpoints.Submitted, ["submitted_by"] = a.Context.UserId, ["submitted_at"] = a.Now,
                ["submission_signature_id"] = signatureId, ["decision_comment"] = null,
            },
            Approve => new Dictionary<string, object?>
            {
                ["status"] = WorkflowAuthoringEndpoints.Approved, ["approved_by"] = a.Context.UserId, ["approved_at"] = a.Now,
                ["approval_signature_id"] = signatureId, ["decision_comment"] = string.IsNullOrWhiteSpace(a.Reason) ? null : a.Reason.Trim(),
            },
            _ => await ReleaseAsync(conn, tx, a, draft!, signatureId, ct),
        };
        return await new WorkflowDraftStore(conn, tx).SetAsync(draft!.Id, draft.RowVersion, columns, ct)
            ? null
            : new(StatusCodes.Status412PreconditionFailed, "STALE_VERSION", "Stale version",
                "The draft was changed by someone else. Reload it and review the changes.");
    }

    /// <summary>Releases the approved draft as the newest version of its record type's workflow; new records start on it.</summary>
    private static async Task<Dictionary<string, object?>> ReleaseAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, DraftRow draft, Guid signatureId, CancellationToken ct)
    {
        var input = draft.ToInput();
        await ReleaseVersionAsync(conn, tx, a.Context, input, $"Workflow {input.Code} v{input.Version} activated from draft", ct);
        await WorkflowTransitions.SetChangeReasonAsync(conn, tx, a.Context.ChangeReason, ct);
        return new() { ["status"] = WorkflowAuthoringEndpoints.Active, ["activated_at"] = a.Now, ["activation_signature_id"] = signatureId };
    }

    /// <summary>
    /// Writes <paramref name="input"/> as a released version of its record type's workflow, creating the definition on first use.
    /// Shared by activating a draft and deploying a configuration package, so both release a workflow the same way.
    /// </summary>
    internal static async Task ReleaseVersionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, GxpContext ctx, DraftInput input, string changeReason, CancellationToken ct)
    {
        var target = BuiltInWorkflows.Find(input.Code)!;
        var wf = new WorkflowStore(conn, tx);
        var entityDefinitionId = await new EsigStore(conn, tx).EnsureEntityDefinitionAsync(ctx, target.Signable, ct);
        var definitionId = await wf.EnsureDefinitionAsync(ctx, target.Release, target.Name, entityDefinitionId, ct);
        await WorkflowTransitions.SetChangeReasonAsync(conn, tx, changeReason, ct);
        await wf.WriteVersionAsync(ctx, definitionId, new PharmaOS.Workflow.WorkflowDefinition(input.Code, input.Version, input.InitialState, input.Transitions), ct);
    }

    private async Task<(ActionRefusal? Refusal, DraftRow? Draft)> CheckAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct)
    {
        var store = new WorkflowDraftStore(conn, tx);
        var draft = await store.GetAsync(a.RecordId, forUpdate: true, ct);
        if (draft is null) return (new(StatusCodes.Status404NotFound, "NOT_FOUND", "Not found", "The workflow draft does not exist."), null);
        if (!MeaningOf.TryGetValue(a.Action, out var meaning))
            return (new(StatusCodes.Status400BadRequest, "ACTION_NOT_SUPPORTED", "Action not supported", $"'{a.Action}' cannot be signed on a workflow draft."), draft);

        if (packages.Value.IsProduction)
            return (WorkflowAuthoringEndpoints.ProductionLockedRefusal(), draft);

        var permission = a.Action == Submit ? Permissions.WorkflowAuthor : Permissions.WorkflowApprove;
        if (!a.Permissions.Contains(permission))
            return (new(StatusCodes.Status403Forbidden, "MISSING_PERMISSION", "Not allowed", $"Missing permission '{permission}'"), draft);

        var statusOk = a.Action switch
        {
            Submit => draft.Status is WorkflowAuthoringEndpoints.Draft or WorkflowAuthoringEndpoints.Valid,
            Approve => draft.Status == WorkflowAuthoringEndpoints.Submitted,
            _ => draft.Status == WorkflowAuthoringEndpoints.Approved,
        };
        if (!statusOk)
            return (new(StatusCodes.Status409Conflict, "WRONG_STATUS", "Not possible in this status",
                $"The draft is {draft.Status}; {a.Action} needs {NeededStatus(a.Action)}."), draft);

        if (a.Meaning.Code != meaning)
            return (new(StatusCodes.Status400BadRequest, "WRONG_MEANING", "Wrong signature meaning",
                $"'{a.Action}' is signed as {EsigCatalog.FindMeaning(meaning)?.Label ?? meaning}."), draft);

        if (a.Action == Approve && options.Value.RequireSecondPerson && draft.SubmittedBy == a.Context.UserId)
            return (new(StatusCodes.Status403Forbidden, "SAME_PERSON", "Second person required",
                "The person who submitted a workflow cannot approve it. Ask someone else with the approval permission."), draft);

        if (a.Action == Activate && draft.ApprovalSignatureId is null)
            return (new(StatusCodes.Status409Conflict, "NOT_SIGNED", "Approval not signed", "Only a signed approval can be activated."), draft);

        // The rules are checked again at the step that puts the draft in front of others or into use: a release since the draft
        // was written may have taken its version number.
        if (a.Action is Submit or Activate)
        {
            var (_, problems) = await WorkflowAuthoringEndpoints.CheckAsync(store, draft.ToInput(), ct);
            if (problems.Count > 0)
                return (new(a.Action == Submit ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status409Conflict,
                    a.Action == Submit ? "NOT_VALID" : "NO_LONGER_VALID",
                    a.Action == Submit ? "Draft is not valid" : "Draft is no longer valid", string.Join(" ", problems)), draft);
        }
        return (null, draft);
    }

    private static string NeededStatus(string action) => action switch
    {
        Submit => "DRAFT or VALID",
        Approve => WorkflowAuthoringEndpoints.Submitted,
        _ => WorkflowAuthoringEndpoints.Approved,
    };

    /// <summary>record_hash over everything the signatures cover.</summary>
    private static string ContentHash(DraftRow d) => CanonicalJson.Sha256Hex(new JsonObject
    {
        ["entity"] = Entity,
        ["id"] = d.Id.ToString("D"),
        ["code"] = d.Code,
        ["version"] = d.Version,
        ["initialState"] = d.InitialState,
        ["transitions"] = d.Transitions.DeepClone(),
    });
}
