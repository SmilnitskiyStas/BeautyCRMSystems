using BeautyCrm.Api.Auth;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BeautyCrm.Tests.Auth;

public class RolePolicyTests
{
    [Theory]
    [InlineData(Roles.Owner, Roles.Admin, true)]
    [InlineData(Roles.Owner, Roles.Specialist, true)]
    [InlineData(Roles.Owner, Roles.Owner, false)]
    [InlineData(Roles.Admin, Roles.Specialist, true)]
    [InlineData(Roles.Admin, Roles.Admin, false)]
    [InlineData(Roles.Admin, Roles.Owner, false)]
    [InlineData(Roles.Specialist, Roles.Specialist, false)]
    [InlineData(Roles.Specialist, Roles.Admin, false)]
    [InlineData(Roles.PlatformOperator, Roles.Owner, false)]
    public void canManage_follows_hierarchy_and_owner_is_untouchable(string actor, string target, bool expected) =>
        Assert.Equal(expected, RolePolicy.CanManage(actor, target));
}

public class SpecialistScopeTests
{
    private static Actor Make(string role, Guid? specialistId) => new(Guid.NewGuid(), Guid.NewGuid(), role, specialistId);

    [Fact]
    public void effectiveSpecialistId_forces_own_profile_for_specialist_and_ignores_requested()
    {
        var own = Guid.NewGuid();
        Assert.Equal(own, AppointmentAccessService.EffectiveSpecialistId(Make(Roles.Specialist, own), Guid.NewGuid()));
        Assert.Equal(own, AppointmentAccessService.EffectiveSpecialistId(Make(Roles.Specialist, own), null));
        // профіль не прив'язано: порожня вибірка, а не "усі записи"
        Assert.Equal(Guid.Empty, AppointmentAccessService.EffectiveSpecialistId(Make(Roles.Specialist, null), null));
    }

    [Fact]
    public void effectiveSpecialistId_passes_requested_for_management()
    {
        var requested = Guid.NewGuid();
        Assert.Equal(requested, AppointmentAccessService.EffectiveSpecialistId(Make(Roles.Admin, null), requested));
        Assert.Null(AppointmentAccessService.EffectiveSpecialistId(Make(Roles.Owner, null), null));
    }

    [Fact]
    public void canCreateFor_allows_specialist_only_for_self()
    {
        var own = Guid.NewGuid();
        Assert.True(AppointmentAccessService.CanCreateFor(Make(Roles.Specialist, own), own));
        Assert.False(AppointmentAccessService.CanCreateFor(Make(Roles.Specialist, own), Guid.NewGuid()));
        Assert.False(AppointmentAccessService.CanCreateFor(Make(Roles.Specialist, null), Guid.NewGuid()));
        Assert.True(AppointmentAccessService.CanCreateFor(Make(Roles.Admin, null), Guid.NewGuid()));
    }
}

public class TokenCodecTests
{
    [Fact]
    public void generate_then_parse_roundtrips_tenant_and_hash_without_storing_plain_token()
    {
        var tenant = Guid.NewGuid();
        var (token, hash) = TokenCodec.Generate(tenant);
        var parsed = TokenCodec.Parse(token)!;
        Assert.Equal(tenant, parsed.TenantId);
        Assert.Equal(hash, parsed.Hash);
        Assert.DoesNotContain(token, hash);
        Assert.NotEqual(token, TokenCodec.Generate(tenant).Token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("00000000000000000000000000000000.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void parse_rejects_malformed_tokens(string? token) => Assert.Null(TokenCodec.Parse(token));
}

public class PasswordHasherTests
{
    private static BcryptPasswordHasher Make() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:BcryptWorkFactor"] = "4" }).Build());

    [Fact]
    public void hash_verifies_only_the_right_password_and_uses_random_salt()
    {
        var h = Make();
        var hash = h.Hash("Correct-Horse-Battery-9");
        Assert.DoesNotContain("Correct-Horse", hash);
        Assert.True(h.Verify("Correct-Horse-Battery-9", hash));
        Assert.False(h.Verify("Correct-Horse-Battery-8", hash));
        Assert.NotEqual(hash, h.Hash("Correct-Horse-Battery-9"));
    }

    [Fact]
    public void verify_distinguishes_passwords_longer_than_72_bytes()
    {
        var h = Make();
        var prefix = new string('a', 80);
        var hash = h.Hash(prefix + "X");
        Assert.False(h.Verify(prefix + "Y", hash)); // без enhanced-режиму bcrypt усік би пароль до 72 байт
    }

    [Fact]
    public void verify_returns_false_for_corrupt_hash() => Assert.False(Make().Verify("x", "not-a-bcrypt-hash"));
}

public class JwtTests
{
    private static readonly string Key = Convert.ToBase64String(new byte[48].Select((_, i) => (byte)(i + 1)).ToArray());

    private static JwtSettings Settings() => JwtSettings.From(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:JwtSigningKey"] = Key }).Build());

    [Fact]
    public void issue_puts_tenant_role_and_specialist_in_claims_without_pii()
    {
        var user = new UserRecord(Guid.NewGuid(), Guid.NewGuid(), "a@b.c", "Name", "hash", Roles.Specialist, Guid.NewGuid(), true, 0, null, null);
        var token = new JwtTokenIssuer(Settings(), new AuthOptions()).Issue(user, DateTimeOffset.UtcNow);
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.Token);

        Assert.Equal(user.Id.ToString(), jwt.GetClaim("sub").Value);
        Assert.Equal(user.TenantId.ToString(), jwt.GetClaim("tenant_id").Value);
        Assert.Equal("specialist", jwt.GetClaim("role").Value);
        Assert.Equal(user.SpecialistId.ToString(), jwt.GetClaim("specialist_id").Value);
        Assert.DoesNotContain("a@b.c", token.Token);
        Assert.Equal("HS256", jwt.Alg);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("c2hvcnQ=")] // 5 байт
    public void jwtSettings_fail_closed_without_a_strong_key(string? key)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:JwtSigningKey"] = key }).Build();
        Assert.Throws<InvalidOperationException>(() => JwtSettings.From(cfg));
    }
}
