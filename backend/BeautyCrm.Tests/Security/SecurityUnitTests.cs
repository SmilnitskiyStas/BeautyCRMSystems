using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Tests.Security;

public class LoginAttemptTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2030, 1, 7, 8, 0, 0, TimeSpan.Zero);
    private static readonly AuthOptions Opt = new() { MaxFailedAttempts = 3, LockoutMinutes = 15 };

    [Fact]
    public void locks_after_max_failures_and_unlocks_after_lockout_period()
    {
        var t = new LoginAttemptTracker(Opt);
        var key = LoginAttemptTracker.KeyOf("salon", "ghost@x.test");
        for (var i = 0; i < 3; i++)
        {
            Assert.False(t.IsLocked(key, T0)); // перед кожною спробою ще не заблоковано
            t.RegisterFailure(key, T0);
        }
        Assert.True(t.IsLocked(key, T0.AddMinutes(14)));
        Assert.False(t.IsLocked(key, T0.AddMinutes(15))); // блокування спливло
        Assert.False(t.IsLocked(key, T0.AddMinutes(16)));
    }

    [Fact]
    public void counter_is_per_key_and_stale_failures_expire()
    {
        var t = new LoginAttemptTracker(Opt);
        var a = LoginAttemptTracker.KeyOf("salon", "a@x.test");
        var b = LoginAttemptTracker.KeyOf("salon", "b@x.test");
        var c = LoginAttemptTracker.KeyOf("other", "a@x.test");
        t.RegisterFailure(a, T0); t.RegisterFailure(a, T0); t.RegisterFailure(a, T0);
        Assert.True(t.IsLocked(a, T0));
        Assert.False(t.IsLocked(b, T0));
        Assert.False(t.IsLocked(c, T0));

        // дві старі помилки не накопичуються з новими після вікна
        t.RegisterFailure(b, T0); t.RegisterFailure(b, T0);
        Assert.False(t.IsLocked(b, T0.AddMinutes(20)));
        t.RegisterFailure(b, T0.AddMinutes(20));
        Assert.False(t.IsLocked(b, T0.AddMinutes(20)));
    }

    [Fact]
    public void key_hides_the_email_and_is_stable()
    {
        var k = LoginAttemptTracker.KeyOf("salon", "someone@x.test");
        Assert.DoesNotContain("someone", k, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(k, LoginAttemptTracker.KeyOf("salon", "someone@x.test"));
        Assert.NotEqual(k, LoginAttemptTracker.KeyOf("salon2", "someone@x.test"));
    }
}

public class ConfigListTests
{
    private static IConfiguration Cfg(params (string K, string? V)[] v) =>
        new ConfigurationBuilder().AddInMemoryCollection(v.ToDictionary(x => x.K, x => x.V)).Build();

    [Fact]
    public void reads_comma_separated_string_and_indexed_arrays_and_ignores_blanks()
    {
        Assert.Equal(["a", "b"], Cfg(("L", " a , b ,, ")).GetList("L"));
        Assert.Equal(["a", "b"], Cfg(("L:0", "a"), ("L:1", "b")).GetList("L"));
        Assert.Empty(Cfg(("L", "")).GetList("L"));
        Assert.Empty(Cfg().GetList("L"));
        Assert.Equal(["a"], Cfg(("L", "a;a")).GetList("L"));
    }
}
