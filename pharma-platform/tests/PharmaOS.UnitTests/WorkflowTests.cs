using System.Text.Json.Nodes;
using PharmaOS.Rules;
using PharmaOS.Workflow;

namespace PharmaOS.UnitTests;

[Trait("Req", "URS-WF-001")]
public class WorkflowTests
{
    // Simplified deviation lifecycle from FS §9.3.1.
    private static readonly WorkflowDefinition Deviation = new("DEVIATION_STD", 5, "DRAFT",
    [
        new("submit", "DRAFT", "TRIAGE", Permission: "qms.deviation:submit", SignatureMeaning: "AUTHORED"),
        new("classify_minor", "TRIAGE", "OWNER_INVESTIGATION",
            Guard: JsonNode.Parse("""{"==":[{"var":"classification"},"MINOR"]}""")),
        new("classify_major", "TRIAGE", "INVESTIGATION",
            Guard: JsonNode.Parse("""{"in":[{"var":"classification"},["MAJOR","CRITICAL"]]}""")),
        new("approve", "QA_REVIEW", "CLOSED", Permission: "qms.deviation:approve", SignatureMeaning: "APPROVED",
            Guard: JsonNode.Parse("""{"not":{"isEmpty":{"var":"root_cause_summary"}}}""")),
        new("reject", "QA_REVIEW", "INVESTIGATION", Permission: "qms.deviation:approve", SignatureMeaning: "REJECTED"),
    ]);

    private static readonly HashSet<string> QaPerms = ["qms.deviation:submit", "qms.deviation:approve"];

    [Fact]
    public void Transition_requiring_signature_asks_for_it_first()
    {
        var d = TransitionEvaluator.Evaluate(Deviation, "DRAFT", "submit", new JsonObject(), QaPerms, signaturePresented: false);
        Assert.Equal(new TransitionDecision.SignatureRequired("TRIAGE", "AUTHORED"), d);
    }

    [Fact]
    public void Transition_with_signature_is_allowed()
    {
        var d = TransitionEvaluator.Evaluate(Deviation, "DRAFT", "submit", new JsonObject(), QaPerms, signaturePresented: true);
        Assert.Equal(new TransitionDecision.Allowed("TRIAGE"), d);
    }

    [Fact]
    [Trait("Req", "URS-SEC-003")]
    public void Missing_permission_is_denied_before_signature_is_requested()
    {
        var d = TransitionEvaluator.Evaluate(Deviation, "DRAFT", "submit", new JsonObject(), new HashSet<string>(), false);
        Assert.IsType<TransitionDecision.Denied>(d);
    }

    [Fact]
    public void Action_not_available_in_state_is_denied()
    {
        var d = TransitionEvaluator.Evaluate(Deviation, "DRAFT", "approve", new JsonObject(), QaPerms, true);
        Assert.Equal(new TransitionDecision.Denied("Action 'approve' is not available in state 'DRAFT'"), d);
    }

    [Theory]
    [InlineData("MINOR", "classify_minor", true)]
    [InlineData("MAJOR", "classify_minor", false)]
    [InlineData("CRITICAL", "classify_major", true)]
    public void Conditional_routing_uses_guards(string classification, string action, bool allowed)
    {
        var record = new JsonObject { ["classification"] = classification };
        var d = TransitionEvaluator.Evaluate(Deviation, "TRIAGE", action, record, QaPerms, false);
        Assert.Equal(allowed, d is TransitionDecision.Allowed);
    }

    [Fact]
    public void Approval_guard_requires_root_cause()
    {
        var empty = new JsonObject { ["root_cause_summary"] = "" };
        var filled = new JsonObject { ["root_cause_summary"] = "Chiller setpoint drift" };
        Assert.IsType<TransitionDecision.Denied>(TransitionEvaluator.Evaluate(Deviation, "QA_REVIEW", "approve", empty, QaPerms, true));
        Assert.IsType<TransitionDecision.Allowed>(TransitionEvaluator.Evaluate(Deviation, "QA_REVIEW", "approve", filled, QaPerms, true));
    }

    [Fact]
    public void Available_lists_transitions_from_state() =>
        Assert.Equal(["approve", "reject"], TransitionEvaluator.Available(Deviation, "QA_REVIEW").Select(t => t.Action));

    [Fact]
    public void Duplicate_transition_is_rejected_at_definition_time() =>
        Assert.Throws<ArgumentException>(() => new WorkflowDefinition("X", 1, "A", [new("go", "A", "B"), new("go", "A", "C")]));

    [Fact]
    public void Invalid_guard_is_rejected_at_definition_time() =>
        Assert.Throws<RuleException>(() => new WorkflowDefinition("X", 1, "A",
            [new("go", "A", "B", Guard: JsonNode.Parse("""{"eval":"code"}"""))]));

    [Theory]
    [InlineData(ApprovalMode.ParallelAll, 3, 2, 0, 0, ApprovalOutcome.Pending)]
    [InlineData(ApprovalMode.ParallelAll, 3, 3, 0, 0, ApprovalOutcome.Approved)]
    [InlineData(ApprovalMode.ParallelAny, 3, 1, 0, 0, ApprovalOutcome.Approved)]
    [InlineData(ApprovalMode.Quorum, 5, 2, 0, 3, ApprovalOutcome.Pending)]
    [InlineData(ApprovalMode.Quorum, 5, 3, 0, 3, ApprovalOutcome.Approved)]
    [InlineData(ApprovalMode.ParallelAll, 3, 2, 1, 0, ApprovalOutcome.Rejected)]
    public void Parallel_approval_completion(ApprovalMode mode, int assignees, int approvals, int rejections, int quorum, ApprovalOutcome expected) =>
        Assert.Equal(expected, ApprovalTally.Outcome(mode, assignees, approvals, rejections, quorum));

    [Fact]
    public void Quorum_larger_than_assignees_is_invalid() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ApprovalTally.Outcome(ApprovalMode.Quorum, 2, 0, 0, quorum: 3));
}
