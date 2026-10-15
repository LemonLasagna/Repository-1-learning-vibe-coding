using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PharmaOS.Gxp;
using PharmaOS.Workflow;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>
/// Reference workflow "Deviation" (FS §9.3.1), minor path. Guards are rule-language JSON (the same language the
/// forms use), so they can move into configuration unchanged.
/// <code>
/// DRAFT ─submit✍Authored→ TRIAGE ─start_investigation→ INVESTIGATION ─submit_for_approval→ QA_APPROVAL ─approve✍Approved→ CLOSED
///                                                          ▲                                   │
///                                                          └──────────── reject (reason) ──────┘
/// DRAFT, TRIAGE ─cancel✍Cancelled (reason)→ CANCELLED
/// </code>
/// Not yet: the major/critical branch with parallel QA and production review, timers and escalation, the CAPA and
/// batch-impact guards on closure, and due-date extensions.
/// </summary>
public static class DeviationWorkflow
{
    public const string Draft = "DRAFT";
    public const string Triage = "TRIAGE";
    public const string Investigation = "INVESTIGATION";
    public const string QaApproval = "QA_APPROVAL";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";

    public static readonly WorkflowDefinition Definition = new("DEVIATION_STD", 1, Draft,
    [
        new("submit", Draft, Triage, Permissions.DeviationWrite,
            Guard("""{"or":[{"not":{"var":"is_planned"}},{"not":{"isEmpty":{"var":"planned_justification"}}}]}"""),
            SignatureMeaning: "AUTHORED", Label: "Submit", GuardMessage: "A planned deviation needs a justification."),
        new("cancel", Draft, Cancelled, Permissions.DeviationWrite, SignatureMeaning: "CANCELLED", Label: "Cancel", RequireReason: true),
        new("start_investigation", Triage, Investigation, Permissions.DeviationTriage, Label: "Start investigation"),
        new("cancel", Triage, Cancelled, Permissions.DeviationTriage, SignatureMeaning: "CANCELLED", Label: "Cancel", RequireReason: true),
        new("submit_for_approval", Investigation, QaApproval, Permissions.DeviationWrite,
            Guard("""
                {"and":[{"not":{"isEmpty":{"var":"root_cause_summary"}}},
                        {"or":[{"==":[{"var":"classification"},"MINOR"]},{"not":{"isEmpty":{"var":"impact_assessment"}}}]}]}
                """),
            Label: "Submit for QA approval",
            GuardMessage: "Enter the root cause summary and, for a major or critical deviation, the product impact assessment."),
        new("approve", QaApproval, Closed, Permissions.DeviationApprove, SignatureMeaning: "APPROVED", Label: "Approve and close"),
        new("reject", QaApproval, Investigation, Permissions.DeviationApprove, Label: "Return to investigation", RequireReason: true),
    ]);

    public static bool IsFinal(string state) => state is Closed or Cancelled;

    /// <summary>Which fields may change in which state, and the permission that allows it.</summary>
    public static readonly IReadOnlyDictionary<string, (string Permission, IReadOnlySet<string> Fields)> Editable =
        new Dictionary<string, (string, IReadOnlySet<string>)>
        {
            [Draft] = (Permissions.DeviationWrite, new HashSet<string>
            {
                "siteId", "title", "description", "classification", "deviationType", "isPlanned", "occurredAt", "discoveredAt",
                "immediateAction", "impactAssessment", "rootCauseSummary", "riskScore", "plannedJustification", "shift", "batchesAffected",
            }),
            [Triage] = (Permissions.DeviationTriage, new HashSet<string> { "classification", "deviationType", "dueDate" }),
            [Investigation] = (Permissions.DeviationWrite, new HashSet<string>
                { "immediateAction", "impactAssessment", "rootCauseSummary", "riskScore", "batchesAffected" }),
        };

    /// <summary>Days from discovery to the due date unless QA sets another at triage.</summary>
    public const int DefaultDueDays = 30;

    private static JsonNode Guard(string json) => JsonNode.Parse(json)!;
}

public enum FieldKind { Text, Enum, Bool, Timestamp, Date, Int, Uuid }

