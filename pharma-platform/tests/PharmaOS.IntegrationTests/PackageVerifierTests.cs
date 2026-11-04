using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PharmaOS.Platform.Host.ConfigPackages;

namespace PharmaOS.IntegrationTests;

/// <summary>The package document and what it takes to believe a package file: well formed, hash matches, signed by a trusted key.</summary>
public sealed class PackageVerifierTests
{
    private static JsonArray Transitions(string action = "submit") => new(
        new JsonObject { ["action"] = action, ["from"] = "OPEN", ["to"] = "CLOSED", ["permission"] = "capa:write", ["requireReason"] = false });

    private static PackageDocument Document(string action = "submit", string code = "PKG-1") => PackageDocument.Create(
        code, "Package one", "1.0", "CONFIG", "0.9.0",
        [new PackageItem(PackageItem.Workflow, "CAPA", 2, "OPEN", Transitions(action))]);

    private static string Sign(ECDsa key, byte[] data, int version = 1) =>
        $"vault:v{version}:" + Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonObject File(PackageDocument doc, ECDsa signer, int version = 1, string? embeddedKey = null) => new()
    {
        ["format"] = PackageDocument.Format,
        ["package"] = doc.ToJson(),
        ["contentHash"] = doc.ContentHash,
        ["signature"] = new JsonObject { ["value"] = Sign(signer, PackageDocument.SignedBytes(doc.ContentHash), version), ["keyName"] = "pharmaos-packages", ["keyVersion"] = version },
        ["publicKeyPem"] = embeddedKey ?? signer.ExportSubjectPublicKeyInfoPem(),
    };

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void A_file_with_a_matching_hash_and_a_trusted_signature_verifies()
    {
        using var key = NewKey();
        var result = PackageVerifier.Verify(File(Document(), key), [new("config-env", 1, key.ExportSubjectPublicKeyInfoPem())]);

        Assert.True(result.Ok, string.Join(" ", result.Problems));
        Assert.Equal("config-env", result.Package!.TrustedAs);
        Assert.Equal("PKG-1", result.Package.Document.Code);
        Assert.Equal(Document().ContentHash, result.Package.ContentHash);
    }

    [Fact]
    public void The_hash_depends_on_content_not_on_how_the_items_were_listed()
    {
        var a = PackageDocument.Create("P", "n", "1", "CONFIG", "1", [
            new PackageItem(PackageItem.Workflow, "CHANGE", 2, "OPEN", Transitions()),
            new PackageItem(PackageItem.Workflow, "CAPA", 2, "OPEN", Transitions())]);
        var b = PackageDocument.Create("P", "n", "1", "CONFIG", "1", [
            new PackageItem(PackageItem.Workflow, "CAPA", 2, "OPEN", Transitions()),
            new PackageItem(PackageItem.Workflow, "CHANGE", 2, "OPEN", Transitions())]);

        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.NotEqual(a.ContentHash, Document("approve").ContentHash);
    }

    [Fact]
    public void The_manifest_lists_the_items_without_their_content()
    {
        var manifest = Document().Manifest();

        var item = Assert.Single(manifest["items"]!.AsArray())!.AsObject();
        Assert.Equal("CAPA", (string?)item["code"]);
        Assert.Equal(2, (int?)item["version"]);
        Assert.Null(item["definition"]);
        Assert.Equal("0.9.0", (string?)manifest["platformVersion"]);
    }

    [Fact]
    public void Content_changed_after_signing_is_a_hash_mismatch()
    {
        using var key = NewKey();
        var file = File(Document(), key);
        file["package"]!["items"]![0]!["definition"]!["transitions"]![0]!["to"] = "ARCHIVED";
        file["package"]!["items"]![0]!["definition"]!["initialState"] = "OPEN";

        var result = PackageVerifier.Verify(file, [new("k", 1, key.ExportSubjectPublicKeyInfoPem())]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.StartsWith("HASH_MISMATCH"));
    }

