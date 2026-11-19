using System.Net;
using BeautyCrm.Api.PublicBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Application.Features.BeautyPublicCatalog;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Tests.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Tests.PublicBooking;

/// <summary>Серверні знахідки security-рев'ю ADR-022 (M2, M3, L3, L6, L10); без БД.</summary>
public sealed class PublicHardeningFollowupTests
{
    // ---------- M2: координати округлюються до виклику store ----------

    private sealed class SpyStore : IPublicCatalogStore
    {
        public (double Lat, double Lng)? Seen;
        public Task<PublicLocationRefDto?> ResolveAsync(string code, CancellationToken ct) => Task.FromResult<PublicLocationRefDto?>(null);
        public Task<IReadOnlyList<PublicNearbyItemDto>> NearbyAsync(double lat, double lng, double radiusKm, int limit, CancellationToken ct)
        {
            Seen = (lat, lng);
            return Task.FromResult<IReadOnlyList<PublicNearbyItemDto>>([]);
        }
        public Task<IReadOnlyList<PublicLocationRefDto>> SearchByCityAsync(string city, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PublicLocationRefDto>>([]);
    }

    [Fact]
    public async Task nearby_rounds_coordinates_to_3_decimals_before_the_store()
    {
        var store = new SpyStore();
        var r = await new PublicCatalogService(store).NearbyAsync(new NearbyRequest(48.123456, -31.654321), CancellationToken.None);
        Assert.Null(r.Error);
        Assert.Equal((48.123, -31.654), store.Seen);
    }

    // ---------- L3: IPv6 /64 ----------

    [Fact]
    public void rate_limit_key_uses_slash_64_for_ipv6_and_keeps_ipv4()
    {
        var a = PublicRateLimit.KeyFor(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        var b = PublicRateLimit.KeyFor(IPAddress.Parse("2001:db8:1:2:1111:2222:3333:4444"));
        Assert.Equal(a, b);
        Assert.Equal("2001:db8:1:2::/64", a);
        Assert.NotEqual(a, PublicRateLimit.KeyFor(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("203.0.113.10", PublicRateLimit.KeyFor(IPAddress.Parse("203.0.113.10")));
        Assert.Equal("203.0.113.10", PublicRateLimit.KeyFor(IPAddress.Parse("::ffff:203.0.113.10")));
        Assert.Equal("unknown", PublicRateLimit.KeyFor(null));
    }

    // ---------- M3: розбіжні режими Protection ----------

    private static (IServiceCollection Services, Action Add) Setup(string env, params (string, string)[] cfg)
    {
        var dict = new Dictionary<string, string?> { ["PublicBooking:TokenKey"] = Convert.ToBase64String(new byte[32]) };
        foreach (var (k, v) in cfg) dict[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var services = new ServiceCollection();
        return (services, () => services.AddBeautyPublicBookingApi(config, new TestEnv(env)));
    }

    [Fact]
    public void uniform_modes_pass_in_production()
    {
        Setup("Production").Add();
        Setup("Production", ("PublicBooking:Protection:Web", "enforce"), ("PublicBooking:Protection:Ios", "enforce"), ("PublicBooking:Protection:Android", "enforce")).Add();
    }

    [Theory]
    [InlineData("PublicBooking:CaptchaRequired", "true")] // web enforce, мобільні none
    [InlineData("PublicBooking:Protection:Ios", "enforce")]
    public void weaker_client_path_fails_in_production_unless_explicitly_allowed(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(Setup("Production", (key, value)).Add);
        Assert.Contains("AllowWeakerClientPaths", ex.Message);
        Setup("Production", (key, value), ("PublicBooking:AllowWeakerClientPaths", "true")).Add();
    }

    [Fact]
    public void weaker_client_path_only_warns_in_development()
    {
        var (services, add) = Setup("Development", ("PublicBooking:CaptchaRequired", "true"));
        add();
        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        Assert.Single(hosted);

        var lines = new List<(LogLevel, string)>();
        var sp = new ServiceCollection().AddLogging(l => l.AddProvider(new ListProvider(lines))).BuildServiceProvider();
        var svc = (IHostedService)hosted[0].ImplementationFactory!(sp);
        svc.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var warn = Assert.Single(lines);
        Assert.Equal(LogLevel.Warning, warn.Item1);
        Assert.Contains("weakest accepted path", warn.Item2);

        var (s2, add2) = Setup("Development", ("PublicBooking:CaptchaRequired", "true"), ("PublicBooking:AllowWeakerClientPaths", "true"));
        add2();
        Assert.DoesNotContain(s2, d => d.ServiceType == typeof(IHostedService));
    }

    private sealed class ListProvider(List<(LogLevel, string)> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add((logLevel, formatter(state, exception)));
    }

    // ---------- L6: зарезервовані slug-и ----------

    [Fact]
    public void integrity_is_a_reserved_slug_and_not_resolvable()
    {
        Assert.Contains("integrity", PublicTenantResolver.ReservedSlugs);
        Assert.All(new[] { "resolve", "locations", "integrity" }, s => Assert.Contains(s, PublicTenantResolver.ReservedSlugs));
    }

    // ---------- L10: повтор при колізії public_code ----------

    private static DbUpdateException Collision(string constraint = "ux_beauty_locations_public_code", string state = PostgresErrorCodes.UniqueViolation) =>
        new("x", new PostgresException("dup", "ERROR", "ERROR", state, constraintName: constraint));

    [Fact]
    public async Task public_code_collision_is_retried_up_to_5_attempts()
    {
        var calls = 0;
        var result = await EfBeautyStore.RetryOnPublicCodeCollisionAsync(n => { calls++; return n < 3 ? throw Collision() : Task.FromResult("ok"); });
        Assert.Equal(("ok", 3), (result, calls));

        calls = 0;
        await Assert.ThrowsAsync<DbUpdateException>(() => EfBeautyStore.RetryOnPublicCodeCollisionAsync<string>(_ => { calls++; throw Collision(); }));
        Assert.Equal(EfBeautyStore.MaxPublicCodeAttempts, calls);
        Assert.Equal(5, calls);
    }

    [Theory]
    [InlineData("ux_beauty_locations_tenant_name", PostgresErrorCodes.UniqueViolation)]
    [InlineData("ux_beauty_locations_public_code", PostgresErrorCodes.ForeignKeyViolation)]
    public async Task other_database_errors_are_not_retried(string constraint, string state)
    {
        var calls = 0;
        await Assert.ThrowsAsync<DbUpdateException>(() => EfBeautyStore.RetryOnPublicCodeCollisionAsync<string>(_ => { calls++; throw Collision(constraint, state); }));
        Assert.Equal(1, calls);
    }
}
