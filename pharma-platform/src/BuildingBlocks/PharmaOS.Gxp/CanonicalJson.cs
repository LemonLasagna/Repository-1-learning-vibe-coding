using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PharmaOS.Gxp;

/// <summary>
/// Deterministic JSON used to bind electronic signatures to record content (DS §10.5).
/// Object keys are sorted by UTF-16 code unit and there is no insignificant whitespace, as in RFC 8785.
/// Numbers deliberately differ from RFC 8785: they are written as normalised decimals (no exponent,
/// no trailing zeros) so GxP quantities keep their exact decimal value instead of passing through a double.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    public static string Sha256Hex(JsonNode? node) => Sha256Hex(Serialize(node));

    public static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    Write(sb, value);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, arr[i]);
                }
                sb.Append(']');
                break;
            case JsonValue value:
                WriteValue(sb, value);
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                WriteString(sb, value.GetValue<string>());
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            case JsonValueKind.Number:
                sb.Append(NormaliseNumber(value.ToJsonString()));
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON value kind {value.GetValueKind()}");
        }
    }

    internal static string NormaliseNumber(string raw)
    {
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw new InvalidOperationException($"Number '{raw}' cannot be represented exactly as a decimal");
        return d.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
