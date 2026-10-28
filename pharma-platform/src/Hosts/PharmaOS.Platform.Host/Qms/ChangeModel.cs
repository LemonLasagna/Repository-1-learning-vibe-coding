using System.Globalization;
using System.Text.Json.Nodes;
using PharmaOS.Gxp;
using PharmaOS.Platform.Host.Workflow;
using PharmaOS.Workflow;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>
/// Reference workflow "Change Control" (FS §9.3.3, URS-QMS-009). A reduced first version:
/// <code>
/// DRAFT ─submit→ ASSESSMENT ─submit_for_approval→ APPROVAL ─approve✍Approved→ IMPLEMENTATION ─verify_implementation✍Verified→
///   │                ▲                │
///   │                └── return ──────┘ (reason)
///   └ DRAFT, ASSESSMENT, APPROVAL ─cancel✍Cancelled (reason)→ CANCELLED
/// POST_REVIEW ─complete_review (reason)→ CLOSURE ─close✍Verified→ CLOSED
/// </code>
/// The impact assessments (one per area) and the implementation tasks are part of the record; the approval signature covers
/// them (record hash). The owner records the post-implementation review; its conclusion is the reason of that step (history and
/// audit trail). QA then signs the closure, which a second person must do, so the verifier is not also the last editor.
/// Not yet: separate initial and parallel impact assessments assigned by affected item type, affected items, regulatory
/// assessments per country, a risk assessment link, the coordinated effective date triggering documents and retraining, and
/// enforcement of the end date of a temporary change.
/// </summary>
public static class ChangeWorkflow
{
    public const string Draft = "DRAFT";
    public const string Assessment = "ASSESSMENT";
    public const string Approval = "APPROVAL";
    public const string Implementation = "IMPLEMENTATION";
    public const string PostReview = "POST_REVIEW";
    public const string Closure = "CLOSURE";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> Areas =
        ["QUALITY", "VALIDATION", "DOCUMENTATION", "REGULATORY", "MANUFACTURING", "LABORATORY", "TRAINING"];

    public static readonly WorkflowDefinition Definition = new("CHANGE_STD", 1, Draft,
    [
        new("submit", Draft, Assessment, Permissions.ChangeWrite,
            Guard("""{"or":[{"not":{"var":"is_temporary"}},{"not":{"isEmpty":{"var":"temporary_end_date"}}}]}"""),
            Label: "Submit for assessment", GuardMessage: "A temporary change needs its end date."),
        new("cancel", Draft, Cancelled, Permissions.ChangeWrite, SignatureMeaning: "CANCELLED", Label: "Cancel", RequireReason: true),
        new("submit_for_approval", Assessment, Approval, Permissions.ChangeWrite,
            Guard("""
                {"and":[
                  {">":[{"var":"assessments_total"},0]},
                  {"or":[{"!=":[{"var":"regulatory_impact"},"MAJOR"]},{"var":"regulatory_assessed"}]}
                ]}
                """),
            Label: "Submit for approval",
            GuardMessage: "Record at least one impact assessment, and one for the regulatory area when the regulatory impact is major."),
        new("cancel", Assessment, Cancelled, Permissions.ChangeApprove, SignatureMeaning: "CANCELLED", Label: "Cancel", RequireReason: true),
        new("approve", Approval, Implementation, Permissions.ChangeApprove, SignatureMeaning: "APPROVED", Label: "Approve change"),
        new("return", Approval, Assessment, Permissions.ChangeApprove, Label: "Return for assessment", RequireReason: true),
        new("cancel", Approval, Cancelled, Permissions.ChangeApprove, SignatureMeaning: "CANCELLED", Label: "Cancel", RequireReason: true),
        new("verify_implementation", Implementation, PostReview, Permissions.ChangeApprove,
            Guard("""
                {"and":[
                  {">":[{"var":"tasks_planned"},0]},
                  {"==":[{"var":"tasks_open"},0]},
                  {"not":{"isEmpty":{"var":"effective_date"}}}
                ]}
                """),
            SignatureMeaning: "VERIFIED", Label: "Verify implementation",
            GuardMessage: "Plan and complete every task, and set the effective date first."),
        new("complete_review", PostReview, Closure, Permissions.ChangeWrite,
            Label: "Record post-implementation review", RequireReason: true),
        new("close", Closure, Closed, Permissions.ChangeApprove, SignatureMeaning: "VERIFIED", Label: "Close change"),
    ]);

