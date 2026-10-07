using System.Net;
using System.Text.Json;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Regression;

/// <summary>
/// TASK-681: спільні помічники наскрізних регресійних тестів (повний HTTP-конвеєр + реальний PostgreSQL з RLS).
/// Довідники, для яких немає API (заклад, майстер, графік), створюються через DbContext під роллю app + RLS tenant.
/// </summary>
public sealed class RegressionHarness(AuthApiFixture fx)
{
    public const string AllDays = """
        {"mon":[{"from":"09:00","to":"18:00"}],"tue":[{"from":"09:00","to":"18:00"}],"wed":[{"from":"09:00","to":"18:00"}],
         "thu":[{"from":"09:00","to":"18:00"}],"fri":[{"from":"09:00","to":"18:00"}],"sat":[{"from":"09:00","to":"18:00"}],
         "sun":[{"from":"09:00","to":"18:00"}]}
        """;

    public sealed record Salon(AuthApiFixture.TenantSeed Tenant, string OwnerToken, Guid LocationId, Guid SpecialistId, Guid ServiceId);

    public static string SlotDate(int daysAhead = 3) => DateTime.UtcNow.Date.AddDays(daysAhead).ToString("yyyy-MM-dd");

    /// <summary>
/// Timezone за замовчуванням UTC: для будь-якої не-UTC зони GET /slots і POST /appointments падають з 500
/// (Npgsql не приймає DateTimeOffset з ненульовим offset) - див. BUG-681-1 у BugReproTests.
/// </summary>
    public async Task<(Guid LocationId, Guid SpecialistId)> AddLocationAsync(Guid tenantId, Guid? existingSpecialist = null, string timezone = "UTC")
    {
        await using var ctx = fx.Db.CreateContext(tenantId);
        var location = new Location { Name = "Loc-" + Guid.NewGuid().ToString("N")[..6], Timezone = timezone };
        var specialist = existingSpecialist is null ? new Specialist { FullName = "Master" } : null;
        ctx.Add(location);
        if (specialist is not null) ctx.Add(specialist);
        await ctx.SaveChangesAsync();
        var specialistId = existingSpecialist ?? specialist!.Id;
        ctx.Add(new SpecialistLocation { SpecialistId = specialistId, LocationId = location.Id, WorkingHours = AllDays });
        await ctx.SaveChangesAsync();
        return (location.Id, specialistId);
    }

    /// <summary>Новий tenant + owner + заклад + майстер + послуга (60 хв) з мережевою ціною.</summary>
    public async Task<Salon> CreateSalonAsync(decimal networkPrice = 1000m, int durationMinutes = 60, string timezone = "UTC")
    {
        var tenant = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(tenant.Slug, tenant.OwnerEmail);
        var (loc, spec) = await AddLocationAsync(tenant.TenantId, timezone: timezone);
        var created = await fx.Send(HttpMethod.Post, "/api/beauty/services",
            new { name = "Haircut", durationMinutes }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var serviceId = (await Read<JsonElement>(created)).GetProperty("id").GetGuid();
        var price = await fx.Send(HttpMethod.Put, $"/api/beauty/services/{serviceId}/prices",
            new { locationId = (Guid?)null, price = networkPrice }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, price.StatusCode);
        return new Salon(tenant, owner.AccessToken, loc, spec, serviceId);
    }

    public async Task<List<JsonElement>> SlotsAsync(Salon s, string date, Guid? locationId = null, Guid? specialistId = null)
    {
        var r = await fx.Send(HttpMethod.Get,
            $"/api/beauty/slots?locationId={locationId ?? s.LocationId}&serviceId={s.ServiceId}&date={date}&specialistId={specialistId ?? s.SpecialistId}",
            bearer: s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Read<JsonElement>(r)).EnumerateArray().ToList();
    }

    public static object Booking(Salon s, DateTimeOffset startsAt, string reminder = "none", string payment = "cash",
        Guid? locationId = null, string phone = "+380501112233") => new
    {
        locationId = locationId ?? s.LocationId, specialistId = s.SpecialistId, serviceId = s.ServiceId, startsAt,
        client = new { name = "Anna", phone }, reminder, paymentMethod = payment,
    };

    public Task<HttpResponseMessage> BookAsync(Salon s, DateTimeOffset startsAt, string reminder = "none", string payment = "cash",
        Guid? locationId = null) =>
        fx.Send(HttpMethod.Post, "/api/beauty/appointments", Booking(s, startsAt, reminder, payment, locationId), s.OwnerToken);

    public static async Task<string> CodeAsync(HttpResponseMessage r) =>
        (await Read<JsonElement>(r)).GetProperty("code").GetString()!;
}
