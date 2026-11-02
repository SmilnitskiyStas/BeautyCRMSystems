using BeautyCrm.Application.Features.BeautyPublicBooking;

namespace BeautyCrm.Tests.Beauty;

/// <summary>TASK-769 (§25.5.3, §25.10 п. 5): нормалізація до E.164, цифри для порівняння, маска inbox.</summary>
public sealed class ClientPhoneNormalizerTests
{
    [Theory]
    [InlineData("+380501112233", "+380501112233")]
    [InlineData("380501112233", "+380501112233")]
    [InlineData("0501112233", "+380501112233")]
    [InlineData("050 111 22 33", "+380501112233")]
    [InlineData("(050) 111-22-33", "+380501112233")]
    [InlineData("050.111.22.33", "+380501112233")]
    [InlineData("00380501112233", "+380501112233")]
    [InlineData("+38 (050) 111-22-33", "+380501112233")]
    [InlineData("+1 202 555 0143", "+12025550143")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    public void normalize_accepts_common_formats(string raw, string expected) => Assert.Equal(expected, PhoneNormalizer.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("050abc1112233")]
    [InlineData("+38050111223")] // +380 і 8 цифр абонента
    [InlineData("+3805011122334")] // +380 і 10 цифр абонента
    [InlineData("+0501112233")]
    [InlineData("050111")] // 6 цифр
    [InlineData("0501112233, 0671112233")]
    [InlineData("+380501112233; DROP TABLE beauty_clients")]
    [InlineData("+１２３４５６７８９０１２")] // повноширинні цифри
    [InlineData("+٣٨٠٥٠١١١٢٢٣٣")] // арабсько-індійські цифри
    [InlineData("+1234567890123456")] // 16 цифр
    [InlineData("++380501112233")]
    public void normalize_rejects_garbage_wrong_length_and_non_ascii_digits(string? raw) => Assert.Null(PhoneNormalizer.Normalize(raw));

    [Fact]
    public void match_candidates_cover_national_and_international_forms_but_not_other_numbers()
    {
        var a = PhoneNormalizer.MatchCandidates("+380501112233");
        Assert.Contains("380501112233", a);
        Assert.Contains("0501112233", a);
        var b = PhoneNormalizer.MatchCandidates("050 111 22 33");
        Assert.Equal(a.OrderBy(x => x), b.OrderBy(x => x));
        Assert.DoesNotContain("380501112233", PhoneNormalizer.MatchCandidates("+380671112233"));
        Assert.Empty(PhoneNormalizer.MatchCandidates("abc"));
        Assert.Empty(PhoneNormalizer.MatchCandidates(null));
    }

    [Theory]
    [InlineData("+380501112233", "+••••••••••33")]
    [InlineData("050 111 22 33", "+••••••••33")]
    [InlineData("12", "+12")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void mask_keeps_plus_and_last_two_digits_only(string? raw, string? expected)
    {
        var masked = PhoneNormalizer.Mask(raw);
        Assert.Equal(expected, masked);
        if (masked is not null && raw is { Length: > 6 }) Assert.DoesNotContain("0501", masked);
    }
}
