using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Regression;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-690: прогалини адмінки — GET /locations, timezone у слотах і записах, POST/DELETE /specialists/{id}/locations,
/// GET /overview. Повний HTTP-конвеєр на PostgreSQL (роль без BYPASSRLS); без БД пропускаються.
/// </summary>
public sealed class AdminGapsTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private readonly RegressionHarness _h = new(fx);

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private static DateTimeOffset At(int ahead, int hour = 10) => new(DateTime.UtcNow.Date.AddDays(ahead).AddHours(hour), TimeSpan.Zero);
    private static string Day(int ahead) => DateTime.UtcNow.Date.AddDays(ahead).ToString("yyyy-MM-dd");

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);

    private sealed record Ctx(RegressionHarness.Salon Salon, string Admin, string Specialist)
    {
        public string Owner => Salon.OwnerToken;
    }

    private async Task<Ctx> NewCtxAsync(string timezone = "UTC")
    {
        var salon = await _h.CreateSalonAsync(timezone: timezone);
        var admin = await fx.InviteAndLoginAsync(salon.Tenant, salon.OwnerToken, Roles.Admin);
        var specialist = await fx.InviteAndLoginAsync(salon.Tenant, salon.OwnerToken, Roles.Specialist, salon.SpecialistId);
        return new Ctx(salon, admin.AccessToken, specialist.AccessToken);
    }

    // ================= GET /locations =================

    [SkippableFact]
    public async Task locations_lists_tenant_locations_for_every_staff_role_with_timezone_and_address()
    {
        NeedDb();
        var c = await NewCtxAsync("Europe/Kyiv");
        await using (var ctx = fx.Db.CreateContext(c.Salon.Tenant.TenantId))
        {
            var withAddress = await ctx.Locations.SingleAsync();
            withAddress.Address = "Khreshchatyk 1";
            await ctx.SaveChangesAsync();
        }
        var other = await _h.CreateSalonAsync(); // чужий tenant: не потрапляє у відповідь

        foreach (var token in new[] { c.Owner, c.Admin, c.Specialist })
        {
            var r = await fx.Send(HttpMethod.Get, "/api/beauty/locations", bearer: token);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var item = Assert.Single((await J(r)).EnumerateArray());
            Assert.Equal(c.Salon.LocationId, item.GetProperty("id").GetGuid());
            Assert.StartsWith("Loc-", item.GetProperty("name").GetString());
            Assert.Equal("Khreshchatyk 1", item.GetProperty("address").GetString());
            Assert.Equal("Europe/Kyiv", item.GetProperty("timezone").GetString());
            Assert.NotEqual(other.LocationId, item.GetProperty("id").GetGuid());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Get, "/api/beauty/locations")).StatusCode);
    }

    // ================= timezone у слотах і записах =================

    [SkippableFact]
    public async Task slots_and_appointments_carry_the_location_timezone()
    {
        NeedDb();
        var c = await NewCtxAsync("Europe/Kyiv");
        var slots = await _h.SlotsAsync(c.Salon, Day(3));
        Assert.NotEmpty(slots);
        Assert.All(slots, s => Assert.Equal("Europe/Kyiv", s.GetProperty("timezone").GetString()));

        var booked = await _h.BookAsync(c.Salon, slots[0].GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var created = await J(booked);
        Assert.Equal("Europe/Kyiv", created.GetProperty("timezone").GetString());
        var id = created.GetProperty("id").GetGuid();

        var get = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/appointments/{id}", bearer: c.Owner));
        Assert.Equal("Europe/Kyiv", get.GetProperty("timezone").GetString());
        var from = Uri.EscapeDataString(At(2, 0).ToString("o"));
        var to = Uri.EscapeDataString(At(5, 0).ToString("o"));
        var list = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/appointments?from={from}&to={to}", bearer: c.Owner));
        Assert.Equal("Europe/Kyiv", Assert.Single(list.EnumerateArray()).GetProperty("timezone").GetString());
    }

    // ================= POST/DELETE /specialists/{id}/locations =================

    private Task<HttpResponseMessage> Assign(string token, Guid specialistId, Guid locationId, object? hours = null) =>
        fx.Send(HttpMethod.Post, $"/api/beauty/specialists/{specialistId}/locations", new { locationId, workingHours = hours }, token);

    private Task<HttpResponseMessage> Remove(string token, Guid specialistId, Guid locationId) =>
        fx.Send(HttpMethod.Delete, $"/api/beauty/specialists/{specialistId}/locations/{locationId}", bearer: token);

    private async Task<Guid> NewLocationAsync(Ctx c, string timezone = "UTC")
    {
        await using var ctx = fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        var location = new Location { Name = "Second-" + Guid.NewGuid().ToString("N")[..6], Timezone = timezone };
        ctx.Add(location);
        await ctx.SaveChangesAsync();
        return location.Id;
    }

    private static JsonElement? LocationOf(JsonElement specialist, Guid locationId) =>
        specialist.GetProperty("locations").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(l => l!.Value.GetProperty("locationId").GetGuid() == locationId);

    private static JsonElement AllDays() => JsonSerializer.Deserialize<JsonElement>(RegressionHarness.AllDays);

    [SkippableFact]
    public async Task assign_location_adds_schedule_and_opens_slots_there()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var second = await NewLocationAsync(c);

        var r = await Assign(c.Admin, c.Salon.SpecialistId, second, AllDays());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var loc = LocationOf(await J(r), second)!.Value;
        Assert.True(loc.GetProperty("isActive").GetBoolean());
        Assert.Equal("09:00", loc.GetProperty("workingHours").GetProperty("mon")[0].GetProperty("from").GetString());
        Assert.NotEmpty(await _h.SlotsAsync(c.Salon, Day(3), locationId: second));

        // повторне додавання -> 409; невідомий заклад -> 404; хибний графік -> 422
        var again = await Assign(c.Owner, c.Salon.SpecialistId, second, AllDays());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("location_already_assigned", await RegressionHarness.CodeAsync(again));
        var missing = await Assign(c.Owner, c.Salon.SpecialistId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("location_not_found", await RegressionHarness.CodeAsync(missing));
        var third = await NewLocationAsync(c);
        var bad = await Assign(c.Owner, c.Salon.SpecialistId, third, new { mon = new[] { new { from = "18:00", to = "09:00" } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("invalid_working_hours", await RegressionHarness.CodeAsync(bad));
        Assert.Null(LocationOf(await J(await fx.Send(HttpMethod.Get, $"/api/beauty/specialists/{c.Salon.SpecialistId}", bearer: c.Owner)), third));
    }

    [SkippableFact]
    public async Task assign_without_hours_links_the_location_but_gives_no_slots_until_schedule_is_set()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var second = await NewLocationAsync(c);
        var r = await Assign(c.Owner, c.Salon.SpecialistId, second);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(JsonValueKind.Null, LocationOf(await J(r), second)!.Value.GetProperty("workingHours").ValueKind);
        Assert.Empty(await _h.SlotsAsync(c.Salon, Day(3), locationId: second));

        var schedule = await fx.Send(HttpMethod.Put, $"/api/beauty/specialists/{c.Salon.SpecialistId}/schedule",
            new { locationId = second, workingHours = AllDays() }, c.Owner);
        Assert.Equal(HttpStatusCode.OK, schedule.StatusCode);
        Assert.NotEmpty(await _h.SlotsAsync(c.Salon, Day(3), locationId: second));
    }

    [SkippableFact]
    public async Task remove_location_is_blocked_by_future_active_appointments_and_allowed_otherwise()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var booked = await _h.BookAsync(c.Salon, At(3, 10));
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var apptId = (await J(booked)).GetProperty("id").GetGuid();

        var blocked = await Remove(c.Admin, c.Salon.SpecialistId, c.Salon.LocationId);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("has_future_appointments", await RegressionHarness.CodeAsync(blocked));
        var still = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/specialists/{c.Salon.SpecialistId}", bearer: c.Owner));
        Assert.True(LocationOf(still, c.Salon.LocationId)!.Value.GetProperty("isActive").GetBoolean());

        // скасований майбутній запис не блокує
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{apptId}/cancel", bearer: c.Owner)).StatusCode);
        var removed = await Remove(c.Admin, c.Salon.SpecialistId, c.Salon.LocationId);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var after = LocationOf(await J(removed), c.Salon.LocationId)!.Value;
        Assert.False(after.GetProperty("isActive").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("workingHours").ValueKind); // графік збережено

        // слотів у закладі більше немає, запис -> specialist_not_at_location; повторне видалення -> 404
        Assert.Empty(await _h.SlotsAsync(c.Salon, Day(3)));
        var rebook = await _h.BookAsync(c.Salon, At(3, 12));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rebook.StatusCode);
        var twice = await Remove(c.Owner, c.Salon.SpecialistId, c.Salon.LocationId);
        Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode);
        Assert.Equal("location_not_assigned", await RegressionHarness.CodeAsync(twice));

        // повторне додавання без графіка реактивує збережений графік
        var back = await Assign(c.Owner, c.Salon.SpecialistId, c.Salon.LocationId);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.True(LocationOf(await J(back), c.Salon.LocationId)!.Value.GetProperty("isActive").GetBoolean());
        Assert.NotEmpty(await _h.SlotsAsync(c.Salon, Day(3)));
    }

    [SkippableFact]
    public async Task remove_location_ignores_past_completed_and_other_location_appointments()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var (otherLoc, _) = await _h.AddLocationAsync(c.Salon.Tenant.TenantId, c.Salon.SpecialistId);
        await using (var ctx = fx.Db.CreateContext(c.Salon.Tenant.TenantId))
        {
            var client = new Client { FullName = "C", Phone = "+380000000001" };
            ctx.Add(client);
            await ctx.SaveChangesAsync();
            Appointment Make(Guid location, DateTimeOffset start, AppointmentStatus status) => new()
            {
                LocationId = location, SpecialistId = c.Salon.SpecialistId, ServiceId = c.Salon.ServiceId, ClientId = client.Id,
                StartsAt = start, DurationMinutes = 60, Status = status, Source = AppointmentSource.Admin, PriceOriginal = 100m, PriceFinal = 100m,
            };
            ctx.Add(Make(c.Salon.LocationId, At(-3, 10), AppointmentStatus.Completed));  // минуле
            ctx.Add(Make(c.Salon.LocationId, At(4, 10), AppointmentStatus.NoShow));      // не активний статус
            ctx.Add(Make(otherLoc, At(4, 12), AppointmentStatus.Confirmed));             // інший заклад
            await ctx.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await Remove(c.Owner, c.Salon.SpecialistId, c.Salon.LocationId)).StatusCode);
        // а в закладі з майбутнім активним записом - ні
        Assert.Equal(HttpStatusCode.Conflict, (await Remove(c.Owner, c.Salon.SpecialistId, otherLoc)).StatusCode);
    }

    [SkippableFact]
    public async Task location_management_requires_manager_role_and_tenant_scope()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var second = await NewLocationAsync(c);
        Assert.Equal(HttpStatusCode.Forbidden, (await Assign(c.Specialist, c.Salon.SpecialistId, second, AllDays())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Remove(c.Specialist, c.Salon.SpecialistId, c.Salon.LocationId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Post, $"/api/beauty/specialists/{c.Salon.SpecialistId}/locations", new { locationId = second })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Delete, $"/api/beauty/specialists/{c.Salon.SpecialistId}/locations/{c.Salon.LocationId}")).StatusCode);

        var stranger = await _h.CreateSalonAsync();
        var foreignSpecialist = await Assign(stranger.OwnerToken, c.Salon.SpecialistId, stranger.LocationId, AllDays());
        Assert.Equal(HttpStatusCode.NotFound, foreignSpecialist.StatusCode);
        Assert.Equal("specialist_not_found", await RegressionHarness.CodeAsync(foreignSpecialist));
        Assert.Equal(HttpStatusCode.NotFound, (await Remove(stranger.OwnerToken, c.Salon.SpecialistId, c.Salon.LocationId)).StatusCode);
        // чужий заклад для свого майстра -> location_not_found
        var foreignLocation = await Assign(c.Owner, c.Salon.SpecialistId, stranger.LocationId, AllDays());
        Assert.Equal(HttpStatusCode.NotFound, foreignLocation.StatusCode);
        Assert.Equal("location_not_found", await RegressionHarness.CodeAsync(foreignLocation));
        // без змін: заклад свого майстра лишився активним
        Assert.True(LocationOf(await J(await fx.Send(HttpMethod.Get, $"/api/beauty/specialists/{c.Salon.SpecialistId}", bearer: c.Owner)),
            c.Salon.LocationId)!.Value.GetProperty("isActive").GetBoolean());
    }

    [SkippableFact]
    public async Task parallel_assign_of_the_same_location_succeeds_once()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var second = await NewLocationAsync(c);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Assign(c.Owner, c.Salon.SpecialistId, second, AllDays())));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        await using var ctx = fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        Assert.Equal(1, await ctx.SpecialistLocations.CountAsync(x => x.SpecialistId == c.Salon.SpecialistId && x.LocationId == second));
    }

    // ================= GET /overview =================

    private async Task SeedDayAsync(Ctx c, int ahead)
    {
        await using var ctx = fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        var clientA = new Client { FullName = "A", Phone = "+380000000011" };
        var clientB = new Client { FullName = "B", Phone = "+380000000012" };
        ctx.AddRange(clientA, clientB);
        await ctx.SaveChangesAsync();
        Appointment Make(Client client, int hour, AppointmentStatus status, decimal price) => new()
        {
            LocationId = c.Salon.LocationId, SpecialistId = c.Salon.SpecialistId, ServiceId = c.Salon.ServiceId, ClientId = client.Id,
            StartsAt = At(ahead, hour), DurationMinutes = 60, Status = status, Source = AppointmentSource.Admin, PriceOriginal = price, PriceFinal = price,
        };
        ctx.AddRange(
            Make(clientA, 10, AppointmentStatus.Completed, 700m),
            Make(clientB, 12, AppointmentStatus.Confirmed, 500m),
            Make(clientA, 14, AppointmentStatus.Cancelled, 300m),
            Make(clientB, 16, AppointmentStatus.NoShow, 400m));
        await ctx.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task overview_returns_day_appointments_and_kpi()
    {
        NeedDb();
        var c = await NewCtxAsync();
        await SeedDayAsync(c, ahead: 3);

        foreach (var token in new[] { c.Owner, c.Admin })
        {
            var r = await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={Day(3)}", bearer: token);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var o = await J(r);
            Assert.Equal(Day(3), o.GetProperty("date").GetString());
            Assert.Equal("UTC", o.GetProperty("timezone").GetString());
            var statuses = o.GetProperty("appointments").EnumerateArray().Select(a => a.GetProperty("status").GetString()).ToList();
            Assert.Equal(["completed", "confirmed", "cancelled", "no_show"], statuses); // за часом
            var kpi = o.GetProperty("kpi");
            Assert.Equal(3, kpi.GetProperty("appointmentsCount").GetInt32());   // без скасованих
            Assert.Equal(700m, kpi.GetProperty("revenue").GetDecimal());        // лише completed
            Assert.Equal(0, kpi.GetProperty("newClients").GetInt32());          // клієнти створені сьогодні, не в цей день
            // робочий день 09-18, послуга 60 хв; слот блокують лише completed 10-11 і confirmed 12-13 (cancelled/no_show - ні):
            // вільні проміжки 9-10 (1), 11-12 (1), 13-18 (5) = 7
            Assert.Equal(7, kpi.GetProperty("freeSlots").GetInt32());
            Assert.Equal(12, o.GetProperty("appointments")[0].GetProperty("cancellation").GetProperty("windowHours").GetInt32());
            Assert.Equal("UTC", o.GetProperty("appointments")[0].GetProperty("timezone").GetString());
        }

        // сьогодні: два клієнти створені щойно -> newClients == 2, записів немає
        var today = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={Day(0)}", bearer: c.Owner));
        Assert.Equal(2, today.GetProperty("kpi").GetProperty("newClients").GetInt32());
        Assert.Equal(0, today.GetProperty("kpi").GetProperty("appointmentsCount").GetInt32());
        Assert.Empty(today.GetProperty("appointments").EnumerateArray());

        // без date — локальне "сьогодні" еталонного закладу
        var dflt = await J(await fx.Send(HttpMethod.Get, "/api/beauty/overview", bearer: c.Owner));
        Assert.Equal(Day(0), dflt.GetProperty("date").GetString());
    }

    [SkippableFact]
    public async Task overview_respects_each_locations_timezone_and_location_filter()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var (nz, _) = await _h.AddLocationAsync(c.Salon.Tenant.TenantId, c.Salon.SpecialistId, "Pacific/Auckland");
        Guid clientId;
        await using (var ctx = fx.Db.CreateContext(c.Salon.Tenant.TenantId))
        {
            var client = new Client { FullName = "NZ", Phone = "+64000000001" };
            ctx.Add(client);
            await ctx.SaveChangesAsync();
            clientId = client.Id;
            // D-1 20:00 UTC = D 08:00/09:00 за Оклендом
            ctx.Add(new Appointment
            {
                LocationId = nz, SpecialistId = c.Salon.SpecialistId, ServiceId = c.Salon.ServiceId, ClientId = client.Id,
                StartsAt = At(4, 20), DurationMinutes = 60, Status = AppointmentStatus.Confirmed, Source = AppointmentSource.Admin,
                PriceOriginal = 100m, PriceFinal = 100m,
            });
            await ctx.SaveChangesAsync();
        }
        var localDay = Day(5); // At(4,20) UTC -> наступний день за Оклендом
        var inNz = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={localDay}&locationId={nz}", bearer: c.Owner));
        Assert.Equal("Pacific/Auckland", inNz.GetProperty("timezone").GetString());
        var a = Assert.Single(inNz.GetProperty("appointments").EnumerateArray());
        Assert.Equal(clientId, a.GetProperty("clientId").GetGuid());
        Assert.Equal("Pacific/Auckland", a.GetProperty("timezone").GetString());

        var prevDay = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={Day(4)}&locationId={nz}", bearer: c.Owner));
        Assert.Empty(prevDay.GetProperty("appointments").EnumerateArray());

        // фільтр: заклад UTC не бачить запис Окленда
        var utcOnly = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={localDay}&locationId={c.Salon.LocationId}", bearer: c.Owner));
        Assert.Empty(utcOnly.GetProperty("appointments").EnumerateArray());

        var missing = await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={localDay}&locationId={Guid.NewGuid()}", bearer: c.Owner);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("location_not_found", await RegressionHarness.CodeAsync(missing));
    }

    [SkippableFact]
    public async Task overview_is_for_managers_only_and_isolated_per_tenant()
    {
        NeedDb();
        var c = await NewCtxAsync();
        await SeedDayAsync(c, ahead: 3);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={Day(3)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={Day(3)}", bearer: c.Specialist)).StatusCode);

        var stranger = await _h.CreateSalonAsync();
        var foreign = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={Day(3)}", bearer: stranger.OwnerToken));
        Assert.Empty(foreign.GetProperty("appointments").EnumerateArray());
        Assert.Equal(0, foreign.GetProperty("kpi").GetProperty("revenue").GetDecimal());
        var foreignLoc = await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={Day(3)}&locationId={c.Salon.LocationId}", bearer: stranger.OwnerToken);
        Assert.Equal(HttpStatusCode.NotFound, foreignLoc.StatusCode);
    }

    [SkippableFact]
    public async Task overview_of_tenant_without_locations_is_empty_not_an_error()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var owner = (await fx.LoginAsync(t.Slug, t.OwnerEmail)).AccessToken;
        var r = await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={Day(1)}", bearer: owner);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var o = await J(r);
        Assert.Empty(o.GetProperty("appointments").EnumerateArray());
        Assert.Equal(0, o.GetProperty("kpi").GetProperty("freeSlots").GetInt32());
        Assert.Equal(0, o.GetProperty("kpi").GetProperty("revenue").GetDecimal());
    }

    [SkippableFact]
    public async Task overview_free_slots_drop_to_zero_when_specialist_is_absent()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var before = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={Day(3)}", bearer: c.Owner));
        Assert.Equal(9, before.GetProperty("kpi").GetProperty("freeSlots").GetInt32()); // 09-18 по 60 хв
        var absence = await fx.Send(HttpMethod.Post, $"/api/beauty/specialists/{c.Salon.SpecialistId}/absences",
            new { type = "day_off", dateFrom = Day(3), dateTo = Day(3) }, c.Owner);
        Assert.Equal(HttpStatusCode.Created, absence.StatusCode);
        var after = await J(await fx.Send(HttpMethod.Get, $"/api/beauty/overview?date={Day(3)}", bearer: c.Owner));
        Assert.Equal(0, after.GetProperty("kpi").GetProperty("freeSlots").GetInt32());
    }
}
