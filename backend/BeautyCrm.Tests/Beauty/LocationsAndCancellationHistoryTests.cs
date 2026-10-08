using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyLocations;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Regression;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-697 (§16): заклади (CRUD + 409), історія скасувань (хто/коли/чому), includeCancelled, видимість деталей за роллю,
/// публічний API не розкриває скасування й неактивні заклади. Повний HTTP-конвеєр на реальному PostgreSQL (роль без BYPASSRLS).
/// </summary>
public sealed class LocationsAndCancellationHistoryTests : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly HttpClient _public = null!;

    public LocationsAndCancellationHistoryTests(AuthApiFixture fx)
    {
        _fx = fx;
        _h = new RegressionHarness(fx);
        if (fx.SkipReason is null)
        {
            var f = fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b =>
            {
                b.UseSetting("PublicBooking:RateLimit:ReadPermit", "10000");
                b.UseSetting("PublicBooking:RateLimit:TokenPermit", "10000");
                b.UseSetting("PublicBooking:RateLimit:WritePermit", "10000");
            });
            _factories.Add(f);
            _public = f.CreateClient();
        }
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    private static string Day(int ahead) => DateTime.UtcNow.Date.AddDays(ahead).ToString("yyyy-MM-dd");
    private static DateTimeOffset At(int ahead, int hour = 10) => new(DateTime.UtcNow.Date.AddDays(ahead).AddHours(hour), TimeSpan.Zero);
    private static string Range(int from = 0, int to = 20) =>
        $"from={Uri.EscapeDataString(At(from, 0).ToString("o"))}&to={Uri.EscapeDataString(At(to, 0).ToString("o"))}";

    private Task<HttpResponseMessage> Api(HttpMethod m, string url, string token, object? body = null) => _fx.Send(m, url, body, token);
    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;

    private sealed record Ctx(RegressionHarness.Salon Salon, TokenResponse Admin, TokenResponse Specialist)
    {
        public string Owner => Salon.OwnerToken;
    }

    private async Task<Ctx> NewCtxAsync()
    {
        var salon = await _h.CreateSalonAsync();
        var admin = await _fx.InviteAndLoginAsync(salon.Tenant, salon.OwnerToken, Roles.Admin);
        var specialist = await _fx.InviteAndLoginAsync(salon.Tenant, salon.OwnerToken, Roles.Specialist, salon.SpecialistId);
        return new Ctx(salon, admin, specialist);
    }

    private async Task<Guid> BookAsync(Ctx c, int ahead, int hour = 10, string phone = "+380501112233")
    {
        var r = await _fx.Send(HttpMethod.Post, "/api/beauty/appointments", RegressionHarness.Booking(c.Salon, At(ahead, hour), phone: phone), c.Owner);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await J(r)).GetProperty("id").GetGuid();
    }

    private async Task<Appointment> RowAsync(Guid tenantId, Guid id)
    {
        await using var ctx = _fx.Db.CreateContext(tenantId);
        return await ctx.Appointments.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private static JsonElement? Find(JsonElement list, Guid id) =>
        list.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(a => a!.Value.GetProperty("id").GetGuid() == id);

    // ================= скасування: хто, коли, чому =================

    [SkippableFact]
    public async Task staff_cancel_records_who_when_and_reason_and_hides_the_appointment_from_the_default_calendar()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var id = await BookAsync(c, 3);
        var other = await BookAsync(c, 3, 13, "+380501112244");
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);

        var cancel = await Api(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", c.Admin.AccessToken, new { reason = "  Client is ill  " });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var body = await J(cancel);
        var dto = body.GetProperty("appointment");
        Assert.Equal("cancelled", dto.GetProperty("status").GetString());
        Assert.Equal("staff", dto.GetProperty("cancelledBy").GetProperty("type").GetString());
        Assert.Equal(c.Admin.User.FullName, dto.GetProperty("cancelledBy").GetProperty("name").GetString());
        Assert.Equal("Client is ill", dto.GetProperty("cancelReason").GetString());
        Assert.True(dto.GetProperty("cancelledAt").GetDateTimeOffset() >= before);

        // БД: дані не видалено, хто/коли/чому збережено
        var row = await RowAsync(c.Salon.Tenant.TenantId, id);
        Assert.Equal(AppointmentStatus.Cancelled, row.Status);
        Assert.Equal("staff", row.CancelledByType);
        Assert.Equal(c.Admin.User.Id, row.CancelledByUserId);
        Assert.Equal("Client is ill", row.CancelReason);
        Assert.NotNull(row.CancelledAt);

        // календар за замовчуванням: без скасованих; з includeCancelled — є
        var def = await J(await Api(HttpMethod.Get, $"/api/beauty/appointments?{Range()}", c.Owner));
        Assert.Null(Find(def, id));
        Assert.NotNull(Find(def, other));
        var all = await J(await Api(HttpMethod.Get, $"/api/beauty/appointments?{Range()}&includeCancelled=true", c.Owner));
        var cancelled = Find(all, id)!.Value;
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("staff", cancelled.GetProperty("cancelledBy").GetProperty("type").GetString());
        Assert.Equal(c.Admin.User.FullName, cancelled.GetProperty("cancelledBy").GetProperty("name").GetString());
        Assert.Equal("Client is ill", cancelled.GetProperty("cancelReason").GetString());
        // нескасований запис не має полів скасування
        var active = Find(all, other)!.Value;
        Assert.False(active.TryGetProperty("cancelledBy", out _));
        Assert.False(active.TryGetProperty("cancelReason", out _));
        Assert.False(active.TryGetProperty("cancelledAt", out _));
        // прямий доступ за id лишається
        Assert.Equal("cancelled", (await J(await Api(HttpMethod.Get, $"/api/beauty/appointments/{id}", c.Owner))).GetProperty("status").GetString());
        // слот знову вільний
        Assert.Contains(await _h.SlotsAsync(c.Salon, Day(3)), s => s.GetProperty("startsAt").GetDateTimeOffset() == At(3, 10));
    }

    [SkippableFact]
    public async Task cancel_without_body_or_reason_works_and_too_long_reason_is_rejected_without_cancelling()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var a = await BookAsync(c, 3);
        var tooLong = await Api(HttpMethod.Post, $"/api/beauty/appointments/{a}/cancel", c.Owner, new { reason = new string('x', 301) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("invalid_reason", await Code(tooLong));
        Assert.NotEqual(AppointmentStatus.Cancelled, (await RowAsync(c.Salon.Tenant.TenantId, a)).Status);

        var exact = await Api(HttpMethod.Post, $"/api/beauty/appointments/{a}/cancel", c.Owner, new { reason = new string('y', 300) });
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(300, (await RowAsync(c.Salon.Tenant.TenantId, a)).CancelReason!.Length);

        var b = await BookAsync(c, 3, 13, "+380501112299");
        var blank = await Api(HttpMethod.Post, $"/api/beauty/appointments/{b}/cancel", c.Owner, new { reason = "   " });
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
        var row = await RowAsync(c.Salon.Tenant.TenantId, b);
        Assert.Null(row.CancelReason);
        Assert.Equal("staff", row.CancelledByType);
        Assert.Equal(c.Salon.Tenant.OwnerId, row.CancelledByUserId);

        // повторне скасування лишається 409 і не перезаписує автора
        var again = await Api(HttpMethod.Post, $"/api/beauty/appointments/{b}/cancel", c.Admin.AccessToken, new { reason = "later" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_cancelled", await Code(again));
        Assert.Equal(c.Salon.Tenant.OwnerId, (await RowAsync(c.Salon.Tenant.TenantId, b)).CancelledByUserId);
    }

    [SkippableFact]
    public async Task specialist_sees_cancel_type_and_time_but_never_the_name_or_reason()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var id = await BookAsync(c, 3);
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", c.Admin.AccessToken, new { reason = "secret reason" })).StatusCode);

        var list = await J(await Api(HttpMethod.Get, $"/api/beauty/appointments?{Range()}&includeCancelled=true", c.Specialist.AccessToken));
        var seen = Find(list, id)!.Value;
        Assert.Equal("staff", seen.GetProperty("cancelledBy").GetProperty("type").GetString());
        Assert.False(seen.GetProperty("cancelledBy").TryGetProperty("name", out _));
        Assert.False(seen.TryGetProperty("cancelReason", out _));
        Assert.True(seen.TryGetProperty("cancelledAt", out _));
        // за замовчуванням specialist теж не бачить скасованих
        Assert.Null(Find(await J(await Api(HttpMethod.Get, $"/api/beauty/appointments?{Range()}", c.Specialist.AccessToken)), id));

        var one = await J(await Api(HttpMethod.Get, $"/api/beauty/appointments/{id}", c.Specialist.AccessToken));
        Assert.False(one.GetProperty("cancelledBy").TryGetProperty("name", out _));
        Assert.False(one.TryGetProperty("cancelReason", out _));

        // власне скасування specialist-ом: відповідь теж без причини, у БД staff + його user id
        var mine = await BookAsync(c, 4);
        var cancel = await Api(HttpMethod.Post, $"/api/beauty/appointments/{mine}/cancel", c.Specialist.AccessToken, new { reason = "my reason" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var appt = (await J(cancel)).GetProperty("appointment");
        Assert.False(appt.TryGetProperty("cancelReason", out _));
        Assert.False(appt.GetProperty("cancelledBy").TryGetProperty("name", out _));
        var row = await RowAsync(c.Salon.Tenant.TenantId, mine);
        Assert.Equal(("staff", (Guid?)c.Specialist.User.Id, "my reason"), (row.CancelledByType, row.CancelledByUserId, row.CancelReason));
    }

    private async Task<(string Token, Guid Id)> PublicBookingAsync(RegressionHarness.Salon s, DateTimeOffset at, string phone = "+380677778899")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{s.Tenant.Slug}/appointments")
        {
            Content = JsonContent.Create(new
            {
                locationId = s.LocationId, specialistId = s.SpecialistId, serviceId = s.ServiceId, startsAt = at,
                client = new { name = "Olena Test", phone }, reminder = "none", paymentMethod = "cash",
            }, options: Json),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var r = await _public.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var token = (await J(r)).GetProperty("publicToken").GetString()!;
        var list = await J(await _fx.Send(HttpMethod.Get, $"/api/beauty/appointments?{Range()}", bearer: s.OwnerToken));
        var id = list.EnumerateArray().Single(a => a.GetProperty("startsAt").GetDateTimeOffset() == at).GetProperty("id").GetGuid();
        return (token, id);
    }

    [SkippableFact]
    public async Task public_cancel_records_client_and_the_public_api_never_discloses_cancellation_details()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var (token, id) = await PublicBookingAsync(c.Salon, At(3, 11));

        var tooLong = await _public.PostAsJsonAsync($"/api/public/{c.Salon.Tenant.Slug}/appointments/{token}/cancel", new { reason = new string('z', 301) }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("invalid_reason", await Code(tooLong));

        var cancel = await _public.PostAsJsonAsync($"/api/public/{c.Salon.Tenant.Slug}/appointments/{token}/cancel", new { reason = "Changed my mind" }, Json);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var text = await cancel.Content.ReadAsStringAsync();
        Assert.DoesNotContain("cancelledBy", text);
        Assert.DoesNotContain("cancelReason", text);
        Assert.DoesNotContain("Changed my mind", text);

        var view = await _public.GetStringAsync($"/api/public/{c.Salon.Tenant.Slug}/appointments/{token}");
        Assert.DoesNotContain("cancelledBy", view);
        Assert.DoesNotContain("cancelReason", view);
        Assert.DoesNotContain("cancelledAt", view);

        var row = await RowAsync(c.Salon.Tenant.TenantId, id);
        Assert.Equal(("client", (Guid?)null, "Changed my mind"), (row.CancelledByType, row.CancelledByUserId, row.CancelReason));
        Assert.NotNull(row.CancelledAt);

        var all = await J(await Api(HttpMethod.Get, $"/api/beauty/appointments?{Range()}&includeCancelled=true", c.Owner));
        var seen = Find(all, id)!.Value;
        Assert.Equal("client", seen.GetProperty("cancelledBy").GetProperty("type").GetString());
        Assert.False(seen.GetProperty("cancelledBy").TryGetProperty("name", out _));
        Assert.Equal("Changed my mind", seen.GetProperty("cancelReason").GetString());
    }

    [SkippableFact]
    public async Task public_cancel_without_body_still_works()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var (token, id) = await PublicBookingAsync(c.Salon, At(3, 12));
        var r = await _public.PostAsync($"/api/public/{c.Salon.Tenant.Slug}/appointments/{token}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var row = await RowAsync(c.Salon.Tenant.TenantId, id);
        Assert.Equal(("client", (Guid?)null, (string?)null), (row.CancelledByType, row.CancelledByUserId, row.CancelReason));
    }

    // ================= картка клієнта =================

    [SkippableFact]
    public async Task client_card_history_keeps_cancelled_visits_with_who_and_counts_cancellations()
    {
        NeedDb();
        var c = await NewCtxAsync();
        const string phone = "+380677778800";
        var (token, byClient) = await PublicBookingAsync(c.Salon, At(3, 11), phone);
        var byStaff = await BookAsync(c, 4, 10, phone);
        var active = await BookAsync(c, 5, 10, phone);
        Assert.Equal(HttpStatusCode.OK, (await _public.PostAsync($"/api/public/{c.Salon.Tenant.Slug}/appointments/{token}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Post, $"/api/beauty/appointments/{byStaff}/cancel", c.Admin.AccessToken, new { reason = "Master ill" })).StatusCode);

        var clientId = (await J(await Api(HttpMethod.Get, "/api/beauty/clients?search=Olena", c.Owner))).EnumerateArray().Single().GetProperty("id").GetGuid();
        var card = await J(await Api(HttpMethod.Get, $"/api/beauty/clients/{clientId}", c.Owner));
        var summary = card.GetProperty("client");
        Assert.Equal(2, summary.GetProperty("cancelledCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("cancelledByClientCount").GetInt32());
        var history = card.GetProperty("history");
        Assert.Equal(3, history.GetArrayLength());
        var staffEntry = Find(history, byStaff)!.Value;
        Assert.Equal("staff", staffEntry.GetProperty("cancelledBy").GetProperty("type").GetString());
        Assert.Equal(c.Admin.User.FullName, staffEntry.GetProperty("cancelledBy").GetProperty("name").GetString());
        Assert.Equal("Master ill", staffEntry.GetProperty("cancelReason").GetString());
        Assert.True(staffEntry.TryGetProperty("cancelledAt", out _));
        Assert.Equal("client", Find(history, byClient)!.Value.GetProperty("cancelledBy").GetProperty("type").GetString());
        Assert.False(Find(history, active)!.Value.TryGetProperty("cancelledBy", out _));

        // список клієнтів теж несе лічильники; specialist до клієнтів доступу не має
        var listed = (await J(await Api(HttpMethod.Get, "/api/beauty/clients?search=Olena", c.Owner))).EnumerateArray().Single();
        Assert.Equal(2, listed.GetProperty("cancelledCount").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await Api(HttpMethod.Get, $"/api/beauty/clients/{clientId}", c.Specialist.AccessToken)).StatusCode);
        // дані не видалено
        await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
        Assert.Equal(3, await ctx.Appointments.CountAsync(a => a.ClientId == clientId));
    }

    [SkippableFact]
    public async Task overview_keeps_returning_all_statuses_including_cancelled()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var id = await BookAsync(c, 3);
        await Api(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", c.Owner);
        var ov = await J(await Api(HttpMethod.Get, $"/api/beauty/overview?date={Day(3)}", c.Owner));
        var item = Find(ov.GetProperty("appointments"), id)!.Value;
        Assert.Equal("cancelled", item.GetProperty("status").GetString());
        Assert.Equal(0, ov.GetProperty("kpi").GetProperty("appointmentsCount").GetInt32());
    }

    // ================= заклади: CRUD =================

    private async Task<HttpResponseMessage> CreateLocation(string token, object body) => await Api(HttpMethod.Post, "/api/beauty/locations", token, body);

    [SkippableFact]
    public async Task owner_and_admin_create_and_update_locations_specialist_only_reads()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var created = await CreateLocation(c.Owner, new { name = "  Downtown  ", address = "Main 1", phone = "+380441112233", timezone = "Europe/Kyiv" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var loc = await J(created);
        var id = loc.GetProperty("id").GetGuid();
        Assert.Equal("Downtown", loc.GetProperty("name").GetString());
        Assert.Equal("Main 1", loc.GetProperty("address").GetString());
        Assert.Equal("+380441112233", loc.GetProperty("phone").GetString());
        Assert.Equal("Europe/Kyiv", loc.GetProperty("timezone").GetString());
        Assert.True(loc.GetProperty("isActive").GetBoolean());

        var byAdmin = await CreateLocation(c.Admin.AccessToken, new { name = "Uptown", timezone = "UTC" });
        Assert.Equal(HttpStatusCode.Created, byAdmin.StatusCode);
        Assert.False((await J(byAdmin)).TryGetProperty("address", out var a) && a.ValueKind != JsonValueKind.Null);

        // specialist: читає, не змінює
        var spec = c.Specialist.AccessToken;
        var read = await J(await Api(HttpMethod.Get, "/api/beauty/locations", spec));
        Assert.Contains(read.EnumerateArray(), l => l.GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateLocation(spec, new { name = "Hack", timezone = "UTC" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Api(HttpMethod.Put, $"/api/beauty/locations/{id}", spec, new { name = "Hack", timezone = "UTC", isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Send(HttpMethod.Post, "/api/beauty/locations", new { name = "X", timezone = "UTC" })).StatusCode);

        var put = await Api(HttpMethod.Put, $"/api/beauty/locations/{id}", c.Admin.AccessToken,
            new { name = "Downtown Plus", address = (string?)null, phone = "+380441112244", timezone = "Europe/Kyiv", isActive = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await J(put);
        Assert.Equal("Downtown Plus", updated.GetProperty("name").GetString());
        Assert.False(updated.TryGetProperty("address", out var addr) && addr.ValueKind != JsonValueKind.Null);
        Assert.Equal("+380441112244", updated.GetProperty("phone").GetString());
    }

    [SkippableFact]
    public async Task location_validation_returns_422_with_codes()
    {
        NeedDb();
        var c = await NewCtxAsync();
        foreach (var tz in new[] { "Mars/Phobos", "FLE Standard Time", "Europe/Kyiv; DROP", "", "  ", "../etc/passwd", new string('a', 70) + "/B" })
        {
            var r = await CreateLocation(c.Owner, new { name = "Zone test", timezone = tz });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("invalid_timezone", await Code(r));
        }
        var missingTz = await CreateLocation(c.Owner, new { name = "Zone test" });
        Assert.Equal("invalid_timezone", await Code(missingTz));
        foreach (var name in new[] { "", "   ", new string('n', 201) })
            Assert.Equal("invalid_name", await Code(await CreateLocation(c.Owner, new { name, timezone = "UTC" })));
        Assert.Equal("invalid_phone", await Code(await CreateLocation(c.Owner, new { name = "P", timezone = "UTC", phone = new string('1', 33) })));
        Assert.Equal("invalid_address", await Code(await CreateLocation(c.Owner, new { name = "A", timezone = "UTC", address = new string('a', 501) })));
        // валідні IANA-зони різної форми
        foreach (var tz in new[] { "UTC", "America/Argentina/Buenos_Aires", "Pacific/Auckland" })
            Assert.Equal(HttpStatusCode.Created, (await CreateLocation(c.Owner, new { name = "Zone " + tz, timezone = tz })).StatusCode);
    }

    [SkippableFact]
    public async Task location_name_must_be_unique_per_tenant_case_insensitively_but_not_across_tenants()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var other = await _h.CreateSalonAsync();
        var first = await J(await CreateLocation(c.Owner, new { name = "Central", timezone = "UTC" }));
        var dup = await CreateLocation(c.Admin.AccessToken, new { name = "  CENTRAL ", timezone = "UTC" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("location_name_taken", await Code(dup));
        Assert.Equal(HttpStatusCode.Created, (await CreateLocation(other.OwnerToken, new { name = "Central", timezone = "UTC" })).StatusCode);

        var second = await J(await CreateLocation(c.Owner, new { name = "Riverside", timezone = "UTC" }));
        var clash = await Api(HttpMethod.Put, $"/api/beauty/locations/{second.GetProperty("id").GetGuid()}", c.Owner,
            new { name = "central", timezone = "UTC", isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("location_name_taken", await Code(clash));
        // власне ім'я (інший регістр) для того ж закладу - дозволено
        var self = await Api(HttpMethod.Put, $"/api/beauty/locations/{first.GetProperty("id").GetGuid()}", c.Owner,
            new { name = "CENTRAL", timezone = "UTC", isActive = true });
        Assert.Equal(HttpStatusCode.OK, self.StatusCode);
        // назву неактивного закладу теж не можна повторити
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Put, $"/api/beauty/locations/{second.GetProperty("id").GetGuid()}", c.Owner,
            new { name = "Riverside", timezone = "UTC", isActive = false })).StatusCode);
        Assert.Equal("location_name_taken", await Code(await CreateLocation(c.Owner, new { name = "riverside", timezone = "UTC" })));
    }

    [SkippableFact]
    public async Task list_hides_inactive_by_default_and_only_managers_can_include_them()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var id = (await J(await CreateLocation(c.Owner, new { name = "Closed soon", timezone = "UTC" }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Put, $"/api/beauty/locations/{id}", c.Owner,
            new { name = "Closed soon", timezone = "UTC", isActive = false })).StatusCode);

        bool Has(JsonElement l) => l.EnumerateArray().Any(x => x.GetProperty("id").GetGuid() == id);
        Assert.False(Has(await J(await Api(HttpMethod.Get, "/api/beauty/locations", c.Owner))));
        var all = await J(await Api(HttpMethod.Get, "/api/beauty/locations?includeInactive=true", c.Admin.AccessToken));
        Assert.True(Has(all));
        Assert.False(all.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id).GetProperty("isActive").GetBoolean());
        Assert.False(Has(await J(await Api(HttpMethod.Get, "/api/beauty/locations?includeInactive=true", c.Specialist.AccessToken))));

        // isActive пропущено = без змін; повторна активація працює
        var keep = await J(await Api(HttpMethod.Put, $"/api/beauty/locations/{id}", c.Owner, new { name = "Closed soon 2", timezone = "UTC" }));
        Assert.False(keep.GetProperty("isActive").GetBoolean());
        var back = await J(await Api(HttpMethod.Put, $"/api/beauty/locations/{id}", c.Owner, new { name = "Closed soon 2", timezone = "UTC", isActive = true }));
        Assert.True(back.GetProperty("isActive").GetBoolean());
        Assert.True(Has(await J(await Api(HttpMethod.Get, "/api/beauty/locations", c.Specialist.AccessToken))));
    }

    [SkippableFact]
    public async Task update_of_a_foreign_or_unknown_location_is_404_and_changes_nothing()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var foreign = await _h.CreateSalonAsync();
        var r = await Api(HttpMethod.Put, $"/api/beauty/locations/{foreign.LocationId}", c.Owner,
            new { name = "Stolen", timezone = "UTC", isActive = false });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("location_not_found", await Code(r));
        Assert.Equal(HttpStatusCode.NotFound,
            (await Api(HttpMethod.Put, $"/api/beauty/locations/{Guid.NewGuid()}", c.Owner, new { name = "X", timezone = "UTC", isActive = true })).StatusCode);
        await using var ctx = _fx.Db.CreateContext(foreign.Tenant.TenantId);
        var loc = await ctx.Locations.AsNoTracking().SingleAsync(l => l.Id == foreign.LocationId);
        Assert.True(loc.IsActive);
        Assert.StartsWith("Loc-", loc.Name);
        // чужі заклади не видно в списку
        Assert.DoesNotContain((await J(await Api(HttpMethod.Get, "/api/beauty/locations?includeInactive=true", c.Owner))).EnumerateArray(),
            l => l.GetProperty("id").GetGuid() == foreign.LocationId);
    }

    [SkippableFact]
    public async Task deactivation_and_timezone_change_are_blocked_by_upcoming_active_appointments_only()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var path = $"/api/beauty/locations/{c.Salon.LocationId}";
        var name = (await J(await Api(HttpMethod.Get, "/api/beauty/locations", c.Owner))).EnumerateArray().Single().GetProperty("name").GetString();
        var appt = await BookAsync(c, 3);

        var deactivate = await Api(HttpMethod.Put, path, c.Owner, new { name, timezone = "UTC", isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, deactivate.StatusCode);
        Assert.Equal("has_future_appointments", await Code(deactivate));
        var tz = await Api(HttpMethod.Put, path, c.Owner, new { name, timezone = "Europe/Kyiv", isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, tz.StatusCode);
        Assert.Equal("timezone_locked", await Code(tz));
        // назва/адреса/телефон змінюються й при майбутніх записах
        var rename = await Api(HttpMethod.Put, path, c.Owner, new { name = name + " renamed", address = "New 5", timezone = "UTC", isActive = true });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        name += " renamed";
        // confirmed теж блокує; completed/no_show/cancelled - ні
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Patch, $"/api/beauty/appointments/{appt}", c.Owner, new { status = "confirmed" })).StatusCode);
        Assert.Equal("has_future_appointments", await Code(await Api(HttpMethod.Put, path, c.Owner, new { name, timezone = "UTC", isActive = false })));
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Post, $"/api/beauty/appointments/{appt}/cancel", c.Owner)).StatusCode);

        var tzOk = await Api(HttpMethod.Put, path, c.Owner, new { name, timezone = "Europe/Kyiv", isActive = true });
        Assert.Equal(HttpStatusCode.OK, tzOk.StatusCode);
        Assert.Equal("Europe/Kyiv", (await J(tzOk)).GetProperty("timezone").GetString());
        var off = await Api(HttpMethod.Put, path, c.Owner, new { name, timezone = "Europe/Kyiv", isActive = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await J(off)).GetProperty("isActive").GetBoolean());
        // скасований запис і дані закладу збереглися
        Assert.Equal(AppointmentStatus.Cancelled, (await RowAsync(c.Salon.Tenant.TenantId, appt)).Status);
        // вже неактивний заклад: повторне збереження не перевіряє записи
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Put, path, c.Owner, new { name, timezone = "Europe/Kyiv", isActive = false })).StatusCode);
    }

    [SkippableFact]
    public async Task past_appointments_and_other_locations_do_not_block_deactivation()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var second = (await J(await CreateLocation(c.Owner, new { name = "Branch", timezone = "UTC" }))).GetProperty("id").GetGuid();
        await BookAsync(c, 3); // у основному закладі
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Put, $"/api/beauty/locations/{second}", c.Owner,
            new { name = "Branch", timezone = "Pacific/Auckland", isActive = false })).StatusCode);
        // минулий активний запис не блокує (starts_at <= now)
        await using (var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId))
        {
            var clientId = (await ctx.Clients.AsNoTracking().FirstAsync()).Id;
            var serviceId = c.Salon.ServiceId;
            ctx.Appointments.Add(new Appointment
            {
                LocationId = second, SpecialistId = c.Salon.SpecialistId, ServiceId = serviceId, ClientId = clientId,
                StartsAt = DateTimeOffset.UtcNow.AddDays(-3), DurationMinutes = 60, Status = AppointmentStatus.Completed,
                Source = AppointmentSource.Admin, PriceOriginal = 1, PriceFinal = 1,
            });
            await ctx.SaveChangesAsync();
        }
        var r = await Api(HttpMethod.Put, $"/api/beauty/locations/{second}", c.Owner, new { name = "Branch", timezone = "UTC", isActive = false });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    // ================= неактивний заклад не віддається публічно / у слотах =================

    [SkippableFact]
    public async Task inactive_location_disappears_from_public_api_slots_and_booking_but_keeps_its_data()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var s = c.Salon;
        var slug = s.Tenant.Slug;
        var name = (await J(await Api(HttpMethod.Get, "/api/beauty/locations", c.Owner))).EnumerateArray().Single().GetProperty("name").GetString();

        var before = await J(await _public.GetAsync($"/api/public/{slug}/locations"));
        Assert.Contains(before.EnumerateArray(), l => l.GetProperty("id").GetGuid() == s.LocationId);
        Assert.Equal(HttpStatusCode.OK, (await _public.GetAsync($"/api/public/{slug}/locations/{s.LocationId}/specialists")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Put, $"/api/beauty/locations/{s.LocationId}", c.Owner,
            new { name, timezone = "UTC", isActive = false })).StatusCode);

        var after = await J(await _public.GetAsync($"/api/public/{slug}/locations"));
        Assert.DoesNotContain(after.EnumerateArray(), l => l.GetProperty("id").GetGuid() == s.LocationId);
        foreach (var path in new[]
                 {
                     $"/api/public/{slug}/locations/{s.LocationId}/specialists",
                     $"/api/public/{slug}/locations/{s.LocationId}/services",
                     $"/api/public/{slug}/slots?locationId={s.LocationId}&serviceId={s.ServiceId}&date={Day(3)}",
                 })
        {
            var r = await _public.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Equal("location_not_found", await Code(r));
        }
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{slug}/appointments")
        {
            Content = JsonContent.Create(new
            {
                locationId = s.LocationId, specialistId = s.SpecialistId, serviceId = s.ServiceId, startsAt = At(3),
                client = new { name = "Olena Test", phone = "+380677778811" }, reminder = "none", paymentMethod = "cash",
            }, options: Json),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, (await _public.SendAsync(req)).StatusCode);

        // staff: слоти й запис у неактивному закладі недоступні
        var slots = await Api(HttpMethod.Get, $"/api/beauty/slots?locationId={s.LocationId}&serviceId={s.ServiceId}&date={Day(3)}", c.Owner);
        Assert.Equal(HttpStatusCode.NotFound, slots.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _h.BookAsync(s, At(3))).StatusCode);

        // записи/графіки/ціни збережено
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.Equal(1, await ctx.SpecialistLocations.CountAsync(sl => sl.LocationId == s.LocationId && sl.IsActive));
        // реактивація повертає заклад у публічний API
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Put, $"/api/beauty/locations/{s.LocationId}", c.Owner,
            new { name, timezone = "UTC", isActive = true })).StatusCode);
        Assert.Contains((await J(await _public.GetAsync($"/api/public/{slug}/locations"))).EnumerateArray(),
            l => l.GetProperty("id").GetGuid() == s.LocationId);
    }

    // ================= гонка: запис проти деактивації / зміни зони =================

    [SkippableFact]
    public async Task booking_and_location_update_wait_for_the_location_row_lock()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var name = (await J(await Api(HttpMethod.Get, "/api/beauty/locations", c.Owner))).EnumerateArray().Single().GetProperty("name").GetString();

        // 1) заклад заблоковано FOR UPDATE (як роблять PUT /locations): запис чекає, потім проходить
        await using (var conn = new NpgsqlConnection(_fx.Db.AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand("SELECT 1 FROM beauty_locations WHERE id = @id FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", c.Salon.LocationId);
                await cmd.ExecuteScalarAsync();
            }
            var pending = _h.BookAsync(c.Salon, At(6));
            await Task.Delay(1500);
            Assert.False(pending.IsCompleted, "booking must wait for the location row lock");
            await tx.CommitAsync();
            Assert.Equal(HttpStatusCode.Created, (await pending.WaitAsync(TimeSpan.FromSeconds(20))).StatusCode);
        }

        // 2) запис тримає FOR SHARE (відкрита транзакція) -> PUT /locations чекає і після коміту бачить запис -> 409
        await using (var conn = new NpgsqlConnection(_fx.Db.AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand("SELECT 1 FROM beauty_locations WHERE id = @id FOR SHARE", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", c.Salon.LocationId);
                await cmd.ExecuteScalarAsync();
            }
            var pending = Api(HttpMethod.Put, $"/api/beauty/locations/{c.Salon.LocationId}", c.Owner, new { name, timezone = "UTC", isActive = false });
            await Task.Delay(1500);
            Assert.False(pending.IsCompleted, "update must wait for the share lock");
            await tx.CommitAsync();
            var r = await pending.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); // запис із кроку 1 майбутній
            Assert.Equal("has_future_appointments", await Code(r));
        }
    }

    [SkippableFact]
    public async Task concurrent_booking_and_deactivation_never_leave_an_active_future_appointment_in_an_inactive_location()
    {
        NeedDb();
        for (var round = 0; round < 4; round++)
        {
            var c = await NewCtxAsync();
            var name = (await J(await Api(HttpMethod.Get, "/api/beauty/locations", c.Owner))).EnumerateArray().Single().GetProperty("name").GetString();
            var book = _h.BookAsync(c.Salon, At(3 + round));
            var off = Api(HttpMethod.Put, $"/api/beauty/locations/{c.Salon.LocationId}", c.Owner, new { name, timezone = "UTC", isActive = false });
            await Task.WhenAll(book, off);
            await using var ctx = _fx.Db.CreateContext(c.Salon.Tenant.TenantId);
            var loc = await ctx.Locations.AsNoTracking().SingleAsync();
            var active = await ctx.Appointments.CountAsync(a => a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed);
            Assert.True(loc.IsActive || active == 0, $"round {round}: inactive location with {active} active appointments");
            Assert.Contains((await book).StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.NotFound, HttpStatusCode.Conflict });
        }
    }
}

