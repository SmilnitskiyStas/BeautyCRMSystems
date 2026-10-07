using System.Security.Claims;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features;
using BeautyCrm.Application.Features.BeautyChannels;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeautyCrm.Tests.Beauty;

public class TenantMiddlewareTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static async Task<TenantContext> Run(HttpContext http, bool allowHeader, string env = "Production")
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Beauty:AllowTenantHeader"] = allowHeader.ToString() }).Build();
        var tenant = new TenantContext();
        await new TenantMiddleware(_ => Task.CompletedTask, cfg, new FakeEnv(env)).InvokeAsync(http, tenant);
        return tenant;
    }

    [Fact]
    public async Task invoke_sets_tenant_from_claim()
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", Tenant.ToString())], "t")) };
        Assert.Equal(Tenant, (await Run(http, false)).TenantId);
    }

    [Fact]
    public async Task invoke_ignores_header_when_not_allowed()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Tenant-Id"] = Tenant.ToString();
        Assert.Null((await Run(http, false)).TenantId);
    }

    [Fact]
    public async Task invoke_reads_header_only_in_development_and_ignores_garbage()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Tenant-Id"] = Tenant.ToString();
        Assert.Equal(Tenant, (await Run(http, true, "Development")).TenantId);

        var bad = new DefaultHttpContext();
        bad.Request.Headers["X-Tenant-Id"] = "not-a-guid";
        Assert.Null((await Run(bad, true, "Development")).TenantId);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task invoke_ignores_header_outside_development_even_when_config_allows(string env)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Tenant-Id"] = Tenant.ToString();
        Assert.Null((await Run(http, true, env)).TenantId);
    }

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

public class RequireModuleTests
{
    private static async Task<(IActionResult? Result, bool NextCalled)> Run(Guid? tenantId, string? enabledModules)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            enabledModules is null ? [] : new Dictionary<string, string?> { ["Beauty:EnabledModules:0"] = enabledModules }).Build();
        var services = new ServiceCollection()
            .AddSingleton<ITenantContext>(tenantId is null ? new TenantContext() : new TenantContext(tenantId.Value))
            .AddSingleton<ITenantModuleProvider>(new ConfigTenantModuleProvider(cfg)).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var ctx = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [], new Dictionary<string, object?>(), new object());
        var called = false;
        await new RequireModuleAttribute("beauty_booking").OnActionExecutionAsync(ctx, () =>
        {
            called = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        return (ctx.Result, called);
    }

    [Fact]
    public async Task onActionExecution_returns_401_without_tenant()
    {
        var (result, called) = await Run(null, null);
        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.False(called);
    }

    [Fact]
    public async Task onActionExecution_returns_403_when_module_disabled()
    {
        var (result, called) = await Run(Guid.NewGuid(), "beauty_clients");
        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task onActionExecution_passes_when_module_enabled_or_unconfigured()
    {
        Assert.True((await Run(Guid.NewGuid(), "beauty_booking")).NextCalled);
        Assert.True((await Run(Guid.NewGuid(), null)).NextCalled);
    }
}

public class BeautyDiTests
{
    /// <summary>Усі зони реєструються разом: порти Wave A реалізовані, scoped-залежності не потрапляють у singleton.</summary>
    [Fact]
    public void addBeauty_resolves_all_services_with_scope_validation()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=x;Password=x",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(cfg);
        services.AddBeautyData().AddBeautyChannels().AddBeautyAi().AddBeautyApplication();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        foreach (var type in new[]
                 {
                     typeof(BeautyCrm.Application.Features.BeautyBooking.BookingService),
                     typeof(BeautyCrm.Application.Features.BeautyBooking.CancellationService),
                     typeof(BeautyCrm.Application.Features.BeautyBooking.IPaymentService),
                     typeof(IChannelCredentialsProvider), typeof(IChannelMessageRepository), typeof(IChannelQueue),
                     typeof(WebhookIngestService), typeof(OutboundDispatcher), typeof(IAiActionJournal), typeof(IAiActionReader),
                     typeof(AiToolExecutor), typeof(BeautyAssistant),
                     typeof(BeautyCrm.Application.Features.BeautyClients.ClientService),
                     typeof(BeautyCrm.Application.Features.BeautyAnalytics.AnalyticsService),
                 })
            Assert.NotNull(sp.GetRequiredService(type));
    }
}

