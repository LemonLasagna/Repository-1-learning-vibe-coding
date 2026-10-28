using System.Text.Json.Nodes;
using PharmaOS.Gxp;
using PharmaOS.Rules;

namespace PharmaOS.UnitTests;

public class RuleEvaluatorTests
{
    public static TheoryData<string> GoldenCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in LoadGolden()) data.Add(c!["name"]!.GetValue<string>());
        return data;
    }

    private static JsonArray LoadGolden() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rules-golden", "core.json")))!.AsArray();

    [Theory]
    [MemberData(nameof(GoldenCases))]
    [Trait("Req", "URS-CFG-003")]
    public void Golden_vector_matches(string name)
    {
        var c = LoadGolden().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var actual = RuleEvaluator.Evaluate(c["rule"], c["data"]);
        // Compare canonically so 0.30 and 0.3 are the same value, as in the shared TypeScript parity test.
        Assert.Equal(CanonicalJson.Serialize(c["expected"]), CanonicalJson.Serialize(actual));
    }

    [Fact]
    public void Unknown_operator_is_rejected_at_validation()
    {
        var ex = Assert.Throws<RuleException>(() => RuleEvaluator.Validate(JsonNode.Parse("""{"exec":["rm -rf /"]}""")));
        Assert.Contains("Unknown operator", ex.Message);
    }

    [Fact]
    public void Operator_object_with_two_keys_is_rejected()
    {
        Assert.Throws<RuleException>(() => RuleEvaluator.Validate(JsonNode.Parse("""{"==":[1,1],"!=":[1,2]}""")));
    }

    [Fact]
    public void Depth_limit_is_enforced()
    {
        JsonNode rule = JsonValue.Create(true);
        for (var i = 0; i < RuleEvaluator.MaxDepth + 2; i++) rule = new JsonObject { ["not"] = rule };
        Assert.Throws<RuleException>(() => RuleEvaluator.Validate(rule));
    }

    [Fact]
    public void Size_limit_is_enforced()
    {
        var big = new JsonArray([.. Enumerable.Range(0, RuleEvaluator.MaxNodes + 1).Select(i => (JsonNode)JsonValue.Create(i))]);
        Assert.Throws<RuleException>(() => RuleEvaluator.Validate(new JsonObject { ["+"] = big }));
    }

    [Fact]
    public void Division_by_zero_is_an_error_not_infinity()
    {
        Assert.Throws<RuleException>(() => RuleEvaluator.Evaluate(JsonNode.Parse("""{"/":[1,0]}"""), null));
    }

    [Fact]
    public void Arithmetic_on_text_is_an_error()
    {
        Assert.Throws<RuleException>(() => RuleEvaluator.Evaluate(JsonNode.Parse("""{"+":["1",2]}"""), null));
    }

    [Fact]
    public void Variables_lists_every_field_the_rule_reads()
    {
        var rule = JsonNode.Parse("""
            {"and":[{">":[{"var":"actions_planned"},0]},{"or":[{"var":"effectiveness_defined"},{"not":{"isEmpty":{"var":"result"}}}]}]}
            """);
        Assert.Equal(new[] { "actions_planned", "effectiveness_defined", "result" }, RuleEvaluator.Variables(rule).Order());
    }

    [Fact]
    public void Variables_reads_the_field_from_a_dotted_name_and_the_fallback_value()
    {
        var rule = JsonNode.Parse("""{"==":[{"var":["owner.name",{"var":"title"}]},"x"]}""");
        Assert.Equal(new[] { "owner", "title" }, RuleEvaluator.Variables(rule).Order());
    }

    [Fact]
    public void Variables_finds_nothing_in_a_rule_without_fields()
    {
        Assert.Empty(RuleEvaluator.Variables(JsonNode.Parse("""{"==":[1,1]}""")));
    }

    [Fact]
    public void Variables_refuses_a_computed_field_name()
    {
        var rule = JsonNode.Parse("""{"var":{"if":[true,"a","b"]}}""");
        Assert.Throws<RuleException>(() => RuleEvaluator.Variables(rule));
    }
}