/// <summary>TASK-697: логіка Application без БД - автор скасування, валідація зони/полів закладу, видимість деталей.</summary>
public class CancellationOriginUnitTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 7, 8, 0, 0, TimeSpan.Zero);
    private readonly FakeBookingStore _store = new();
    private CancellationService Sut() => new(_store, new SpyPayments(), FakeSettings.Service(), new FakeClock(Now));

    [Fact]
    public async Task cancel_defaults_to_system_origin_and_records_time()
    {
        var a = _store.Seed(Now.AddHours(30));
        var r = await Sut().CancelAsync(a.Id, default);
        Assert.True(r.IsOk);
        Assert.Equal(CancelOrigin.System, _store.LastCancelOrigin);
        Assert.Equal("system", r.Value!.Appointment.CancelledBy!.Type);
        Assert.Equal(Now, r.Value.Appointment.CancelledAt);
    }

    [Fact]
    public async Task cancel_passes_client_and_staff_origin_and_trims_reason()
    {
        var userId = Guid.NewGuid();
        var a = _store.Seed(Now.AddHours(30));
        var r = await Sut().CancelAsync(a.Id, default, CancelOrigin.Staff(userId), "  too busy ");
        Assert.True(r.IsOk);
        Assert.Equal(new CancelOrigin("staff", userId), _store.LastCancelOrigin);
        Assert.Equal("too busy", r.Value!.Appointment.CancelReason);

        var b = _store.Seed(Now.AddHours(40));
        var rb = await Sut().CancelAsync(b.Id, default, CancelOrigin.Client);
        Assert.Equal(CancelOrigin.Client, _store.LastCancelOrigin);
        Assert.Null(rb.Value!.Appointment.CancelReason);
    }

    [Fact]
    public async Task cancel_rejects_too_long_reason_before_touching_the_appointment()
    {
        var a = _store.Seed(Now.AddHours(30));
        var r = await Sut().CancelAsync(a.Id, default, CancelOrigin.System, new string('r', 301));
        Assert.Equal("invalid_reason", r.Error!.Code);
        Assert.Equal(ErrorKind.Validation, r.Error.Kind);
        Assert.NotEqual("cancelled", _store.Appointments.Single().Status);
    }

    [Fact]
    public void viewer_sees_name_and_reason_only_as_manager()
    {
        var dto = _store.Seed(Now.AddHours(30)) with
        {
            Status = "cancelled", CancelledAt = Now, CancelledBy = new CancelledByDto("staff", "Olga"), CancelReason = "why",
        };
        var owner = new Actor(Guid.NewGuid(), Guid.NewGuid(), Roles.Owner, null);
        var admin = owner with { Role = Roles.Admin };
        var spec = owner with { Role = Roles.Specialist, SpecialistId = Guid.NewGuid() };
        Assert.Equal(dto, dto.ForViewer(owner));
        Assert.Equal(dto, dto.ForViewer(admin));
        var seen = dto.ForViewer(spec);
        Assert.Equal(new CancelledByDto("staff", null), seen.CancelledBy);
        Assert.Null(seen.CancelReason);
        Assert.Equal(Now, seen.CancelledAt);
    }

    [Theory]
    [InlineData("Europe/Kyiv", true)]
    [InlineData("UTC", true)]
    [InlineData("America/Argentina/Buenos_Aires", true)]
    [InlineData("Mars/Phobos", false)]
    [InlineData("FLE Standard Time", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Europe/", false)]
    public void timezone_validation_accepts_only_known_iana_zones(string? tz, bool valid) =>
        Assert.Equal(valid, LocationService.IsValidTimezone(tz));
}
