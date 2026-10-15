using Npgsql;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Workflow;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>
/// Deviations are signed only through workflow transitions (submit, approve, cancel): the signature and the state
/// change it authorises commit together, in the e-signature completion transaction.
/// </summary>
public sealed class DeviationSignable : ISignableSource
{
    public string EntityCode => "qms.deviation";
    public string EntityName => "Deviation";
    public string ModuleCode => "qms";
    public string ModuleName => "Quality management";
    public string PhysicalTable => "qms.deviation";
    public string ReadPermission => Permissions.DeviationRead;
    public IReadOnlyList<string> Meanings { get; } = [EsigCatalog.Authored.Code, EsigCatalog.Approved.Code, EsigCatalog.Cancelled.Code];
    public bool SignaturesRequireAction => true;

    public async Task<SignableRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool lockRow, CancellationToken ct) =>
        await new DeviationStore(conn, tx).GetAsync(id, lockRow, ct) is { } row
            ? new SignableRecord(row.Id, row.DeviationNo, row.RowVersion, row.ContentHash(), row.UpdatedBy)
            : null;

    public async Task<ActionRefusal?> CheckActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct) =>
        (await CheckAsync(conn, tx, a, ct)).Refusal;

    public async Task<ActionRefusal?> ApplyActionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, Guid signatureId, CancellationToken ct)
    {
        var (refusal, row, transition) = await CheckAsync(conn, tx, a, ct);
        if (refusal is not null) return refusal;
        return await DeviationService.ApplyAsync(conn, tx, a.Context, row!, transition!, a.Reason, signatureId, a.Meaning.Label, a.Now, ct);
    }

    private static async Task<(ActionRefusal? Refusal, DeviationRow? Row, TransitionDefinition? Transition)> CheckAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, SignedAction a, CancellationToken ct)
    {
        var row = await new DeviationStore(conn, tx).GetAsync(a.RecordId, forUpdate: true, ct);
        if (row is null) return (new(StatusCodes.Status404NotFound, "NOT_FOUND", "Not found", "The deviation does not exist."), null, null);
        var decision = TransitionEvaluator.Evaluate(DeviationWorkflow.Definition, row.Status, a.Action, row.ToRuleData(), a.Permissions,
            signaturePresented: false);
        if (DeviationService.Refusal(decision) is { } refused) return (refused, row, null);
        var t = TransitionEvaluator.Find(DeviationWorkflow.Definition, row.Status, a.Action)!;
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