    public const int DefaultDueDays = 90;

    public static readonly IReadOnlyList<FieldSpec> HeaderFields =
    [
        new("title", "title", null, FieldKind.Text, Required: true, MaxLength: 1000),
        new("description", "description", null, FieldKind.Text, Required: true, MaxLength: 20000),
        new("justification", "justification", null, FieldKind.Text, Required: true, MaxLength: 20000),
        new("changeType", "change_type", null, FieldKind.Enum, Required: true, Options: ["PLANNED", "EMERGENCY"]),
        new("changeCategory", "change_category", null, FieldKind.Enum, Required: true, Options: ["MINOR", "MAJOR", "CRITICAL"]),
        new("isTemporary", "is_temporary", null, FieldKind.Bool),
        new("temporaryEndDate", "temporary_end_date", null, FieldKind.Date),
        new("regulatoryImpact", "regulatory_impact", null, FieldKind.Enum, Options: ["NONE", "MINOR", "MAJOR"]),
        new("implementationDue", "implementation_due", null, FieldKind.Date),
    ];

    /// <summary>Set in Implementation, by the owner, when the change takes effect.</summary>
    public static readonly IReadOnlyList<FieldSpec> ImplementationFields =
    [
        new("effectiveDate", "effective_date", null, FieldKind.Date),
        new("implementationDue", "implementation_due", null, FieldKind.Date),
    ];

    public static readonly IReadOnlyList<FieldSpec> ImpactFields =
    [
        new("impactedArea", "impacted_area", null, FieldKind.Enum, Required: true, Options: [.. Areas]),
        new("isImpacted", "is_impacted", null, FieldKind.Bool, Required: true),
        new("assessment", "assessment", null, FieldKind.Text, Required: true, MaxLength: 20000),
        new("actionRequired", "action_required", null, FieldKind.Text, MaxLength: 20000),
    ];

    public static readonly IReadOnlyList<FieldSpec> TaskFields =
    [
        new("phase", "phase", null, FieldKind.Enum, Required: true, Options: ["BEFORE", "IMPLEMENTATION", "AFTER"]),
        new("description", "description", null, FieldKind.Text, Required: true, MaxLength: 20000),
        new("assigneeId", "assignee_id", null, FieldKind.Uuid),
        new("dueDate", "due_date", null, FieldKind.Date, Required: true),
    ];

    private static JsonNode Guard(string json) => JsonNode.Parse(json)!;
}

public static class ChangeTaskStatus
{
    public const string Planned = "PLANNED";
    public const string Done = "DONE";
    public const string Withdrawn = "WITHDRAWN";
}

public sealed record ChangeImpactRow(
    string ImpactedArea, Guid AssessorId, string? AssessorName, bool IsImpacted, string? Assessment, string? ActionRequired, DateTimeOffset UpdatedAt);

public sealed record ChangeTaskRow(
    int No, string Phase, string Description, Guid AssigneeId, string? AssigneeName, DateOnly DueDate, string Status, DateTimeOffset? CompletedAt);

/// <summary>A CAPA action that is implemented through the change (the link is stored on the action).</summary>
public sealed record ChangeLinkedAction(Guid CapaId, string CapaNo, int ActionNo, string Description, string Status);

