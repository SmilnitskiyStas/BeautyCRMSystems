using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Tests.Security;

// LoginAttemptTrackerTests видалено (ADR-024, §31.2.1): in-memory LoginAttemptTracker замінено лічильником у БД (auth_login_attempts);
// його поведінку покриває Auth/GlobalIdentityLoginTests.

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
