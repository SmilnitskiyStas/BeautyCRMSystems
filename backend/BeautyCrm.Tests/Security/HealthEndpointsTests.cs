using System.Net;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace BeautyCrm.Tests.Security;

/// <summary>TASK-800: /healthz і /readyz анонімні (Production, FallbackPolicy вимагає JWT), без деталей, readyz залежить від БД.</summary>
public sealed class HealthEndpointsTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    [SkippableFact]
    public async Task healthz_is_anonymous_and_minimal()
    {
        NeedDb();
        var r = await fx.Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", await r.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task readyz_is_anonymous_ok_with_database_and_leaks_nothing()
    {
        NeedDb();
        var r = await fx.Client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task readyz_returns_503_without_details_when_database_is_unreachable_and_healthz_stays_up()
    {
        const string secretMarker = "never-leak-this-password";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["ConnectionStrings:Default"] =
            $"Host=127.0.0.1;Port=1;Database=leakdb;Username=leakuser;Password={secretMarker};Timeout=2";
        await using var app = builder.Build();
        app.MapBeautyHealth();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var ready = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.Equal("unavailable", body);
        Assert.DoesNotContain(secretMarker, body);
        Assert.DoesNotContain("leakdb", body);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task readyz_returns_503_when_connection_string_is_missing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapBeautyHealth();
        await app.StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/readyz")).StatusCode);
        await app.StopAsync();
    }
}
