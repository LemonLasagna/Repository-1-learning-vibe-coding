using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.Api;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Workflow;

namespace PharmaOS.Platform.Host.Workflow;

/// <summary>A record that moves through a workflow (deviation, CAPA, …).</summary>
public interface IWorkflowRecord
{
    Guid Id { get; }
    string Number { get; }
    int RowVersion { get; }
    string Status { get; }
    Guid UpdatedBy { get; }
    Guid? WorkflowInstanceId { get; }

    /// <summary>record_hash (DS §10.5) over the signed content, excluding workflow state.</summary>
    string ContentHash();

    /// <summary>The record as the workflow guards see it.</summary>
    JsonObject ToRuleData();
}

/// <summary>What the generic workflow code needs to know about one record type.</summary>
public interface IWorkflowEntity
{
    /// <summary>For messages, e.g. "deviation".</summary>
    string Noun { get; }
    WorkflowDefinition Definition { get; }

    Task<IWorkflowRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool forUpdate, CancellationToken ct);

    /// <summary>
    /// Writes the new state and the transition's side effects, only if the row is still at
    /// <c>row.RowVersion</c>. False when someone else changed it first.
    /// </summary>
    Task<bool> SetStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, GxpContext ctx, IWorkflowRecord row, TransitionDefinition t,
        DateTimeOffset now, CancellationToken ct);
}

/// <summary>An action the current user may see, and what stands in its way.</summary>
public sealed record AvailableAction(string Action, string Label, string To, string? SignatureMeaning, bool RequiresReason, string? BlockedBy);

public sealed record TransitionRequest(string? Action, string? Reason);

/// <summary>Workflow rules shared by every record type: evaluation, signed and unsigned transitions, history.</summary>
public static class WorkflowTransitions
{
    public static ActionRefusal? Refusal(TransitionDecision decision) => decision switch
    {
        TransitionDecision.Denied { Kind: DenialKind.MissingPermission } d =>
            new(StatusCodes.Status403Forbidden, "MISSING_PERMISSION", "Not allowed", d.Reason),
        TransitionDecision.Denied { Kind: DenialKind.GuardFailed } d =>
            new(StatusCodes.Status422UnprocessableEntity, "GUARD_FAILED", "Not ready", d.Reason),
        TransitionDecision.Denied d =>
            new(StatusCodes.Status409Conflict, "ACTION_NOT_AVAILABLE", "Action not available", d.Reason),
        _ => null,
    };

    public static bool IsFinal(WorkflowDefinition def, string state) => def.Transitions.All(t => t.From != state);

    public static IReadOnlyList<AvailableAction> Available(WorkflowDefinition def, IWorkflowRecord row, IReadOnlySet<string> permissions)
    {
        var data = row.ToRuleData();
        return TransitionEvaluator.Available(def, row.Status)
            .Where(t => t.Permission is null || permissions.Contains(t.Permission))
            .Select(t => new AvailableAction(t.Action, t.Label ?? t.Action, t.To, t.SignatureMeaning, t.RequireReason,
                TransitionEvaluator.Evaluate(def, row.Status, t.Action, data, permissions, signaturePresented: true)
                    is TransitionDecision.Denied d ? d.Reason : null))
            .ToList();
    }

    /// <summary>State change, history entry (with the signature, if any) and instance completion, in the caller's transaction.</summary>
    public static async Task<ActionRefusal?> ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, GxpContext ctx, IWorkflowEntity entity, WorkflowDefinition definition, IWorkflowRecord row,
        TransitionDefinition t,
        string? reason, Guid? signatureId, string? signatureLabel, DateTimeOffset now, CancellationToken ct)
    {
        var label = t.Label ?? t.Action;
        var suffix = string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}";
        await SetChangeReasonAsync(conn, tx, signatureLabel is null ? $"{label}{suffix}" : $"{label} (signed: {signatureLabel}){suffix}", ct);
        if (!await entity.SetStateAsync(conn, tx, ctx, row, t, now, ct))
            return new(StatusCodes.Status409Conflict, "RECORD_CHANGED", "Record changed",
                $"The {entity.Noun} was changed by someone else. Reload it and try again.");

        if (row.WorkflowInstanceId is { } instance)
        {
            var wf = new WorkflowStore(conn, tx);
            await wf.AddHistoryAsync(ctx, instance, row.Status, t.To, now, reason, signatureId, ct);
            if (IsFinal(definition, t.To)) await wf.CompleteInstanceAsync(instance, t.To == "CANCELLED" ? "CANCELLED" : "COMPLETED", now, ct);
        }
        return null;
    }

    /// <summary>The reason the audit trigger records for the next changes in this transaction.</summary>
    public static async Task SetChangeReasonAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string? reason, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.change_reason', @reason, true)", conn, tx);
        cmd.Parameters.AddWithValue("reason", reason ?? "");
        await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>
    /// POST …/transitions for actions without a signature. Signed actions answer 428 and go through the e-signature
    /// service with the action attached.
    /// </summary>
    public static async Task<IResult> HandleAsync(
        IWorkflowEntity entity, Guid id, TransitionRequest body, PlatformDbContext db, GxpContext ctx, TimeProvider time,
        ClaimsPrincipal user, HttpContext http, Func<NpgsqlConnection, NpgsqlTransaction, Guid, CancellationToken, Task<object>> respond,
        CancellationToken ct)
    {
        if (!ApiResults.TryParseIfMatch(http, out var expected)) return ApiResults.IfMatchRequired();
        var reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim();
        if (reason is { Length: > 1000 }) return ApiResults.Invalid("reason", "Maximum length is 1000.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (conn, sqlTx) = ApiResults.Sql(db);
        var row = await entity.LoadAsync(conn, sqlTx, id, forUpdate: true, ct);
        if (row is null) return Results.NotFound();
        if (row.RowVersion != expected) return ApiResults.Stale(entity.Noun);
        var definition = await new WorkflowStore(conn, sqlTx).DefinitionOfAsync(row, entity, ct);

        var action = body.Action?.Trim() ?? "";
        var decision = TransitionEvaluator.Evaluate(definition, row.Status, action, row.ToRuleData(), user.PermissionsOf(),
            signaturePresented: false);
        if (Refusal(decision) is { } refused) return ApiResults.Problem(refused);
        if (decision is TransitionDecision.SignatureRequired sig)
            return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "Signature required",
                detail: $"This step is signed as {EsigCatalog.FindMeaning(sig.Meaning)?.Label ?? sig.Meaning}. Use the e-signature service with action '{action}'.",
                extensions: new Dictionary<string, object?> { ["code"] = "SIGNATURE_REQUIRED", ["meaning"] = sig.Meaning });

        var t = TransitionEvaluator.Find(definition, row.Status, action)!;
        if (t.RequireReason && reason is null) return ApiResults.Invalid("reason", $"'{t.Label}' needs a reason.");
        // The history refers to the acting user's record.
        await new EsigStore(conn, sqlTx).EnsureUserAsync(ctx, user.Login(), user.DisplayName(), user.FindFirst("email")?.Value, ct);
        if (await ApplyAsync(conn, sqlTx, ctx, entity, definition, row, t, reason, null, null, time.GetUtcNow(), ct) is { } failed)
            return ApiResults.Problem(failed);
        var updated = await entity.LoadAsync(conn, sqlTx, id, forUpdate: false, ct);
        var response = await respond(conn, sqlTx, id, ct);
        await tx.CommitAsync(ct);

        ApiResults.SetETag(http, updated!.RowVersion);
        return Results.Ok(response);
    }
}

