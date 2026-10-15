using PharmaOS.ESig;

namespace PharmaOS.UnitTests;

[Trait("Req", "URS-SEC-011")]
public class SignatureBindingTests
{
    private static readonly Guid Author = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Approver = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Record = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private const string Hash = "9f3a0000000000000000000000000000000000000000000000000000000c21e";

    private static readonly SignaturePolicy Approve = new(
        Guid.NewGuid(), "APPROVED", RequireSecondPerson: true, RequireAllComponentsEverySignature: false,
        ConflictingMeanings: new HashSet<string> { "AUTHORED" });

    private static SignatureEvidence Sig(Guid signer, string meaning = "APPROVED", int version = 7, string hash = Hash) =>
        new(Guid.NewGuid(), signer, meaning, "DEVIATION", Record, version, hash,
            new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.FromHours(2)), "Investigation adequate", Approve.Id);

    private static BindingResult Verify(SignatureEvidence s, Guid actingUser, int currentVersion = 7,
        string currentHash = Hash, Guid? author = null, params PriorSignature[] prior) =>
        SignatureBinding.Verify(s, actingUser, Record, currentVersion, currentHash, Approve, author ?? Author, prior);

    [Fact]
    public void Valid_independent_approval_binds() =>
        Assert.Equal(BindingResult.Bound, Verify(Sig(Approver), Approver));

    [Fact]
    public void Someone_elses_signature_cannot_be_used() =>
        Assert.Equal(BindingResult.NotSignersOwnSignature, Verify(Sig(Approver), Author));

    [Fact]
    [Trait("Req", "OQ-ESIG-007")]
    public void Signature_for_an_older_version_is_stale() =>
        Assert.Equal(BindingResult.StaleRecordVersion, Verify(Sig(Approver, version: 7), Approver, currentVersion: 8));

    [Fact]
    [Trait("Req", "OQ-ESIG-004")]
    public void Changed_content_with_same_version_is_detected() =>
        Assert.Equal(BindingResult.RecordChanged, Verify(Sig(Approver), Approver, currentHash: Hash.Replace('9', '8')));

    [Fact]
    public void Hash_comparison_ignores_hex_case() =>
        Assert.Equal(BindingResult.Bound, Verify(Sig(Approver, hash: Hash.ToUpperInvariant()), Approver));

    [Fact]
    public void Meaning_must_match_policy() =>
        Assert.Equal(BindingResult.WrongMeaning, Verify(Sig(Approver, meaning: "REVIEWED"), Approver));

    [Fact]
    [Trait("Req", "OQ-ESIG-005")]
    public void Author_cannot_approve_own_record() =>
        Assert.Equal(BindingResult.SegregationOfDuties, Verify(Sig(Author), Author));

    [Fact]
    public void Prior_conflicting_signature_blocks_approval() =>
        Assert.Equal(BindingResult.SegregationOfDuties,
            Verify(Sig(Approver), Approver, author: Guid.NewGuid(), prior: new PriorSignature(Approver, "AUTHORED")));

    [Fact]
    public void Prior_non_conflicting_signature_is_allowed() =>
        Assert.Equal(BindingResult.Bound,
            Verify(Sig(Approver), Approver, prior: new PriorSignature(Approver, "REVIEWED")));

    [Theory]
    [Trait("Req", "OQ-ESIG-002")]
    [InlineData(false, false, RequiredAuthentication.AllComponents)]
    [InlineData(false, true, RequiredAuthentication.PasswordOnly)]
    [InlineData(true, true, RequiredAuthentication.AllComponents)]
    public void Continuous_session_rule(bool allEveryTime, bool signedBefore, RequiredAuthentication expected)
    {
        var policy = Approve with { RequireAllComponentsEverySignature = allEveryTime };
        Assert.Equal(expected, SignatureBinding.RequiredComponents(policy, signedBefore));
    }

    [Fact]
    public void Signature_hash_is_stable_across_time_zone_offsets()
    {
        var s = Sig(Approver);
        var sameInstantUtc = s with { SignedAt = s.SignedAt.ToUniversalTime() };
        Assert.Equal(SignatureBinding.ComputeSignatureHash(s), SignatureBinding.ComputeSignatureHash(sameInstantUtc));
        Assert.NotEqual(SignatureBinding.ComputeSignatureHash(s),
            SignatureBinding.ComputeSignatureHash(s with { Reason = "different" }));
    }
}
