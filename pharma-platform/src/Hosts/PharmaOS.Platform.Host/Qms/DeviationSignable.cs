using Npgsql;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.ESignature;
using PharmaOS.Platform.Host.Workflow;
using PharmaOS.Workflow;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>Deviations as a workflow record type.</summary>
public sealed class DeviationEntity : IWorkflowEntity
{
    public static readonly DeviationEntity Instance = new();

    public string Noun => "deviation";
    public WorkflowDefinition Definition => DeviationWorkflow.Definition;

    public async Task<IWorkflowRecord?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool forUpdate, CancellationToken ct) =>
        await new DeviationStore(conn, tx).GetAsync(id, forUpdate, ct);

    /// <summary>Status plus side effects: the triaging QA becomes QA owner; approval sets the closure time.</summary>
    public Task<bool> SetStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, GxpContext ctx, IWorkflowRecord row, TransitionDefinition t,
        DateTimeOffset now, CancellationToken ct)
    {
        var columns = new Dictionary<string, object?> { ["status"] = t.To, ["workflow_state"] = t.To };
        if (t.Action == "start_investigation") columns["qa_owner_id"] = ctx.UserId;
        if (t.To == DeviationWorkflow.Closed) columns["closed_at"] = now;
        return new DeviationStore(conn, tx).UpdateAsync(row.Id, row.RowVersion, columns, ext: null, ct);
    }
}

/// <summary>Deviations are signed only through workflow transitions (submit, approve, cancel).</summary>
public sealed class DeviationSignable() : WorkflowSignable(DeviationEntity.Instance)
{
    public override string EntityCode => "qms.deviation";
    public override string EntityName => "Deviation";
    public override string ModuleCode => "qms";
    public override string ModuleName => "Quality management";
    public override string PhysicalTable => "qms.deviation";
    public override string ReadPermission => Permissions.DeviationRead;
    public override IReadOnlyList<string> Meanings { get; } =
        [EsigCatalog.Authored.Code, EsigCatalog.Approved.Code, EsigCatalog.Cancelled.Code];
}
