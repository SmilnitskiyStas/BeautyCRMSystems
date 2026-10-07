using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BeautyCrm.Tests.Auth;

/// <summary>Повний API (WebApplicationFactory) поверх реального PostgreSQL з роллю без BYPASSRLS (PostgresRlsFixture).</summary>
public sealed class AuthApiFixture : IAsyncLifetime
{
    public const string PlatformKey = "test-platform-key-0123456789-abcdefghijklmnop";
    public const string Password = "Correct-Horse-Battery-9";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PostgresRlsFixture Db { get; } = new();
    public WebApplicationFactory<Program>? Factory { get; private set; }
    public HttpClient Client { get; private set; } = null!;
    public string? SkipReason => Db.SkipReason;

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        if (SkipReason is not null) return;
        Factory = CreateFactory(permitLimit: 1000);
        Client = Factory.CreateClient();
    }

    public WebApplicationFactory<Program> CreateFactory(int permitLimit) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production"); // доводить, що X-Tenant-Id поза Development не працює
            b.UseSetting("ConnectionStrings:Default", Db.AppConnectionString);
            b.UseSetting("Auth:JwtSigningKey", Convert.ToBase64String(Enumerable.Range(1, 48).Select(i => (byte)i).ToArray()));
            b.UseSetting("Auth:PlatformKey", PlatformKey);
            b.UseSetting("Auth:BcryptWorkFactor", "4");
            b.UseSetting("Auth:RateLimit:PermitLimit", permitLimit.ToString());
            b.UseSetting("Beauty:AllowTenantHeader", "true");
        });

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        Factory?.Dispose();
        await Db.DisposeAsync();
    }

    // ---------- helpers ----------

    public record TenantSeed(Guid TenantId, string Slug, Guid OwnerId, string OwnerEmail);

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    public static HttpRequestMessage Req(HttpMethod method, string url, object? body = null, string? bearer = null, string? platformKey = null)
    {
        var r = new HttpRequestMessage(method, url);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        if (bearer is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (platformKey is not null) r.Headers.Add("X-Platform-Key", platformKey);
        return r;
    }

    public Task<HttpResponseMessage> Send(HttpMethod m, string url, object? body = null, string? bearer = null, string? platformKey = null,
        HttpClient? client = null) =>
        (client ?? Client).SendAsync(Req(m, url, body, bearer, platformKey));

    public static async Task<T> Read<T>(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<T>(Json))!;

    public async Task<TenantSeed> CreateTenantAsync(string[]? modules = null)
    {
        var slug = Unique("salon");
        var email = $"owner@{slug}.test";
        var r = await Send(HttpMethod.Post, "/api/platform/tenants", new
        {
            name = $"Salon {slug}", slug, modules,
            owner = new { email, fullName = "Owner", password = Password },
        }, platformKey: PlatformKey);
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        var dto = await Read<TenantCreatedDto>(r);
        return new TenantSeed(dto.TenantId, slug, dto.OwnerUserId, email);
    }

    public async Task<TokenResponse> LoginAsync(string slug, string email, string password = Password)
    {
        var r = await Send(HttpMethod.Post, "/api/auth/login", new { tenant = slug, email, password });
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        return await Read<TokenResponse>(r);
    }

    /// <summary>owner запрошує -> адресат приймає -> логін. Повертає токени нового користувача.</summary>
    public async Task<TokenResponse> InviteAndLoginAsync(TenantSeed t, string ownerToken, string role, Guid? specialistId = null)
    {
        var email = $"{role}-{Guid.NewGuid():N}"[..20] + "@" + t.Slug + ".test";
        var created = await Send(HttpMethod.Post, "/api/invites", new { email, role, specialistId }, ownerToken);
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        var invite = await Read<InviteCreatedDto>(created);
        var accepted = await Send(HttpMethod.Post, "/api/auth/invites/accept", new { token = invite.Token, fullName = role, password = Password });
        Assert.Equal(System.Net.HttpStatusCode.Created, accepted.StatusCode);
        return await LoginAsync(t.Slug, email);
    }

    public sealed record BeautySeed(Guid LocationId, Guid SpecialistA, Guid SpecialistB, Guid AppointmentA, Guid AppointmentB, Guid ServiceId, Guid ClientId);

    /// <summary>Два спеціалісти й по одному майбутньому запису (під роллю app + RLS tenant).</summary>
    public async Task<BeautySeed> SeedBeautyAsync(Guid tenantId)
    {
        await using var ctx = Db.CreateContext(tenantId);
        var location = new Location { Name = "Main", Timezone = "Europe/Kyiv" };
        var a = new Specialist { FullName = "Master A" };
        var b = new Specialist { FullName = "Master B" };
        var service = new Service { Name = "Haircut", DurationMinutes = 60 };
        var client = new Client { FullName = "Client", Phone = "+380000000000" };
        ctx.AddRange(location, a, b, service, client);
        await ctx.SaveChangesAsync();

        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(10), TimeSpan.Zero);
        Appointment Make(Specialist s, int hour) => new()
        {
            LocationId = location.Id, SpecialistId = s.Id, ServiceId = service.Id, ClientId = client.Id,
            StartsAt = start.AddHours(hour), DurationMinutes = 60, Status = AppointmentStatus.Confirmed,
            Source = AppointmentSource.Admin, PriceOriginal = 500m, PriceFinal = 500m,
        };
        var apptA = Make(a, 0);
        var apptB = Make(b, 0);
        ctx.AddRange(apptA, apptB);
        await ctx.SaveChangesAsync();
        return new BeautySeed(location.Id, a.Id, b.Id, apptA.Id, apptB.Id, service.Id, client.Id);
    }
}