/// <summary>
/// A workflow record type signed only together with a transition (submit, approve, cancel, …): the signature and the
/// state change it authorises commit together, in the e-signature completion transaction.
/// </summary>
public abstract class WorkflowSignable(IWorkflowEntity entity) : ISignableSource
{
    public abstract string EntityCode { get; }
    public abstract string EntityName { get; }
    public abstract string ModuleCode { get; }
    public abstract string ModuleName { get; }
    public abstract string PhysicalTable { get; }
    public abstract string ReadPermission { get; }
    public abstract IReadOnlyList<string> Meanings { get; }
    public bool SignaturesRequireAction => true;

    public async Task<SignableRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool lockRow, CancellationToken ct) =>
        await entity.LoadAsync(conn, tx, id, lockRow, ct) is { } row
            ? new SignableRecord(row.Id, row.Number, row.RowVersion, row.ContentHash(), row.UpdatedBy)
            : null;

    public async Task<ActionRefusal?> CheckActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct) =>
        (await CheckAsync(conn, tx, a, ct)).Refusal;

    public async Task<ActionRefusal?> ApplyActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, Guid signatureId, CancellationToken ct)
    {
        var (refusal, row, transition) = await CheckAsync(conn, tx, a, ct);
        if (refusal is not null) return refusal;
        var definition = await new WorkflowStore(conn, tx).DefinitionOfAsync(row!, entity, ct);
        return await WorkflowTransitions.ApplyAsync(conn, tx, a.Context, entity, definition, row!, transition!, a.Reason, signatureId, a.Meaning.Label, a.Now, ct);
    }

    private async Task<(ActionRefusal? Refusal, IWorkflowRecord? Row, TransitionDefinition? Transition)> CheckAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct)
    {
        var row = await entity.LoadAsync(conn, tx, a.RecordId, forUpdate: true, ct);
        if (row is null) return (new(StatusCodes.Status404NotFound, "NOT_FOUND", "Not found", $"The {entity.Noun} does not exist."), null, null);
        var definition = await new WorkflowStore(conn, tx).DefinitionOfAsync(row, entity, ct);
        var decision = TransitionEvaluator.Evaluate(definition, row.Status, a.Action, row.ToRuleData(), a.Permissions,
            signaturePresented: false);
        if (WorkflowTransitions.Refusal(decision) is { } refused) return (refused, row, null);
        var t = TransitionEvaluator.Find(definition, row.Status, a.Action)!;
        if (decision is not TransitionDecision.SignatureRequired required)
            return (new(StatusCodes.Status400BadRequest, "NO_SIGNATURE_NEEDED", "No signature needed",
                $"'{t.Label}' is done without a signature."), row, t);
        if (required.Meaning != a.Meaning.Code)
            return (new(StatusCodes.Status400BadRequest, "WRONG_MEANING", "Wrong signature meaning",
                $"'{t.Label}' is signed as {EsigCatalog.FindMeaning(required.Meaning)?.Label ?? required.Meaning}."), row, t);
        if (t.RequireReason && string.IsNullOrWhiteSpace(a.Reason))
            return (new(StatusCodes.Status400BadRequest, "REASON_REQUIRED", "Reason required", $"'{t.Label}' needs a reason."), row, t);
        return (null, row, t);
    }
}
