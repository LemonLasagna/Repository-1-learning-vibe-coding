using System.Globalization;
using System.Text.Json.Nodes;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.Workflow;
using PharmaOS.Workflow;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>
/// Reference workflow "CAPA" (FS §9.3.2, URS-QMS-006..008).
/// <code>
/// DRAFT ─submit_plan→ PLAN_REVIEW ─approve_plan✍Approved→ IMPLEMENTATION ─verify_implementation✍Verified→ EFFECTIVENESS_CHECK ─close✍Verified→ CLOSED
///   ▲                    │
///   └── return_plan ─────┘ (reason)            DRAFT, PLAN_REVIEW ─cancel✍Cancelled (reason)→ CANCELLED
/// </code>
/// The plan is the actions plus the effectiveness check; the plan-approval signature covers them (record hash).
/// Not yet: the effectiveness-check timer (the planned date is recorded, not enforced), action-level workflows and
/// change-control links, and raising a follow-up CAPA automatically when the check is ineffective.
/// </summary>
public static class CapaWorkflow
{
    public const string Draft = "DRAFT";
    public const string PlanReview = "PLAN_REVIEW";
    public const string Implementation = "IMPLEMENTATION";
    public const string EffectivenessCheck = "EFFECTIVENESS_CHECK";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";

    /// <summary>States from which a linked deviation may close ("plan approved or later").</summary>
    public static readonly IReadOnlySet<string> PlanApprovedOrLater = new HashSet<string> { Implementation, EffectivenessCheck, Closed };

    public static readonly WorkflowDefinition Definition = new("CAPA_STD", 1, Draft,
    [
        new("submit_plan", Draft, PlanReview, Permissions.CapaWrite,
            Guard("""{"and":[{">":[{"var":"actions_planned"},0]},{"var":"effectiveness_defined"}]}"""),
            Label: "Submit plan for review", GuardMessage: "Plan at least one action and the effectiveness check."),
        new("cancel", Draft, Cancelled, Permissions.CapaWrite, SignatureMeaning: "CANCELLED", Label: "Cancel", RequireReason: true),
        new("approve_plan", PlanReview, Implementation, Permissions.CapaApprove, SignatureMeaning: "APPROVED", Label: "Approve plan"),
        new("return_plan", PlanReview, Draft, Permissions.CapaApprove, Label: "Return plan", RequireReason: true),
        new("cancel", PlanReview, Cancelled, Permissions.CapaApprove, SignatureMeaning: "CANCELLED", Label: "Cancel", RequireReason: true),
        new("verify_implementation", Implementation, EffectivenessCheck, Permissions.CapaApprove,
            Guard("""{"==":[{"var":"actions_open"},0]}"""),
            SignatureMeaning: "VERIFIED", Label: "Verify implementation", GuardMessage: "Complete every action first."),
        new("close", EffectivenessCheck, Closed, Permissions.CapaApprove,
            Guard("""{"not":{"isEmpty":{"var":"effectiveness_result"}}}"""),
            SignatureMeaning: "VERIFIED", Label: "Verify effectiveness and close",
            GuardMessage: "Record the result of the effectiveness check first."),
    ]);

    public const int DefaultDueDays = 90;

    public static readonly IReadOnlyList<FieldSpec> HeaderFields =
    [
        new("title", "title", null, FieldKind.Text, Required: true, MaxLength: 1000),
        new("problemStatement", "problem_statement", null, FieldKind.Text, Required: true, MaxLength: 20000),
        new("capaType", "capa_type", null, FieldKind.Enum, Required: true, Options: ["CORRECTIVE", "PREVENTIVE", "CORRECTIVE_AND_PREVENTIVE"]),
        new("priority", "priority", null, FieldKind.Enum, Required: true, Options: ["LOW", "MEDIUM", "HIGH"]),
        new("dueDate", "due_date", null, FieldKind.Date),
    ];

    public static readonly IReadOnlyList<FieldSpec> ActionFields =
    [
        new("actionType", "action_type", null, FieldKind.Enum, Required: true, Options: ["CORRECTION", "CORRECTIVE", "PREVENTIVE"]),
        new("description", "description", null, FieldKind.Text, Required: true, MaxLength: 20000),
        new("assigneeId", "assignee_id", null, FieldKind.Uuid),
        new("dueDate", "due_date", null, FieldKind.Date, Required: true),
    ];

    public static readonly IReadOnlyList<FieldSpec> EffectivenessFields =
    [
        new("criteria", "criteria", null, FieldKind.Text, Required: true, MaxLength: 20000),
        new("method", "method", null, FieldKind.Text, MaxLength: 1000),
        new("plannedDate", "planned_date", null, FieldKind.Date, Required: true),
    ];

    private static JsonNode Guard(string json) => JsonNode.Parse(json)!;
}

