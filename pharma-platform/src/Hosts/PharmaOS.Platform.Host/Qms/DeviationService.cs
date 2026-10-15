using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Platform.Host.Workflow;
using PharmaOS.Workflow;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>An action the current user may see for a deviation, and what stands in its way.</summary>
public sealed record AvailableAction(string Action, string Label, string To, string? SignatureMeaning, bool RequiresReason, string? BlockedBy);

/// <summary>Workflow rules for deviations, shared by the transition endpoint and signed transitions.</summary>
public static class DeviationService
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

    public static IReadOnlyList<AvailableAction> Available(DeviationRow row, IReadOnlySet<string> permissions)
    {
        var data = row.ToRuleData();
        return TransitionEvaluator.Available(DeviationWorkflow.Definition, row.Status)
            .Where(t => t.Permission is null || permissions.Contains(t.Permission))
            .Select(t => new AvailableAction(t.Action, t.Label ?? t.Action, t.To, t.SignatureMeaning, t.RequireReason,
                TransitionEvaluator.Evaluate(DeviationWorkflow.Definition, row.Status, t.Action, data, permissions, signaturePresented: true)
                    is TransitionDecision.Denied d ? d.Reason : null))
            .ToList();
    }

    /// <summary>
    /// Moves the deviation to the transition's target state: status, side effects (QA owner at triage, closure date),
    /// history entry (with the signature, if any) and instance completion. The audit trigger records the status
    /// change with <paramref name="reason"/>.
    /// </summary>
    public static async Task<ActionRefusal?> ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, GxpContext ctx, DeviationRow row, TransitionDefinition t, string? reason,
        Guid? signatureId, string? signatureLabel, DateTimeOffset now, CancellationToken ct)
    {
        var store = new DeviationStore(conn, tx);
        var wf = new WorkflowStore(conn, tx);
        var label = t.Label ?? t.Action;
        var auditReason = signatureLabel is null ? $"{label}{Suffix(reason)}" : $"{label} (signed: {signatureLabel}){Suffix(reason)}";
        await store.SetChangeReasonAsync(auditReason, ct);

        var columns = new Dictionary<string, object?> { ["status"] = t.To, ["workflow_state"] = t.To };
        if (t.Action == "start_investigation") columns["qa_owner_id"] = ctx.UserId;
        if (t.To == DeviationWorkflow.Closed) columns["closed_at"] = now;
        if (!await store.UpdateAsync(row.Id, row.RowVersion, columns, ext: null, ct))
            return new(StatusCodes.Status409Conflict, "RECORD_CHANGED", "Record changed",
                "The deviation was changed by someone else. Reload it and try again.");

        if (row.WorkflowInstanceId is { } instance)
        {
            await wf.AddHistoryAsync(ctx, instance, row.Status, t.To, now, reason, signatureId, ct);
            if (DeviationWorkflow.IsFinal(t.To)) await wf.CompleteInstanceAsync(instance, t.To == DeviationWorkflow.Closed ? "COMPLETED" : "CANCELLED", now, ct);
        }
        return null;
    }

    private static string Suffix(string? reason) => string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}";
}