/// <summary>An API field and where it is stored (a column, or a key in ext for configurable fields).</summary>
public sealed record FieldSpec(
    string Name, string? Column, string? ExtKey, FieldKind Kind, bool Required = false, int MaxLength = 0,
    IReadOnlyList<string>? Options = null, int Min = int.MinValue, int Max = int.MaxValue);

public static partial class DeviationFields
{
    public static readonly IReadOnlyList<FieldSpec> All =
    [
        new("siteId", "site_id", null, FieldKind.Uuid, Required: true),
        new("title", "title", null, FieldKind.Text, Required: true, MaxLength: 1000),
        new("description", "description", null, FieldKind.Text, Required: true, MaxLength: 20000),
        new("classification", "classification", null, FieldKind.Enum, Required: true, Options: ["MINOR", "MAJOR", "CRITICAL"]),
        new("deviationType", "deviation_type", null, FieldKind.Enum, Required: true,
            Options: ["PROCESS", "EQUIPMENT", "MATERIAL", "DOCUMENTATION", "ENVIRONMENT", "OTHER"]),
        new("isPlanned", "is_planned", null, FieldKind.Bool),
        new("occurredAt", "occurred_at", null, FieldKind.Timestamp, Required: true),
        new("discoveredAt", "discovered_at", null, FieldKind.Timestamp),
        new("dueDate", "due_date", null, FieldKind.Date),
        new("immediateAction", "immediate_action", null, FieldKind.Text, MaxLength: 20000),
        new("impactAssessment", "impact_assessment", null, FieldKind.Text, MaxLength: 20000),
        new("rootCauseSummary", "root_cause_summary", null, FieldKind.Text, MaxLength: 20000),
        new("riskScore", "risk_score", null, FieldKind.Int, Min: 1, Max: 125),
        new("plannedJustification", null, "planned_justification", FieldKind.Text, MaxLength: 2000),
        new("shift", null, "shift", FieldKind.Enum, Options: ["EARLY", "LATE", "NIGHT"]),
        new("batchesAffected", null, "batches_affected", FieldKind.Int, Min: 0, Max: 10000),
    ];

    public static FieldSpec? Find(string name) => All.FirstOrDefault(f => f.Name == name);

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex HasOffset();