public class ChannelSettingsTests
{
    private sealed class MemStore : IChannelSettingsStore
    {
        public ChannelRecord? Saved;
        public Task<IReadOnlyList<ChannelRecord>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChannelRecord>>(Saved is null ? [] : [Saved]);
        public Task<ChannelRecord?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Saved);
        public Task<bool> LocationExistsAsync(Guid id, CancellationToken ct) => Task.FromResult(true);
        public Task<ChannelRecord> UpsertAsync(Guid id, UpsertChannelRequest r, CancellationToken ct) =>
            Task.FromResult(Saved = new ChannelRecord(id, r.Type ?? Saved!.Type, r.Name, r.LocationId, r.IsActive,
                r.Token is { Length: >= 4 } ? r.Token[^4..] : null, r.WebhookSecret is not null, r.SettingsJson));
    }

    [Fact]
    public async Task upsert_never_returns_secrets_only_mask_and_flag()
    {
        var sut = new ChannelSettingsService(new MemStore());
        var id = Guid.NewGuid();
        var r = await sut.UpsertAsync(id, new UpsertChannelRequest("telegram", "Bot", null, true, "123456:SECRETTOKEN-ABCD", "whsecret", null), default);
        Assert.Equal("********ABCD", r.Value!.MaskedToken);
        Assert.True(r.Value.HasWebhookSecret);
        Assert.DoesNotContain("SECRETTOKEN", System.Text.Json.JsonSerializer.Serialize(r.Value));
        Assert.DoesNotContain("whsecret", System.Text.Json.JsonSerializer.Serialize(r.Value));
        Assert.Equal($"/api/beauty/webhooks/telegram/{id}", r.Value.WebhookPath);
    }

    [Fact]
    public async Task upsert_validates_type_and_settings_json()
    {
        var sut = new ChannelSettingsService(new MemStore());
        Assert.Equal("invalid_type", (await sut.UpsertAsync(Guid.NewGuid(), new UpsertChannelRequest("icq", "x", null, true, null, null, null), default)).Error!.Code);
        Assert.Equal("invalid_settings", (await sut.UpsertAsync(Guid.NewGuid(), new UpsertChannelRequest("viber", "x", null, true, null, null, "{oops"), default)).Error!.Code);
    }

    [Fact]
    public void secretProtector_roundtrips_and_fails_closed_without_key()
    {
        var key = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray());
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Channels:EncryptionKey"] = key }).Build();
        var p = new AesGcmSecretProtector(cfg);
        var enc = p.Protect("{\"token\":\"abc\"}");
        Assert.DoesNotContain("abc", enc);
        Assert.Equal("{\"token\":\"abc\"}", p.Unprotect(enc));
        Assert.NotEqual(enc, p.Protect("{\"token\":\"abc\"}")); // випадковий nonce

        Assert.Throws<InvalidOperationException>(() =>
            new AesGcmSecretProtector(new ConfigurationBuilder().Build()).Protect("x"));
    }

    [Fact]
    public void ambientCredentials_are_scoped_to_current_channel_only()
    {
        var ctx = new ChannelRequestContext();
        var provider = new AmbientChannelCredentialsProvider(ctx);
        Assert.Null(provider.Get("telegram"));
        using (ctx.Begin(new ResolvedChannel(Guid.NewGuid(), Guid.NewGuid(), "telegram", new ChannelCredentials("tok", "sec"))))
        {
            Assert.Equal("sec", provider.Get("telegram")!.WebhookSecret);
            Assert.Null(provider.Get("instagram"));
        }
        Assert.Null(provider.Get("telegram"));
    }
}
