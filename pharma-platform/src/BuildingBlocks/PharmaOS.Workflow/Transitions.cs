using System.Text.Json.Nodes;
using PharmaOS.Rules;

namespace PharmaOS.Workflow;

public sealed record TransitionDefinition(
    string Action,
    string From,
    string To,
    string? Permission = null,
    JsonNode? Guard = null,
    string? SignatureMeaning = null,
    string? Label = null,
    bool RequireReason = false,
    string? GuardMessage = null);

/// <summary>An immutable, versioned workflow definition. Running instances stay pinned to their version (URS-WF-005).</summary>
public sealed record WorkflowDefinition
{
    public string Code { get; }
    public int Version { get; }
    public string InitialState { get; }
    public IReadOnlyList<TransitionDefinition> Transitions { get; }

    public WorkflowDefinition(string code, int version, string initialState, IReadOnlyList<TransitionDefinition> transitions)
    {
        var duplicate = transitions.GroupBy(t => (t.From, t.Action)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate transition '{duplicate.Key.Action}' from state '{duplicate.Key.From}'");
        if (!transitions.Any(t => t.From == initialState))
            throw new ArgumentException($"Initial state '{initialState}' has no outgoing transition");
        foreach (var t in transitions.Where(t => t.Guard is not null))
            RuleEvaluator.Validate(t.Guard);

        Code = code;
        Version = version;
        InitialState = initialState;
        Transitions = transitions;
    }
}

public abstract record TransitionDecision
{
    public sealed record Allowed(string To) : TransitionDecision;

    public sealed record SignatureRequired(string To, string Meaning) : TransitionDecision;

    public sealed record Denied(string Reason, DenialKind Kind = DenialKind.NotAvailable) : TransitionDecision;
}

public enum DenialKind
{
    NotAvailable,
    MissingPermission,
    GuardFailed,
}

public static class TransitionEvaluator
{
    /// <summary>
    /// Decides whether <paramref name="action"/> may be taken. Order matches the authorization model
    /// (DS §10.7): availability, permission, guard, then signature. Fails closed.
    /// </summary>
    public static TransitionDecision Evaluate(
        WorkflowDefinition definition,
        string currentState,
        string action,
        JsonNode? record,
        IReadOnlySet<string> permissions,
        bool signaturePresented)
    {
        var t = definition.Transitions.FirstOrDefault(x => x.From == currentState && x.Action == action);
        if (t is null)
            return new TransitionDecision.Denied($"Action '{action}' is not available in state '{currentState}'");
        if (t.Permission is not null && !permissions.Contains(t.Permission))
            return new TransitionDecision.Denied($"Missing permission '{t.Permission}'", DenialKind.MissingPermission);
        if (t.Guard is not null && !RuleEvaluator.IsTruthy(RuleEvaluator.Evaluate(t.Guard, record)))
            return new TransitionDecision.Denied(t.GuardMessage ?? "Guard condition not met", DenialKind.GuardFailed);
        if (t.SignatureMeaning is not null && !signaturePresented)
            return new TransitionDecision.SignatureRequired(t.To, t.SignatureMeaning);
        return new TransitionDecision.Allowed(t.To);
    }

    public static IEnumerable<TransitionDefinition> Available(WorkflowDefinition definition, string currentState) =>
        definition.Transitions.Where(t => t.From == currentState);

    public static TransitionDefinition? Find(WorkflowDefinition definition, string currentState, string action) =>
        definition.Transitions.FirstOrDefault(t => t.From == currentState && t.Action == action);
}

public enum ApprovalMode
{
    ParallelAll,
    ParallelAny,
    Quorum,
}

public enum ApprovalOutcome
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>Completion rule for a parallel approval step (FS-WF-002). Any rejection rejects the step.</summary>
public static class ApprovalTally
{
    public static ApprovalOutcome Outcome(ApprovalMode mode, int assignees, int approvals, int rejections, int quorum = 0)
    {
        if (rejections > 0) return ApprovalOutcome.Rejected;
        var needed = mode switch
        {
            ApprovalMode.ParallelAll => assignees,
            ApprovalMode.ParallelAny => 1,
            ApprovalMode.Quorum when quorum > 0 && quorum <= assignees => quorum,
            ApprovalMode.Quorum => throw new ArgumentOutOfRangeException(nameof(quorum), "Quorum must be between 1 and the number of assignees"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return approvals >= needed ? ApprovalOutcome.Approved : ApprovalOutcome.Pending;
    }
}
