using System.Text.Json.Nodes;
using PharmaOS.Workflow;

namespace PharmaOS.UnitTests;

public class DefinitionValidatorTests
{
    private static readonly TransitionDefinition Submit = new("submit", "DRAFT", "REVIEW", Permission: "qms.capa:write");
    private static readonly TransitionDefinition Approve = new("approve", "REVIEW", "DONE", Permission: "qms.capa:approve");

    private static IReadOnlyList<string> Check(string code = "CAPA_STD", int version = 2, string initial = "DRAFT",
        params TransitionDefinition[] transitions) =>
        DefinitionValidator.Validate(code, version, initial, transitions);

    [Fact]
    public void A_well_formed_definition_has_no_errors()
    {
        Assert.Empty(Check(transitions: [Submit, Approve]));
    }

    [Theory]
    [InlineData("capa_std")]
    [InlineData("1CAPA")]
    [InlineData("CAPA-STD")]
    [InlineData("")]
    public void Code_must_be_capitals_digits_and_underscores(string code)
    {
        Assert.Contains(Check(code: code, transitions: [Submit, Approve]), e => e.StartsWith("Code must"));
    }

    [Fact]
    public void Version_must_be_at_least_one()
    {
        Assert.Contains(Check(version: 0, transitions: [Submit, Approve]), e => e.StartsWith("Version must"));
    }

    [Fact]
    public void A_definition_needs_transitions()
    {
        Assert.Contains(Check(transitions: []), e => e.StartsWith("A workflow needs at least one transition"));
    }

    [Fact]
    public void Every_transition_needs_a_permission()
    {
        var unguarded = Submit with { Permission = null };
        Assert.Contains(Check(transitions: [unguarded, Approve]), e => e.Contains("'submit'") && e.Contains("needs a permission"));
    }

    [Fact]
    public void A_blank_permission_counts_as_missing()
    {
        var blank = Submit with { Permission = "  " };
        Assert.Contains(Check(transitions: [blank, Approve]), e => e.Contains("needs a permission"));
    }

    [Fact]
    public void An_invalid_guard_is_reported_with_its_transition()
    {
        var bad = Submit with { Guard = JsonNode.Parse("""{"no_such_operator":[1,2]}""") };
        Assert.Contains(Check(transitions: [bad, Approve]), e => e.StartsWith("Transition 1 ('submit') has an invalid guard"));
    }

    [Fact]
    public void A_duplicate_action_from_the_same_state_is_reported()
    {
        var again = Submit with { To = "OTHER" };
        Assert.Contains(Check(transitions: [Submit, again, Approve]),
            e => e == "Action 'submit' is defined more than once from state 'DRAFT'.");
    }

    [Fact]
    public void The_initial_state_needs_an_outgoing_transition()
    {
        Assert.Contains(Check(initial: "START", transitions: [Submit, Approve]),
            e => e == "The initial state 'START' has no outgoing transition.");
    }

    [Fact]
    public void A_state_no_transition_can_reach_is_reported()
    {
        var orphan = new TransitionDefinition("cancel", "ORPHAN", "CANCELLED", Permission: "qms.capa:write");
        Assert.Contains(Check(transitions: [Submit, Approve, orphan]), e => e == "State 'ORPHAN' cannot be reached from the initial state.");
        Assert.Contains(Check(transitions: [Submit, Approve, orphan]), e => e == "State 'CANCELLED' cannot be reached from the initial state.");
    }

    [Fact]
    public void Every_problem_is_reported_in_one_pass()
    {
        var broken = Submit with { Permission = null };
        var errors = Check(code: "bad", version: 0, transitions: [broken, broken with { Action = "again" }]);
        Assert.Contains(errors, e => e.StartsWith("Code must"));
        Assert.Contains(errors, e => e.StartsWith("Version must"));
        Assert.Contains(errors, e => e.Contains("needs a permission"));
        Assert.True(errors.Count >= 3);
    }

    [Fact]
    public void A_passing_definition_can_be_constructed()
    {
        var transitions = new[] { Submit, Approve };
        Assert.Empty(DefinitionValidator.Validate("CAPA_STD", 2, "DRAFT", transitions));
        var def = new WorkflowDefinition("CAPA_STD", 2, "DRAFT", transitions);
        Assert.Equal("REVIEW", TransitionEvaluator.Find(def, "DRAFT", "submit")!.To);
    }
}