    [Fact]
    public void A_package_re_hashed_and_re_signed_by_someone_else_is_not_trusted_even_with_their_key_inside_the_file()
    {
        using var trusted = NewKey();
        using var attacker = NewKey();
        var forged = File(Document("approve"), attacker, embeddedKey: attacker.ExportSubjectPublicKeyInfoPem());

        var result = PackageVerifier.Verify(forged, [new("config-env", 1, trusted.ExportSubjectPublicKeyInfoPem())]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.StartsWith("UNTRUSTED_SIGNER"));
    }

    [Fact]
    public void A_signature_from_another_key_version_than_the_trusted_one_is_not_trusted()
    {
        using var key = NewKey();
        var result = PackageVerifier.Verify(File(Document(), key, version: 2), [new("k", 1, key.ExportSubjectPublicKeyInfoPem())]);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.StartsWith("UNTRUSTED_SIGNER") && p.Contains("version 2"));
    }

    [Fact]
    public void A_signature_over_a_different_hash_does_not_verify()
    {
        using var key = NewKey();
        var file = File(Document(), key);
        file["signature"]!["value"] = Sign(key, PackageDocument.SignedBytes(Document("approve").ContentHash));

        Assert.Contains(PackageVerifier.Verify(file, [new("k", 1, key.ExportSubjectPublicKeyInfoPem())]).Problems, p => p.StartsWith("UNTRUSTED_SIGNER"));
    }

    [Fact]
    public void A_signature_made_for_something_other_than_a_package_does_not_verify()
    {
        using var key = NewKey();
        var doc = Document();
        var file = File(doc, key);
        file["signature"]!["value"] = Sign(key, System.Text.Encoding.UTF8.GetBytes(doc.ContentHash));

        Assert.False(PackageVerifier.Verify(file, [new("k", 1, key.ExportSubjectPublicKeyInfoPem())]).Ok);
    }

    [Fact]
    public void Unsigned_or_malformed_signatures_are_reported_not_thrown()
    {
        using var key = NewKey();
        var trusted = new List<TrustedKey> { new("k", 1, key.ExportSubjectPublicKeyInfoPem()) };

        var unsigned = File(Document(), key);
        unsigned.Remove("signature");
        Assert.Contains(PackageVerifier.Verify(unsigned, trusted).Problems, p => p.StartsWith("SIGNATURE_MISSING"));

        var garbage = File(Document(), key);
        garbage["signature"]!["value"] = "not a signature";
        Assert.Contains(PackageVerifier.Verify(garbage, trusted).Problems, p => p.StartsWith("SIGNATURE_INVALID"));

        var short_ = File(Document(), key);
        short_["signature"]!["value"] = "vault:v1:AAAA";
        Assert.False(PackageVerifier.Verify(short_, trusted).Ok);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"package":"x"}""")]
    [InlineData("""{"package":{"format":"other"}}""")]
    public void Files_that_are_not_packages_are_refused_with_reasons(string json)
    {
        var result = PackageVerifier.Verify(JsonNode.Parse(json), []);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Unknown_artifact_types_empty_packages_and_non_standard_forms_are_refused()
    {
        using var key = NewKey();
        var trusted = new List<TrustedKey> { new("k", 1, key.ExportSubjectPublicKeyInfoPem()) };

        var unknownType = File(Document(), key);
        unknownType["package"]!["items"]![0]!["artifactType"] = "FORM";
        Assert.Contains(PackageVerifier.Verify(unknownType, trusted).Problems, p => p.Contains("not supported"));

        var empty = File(Document(), key);
        empty["package"]!["items"] = new JsonArray();
        Assert.Contains(PackageVerifier.Verify(empty, trusted).Problems, p => p.Contains("at least one item"));

        var extra = File(Document(), key);
        extra["package"]!["note"] = "added";
        Assert.Contains(PackageVerifier.Verify(extra, trusted).Problems, p => p.Contains("standard form"));

        var mismatched = File(Document(), key);
        mismatched["package"]!["items"]![0]!["version"] = 9;
        Assert.Contains(PackageVerifier.Verify(mismatched, trusted).Problems, p => p.Contains("differ from its definition"));
    }
}
