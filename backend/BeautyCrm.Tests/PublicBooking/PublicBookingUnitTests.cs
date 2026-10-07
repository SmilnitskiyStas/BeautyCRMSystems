using BeautyCrm.Application.Features.BeautyPublicBooking;

namespace BeautyCrm.Tests.PublicBooking;

public sealed class PublicBookingUnitTests
{
    [Theory]
    [InlineData("+380501234567", "+380501234567")]
    [InlineData("+38 (050) 123-45-67", "+380501234567")]
    [InlineData("0501234567", "+380501234567")]
    [InlineData("380501234567", "+380501234567")]
    [InlineData("00380501234567", "+380501234567")]
    [InlineData("+1 415 555 0100", "+14155550100")]
    public void normalize_accepts_common_formats(string raw, string expected) =>
        Assert.Equal(expected, PhoneNormalizer.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("4155550100")] // 10 цифр без 0 й без +: неоднозначно
    [InlineData("+0501234567")]
    [InlineData("+3805012345678901234")] // задовго
    [InlineData("+380501234567; DROP TABLE x")]
    public void normalize_rejects_invalid(string? raw) => Assert.Null(PhoneNormalizer.Normalize(raw));

    [Fact]
    public void token_is_deterministic_long_and_url_safe_and_differs_per_appointment_and_tenant()
    {
        var svc = new PublicTokenService(new PublicBookingOptions { TokenKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray() });
        var tenant = Guid.NewGuid();
        var a = Guid.NewGuid();
        var t = svc.Create(tenant, a);
        Assert.Equal(PublicTokenService.TokenLength, t.Length);
        Assert.True(PublicTokenService.IsWellFormed(t));
        Assert.Equal(t, svc.Create(tenant, a));
        Assert.NotEqual(t, svc.Create(tenant, Guid.NewGuid()));
        Assert.NotEqual(t, svc.Create(Guid.NewGuid(), a));
        Assert.NotEqual(t, PublicTokenService.Hash(t));
        Assert.Equal(64, PublicTokenService.Hash(t).Length);
    }

    [Fact]
    public void token_without_configured_key_fails_closed() =>
        Assert.Throws<InvalidOperationException>(() => new PublicTokenService(new PublicBookingOptions()).Create(Guid.NewGuid(), Guid.NewGuid()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1")]
    [InlineData("../../etc/passwd/../../../../../../../x")]
    public void malformed_token_is_not_well_formed(string? token) => Assert.False(PublicTokenService.IsWellFormed(token));
}
