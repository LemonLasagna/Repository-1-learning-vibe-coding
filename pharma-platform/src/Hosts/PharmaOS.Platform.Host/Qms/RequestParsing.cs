using System.Text.Json;
using System.Text.Json.Nodes;

namespace PharmaOS.Platform.Host.Qms;

/// <summary>Reads request bodies against field specs, shared by the QMS endpoints.</summary>
internal static class RequestParsing
{
    internal static Dictionary<string, object?> Parse(
        JsonObject body, IReadOnlyList<FieldSpec> specs, bool requireAll, Dictionary<string, string[]> errors, IReadOnlyList<string>? extra = null)
    {
        var columns = new Dictionary<string, object?>();
        foreach (var (name, node) in body)
        {
            var f = specs.FirstOrDefault(s => s.Name == name);
            if (f is null)
            {
                if (extra?.Contains(name) != true) errors[name] = ["Unknown field."];
                continue;
            }
            if (FieldParser.TryParse(f, node, out var value, out var error)) columns[f.Column!] = value;
            else errors[name] = [error!];
        }
        if (requireAll)
            foreach (var f in specs.Where(f => f.Required && !body.ContainsKey(f.Name))) errors.TryAdd(f.Name, ["Required."]);
        return columns;
    }

    internal static Guid? TryGuid(JsonObject body, string name, Dictionary<string, string[]> errors)
    {
        if (body[name] is not { } node || node.GetValueKind() == JsonValueKind.Null) return null;
        if (node.GetValueKind() == JsonValueKind.String && Guid.TryParse(node.GetValue<string>(), out var g)) return g;
        errors[name] = ["A UUID."];
        return null;
    }
}