public sealed record CapaActionRow(
    int No, string ActionType, string Description, Guid AssigneeId, string? AssigneeName, DateOnly DueDate, string Status,
    DateTimeOffset? CompletedAt, string? Evidence, Guid? ChangeId = null, string? ChangeNo = null);

public sealed record CapaEffectivenessRow(
    Guid Id, string Criteria, string? Method, DateOnly PlannedDate, DateTimeOffset? PerformedAt, string? Result, string? Evidence,
    string? PerformedByName);

public sealed record CapaRow(
    Guid Id, string CapaNo, Guid SiteId, string SiteCode, string SourceType, Guid? SourceId, string? SourceNo, string Title,
    string ProblemStatement, string CapaType, string Priority, Guid OwnerId, string? OwnerName, DateOnly DueDate, string Status,
    DateTimeOffset? ClosedAt, Guid? WorkflowInstanceId, int RowVersion, DateTimeOffset UpdatedAt, Guid UpdatedBy,
    IReadOnlyList<CapaActionRow> Actions, CapaEffectivenessRow? Effectiveness) : IWorkflowRecord
{
    public string Number => CapaNo;

    /// <summary>The fields <see cref="ToRuleData"/> offers to guards. A guard in an authored workflow may read only these.</summary>
    public static readonly IReadOnlySet<string> RuleFields = new HashSet<string>
    {
        "capa_type", "priority", "actions_planned", "actions_open", "effectiveness_defined", "effectiveness_result", "status",
    };

    public JsonObject ToRuleData() => new()
    {
        ["capa_type"] = CapaType,
        ["priority"] = Priority,
        ["actions_planned"] = Actions.Count(a => a.Status != CapaActionStatus.Withdrawn),
        ["actions_open"] = Actions.Count(a => a.Status == CapaActionStatus.Planned),
        ["effectiveness_defined"] = Effectiveness is not null,
        ["effectiveness_result"] = Effectiveness?.Result,
        ["status"] = Status,
    };

    /// <summary>record_hash over the header, every action and the effectiveness check (DS §10.5: child records included).</summary>
    public string ContentHash() => CanonicalJson.Sha256Hex(new JsonObject
    {
        ["entity"] = "qms.capa",
        ["id"] = Id.ToString("D"),
        ["capa_no"] = CapaNo,
        ["site_id"] = SiteId.ToString("D"),
        ["source_type"] = SourceType,
        ["source_id"] = SourceId?.ToString("D"),
        ["title"] = Title,
        ["problem_statement"] = ProblemStatement,
        ["capa_type"] = CapaType,
        ["priority"] = Priority,
        ["owner_id"] = OwnerId.ToString("D"),
        ["due_date"] = Date(DueDate),
        ["actions"] = new JsonArray([.. Actions.OrderBy(a => a.No).Select(a => (JsonNode)new JsonObject
        {
            ["no"] = a.No,
            ["action_type"] = a.ActionType,
            ["description"] = a.Description,
            ["assignee_id"] = a.AssigneeId.ToString("D"),
            ["due_date"] = Date(a.DueDate),
            ["status"] = a.Status,
            ["completed_at"] = a.CompletedAt is { } c ? Utc(c) : null,
            ["evidence"] = a.Evidence,
            ["change_control_id"] = a.ChangeId?.ToString("D"),
        })]),
        ["effectiveness"] = Effectiveness is { } e
            ? new JsonObject
            {
                ["criteria"] = e.Criteria,
                ["method"] = e.Method,
                ["planned_date"] = Date(e.PlannedDate),
                ["performed_at"] = e.PerformedAt is { } p ? Utc(p) : null,
                ["result"] = e.Result,
                ["evidence"] = e.Evidence,
            }
            : null,
    });

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}

public static class CapaActionStatus
{
    public const string Planned = "PLANNED";
    public const string Done = "DONE";
    public const string Withdrawn = "WITHDRAWN";
}

public sealed record CapaResponse(
    Guid Id, string CapaNo, Guid SiteId, string SiteCode, string SourceType, Guid? SourceId, string? SourceNo, string Title,
    string ProblemStatement, string CapaType, string Priority, Guid OwnerId, string? OwnerName, DateOnly DueDate, string Status,
    DateTimeOffset? ClosedAt, int RowVersion, DateTimeOffset UpdatedAt, IReadOnlyList<CapaActionRow> Actions,
    CapaEffectivenessRow? Effectiveness)
{
    public static CapaResponse From(CapaRow r) => new(r.Id, r.CapaNo, r.SiteId, r.SiteCode, r.SourceType, r.SourceId, r.SourceNo,
        r.Title, r.ProblemStatement, r.CapaType, r.Priority, r.OwnerId, r.OwnerName, r.DueDate, r.Status, r.ClosedAt, r.RowVersion,
        r.UpdatedAt, r.Actions, r.Effectiveness);
}