public sealed record ChangeRow(
    Guid Id, string CcNo, Guid SiteId, string SiteCode, string Title, string Description, string Justification, string ChangeType,
    string ChangeCategory, bool IsTemporary, DateOnly? TemporaryEndDate, string? RegulatoryImpact, DateOnly? ImplementationDue,
    DateOnly? EffectiveDate, Guid OwnerId, string? OwnerName, string Status, DateTimeOffset UpdatedAt, int RowVersion, Guid UpdatedBy,
    Guid? WorkflowInstanceId, IReadOnlyList<ChangeImpactRow> Impacts, IReadOnlyList<ChangeTaskRow> Tasks,
    IReadOnlyList<ChangeLinkedAction> LinkedActions) : IWorkflowRecord
{
    public string Number => CcNo;

    /// <summary>The fields <see cref="ToRuleData"/> offers to guards. A guard in an authored workflow may read only these.</summary>
    public static readonly IReadOnlySet<string> RuleFields = new HashSet<string>
    {
        "change_type", "change_category", "is_temporary", "temporary_end_date", "regulatory_impact", "effective_date",
        "assessments_total", "regulatory_assessed", "tasks_planned", "tasks_open", "status",
    };

    public JsonObject ToRuleData() => new()
    {
        ["change_type"] = ChangeType,
        ["change_category"] = ChangeCategory,
        ["is_temporary"] = IsTemporary,
        ["temporary_end_date"] = TemporaryEndDate is { } t ? Date(t) : null,
        ["regulatory_impact"] = RegulatoryImpact ?? "NONE",
        ["effective_date"] = EffectiveDate is { } e ? Date(e) : null,
        ["assessments_total"] = Impacts.Count,
        ["regulatory_assessed"] = Impacts.Any(i => i.ImpactedArea == "REGULATORY"),
        ["tasks_planned"] = Tasks.Count(x => x.Status != ChangeTaskStatus.Withdrawn),
        ["tasks_open"] = Tasks.Count(x => x.Status == ChangeTaskStatus.Planned),
        ["status"] = Status,
    };

    /// <summary>record_hash over the header, every impact assessment and every task (DS §10.5: child records included).</summary>
    public string ContentHash() => CanonicalJson.Sha256Hex(new JsonObject
    {
        ["entity"] = "qms.change",
        ["id"] = Id.ToString("D"),
        ["cc_no"] = CcNo,
        ["site_id"] = SiteId.ToString("D"),
        ["title"] = Title,
        ["description"] = Description,
        ["justification"] = Justification,
        ["change_type"] = ChangeType,
        ["change_category"] = ChangeCategory,
        ["is_temporary"] = IsTemporary,
        ["temporary_end_date"] = TemporaryEndDate is { } t ? Date(t) : null,
        ["regulatory_impact"] = RegulatoryImpact,
        ["implementation_due"] = ImplementationDue is { } d ? Date(d) : null,
        ["effective_date"] = EffectiveDate is { } e ? Date(e) : null,
        ["owner_id"] = OwnerId.ToString("D"),
        ["impacts"] = new JsonArray([.. Impacts.OrderBy(i => i.ImpactedArea, StringComparer.Ordinal).Select(i => (JsonNode)new JsonObject
        {
            ["impacted_area"] = i.ImpactedArea,
            ["assessor_id"] = i.AssessorId.ToString("D"),
            ["is_impacted"] = i.IsImpacted,
            ["assessment"] = i.Assessment,
            ["action_required"] = i.ActionRequired,
        })]),
        ["tasks"] = new JsonArray([.. Tasks.OrderBy(x => x.No).Select(x => (JsonNode)new JsonObject
        {
            ["no"] = x.No,
            ["phase"] = x.Phase,
            ["description"] = x.Description,
            ["assignee_id"] = x.AssigneeId.ToString("D"),
            ["due_date"] = Date(x.DueDate),
            ["status"] = x.Status,
            ["completed_at"] = x.CompletedAt is { } c ? Utc(c) : null,
        })]),
    });

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}

public sealed record ChangeResponse(
    Guid Id, string CcNo, Guid SiteId, string SiteCode, string Title, string Description, string Justification, string ChangeType,
    string ChangeCategory, bool IsTemporary, DateOnly? TemporaryEndDate, string? RegulatoryImpact, DateOnly? ImplementationDue,
    DateOnly? EffectiveDate, Guid OwnerId, string? OwnerName, string Status, int RowVersion, DateTimeOffset UpdatedAt,
    IReadOnlyList<ChangeImpactRow> Impacts, IReadOnlyList<ChangeTaskRow> Tasks, IReadOnlyList<ChangeLinkedAction> LinkedActions)
{
    public static ChangeResponse From(ChangeRow r) => new(r.Id, r.CcNo, r.SiteId, r.SiteCode, r.Title, r.Description, r.Justification,
        r.ChangeType, r.ChangeCategory, r.IsTemporary, r.TemporaryEndDate, r.RegulatoryImpact, r.ImplementationDue, r.EffectiveDate,
        r.OwnerId, r.OwnerName, r.Status, r.RowVersion, r.UpdatedAt, r.Impacts, r.Tasks, r.LinkedActions);
}