    /// <summary>Parses one JSON value into the database value. Strings are trimmed; empty means null.</summary>
    public static bool TryParse(FieldSpec f, JsonNode? node, out object? value, out string? error)
    {
        value = null;
        error = null;
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            if (f.Required) error = "Required.";
            return error is null;
        }
        try
        {
            switch (f.Kind)
            {
                case FieldKind.Text:
                    var text = node.GetValue<string>().Trim();
                    if (text.Length == 0) { if (f.Required) error = "Required."; return error is null; }
                    if (f.MaxLength > 0 && text.Length > f.MaxLength) error = $"Maximum length is {f.MaxLength}.";
                    value = text;
                    break;
                case FieldKind.Enum:
                    var code = node.GetValue<string>().Trim().ToUpperInvariant();
                    if (!f.Options!.Contains(code)) error = $"One of: {string.Join(", ", f.Options!)}.";
                    value = code;
                    break;
                case FieldKind.Bool:
                    value = node.GetValue<bool>();
                    break;
                case FieldKind.Timestamp:
                    var raw = node.GetValue<string>().Trim();
                    if (!HasOffset().IsMatch(raw)
                        || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                        error = "An ISO 8601 date and time with a UTC offset, e.g. 2026-10-15T08:30:00+02:00.";
                    else value = ts.ToUniversalTime();
                    break;
                case FieldKind.Date:
                    if (!DateOnly.TryParseExact(node.GetValue<string>().Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        error = "A date as yyyy-MM-dd.";
                    else value = d;
                    break;
                case FieldKind.Int:
                    var n = node.GetValue<int>();
                    if (n < f.Min || n > f.Max) error = $"Between {f.Min} and {f.Max}.";
                    value = n;
                    break;
                case FieldKind.Uuid:
                    if (!Guid.TryParse(node.GetValue<string>(), out var g)) error = "A UUID.";
                    value = g;
                    break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            error = $"Wrong type; expected {f.Kind.ToString().ToLowerInvariant()}.";
        }
        return error is null;
    }
}

/// <summary>A deviation as stored, with display names joined in.</summary>
public sealed record DeviationRow(
    Guid Id, Guid SiteId, string SiteCode, string DeviationNo, string Title, string Description, string Classification,
    string DeviationType, bool IsPlanned, DateTimeOffset OccurredAt, DateTimeOffset DiscoveredAt, string? ImmediateAction,
    string? ImpactAssessment, string? RootCauseSummary, int? RiskScore, Guid OwnerId, string? OwnerName, Guid? QaOwnerId,
    string? QaOwnerName, DateOnly DueDate, DateTimeOffset? ClosedAt, string Status, Guid? WorkflowInstanceId, string ExtJson,
    int RowVersion, DateTimeOffset UpdatedAt, Guid UpdatedBy)
{
    public JsonObject Ext => JsonNode.Parse(ExtJson) as JsonObject ?? [];

    /// <summary>The record as the workflow guards see it (field names as in the form bundle).</summary>
    public JsonObject ToRuleData()
    {
        var o = new JsonObject
        {
            ["title"] = Title,
            ["description"] = Description,
            ["classification"] = Classification,
            ["deviation_type"] = DeviationType,
            ["is_planned"] = IsPlanned,
            ["immediate_action"] = ImmediateAction,
            ["impact_assessment"] = ImpactAssessment,
            ["root_cause_summary"] = RootCauseSummary,
            ["risk_score"] = RiskScore,
            ["status"] = Status,
        };
        foreach (var (k, v) in Ext) o[k] = v?.DeepClone();
        return o;
    }

    /// <summary>
    /// record_hash (DS §10.5) over the signed content: every business field and ext. Workflow state, row_version and
    /// workflow assignments are excluded, so a transition alone does not make earlier signatures look stale; the
    /// version each signature applies to is still bound separately.
    /// </summary>
    public string ContentHash() => CanonicalJson.Sha256Hex(new JsonObject
    {
        ["entity"] = "qms.deviation",
        ["id"] = Id.ToString("D"),
        ["deviation_no"] = DeviationNo,
        ["site_id"] = SiteId.ToString("D"),
        ["title"] = Title,
        ["description"] = Description,
        ["classification"] = Classification,
        ["deviation_type"] = DeviationType,
        ["is_planned"] = IsPlanned,
        ["occurred_at"] = Utc(OccurredAt),
        ["discovered_at"] = Utc(DiscoveredAt),
        ["immediate_action"] = ImmediateAction,
        ["impact_assessment"] = ImpactAssessment,
        ["root_cause_summary"] = RootCauseSummary,
        ["risk_score"] = RiskScore,
        ["owner_id"] = OwnerId.ToString("D"),
        ["due_date"] = DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["ext"] = Ext,
    });

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}

public sealed record DeviationResponse(
    Guid Id, string DeviationNo, Guid SiteId, string SiteCode, string Title, string Description, string Classification,
    string DeviationType, bool IsPlanned, DateTimeOffset OccurredAt, DateTimeOffset DiscoveredAt, string? ImmediateAction,
    string? ImpactAssessment, string? RootCauseSummary, int? RiskScore, string? PlannedJustification, string? Shift,
    int? BatchesAffected, Guid OwnerId, string? OwnerName, Guid? QaOwnerId, string? QaOwnerName, DateOnly DueDate,
    DateTimeOffset? ClosedAt, string Status, int RowVersion, DateTimeOffset UpdatedAt)
{
    public static DeviationResponse From(DeviationRow r)
    {
        var ext = r.Ext;
        return new(r.Id, r.DeviationNo, r.SiteId, r.SiteCode, r.Title, r.Description, r.Classification, r.DeviationType,
            r.IsPlanned, r.OccurredAt, r.DiscoveredAt, r.ImmediateAction, r.ImpactAssessment, r.RootCauseSummary, r.RiskScore,
            ext["planned_justification"]?.GetValue<string>(), ext["shift"]?.GetValue<string>(),
            ext["batches_affected"]?.GetValue<int>(), r.OwnerId, r.OwnerName, r.QaOwnerId, r.QaOwnerName, r.DueDate, r.ClosedAt,
            r.Status, r.RowVersion, r.UpdatedAt);
    }
}
