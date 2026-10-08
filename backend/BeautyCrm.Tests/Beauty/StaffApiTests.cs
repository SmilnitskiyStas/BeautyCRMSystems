using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Regression;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-691: керування працівниками, відсутності, слоти, права ролей, ізоляція tenant і публічний API —
/// повний HTTP-конвеєр на реальному PostgreSQL (роль без BYPASSRLS). Без БД пропускаються.
/// </summary>
public sealed class StaffApiTests : IClassFixture<AuthApiFixture>, IDisposable
{
    private static readonly HttpMethod Put = HttpMethod.Put;
    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly HttpClient _public = null!;

    public StaffApiTests(AuthApiFixture fx)
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

    private Task<HttpResponseMessage> Api(HttpMethod m, string url, string token, object? body = null) =>
        _fx.Send(m, url, body, token);

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);

    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;

    /// <summary>Салон + admin + менеджерські токени; harness-майстер (всі дні 09-18, послуга призначена).</summary>
    private sealed record Ctx(RegressionHarness.Salon Salon, string Admin)
    {
        public string Owner => Salon.OwnerToken;
        public TenantSeed Tenant => Salon.Tenant;
    }

    private async Task<Ctx> NewCtxAsync()
    {
        var salon = await _h.CreateSalonAsync();
        var admin = await _fx.InviteAndLoginAsync(salon.Tenant, salon.OwnerToken, Roles.Admin);
        return new Ctx(salon, admin.AccessToken);
    }

    private async Task<Guid> CreateServiceAsync(Ctx c, string name = "Manicure", int minutes = 30)
    {
        var r = await Api(HttpMethod.Post, "/api/beauty/services", c.Owner, new { name, durationMinutes = minutes });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = (await J(r)).GetProperty("id").GetGuid();
        var p = await Api(Put, $"/api/beauty/services/{id}/prices", c.Owner, new { locationId = (Guid?)null, price = 500m });
        Assert.Equal(HttpStatusCode.OK, p.StatusCode);
        return id;
    }

    private static object AllDaysHours() => JsonSerializer.Deserialize<JsonElement>(RegressionHarness.AllDays);

    private async Task<Guid> CreateMasterAsync(Ctx c, Guid[] serviceIds, bool withHours = true, string name = "New Master")
    {
        var r = await Api(HttpMethod.Post, "/api/beauty/specialists", c.Admin, new
        {
            name, phone = "+380501112233", position = "Stylist",
            locationIds = new[] { c.Salon.LocationId }, serviceIds,
            workingHours = withHours ? AllDaysHours() : (object?)null,
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await J(r)).GetProperty("id").GetGuid();
    }

    private async Task<TokenResponse> SpecialistLoginAsync(Ctx c, Guid specialistId) =>
        await _fx.InviteAndLoginAsync(c.Tenant, c.Owner, Roles.Specialist, specialistId);

    private async Task<JsonElement> SlotsAsync(string token, Ctx c, Guid specialistId, Guid serviceId, int ahead)
    {
        var r = await Api(HttpMethod.Get,
            $"/api/beauty/slots?locationId={c.Salon.LocationId}&serviceId={serviceId}&date={Day(ahead)}&specialistId={specialistId}", token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await J(r);
    }

    private object Booking(Ctx c, Guid specialistId, Guid serviceId, DateTimeOffset at, string phone = "+380501112233") => new
    {
        locationId = c.Salon.LocationId, specialistId, serviceId, startsAt = at,
        client = new { name = "Anna", phone }, reminder = "none", paymentMethod = "cash",
    };

    private async Task<Guid> BookAsync(Ctx c, Guid specialistId, Guid serviceId, DateTimeOffset at, string phone = "+380501112233")
    {
        var r = await Api(HttpMethod.Post, "/api/beauty/appointments", c.Owner, Booking(c, specialistId, serviceId, at, phone));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await J(r)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> CreateAbsenceAsync(string token, Guid specialistId, int fromAhead, int toAhead, string type = "sick",
        string? note = "private note", HttpStatusCode expected = HttpStatusCode.Created)
    {
        var r = await Api(HttpMethod.Post, $"/api/beauty/specialists/{specialistId}/absences", token,
            new { type, dateFrom = Day(fromAhead), dateTo = Day(toAhead), note });
        Assert.Equal(expected, r.StatusCode);
        return await J(r);
    }

    // ---------- профілі: права ролей ----------

    [SkippableFact]
    public async Task specialist_role_cannot_manage_staff_but_can_read_directory_without_phone()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c, [c.Salon.ServiceId]);
        var sp = await SpecialistLoginAsync(c, master);

        Assert.Equal(HttpStatusCode.Forbidden, (await Api(HttpMethod.Post, "/api/beauty/specialists", sp.AccessToken, new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Api(Put, $"/api/beauty/specialists/{master}", sp.AccessToken, new { name = "Hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Api(Put, $"/api/beauty/specialists/{master}/services", sp.AccessToken, new { serviceIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Api(Put, $"/api/beauty/specialists/{master}/schedule", sp.AccessToken, new { locationId = c.Salon.LocationId, workingHours = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Api(HttpMethod.Post, $"/api/beauty/specialists/{master}/invite", sp.AccessToken, new { email = "x@y.test" })).StatusCode);

        var list = await Api(HttpMethod.Get, "/api/beauty/specialists", sp.AccessToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var raw = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("+380501112233", raw);
        Assert.DoesNotContain("\"phone\"", raw);
        Assert.DoesNotContain("hasAccount", raw);

        var asAdmin = await J(await Api(HttpMethod.Get, $"/api/beauty/specialists/{master}", c.Admin));
        Assert.Equal("+380501112233", asAdmin.GetProperty("phone").GetString());
        Assert.Equal("Stylist", asAdmin.GetProperty("position").GetString());
        Assert.Single(asAdmin.GetProperty("services").EnumerateArray());
        Assert.Equal("09:00", asAdmin.GetProperty("locations")[0].GetProperty("workingHours").GetProperty("mon")[0].GetProperty("from").GetString());
    }

    [SkippableFact]
    public async Task unauthenticated_requests_are_rejected()
    {
        NeedDb();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Send(HttpMethod.Get, "/api/beauty/specialists")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Send(HttpMethod.Get, "/api/beauty/absences")).StatusCode);
    }

    [SkippableFact]
    public async Task manager_updates_profile_services_and_schedule_with_validation()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var s2 = await CreateServiceAsync(c, "Pedicure", 60);
        var master = await CreateMasterAsync(c, [c.Salon.ServiceId], withHours: false);

        var put = await Api(Put, $"/api/beauty/specialists/{master}", c.Admin, new { name = "Renamed", phone = "+380 67 000", position = "Colorist" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await J(put);
        Assert.Equal("Renamed", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean()); // isActive не передано = без змін

        var svc = await Api(Put, $"/api/beauty/specialists/{master}/services", c.Admin, new { serviceIds = new[] { s2 } });
        Assert.Equal(s2, (await J(svc)).GetProperty("services")[0].GetProperty("id").GetGuid());
        Assert.Equal("service_not_found",
            await Code(await Api(Put, $"/api/beauty/specialists/{master}/services", c.Admin, new { serviceIds = new[] { Guid.NewGuid() } })));

        var bad = await Api(Put, $"/api/beauty/specialists/{master}/schedule", c.Admin,
            new { locationId = c.Salon.LocationId, workingHours = new { mon = new[] { new { from = "18:00", to = "09:00" } } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("invalid_working_hours", await Code(bad));
        var good = await Api(Put, $"/api/beauty/specialists/{master}/schedule", c.Admin,
            new { locationId = c.Salon.LocationId, workingHours = new { mon = new[] { new { from = "10:00", to = "12:00" } } } });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("10:00", (await J(good)).GetProperty("locations")[0].GetProperty("workingHours").GetProperty("mon")[0].GetProperty("from").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await Api(Put, $"/api/beauty/specialists/{Guid.NewGuid()}", c.Admin, new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Api(Put, $"/api/beauty/specialists/{master}", c.Admin, new { name = "" })).StatusCode);
    }

    [SkippableFact]
    public async Task admin_cannot_modify_profile_linked_to_owner_but_owner_can()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var ownerProfile = await CreateMasterAsync(c, [c.Salon.ServiceId], name: "Owner Master");
        await using (var ctx = _fx.Db.CreateContext(c.Tenant.TenantId))
        {
            var owner = await ctx.Users.FirstAsync(u => u.Id == c.Tenant.OwnerId);
            owner.SpecialistId = ownerProfile;
            await ctx.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await Api(Put, $"/api/beauty/specialists/{ownerProfile}", c.Admin, new { name = "Pwned", isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Api(Put, $"/api/beauty/specialists/{ownerProfile}/services", c.Admin, new { serviceIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Api(Put, $"/api/beauty/specialists/{ownerProfile}/schedule", c.Admin, new { locationId = c.Salon.LocationId, workingHours = new { } })).StatusCode);
        var abs = await Api(HttpMethod.Post, $"/api/beauty/specialists/{ownerProfile}/absences", c.Admin,
            new { type = "sick", dateFrom = Day(5), dateTo = Day(5) });
        Assert.Equal(HttpStatusCode.Forbidden, abs.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Api(HttpMethod.Post, $"/api/beauty/specialists/{ownerProfile}/invite", c.Admin, new { email = "x@y.test" })).StatusCode);

        var own = await Api(Put, $"/api/beauty/specialists/{ownerProfile}", c.Owner, new { name = "Owner Renamed" });
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("Owner Renamed", (await J(own)).GetProperty("name").GetString());
    }

    // ---------- запрошення ----------

    [SkippableFact]
    public async Task invite_links_user_to_profile_and_token_logs_in_as_that_specialist()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c, [c.Salon.ServiceId]);
        var email = $"master-{Guid.NewGuid():N}"[..20] + "@" + c.Tenant.Slug + ".test";

        var inv = await Api(HttpMethod.Post, $"/api/beauty/specialists/{master}/invite", c.Admin, new { email });
        Assert.Equal(HttpStatusCode.Created, inv.StatusCode);
        var created = await Read<InviteCreatedDto>(inv);
        Assert.Equal(master, created.Invite.SpecialistId);
        Assert.Equal(Roles.Specialist, created.Invite.Role);

        var accept = await _fx.Send(HttpMethod.Post, "/api/auth/invites/accept", new { token = created.Token, fullName = "Master", password = Password });
        Assert.Equal(HttpStatusCode.Created, accept.StatusCode);
        var login = await _fx.LoginAsync(c.Tenant.Slug, email);
        Assert.Equal(master, login.User.SpecialistId);

        var profile = await J(await Api(HttpMethod.Get, $"/api/beauty/specialists/{master}", c.Admin));
        Assert.True(profile.GetProperty("hasAccount").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict,
            (await Api(HttpMethod.Post, $"/api/beauty/specialists/{master}/invite", c.Admin, new { email = "other-" + email })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Api(HttpMethod.Post, $"/api/beauty/specialists/{Guid.NewGuid()}/invite", c.Admin, new { email })).StatusCode);
    }

    // ---------- слоти: призначені послуги, деактивація ----------

    [SkippableFact]
    public async Task slots_and_booking_respect_assigned_services_only()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var other = await CreateServiceAsync(c, "Other", 30);
        var master = await CreateMasterAsync(c, [c.Salon.ServiceId]);

        Assert.NotEmpty((await SlotsAsync(c.Owner, c, master, c.Salon.ServiceId, 3)).EnumerateArray());
        Assert.Empty((await SlotsAsync(c.Owner, c, master, other, 3)).EnumerateArray());

        var r = await Api(HttpMethod.Post, "/api/beauty/appointments", c.Owner, Booking(c, master, other, At(3)));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("specialist_unavailable", await Code(r));

        // призначили -> слоти з'явились, запис проходить
        await Api(Put, $"/api/beauty/specialists/{master}/services", c.Admin, new { serviceIds = new[] { c.Salon.ServiceId, other } });
        Assert.NotEmpty((await SlotsAsync(c.Owner, c, master, other, 3)).EnumerateArray());
        Assert.Equal(HttpStatusCode.Created, (await Api(HttpMethod.Post, "/api/beauty/appointments", c.Owner, Booking(c, master, other, At(3)))).StatusCode);
    }

    [SkippableFact]
    public async Task deactivated_specialist_has_no_slots_keeps_appointments_and_can_be_reactivated()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c, [c.Salon.ServiceId]);
        var apptId = await BookAsync(c, master, c.Salon.ServiceId, At(4));

        var off = await Api(Put, $"/api/beauty/specialists/{master}", c.Admin, new { name = "New Master", isActive = false });
        Assert.False((await J(off)).GetProperty("isActive").GetBoolean());

        Assert.Empty((await SlotsAsync(c.Owner, c, master, c.Salon.ServiceId, 3)).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Get, $"/api/beauty/appointments/{apptId}", c.Owner)).StatusCode); // записи не видалено

        var pub = await _public.GetAsync($"/api/public/{c.Tenant.Slug}/locations/{c.Salon.LocationId}/specialists");
        Assert.DoesNotContain(master.ToString(), await pub.Content.ReadAsStringAsync());

        await Api(Put, $"/api/beauty/specialists/{master}", c.Admin, new { name = "New Master", isActive = true });
        Assert.NotEmpty((await SlotsAsync(c.Owner, c, master, c.Salon.ServiceId, 3)).EnumerateArray());
    }

    // ---------- відсутності ----------

    [SkippableFact]
    public async Task approved_absence_blocks_slots_and_booking_returns_conflicts_for_existing_appointments_untouched()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c, [c.Salon.ServiceId]);
        var existing = await BookAsync(c, master, c.Salon.ServiceId, At(5));

        var created = await CreateAbsenceAsync(c.Admin, master, 5, 6);
        Assert.Equal("approved", created.GetProperty("status").GetString());
        var conflict = Assert.Single(created.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(existing, conflict.GetProperty("appointmentId").GetGuid());
        Assert.Equal("Haircut", conflict.GetProperty("serviceName").GetString());

        // наявний запис не чіпаємо
        var appt = await J(await Api(HttpMethod.Get, $"/api/beauty/appointments/{existing}", c.Owner));
        Assert.Equal("confirmed", appt.GetProperty("status").GetString());

        Assert.Empty((await SlotsAsync(c.Owner, c, master, c.Salon.ServiceId, 5)).EnumerateArray());
        Assert.Empty((await SlotsAsync(c.Owner, c, master, c.Salon.ServiceId, 6)).EnumerateArray());
        Assert.NotEmpty((await SlotsAsync(c.Owner, c, master, c.Salon.ServiceId, 7)).EnumerateArray());
        Assert.NotEmpty((await SlotsAsync(c.Owner, c, master, c.Salon.ServiceId, 4)).EnumerateArray());

        var book = await Api(HttpMethod.Post, "/api/beauty/appointments", c.Owner, Booking(c, master, c.Salon.ServiceId, At(6, 12), "+380509998877"));
        Assert.Equal(HttpStatusCode.Conflict, book.StatusCode);
        Assert.Equal("specialist_unavailable", await Code(book));

        // скасування відсутності повертає слоти
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("cancelled", (await J(await Api(HttpMethod.Post, $"/api/beauty/absences/{id}/cancel", c.Admin))).GetProperty("status").GetString());
        Assert.NotEmpty((await SlotsAsync(c.Owner, c, master, c.Salon.ServiceId, 6)).EnumerateArray());
    }

    [SkippableFact]
    public async Task overlapping_active_absence_is_409_absence_overlap()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c, [c.Salon.ServiceId]);
        await CreateAbsenceAsync(c.Admin, master, 10, 12);

        var overlap = await Api(HttpMethod.Post, $"/api/beauty/specialists/{master}/absences", c.Admin,
            new { type = "vacation", dateFrom = Day(12), dateTo = Day(14) });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Equal("absence_overlap", await Code(overlap));
        await CreateAbsenceAsync(c.Admin, master, 13, 14, "vacation"); // впритул — можна
    }

    [SkippableFact]
    public async Task absence_validation_returns_422()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c, [c.Salon.ServiceId]);
        async Task<string> Try(object body)
        {
            var r = await Api(HttpMethod.Post, $"/api/beauty/specialists/{master}/absences", c.Admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            return await Code(r);
        }
        Assert.Equal("invalid_type", await Try(new { type = "holiday", dateFrom = Day(1), dateTo = Day(1) }));
        Assert.Equal("invalid_dates", await Try(new { type = "sick", dateFrom = Day(3), dateTo = Day(2) }));
        Assert.Equal("validation_failed", await Try(new { type = "sick", dateFrom = Day(1), dateTo = Day(1), note = new string('x', 501) }));
    }

    [SkippableFact]
    public async Task specialist_request_flow_note_visibility_and_idor()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var a = await CreateMasterAsync(c, [c.Salon.ServiceId], name: "Master A");
        var b = await CreateMasterAsync(c, [c.Salon.ServiceId], name: "Master B");
        var spA = await SpecialistLoginAsync(c, a);
        var spB = await SpecialistLoginAsync(c, b);

        // запит на власну відсутність -> requested, слоти ще працюють, conflicts не віддаються
        var req = await CreateAbsenceAsync(spA.AccessToken, a, 8, 8, "vacation", "family reasons");
        Assert.Equal("requested", req.GetProperty("status").GetString());
        Assert.False(req.TryGetProperty("conflicts", out _));
        Assert.Equal("family reasons", req.GetProperty("note").GetString()); // автор бачить своє
        Assert.NotEmpty((await SlotsAsync(spA.AccessToken, c, a, c.Salon.ServiceId, 8)).EnumerateArray());
        var id = req.GetProperty("id").GetGuid();

        // IDOR: у чужий профіль створити не можна, чужий запит не бачить/не скасовує
        Assert.Equal(HttpStatusCode.Forbidden, (await Api(HttpMethod.Post, $"/api/beauty/specialists/{b}/absences", spA.AccessToken,
            new { type = "sick", dateFrom = Day(20), dateTo = Day(20) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Api(HttpMethod.Post, $"/api/beauty/specialists/{Guid.NewGuid()}/absences", spA.AccessToken,
            new { type = "sick", dateFrom = Day(20), dateTo = Day(20) })).StatusCode); // існування не розкривається
        var foreign = await Api(HttpMethod.Post, $"/api/beauty/absences/{id}/cancel", spB.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        // колега не бачить чужий requested; керівник бачить із note
        var listB = await (await Api(HttpMethod.Get, $"/api/beauty/absences?from={Day(1)}&to={Day(30)}", spB.AccessToken)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(id.ToString(), listB);
        var listOwner = await J(await Api(HttpMethod.Get, $"/api/beauty/absences?from={Day(1)}&to={Day(30)}", c.Owner));
        Assert.Equal("family reasons", listOwner.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id).GetProperty("note").GetString());

        // працівник не може затверджувати/відхиляти
        Assert.Equal(HttpStatusCode.Forbidden, (await Api(HttpMethod.Post, $"/api/beauty/absences/{id}/approve", spA.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Api(HttpMethod.Post, $"/api/beauty/absences/{id}/reject", spB.AccessToken)).StatusCode);

        // керівник затверджує: слоти зникають, є conflicts[]
        await BookAsync(c, a, c.Salon.ServiceId, At(8));
        var approve = await Api(HttpMethod.Post, $"/api/beauty/absences/{id}/approve", c.Admin);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approved = await J(approve);
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Single(approved.GetProperty("conflicts").EnumerateArray());
        Assert.Empty((await SlotsAsync(c.Owner, c, a, c.Salon.ServiceId, 8)).EnumerateArray());
        Assert.Equal("absence_not_pending", await Code(await Api(HttpMethod.Post, $"/api/beauty/absences/{id}/approve", c.Admin)));

        // тип бачить усіх, note колеги — ні; автор і керівник бачать
        var listB2 = await J(await Api(HttpMethod.Get, $"/api/beauty/absences?from={Day(1)}&to={Day(30)}", spB.AccessToken));
        var seenByB = listB2.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal("vacation", seenByB.GetProperty("type").GetString());
        Assert.False(seenByB.TryGetProperty("note", out _));
        Assert.DoesNotContain("family reasons", listB2.GetRawText());
        var seenByA = (await J(await Api(HttpMethod.Get, $"/api/beauty/absences?from={Day(1)}&to={Day(30)}", spA.AccessToken)))
            .EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal("family reasons", seenByA.GetProperty("note").GetString());

        // автор не може скасувати вже затверджену; керівник може
        Assert.Equal(HttpStatusCode.Forbidden, (await Api(HttpMethod.Post, $"/api/beauty/absences/{id}/cancel", spA.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Post, $"/api/beauty/absences/{id}/cancel", c.Owner)).StatusCode);
    }

    [SkippableFact]
    public async Task author_cancels_own_pending_request_and_reject_keeps_slots()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var a = await CreateMasterAsync(c, [c.Salon.ServiceId]);
        var spA = await SpecialistLoginAsync(c, a);

        var first = await CreateAbsenceAsync(spA.AccessToken, a, 15, 15);
        var cancel = await Api(HttpMethod.Post, $"/api/beauty/absences/{first.GetProperty("id").GetGuid()}/cancel", spA.AccessToken);
        Assert.Equal("cancelled", (await J(cancel)).GetProperty("status").GetString());

        var second = await CreateAbsenceAsync(spA.AccessToken, a, 15, 15);
        var rejected = await Api(HttpMethod.Post, $"/api/beauty/absences/{second.GetProperty("id").GetGuid()}/reject", c.Admin);
        Assert.Equal("rejected", (await J(rejected)).GetProperty("status").GetString());
        Assert.NotEmpty((await SlotsAsync(c.Owner, c, a, c.Salon.ServiceId, 15)).EnumerateArray());
    }

    // ---------- ізоляція tenant ----------

    [SkippableFact]
    public async Task other_tenant_cannot_see_or_touch_staff_and_absences()
    {
        NeedDb();
        var a = await NewCtxAsync();
        var master = await CreateMasterAsync(a, [a.Salon.ServiceId]);
        var absence = await CreateAbsenceAsync(a.Admin, master, 9, 9);
        var absenceId = absence.GetProperty("id").GetGuid();

        var b = await NewCtxAsync();
        Assert.DoesNotContain(master.ToString(), await (await Api(HttpMethod.Get, "/api/beauty/specialists", b.Owner)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Api(HttpMethod.Get, $"/api/beauty/specialists/{master}", b.Owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Api(Put, $"/api/beauty/specialists/{master}", b.Owner, new { name = "Hack" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Api(Put, $"/api/beauty/specialists/{master}/services", b.Owner, new { serviceIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Api(HttpMethod.Post, $"/api/beauty/specialists/{master}/absences", b.Owner, new { type = "sick", dateFrom = Day(1), dateTo = Day(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Api(HttpMethod.Post, $"/api/beauty/absences/{absenceId}/approve", b.Owner)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Api(HttpMethod.Post, $"/api/beauty/absences/{absenceId}/cancel", b.Owner)).StatusCode);
        Assert.DoesNotContain(absenceId.ToString(),
            await (await Api(HttpMethod.Get, $"/api/beauty/absences?from={Day(1)}&to={Day(30)}", b.Owner)).Content.ReadAsStringAsync());

        // чужі локація/послуга не підставляються у власний профіль
        var bad = await Api(HttpMethod.Post, "/api/beauty/specialists", b.Owner,
            new { name = "X", locationIds = new[] { a.Salon.LocationId }, serviceIds = new[] { a.Salon.ServiceId } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    // ---------- RLS нових таблиць ----------

    [SkippableFact]
    public async Task new_tables_have_forced_rls_policy_and_isolate_tenants()
    {
        NeedDb();
        var a = await NewCtxAsync();
        var b = await NewCtxAsync();
        var master = await CreateMasterAsync(a, [a.Salon.ServiceId]);
        await CreateAbsenceAsync(a.Admin, master, 11, 11);

        await using var conn = new NpgsqlConnection(_fx.Db.AppConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT count(*) FROM pg_class c JOIN pg_policies p ON p.tablename = c.relname AND p.policyname = 'tenant_isolation'
            WHERE c.relname IN ('beauty_specialist_services', 'beauty_specialist_absences') AND c.relrowsecurity AND c.relforcerowsecurity", conn))
            Assert.Equal(2L, await cmd.ExecuteScalarAsync());

        // без tenant-контексту рядків немає
        await using (var cmd = new NpgsqlCommand("SELECT (SELECT count(*) FROM beauty_specialist_absences) + (SELECT count(*) FROM beauty_specialist_services)", conn))
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());

        // tenant B бачить нуль рядків tenant A на рівні DbContext
        await using (var ctxB = _fx.Db.CreateContext(b.Tenant.TenantId))
        {
            Assert.DoesNotContain(await ctxB.SpecialistAbsences.AsNoTracking().ToListAsync(), x => x.SpecialistId == master);
            Assert.DoesNotContain(await ctxB.SpecialistServices.AsNoTracking().ToListAsync(), x => x.SpecialistId == master);
        }
        await using (var ctxA = _fx.Db.CreateContext(a.Tenant.TenantId))
        {
            Assert.Single(await ctxA.SpecialistAbsences.AsNoTracking().Where(x => x.SpecialistId == master).ToListAsync());
            Assert.Single(await ctxA.SpecialistServices.AsNoTracking().Where(x => x.SpecialistId == master).ToListAsync());
        }

        // WITH CHECK: tenant B не може вставити рядок з tenant_id чужого tenant; композитний FK не дає посилатися на чужого майстра
        await using (var ctxB = _fx.Db.CreateContext(b.Tenant.TenantId))
        {
            ctxB.SpecialistAbsences.Add(new SpecialistAbsence
            {
                Id = Guid.NewGuid(), TenantId = a.Tenant.TenantId, SpecialistId = master, Type = "sick", Status = "approved",
                DateFrom = new DateOnly(2031, 1, 1), DateTo = new DateOnly(2031, 1, 1), RequestedByUserId = a.Tenant.OwnerId,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => ctxB.SaveChangesAsync());
        }
        await using (var ctxB = _fx.Db.CreateContext(b.Tenant.TenantId))
        {
            ctxB.SpecialistServices.Add(new SpecialistServiceLink { SpecialistId = master, ServiceId = b.Salon.ServiceId });
            await Assert.ThrowsAsync<DbUpdateException>(() => ctxB.SaveChangesAsync());
        }
    }

    [SkippableFact]
    public async Task database_constraints_reject_bad_absence_rows()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c, [c.Salon.ServiceId]);

        async Task<PostgresException> Insert(Action<SpecialistAbsence> tweak)
        {
            await using var ctx = _fx.Db.CreateContext(c.Tenant.TenantId);
            var row = new SpecialistAbsence
            {
                Id = Guid.NewGuid(), SpecialistId = master, Type = "sick", Status = "approved", RequestedByUserId = c.Tenant.OwnerId,
                DateFrom = new DateOnly(2031, 5, 1), DateTo = new DateOnly(2031, 5, 3),
            };
            tweak(row);
            ctx.SpecialistAbsences.Add(row);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            return Assert.IsType<PostgresException>(ex.InnerException);
        }

        Assert.Equal("23514", (await Insert(r => r.DateTo = new DateOnly(2031, 4, 30))).SqlState);   // date_to < date_from
        Assert.Equal("23514", (await Insert(r => r.Type = "nap")).SqlState);
        Assert.Equal("23514", (await Insert(r => r.Status = "maybe")).SqlState);
        Assert.Equal("22001", (await Insert(r => r.Note = new string('x', 501))).SqlState);   // varchar(500)
        Assert.Equal("23503", (await Insert(r => r.RequestedByUserId = Guid.NewGuid())).SqlState);     // композитний FK на users

        // exclusion constraint ловить гонку: два перетини, що обійшли перевірку сервісу
        await using (var ctx = _fx.Db.CreateContext(c.Tenant.TenantId))
        {
            ctx.SpecialistAbsences.Add(new SpecialistAbsence
            {
                Id = Guid.NewGuid(), SpecialistId = master, Type = "sick", Status = "approved", RequestedByUserId = c.Tenant.OwnerId,
                DateFrom = new DateOnly(2031, 5, 1), DateTo = new DateOnly(2031, 5, 3),
            });
            await ctx.SaveChangesAsync();
        }
        Assert.Equal("23P01", (await Insert(r => { r.DateFrom = new DateOnly(2031, 5, 3); r.DateTo = new DateOnly(2031, 5, 4); r.Status = "requested"; })).SqlState);
        // cancelled не блокує
        await using (var ctx = _fx.Db.CreateContext(c.Tenant.TenantId))
        {
            ctx.SpecialistAbsences.Add(new SpecialistAbsence
            {
                Id = Guid.NewGuid(), SpecialistId = master, Type = "sick", Status = "cancelled", RequestedByUserId = c.Tenant.OwnerId,
                DateFrom = new DateOnly(2031, 5, 1), DateTo = new DateOnly(2031, 5, 3),
            });
            await ctx.SaveChangesAsync();
        }
    }

    // ---------- публічний API ----------

    private async Task<JsonElement> PubGet(Ctx c, string path)
    {
        var r = await _public.GetAsync($"/api/public/{c.Tenant.Slug}/{path}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await J(r);
    }

    [SkippableFact]
    public async Task public_api_lists_only_bookable_specialists_with_assigned_services()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var manicure = await CreateServiceAsync(c, "Manicure", 30);
        var unassigned = await CreateServiceAsync(c, "Nobody offers this", 30);
        var good = await CreateMasterAsync(c, [manicure], name: "Good Master");
        var noHours = await CreateMasterAsync(c, [manicure], withHours: false, name: "No Hours");
        var noServices = await CreateMasterAsync(c, [], name: "No Services");
        var emptyHours = await CreateMasterAsync(c, [manicure], name: "Empty Hours");
        await Api(Put, $"/api/beauty/specialists/{emptyHours}/schedule", c.Admin, new { locationId = c.Salon.LocationId, workingHours = new { } });
        var inactive = await CreateMasterAsync(c, [manicure], name: "Inactive");
        await Api(Put, $"/api/beauty/specialists/{inactive}", c.Admin, new { name = "Inactive", isActive = false });

        var specialists = (await PubGet(c, $"locations/{c.Salon.LocationId}/specialists")).EnumerateArray()
            .Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(good, specialists);
        Assert.Contains(c.Salon.SpecialistId, specialists); // harness-майстер: графік + призначена послуга
        Assert.DoesNotContain(noHours, specialists);
        Assert.DoesNotContain(noServices, specialists);
        Assert.DoesNotContain(emptyHours, specialists);
        Assert.DoesNotContain(inactive, specialists);

        // position має пріоритет над title у публічному вигляді; приватні поля не віддаються
        var raw = (await PubGet(c, $"locations/{c.Salon.LocationId}/specialists")).GetRawText();
        Assert.Contains("Stylist", raw);
        Assert.DoesNotContain("+380501112233", raw);

        var services = (await PubGet(c, $"locations/{c.Salon.LocationId}/services")).EnumerateArray()
            .Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(manicure, services);
        Assert.Contains(c.Salon.ServiceId, services);
        Assert.DoesNotContain(unassigned, services);

        // послуги конкретного майстра — лише його призначені; спеціалісти за послугою — лише ті, хто її надає
        var goodServices = (await PubGet(c, $"locations/{c.Salon.LocationId}/services?specialistId={good}")).EnumerateArray()
            .Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new[] { manicure }, goodServices);
        Assert.Empty((await PubGet(c, $"locations/{c.Salon.LocationId}/services?specialistId={noHours}")).EnumerateArray());
        var offering = (await PubGet(c, $"locations/{c.Salon.LocationId}/specialists?serviceId={manicure}")).EnumerateArray()
            .Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new[] { good }, offering);
    }

    [SkippableFact]
    public async Task public_slots_and_booking_respect_absence_and_assigned_services()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var manicure = await CreateServiceAsync(c, "Manicure", 30);
        var master = await CreateMasterAsync(c, [manicure]);
        await CreateAbsenceAsync(c.Admin, master, 6, 6);

        async Task<JsonElement> PubSlots(Guid service, int ahead) =>
            await PubGet(c, $"slots?locationId={c.Salon.LocationId}&serviceId={service}&date={Day(ahead)}&specialistId={master}");

        Assert.Empty((await PubSlots(manicure, 6)).EnumerateArray());
        Assert.NotEmpty((await PubSlots(manicure, 5)).EnumerateArray());
        Assert.Empty((await PubSlots(c.Salon.ServiceId, 5)).EnumerateArray()); // послуга не призначена цьому майстру

        async Task<HttpResponseMessage> Post(Guid service, DateTimeOffset at)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{c.Tenant.Slug}/appointments")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    locationId = c.Salon.LocationId, specialistId = master, serviceId = service, startsAt = at,
                    client = new { name = "Olena Test", phone = "+380501234567" }, reminder = "none", paymentMethod = "cash",
                }, options: Json),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            return await _public.SendAsync(req);
        }

        // TASK-696: публічний API не розкриває причину (відсутність/послуга не призначена) — те саме, що зайнятий слот.
        var onAbsence = await Post(manicure, At(6));
        Assert.Equal(HttpStatusCode.Conflict, onAbsence.StatusCode);
        Assert.Equal("slot_unavailable", await Code(onAbsence));
        var wrongService = await Post(c.Salon.ServiceId, At(5));
        Assert.Equal(HttpStatusCode.Conflict, wrongService.StatusCode);
        Assert.Equal("slot_unavailable", await Code(wrongService));
        Assert.Equal(HttpStatusCode.Created, (await Post(manicure, At(5))).StatusCode);
    }
}
