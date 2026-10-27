using PharmaOS.Platform.Host.Workflow;
using PharmaOS.Rules;

namespace PharmaOS.IntegrationTests;

/// <summary>
/// The shipped workflows must satisfy the rules authored ones are held to. If a guard is added that reads a field missing from a record
/// type's field list, this fails and the list (next to the record's ToRuleData) is updated with it.
/// </summary>
public class BuiltInWorkflowsTests
{
    public static TheoryData<string> Codes => new(BuiltInWorkflows.Codes);

    [Theory]
    [MemberData(nameof(Codes))]
    public void A_shipped_workflow_passes_the_checks_an_authored_one_must_pass(string code)
    {
        var release = BuiltInWorkflows.Find(code)!.Release;
        Assert.Empty(BuiltInWorkflows.Check(code, release.InitialState, release.Transitions));
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_field_a_shipped_guard_reads_is_in_the_record_types_field_list(string code)
    {
        var target = BuiltInWorkflows.Find(code)!;
        if (target.RuleFields is not { } fields) return; // a deviation's field set is open (tenant custom fields)
        var read = target.Release.Transitions.Where(t => t.Guard is not null).SelectMany(t => RuleEvaluator.Variables(t.Guard)).ToHashSet();
        Assert.Empty(read.Except(fields));
    }

    [Fact]
    public void The_capa_and_change_field_lists_are_set_and_deviation_is_open()
    {
        Assert.NotNull(BuiltInWorkflows.Find("CAPA_STD")!.RuleFields);
        Assert.NotNull(BuiltInWorkflows.Find("CHANGE_STD")!.RuleFields);
        Assert.Null(BuiltInWorkflows.Find("DEVIATION_STD")!.RuleFields);
    }
}
