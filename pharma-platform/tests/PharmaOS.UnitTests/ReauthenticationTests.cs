using PharmaOS.ESig;

namespace PharmaOS.UnitTests;

[Trait("Req", "URS-SEC-011")]
public class ReauthenticationTests
{
    private static readonly Guid Signer = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly DateTimeOffset Issued = new(2026, 10, 15, 12, 0, 0, 700, TimeSpan.Zero);
    private static readonly DateTimeOffset Expires = Issued.AddSeconds(120);

    private static ChallengeCheck Check(DateTimeOffset? authTime, DateTimeOffset now, Guid? actingUser = null, DateTimeOffset? consumed = null) =>
        Reauthentication.Check(Signer, actingUser ?? Signer, Issued, Expires, consumed, authTime, now);

    [Fact]
    public void Password_typed_after_the_challenge_is_accepted() =>
        Assert.Equal(ChallengeCheck.Ok, Check(Issued.AddSeconds(20), Issued.AddSeconds(25)));

    [Fact]
    public void Same_second_as_the_challenge_is_accepted_because_auth_time_has_one_second_resolution() =>
        Assert.Equal(ChallengeCheck.Ok, Check(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), Issued.AddSeconds(5)));

    [Fact]
    public void A_recent_sign_in_from_before_the_challenge_is_not_enough() =>
        Assert.Equal(ChallengeCheck.AuthenticationNotFresh, Check(Issued.AddSeconds(-2), Issued.AddSeconds(5)));

    [Fact]
    public void Missing_auth_time_is_refused() =>
        Assert.Equal(ChallengeCheck.AuthenticationNotFresh, Check(null, Issued.AddSeconds(5)));

    [Fact]
    public void Auth_time_in_the_future_beyond_clock_skew_is_refused() =>
        Assert.Equal(ChallengeCheck.AuthenticationNotFresh, Check(Issued.AddSeconds(100), Issued.AddSeconds(10)));

    [Fact]
    public void Expired_challenge_is_refused_even_with_fresh_password() =>
        Assert.Equal(ChallengeCheck.Expired, Check(Issued.AddSeconds(110), Expires.AddSeconds(1)));

    [Fact]
    public void Challenge_is_single_use() =>
        Assert.Equal(ChallengeCheck.AlreadyUsed, Check(Issued.AddSeconds(20), Issued.AddSeconds(25), consumed: Issued.AddSeconds(21)));

    [Fact]
    public void Another_users_challenge_is_refused() =>
        Assert.Equal(ChallengeCheck.WrongUser, Check(Issued.AddSeconds(20), Issued.AddSeconds(25), actingUser: Guid.NewGuid()));

    [Fact]
    public void Manifest_shows_printed_name_date_time_and_meaning()
    {
        var text = SignatureManifest.Render("Avery Approver", "qa.approver", "Approved", "I approve this record for use.",
            new DateTimeOffset(2026, 10, 15, 14, 5, 9, TimeSpan.FromHours(2)), "Unit of measure", "KG", 3, new string('a', 64), "Release for use");
        Assert.Contains("Electronically signed by Avery Approver (qa.approver)", text);
        Assert.Contains("Date and time: 2026-10-15 12:05:09 UTC", text);
        Assert.Contains("Meaning: Approved - \"I approve this record for use.\"", text);
        Assert.Contains("Unit of measure KG, version 3", text);
        Assert.Contains("Reason: Release for use", text);
    }
}
