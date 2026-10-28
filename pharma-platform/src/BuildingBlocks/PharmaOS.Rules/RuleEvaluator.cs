using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PharmaOS.Rules;

public sealed class RuleException(string message) : Exception(message);

/// <summary>
/// Evaluates the platform's rule language: a JSON AST with no loops, no I/O and bounded size (DS §10.2).
/// A rule is a literal, an array of rules, or an object with exactly one key naming the operator.
/// The TypeScript evaluator must produce identical results on tests/rules-golden/*.json.
/// </summary>
public static class RuleEvaluator
{
    public const int MaxDepth = 32;
    public const int MaxNodes = 2000;

    private static readonly HashSet<string> Operators =
    [
        "var", "==", "!=", "<", "<=", ">", ">=", "between", "in", "and", "or", "not", "if", "isEmpty",
        "+", "-", "*", "/", "min", "max", "abs", "round",
    ];

    public static JsonNode? Evaluate(JsonNode? rule, JsonNode? data) => Eval(rule, data, 0, new Budget());

    public static bool IsTruthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject => true,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => ToDecimal(v) != 0m,
            _ => false,
        },
        _ => false,
    };

    /// <summary>Configuration-time check: known operators, size and depth limits. Throws <see cref="RuleException"/>.</summary>
    public static void Validate(JsonNode? rule) => Walk(rule, 0, new Budget());

    /// <summary>
    /// The record fields a rule reads: the first segment of each <c>var</c> name (a record's data is flat, so "a.b" reads field "a").
    /// A variable name must be written out; a computed name cannot be checked, so it throws <see cref="RuleException"/>.
    /// </summary>
    public static IReadOnlySet<string> Variables(JsonNode? rule)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        Collect(rule, found);
        return found;
    }

    private static void Collect(JsonNode? rule, HashSet<string> found)
    {
        switch (rule)
        {
            case JsonArray arr:
                foreach (var item in arr) Collect(item, found);
                break;
            case JsonObject obj:
                var (op, args) = Split(obj);
                if (op == "var")
                {
                    if (args.Count == 0 || args[0] is not JsonValue v || !v.TryGetValue<string>(out var name))
                        throw new RuleException("A variable name must be written out, not computed");
                    var field = name.Split('.')[0];
                    if (field.Length > 0) found.Add(field);
                    foreach (var fallback in args.Skip(1)) Collect(fallback, found);
                }
                else
                {
                    foreach (var a in args) Collect(a, found);
                }
                break;
        }
    }

    private sealed class Budget
    {
        public int Nodes;
    }

    private static void Count(int depth, Budget budget)
    {
        if (depth > MaxDepth) throw new RuleException($"Rule exceeds maximum depth of {MaxDepth}");
        if (++budget.Nodes > MaxNodes) throw new RuleException($"Rule exceeds maximum size of {MaxNodes} nodes");
    }

    private static void Walk(JsonNode? rule, int depth, Budget budget)
    {
        Count(depth, budget);
        switch (rule)
        {
            case JsonArray arr:
                foreach (var item in arr) Walk(item, depth + 1, budget);
                break;
            case JsonObject obj:
                var (op, args) = Split(obj);
                if (!Operators.Contains(op)) throw new RuleException($"Unknown operator '{op}'");
                foreach (var a in args) Walk(a, depth + 1, budget);
                break;
        }
    }

    private static (string Op, List<JsonNode?> Args) Split(JsonObject obj)
    {
        if (obj.Count != 1) throw new RuleException("An operator object must have exactly one key");
        var (op, argNode) = obj.First();
        List<JsonNode?> args = argNode is JsonArray arr ? [.. arr] : [argNode];
        return (op, args);
    }

    private static JsonNode? Eval(JsonNode? rule, JsonNode? data, int depth, Budget budget)
    {
        Count(depth, budget);
        switch (rule)
        {
            case JsonArray arr:
                return new JsonArray([.. arr.Select(item => Eval(item, data, depth + 1, budget))]);
            case JsonObject obj:
                var (op, args) = Split(obj);
                return Apply(op, args, data, depth + 1, budget);
            default:
                return rule?.DeepClone();
        }
    }

    private static JsonNode? Apply(string op, List<JsonNode?> args, JsonNode? data, int depth, Budget budget)
    {
        JsonNode? Arg(int i) => i < args.Count ? Eval(args[i], data, depth, budget) : null;

        switch (op)
        {
            case "var":
                return ResolveVar(Arg(0), data) ?? (args.Count > 1 ? Arg(1) : null);
            case "==":
                return Bool(AreEqual(Arg(0), Arg(1)));
            case "!=":
                return Bool(!AreEqual(Arg(0), Arg(1)));
            case "<" or "<=" or ">" or ">=":
                return Bool(Compare(op, Arg(0), Arg(1)));
            case "between":
                {
                    var x = Arg(0);
                    return Bool(Compare(">=", x, Arg(1)) && Compare("<=", x, Arg(2)));
                }
            case "in":
                {
                    var needle = Arg(0);
                    return Arg(1) switch
                    {
                        JsonArray hay => Bool(hay.Any(h => AreEqual(needle, h))),
                        JsonValue s when s.GetValueKind() == JsonValueKind.String && needle is JsonValue n
                                         && n.GetValueKind() == JsonValueKind.String =>
                            Bool(s.GetValue<string>().Contains(n.GetValue<string>(), StringComparison.Ordinal)),
                        _ => Bool(false),
                    };
                }
            case "and":
                for (var i = 0; i < args.Count; i++)
                    if (!IsTruthy(Arg(i))) return Bool(false);
                return Bool(true);
            case "or":
                for (var i = 0; i < args.Count; i++)
                    if (IsTruthy(Arg(i))) return Bool(true);
                return Bool(false);
            case "not":
                return Bool(!IsTruthy(Arg(0)));
            case "if":
                {
                    // [cond1, then1, cond2, then2, ..., else?]
                    var i = 0;
                    for (; i + 1 < args.Count; i += 2)
                        if (IsTruthy(Arg(i))) return Arg(i + 1);
                    return i < args.Count ? Arg(i) : null;
                }
            case "isEmpty":
                {
                    var v = Arg(0);
                    return Bool(v switch
                    {
                        null => true,
                        JsonArray a => a.Count == 0,
                        JsonObject o => o.Count == 0,
                        JsonValue s when s.GetValueKind() == JsonValueKind.String => s.GetValue<string>().Length == 0,
                        JsonValue n when n.GetValueKind() == JsonValueKind.Null => true,
                        _ => false,
                    });
                }
            case "+":
                return Num(Numbers(args, Arg).Sum());
            case "*":
                return Num(Numbers(args, Arg).Aggregate(1m, (a, b) => a * b));
            case "-":
                {
                    var n = Numbers(args, Arg);
                    return Num(n.Count == 1 ? -n[0] : n[0] - n[1]);
                }
            case "/":
                {
                    var n = Numbers(args, Arg);
                    if (n[1] == 0m) throw new RuleException("Division by zero");
                    return Num(n[0] / n[1]);
                }
            case "min":
                return Num(Numbers(args, Arg).Min());
            case "max":
                return Num(Numbers(args, Arg).Max());
            case "abs":
                return Num(Math.Abs(RequireNumber(Arg(0))));
            case "round":
                {
                    var x = RequireNumber(Arg(0));
                    var digits = args.Count > 1 ? (int)RequireNumber(Arg(1)) : 0;
                    var mode = args.Count > 2 ? (Arg(2) as JsonValue)?.ToString() : "half_up";
                    var rounding = mode switch
                    {
                        "half_up" => MidpointRounding.AwayFromZero,
                        "half_even" => MidpointRounding.ToEven,
                        _ => throw new RuleException($"Unknown rounding mode '{mode}'"),
                    };
                    return Num(Math.Round(x, digits, rounding));
                }
            default:
                throw new RuleException($"Unknown operator '{op}'");
        }
    }

    private static JsonNode? ResolveVar(JsonNode? pathNode, JsonNode? data)
    {
        var path = pathNode switch
        {
            null => "",
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            { } other => other.ToJsonString(),
        };
        if (path.Length == 0) return data?.DeepClone();
        var current = data;
        foreach (var segment in path.Split('.'))
        {
            current = current switch
            {
                JsonObject o => o.TryGetPropertyValue(segment, out var child) ? child : null,
                JsonArray a when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var idx)
                                 && idx < a.Count => a[idx],
                _ => null,
            };
            if (current is null) return null;
        }
        return current?.DeepClone();
    }

    private static List<decimal> Numbers(List<JsonNode?> args, Func<int, JsonNode?> arg)
    {
        if (args.Count == 0) throw new RuleException("Arithmetic operator requires at least one argument");
        return [.. Enumerable.Range(0, args.Count).Select(i => RequireNumber(arg(i)))];
    }

    private static decimal RequireNumber(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? ToDecimal(v)
            : throw new RuleException($"Expected a number but got {node?.ToJsonString() ?? "null"}");

    private static decimal ToDecimal(JsonValue v) =>
        decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool AreEqual(JsonNode? a, JsonNode? b)
    {
        if (IsNull(a) || IsNull(b)) return IsNull(a) && IsNull(b);
        if (a is JsonValue va && b is JsonValue vb
            && va.GetValueKind() == JsonValueKind.Number && vb.GetValueKind() == JsonValueKind.Number)
            return ToDecimal(va) == ToDecimal(vb);
        return JsonNode.DeepEquals(a, b);
    }

    /// <summary>Numbers compare numerically, strings ordinally; any other pairing (including null) is false.</summary>
    private static bool Compare(string op, JsonNode? a, JsonNode? b)
    {
        int cmp;
        if (a is JsonValue va && b is JsonValue vb && va.GetValueKind() == vb.GetValueKind())
        {
            cmp = va.GetValueKind() switch
            {
                JsonValueKind.Number => ToDecimal(va).CompareTo(ToDecimal(vb)),
                JsonValueKind.String => string.CompareOrdinal(va.GetValue<string>(), vb.GetValue<string>()),
                _ => int.MinValue,
            };
            if (cmp == int.MinValue) return false;
        }
        else
        {
            return false;
        }
        return op switch { "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, _ => cmp >= 0 };
    }

    private static bool IsNull(JsonNode? n) => n is null || (n is JsonValue v && v.GetValueKind() == JsonValueKind.Null);

    private static JsonNode Bool(bool b) => JsonValue.Create(b);

    private static JsonNode Num(decimal d) => JsonValue.Create(d);
}
