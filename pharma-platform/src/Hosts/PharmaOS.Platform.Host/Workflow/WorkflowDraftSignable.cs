using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ESignature;

namespace PharmaOS.Platform.Host.Workflow;

/// <summary>
/// A workflow draft signed on approval. The signature is bound to the draft's content (code, version, initial state and every
/// transition), so it holds only for exactly what was approved, and the approval it authorises commits in the same transaction.
/// </summary>
/// <remarks>
/// The e-signature rules for APPROVED (the last editor cannot approve) enforce the <c>WorkflowAuthoring:RequireSecondPerson</c>
/// setting: with it off, no one counts as the last editor, so a single-user installation can approve its own draft.
/// </remarks>
public sealed class WorkflowDraftSignable(IOptions<WorkflowAuthoringOptions> options) : ISignableSource
{
    public const string Entity = "wf.definition";
    public const string Action = "approve";

    public string EntityCode => Entity;
    public string EntityName => "Workflow definition";
    public string ModuleCode => "wf";
    public string ModuleName => "Workflow";
    public string PhysicalTable => "wf.workflow_draft";
    public string ReadPermission => Permissions.WorkflowAuthor;
    public IReadOnlyList<string> Meanings { get; } = [EsigCatalog.Approved.Code];
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
        var applied = await new WorkflowDraftStore(conn, tx).SetAsync(draft!.Id, draft.RowVersion, new Dictionary<string, object?>
        {
            ["status"] = WorkflowAuthoringEndpoints.Approved,
            ["approved_by"] = a.Context.UserId,
            ["approved_at"] = a.Now,
            ["approval_signature_id"] = signatureId,
            ["decision_comment"] = string.IsNullOrWhiteSpace(a.Reason) ? null : a.Reason.Trim(),
        }, ct);
        return applied ? null : new(StatusCodes.Status412PreconditionFailed, "STALE_VERSION", "Stale version",
            "The draft was changed by someone else. Reload it and review the changes.");
    }

    private async Task<(ActionRefusal? Refusal, DraftRow? Draft)> CheckAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct)
    {
        var draft = await new WorkflowDraftStore(conn, tx).GetAsync(a.RecordId, forUpdate: true, ct);
        if (draft is null) return (new(StatusCodes.Status404NotFound, "NOT_FOUND", "Not found", "The workflow draft does not exist."), null);
        if (a.Action != Action)
            return (new(StatusCodes.Status400BadRequest, "ACTION_NOT_SUPPORTED", "Action not supported", $"'{a.Action}' cannot be signed on a workflow draft."), draft);
        if (!a.Permissions.Contains(Permissions.WorkflowApprove))
            return (new(StatusCodes.Status403Forbidden, "MISSING_PERMISSION", "Not allowed", $"Missing permission '{Permissions.WorkflowApprove}'"), draft);
        if (draft.Status != WorkflowAuthoringEndpoints.Submitted)
            return (new(StatusCodes.Status409Conflict, "WRONG_STATUS", "Not possible in this status",
                $"The draft is {draft.Status}; approval needs {WorkflowAuthoringEndpoints.Submitted}."), draft);
        if (a.Meaning.Code != EsigCatalog.Approved.Code)
            return (new(StatusCodes.Status400BadRequest, "WRONG_MEANING", "Wrong signature meaning", "Approving a workflow is signed as Approved."), draft);
        if (options.Value.RequireSecondPerson && draft.SubmittedBy == a.Context.UserId)
            return (new(StatusCodes.Status403Forbidden, "SAME_PERSON", "Second person required",
                "The person who submitted a workflow cannot approve it. Ask someone else with the approval permission."), draft);
        return (null, draft);
    }

    /// <summary>record_hash over everything the approval covers.</summary>
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
