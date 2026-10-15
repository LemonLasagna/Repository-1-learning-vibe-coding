using System.Text.Json.Nodes;
using PharmaOS.Gxp;

namespace PharmaOS.UnitTests;

public class CanonicalJsonTests
{
    [Fact]
    public void Keys_are_sorted_and_whitespace_removed_recursively()
    {
        var node = JsonNode.Parse("""{ "b": 1, "a": { "z": true, "y": [ 2, { "d": null, "c": "x" } ] } }""");
        Assert.Equal("""{"a":{"y":[2,{"c":"x","d":null}],"z":true},"b":1}""", CanonicalJson.Serialize(node));
    }

    [Fact]
    public void Key_order_uses_utf16_code_units()
    {
        // Upper case sorts before lower case; "a" before "aa".
        var node = JsonNode.Parse("""{"aa":1,"a":2,"B":3}""");
        Assert.Equal("""{"B":3,"a":2,"aa":1}""", CanonicalJson.Serialize(node));
    }

    [Theory]
    [InlineData("1.50", "1.5")]
    [InlineData("100", "100")]
    [InlineData("1e2", "100")]
    [InlineData("-0.0", "0")]
    [InlineData("0.000001", "0.000001")]
    public void Numbers_are_normalised_decimals(string raw, string expected)
    {
        Assert.Equal(expected, CanonicalJson.Serialize(JsonNode.Parse(raw)));
    }

    [Fact]
    public void Strings_escape_only_what_json_requires()
    {
        var node = JsonValue.Create("quote\" back\\ tab\t nl\n ctl\u0001 Ü é");
        Assert.Equal("\"quote\\\" back\\\\ tab\\t nl\\n ctl\\u0001 Ü é\"", CanonicalJson.Serialize(node));
    }

    [Fact]
    public void Hash_is_independent_of_key_order_and_formatting()
    {
        var a = JsonNode.Parse("""{"id":"D-1","qty":2.50,"lots":["L1","L2"]}""");
        var b = JsonNode.Parse("""{ "lots": ["L1","L2"], "qty": 2.5, "id": "D-1" }""");
        Assert.Equal(CanonicalJson.Sha256Hex(a), CanonicalJson.Sha256Hex(b));
        Assert.Equal(64, CanonicalJson.Sha256Hex(a).Length);
    }

    [Fact]
    public void Hash_changes_when_content_changes()
    {
        var a = JsonNode.Parse("""{"qty":2.5}""");
        var b = JsonNode.Parse("""{"qty":2.51}""");
        Assert.NotEqual(CanonicalJson.Sha256Hex(a), CanonicalJson.Sha256Hex(b));
    }

    [Fact]
    public void Known_vector()
    {
        // SHA-256 of the UTF-8 bytes {"a":1}
        Assert.Equal("015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862",
            CanonicalJson.Sha256Hex(JsonNode.Parse("""{ "a" : 1 }""")));
    }
}
