using System.Text;
using System.Text.Json.Nodes;
using PharmaOS.Gxp;

namespace PharmaOS.Platform.Host.ConfigPackages;

/// <summary>One artefact in a package: a released workflow version, carried whole so the target needs nothing from the source.</summary>
public sealed record PackageItem(string ArtifactType, string Code, int Version, string InitialState, JsonArray Transitions)
{
    public const string Workflow = "WORKFLOW";

    public JsonObject ToJson() => new()
    {
        ["artifactType"] = ArtifactType,
        ["code"] = Code,
        ["version"] = Version,
        ["definition"] = new JsonObject
        {
            ["code"] = Code, ["version"] = Version, ["initialState"] = InitialState, ["transitions"] = Transitions.DeepClone(),
        },
    };
}

/// <summary>
/// What a configuration package contains and what its hash covers (FS-CFG-014, DS §10.4): the package's identity, the platform it
/// came from and its items in a fixed order. The hash is the SHA-256 of the canonical JSON of exactly this document, so two
/// installations that read the same file compute the same hash, and a changed byte anywhere changes it.
/// </summary>
public sealed record PackageDocument(
    string Code, string Name, string VersionLabel, string SourceEnvironment, string PlatformVersion, IReadOnlyList<PackageItem> Items)
{
    public const string Format = "pharmaos-config-package/1";

    /// <summary>The package with its items in a fixed order (type, code, version).</summary>
    public static PackageDocument Create(
        string code, string name, string versionLabel, string sourceEnvironment, string platformVersion, IEnumerable<PackageItem> items) =>
        new(code, name, versionLabel, sourceEnvironment, platformVersion,
            [.. items.OrderBy(i => i.ArtifactType, StringComparer.Ordinal).ThenBy(i => i.Code, StringComparer.Ordinal).ThenBy(i => i.Version)]);

    public JsonObject ToJson() => new()
    {
        ["format"] = Format,
        ["code"] = Code,
        ["name"] = Name,
        ["versionLabel"] = VersionLabel,
        ["sourceEnvironment"] = SourceEnvironment,
        ["platformVersion"] = PlatformVersion,
        ["items"] = new JsonArray([.. Items.Select(i => (JsonNode)i.ToJson())]),
    };

    /// <summary>What a person reads before importing: the package and its items by type, code and version, without their content.</summary>
    public JsonObject Manifest() => new()
    {
        ["format"] = Format,
        ["platformVersion"] = PlatformVersion,
        ["items"] = new JsonArray([.. Items.Select(i => (JsonNode)new JsonObject
            { ["artifactType"] = i.ArtifactType, ["code"] = i.Code, ["version"] = i.Version })]),
    };

    public string ContentHash => CanonicalJson.Sha256Hex(ToJson());

    /// <summary>The bytes that get signed: the hash, tied to the format so a signature made for another purpose cannot be reused.</summary>
    public static byte[] SignedBytes(string contentHash) => Encoding.UTF8.GetBytes($"{Format}:{contentHash}");

    /// <summary>Reads a package document; shape problems are listed, never thrown. Rules for the content are checked at import.</summary>
    public static PackageDocument? Parse(JsonNode? node, List<string> problems)
    {
        if (node is not JsonObject o)
        {
            problems.Add("The package is not a JSON object.");
            return null;
        }
        if (Text(o, "format") != Format) problems.Add($"The package format must be '{Format}'.");
        var code = Text(o, "code");
        var name = Text(o, "name");
        var label = Text(o, "versionLabel");
        var source = Text(o, "sourceEnvironment");
        var platform = Text(o, "platformVersion");
        foreach (var (field, value) in new[] { ("code", code), ("name", name), ("versionLabel", label), ("sourceEnvironment", source), ("platformVersion", platform) })
            if (string.IsNullOrWhiteSpace(value)) problems.Add($"'{field}' is required.");

        var items = new List<PackageItem>();
        if (o["items"] is not JsonArray array || array.Count == 0)
            problems.Add("A package needs at least one item.");
        else
            for (var i = 0; i < array.Count; i++)
            {
                var item = ParseItem(array[i], i, problems);
                if (item is not null) items.Add(item);
            }
        if (problems.Count > 0) return null;
        var document = Create(code!, name!, label!, source!, platform!, items);
        // The file must already be in the fixed order, so the same content cannot be written two ways with two hashes.
        if (!JsonNode.DeepEquals(document.ToJson(), o)) problems.Add("The package has fields or an item order other than the standard form.");
        return problems.Count == 0 ? document : null;
    }

    private static PackageItem? ParseItem(JsonNode? node, int index, List<string> problems)
    {
        var at = $"items[{index}]";
        if (node is not JsonObject o)
        {
            problems.Add($"{at} is not an object.");
            return null;
        }
        var type = Text(o, "artifactType");
        if (type != PackageItem.Workflow)
        {
            problems.Add($"{at}: artifact type '{type}' is not supported; this version carries {PackageItem.Workflow}.");
            return null;
        }
        if (o["definition"] is not JsonObject d || Text(d, "code") is not { } code || Text(d, "initialState") is not { } initial
            || d["version"] is not JsonValue v || !v.TryGetValue<int>(out var version) || d["transitions"] is not JsonArray transitions)
        {
            problems.Add($"{at}: the workflow definition needs code, version, initialState and transitions.");
            return null;
        }
        if (Text(o, "code") != code || !(o["version"] is JsonValue ov && ov.TryGetValue<int>(out var ver) && ver == version))
        {
            problems.Add($"{at}: the item's code and version differ from its definition.");
            return null;
        }
        return new PackageItem(type, code, version, initial, (JsonArray)transitions.DeepClone());
    }

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
