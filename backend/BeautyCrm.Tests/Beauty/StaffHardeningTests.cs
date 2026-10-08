using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyStaff;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Regression;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-696 (виправлення за аудитом TASK-695): деактивація працівника, одне активне запрошення, однакова відповідь
/// публічного API, advisory-lock відсутність↔запис, ліміти запитів, хто скасував. Повний HTTP-конвеєр на реальному
/// PostgreSQL (роль без BYPASSRLS). Без БД пропускаються.
/// </summary>
public sealed class StaffHardeningTests : IClassFixture<AuthApiFixture>, IDisposable
{
    private static readonly HttpMethod Put = HttpMethod.Put;
    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly HttpClient _public = null!;

    public StaffHardeningTests(AuthApiFixture fx)
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

    private Task<HttpResponseMessage> Api(HttpMethod m, string url, string token, object? body = null, HttpClient? client = null) =>
        _fx.Send(m, url, body, token, client: client);

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;

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

    private async Task<Guid> CreateMasterAsync(Ctx c, string name = "New Master")
    {
        var r = await Api(HttpMethod.Post, "/api/beauty/specialists", c.Admin, new
        {
            name, phone = "+380501112233", position = "Stylist",
            locationIds = new[] { c.Salon.LocationId }, serviceIds = new[] { c.Salon.ServiceId },
            workingHours = JsonSerializer.Deserialize<JsonElement>(RegressionHarness.AllDays),
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await J(r)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SetActive(Ctx c, Guid master, bool active) =>
        Api(Put, $"/api/beauty/specialists/{master}", c.Admin, new { name = "New Master", isActive = active });

    private async Task<HttpResponseMessage> InviteAsync(Ctx c, Guid master, string? email = null) =>
        await Api(HttpMethod.Post, $"/api/beauty/specialists/{master}/invite", c.Admin,
            new { email = email ?? $"inv-{Guid.NewGuid():N}"[..16] + "@" + c.Tenant.Slug + ".test" });

    private Task<HttpResponseMessage> Accept(string token) =>
        _fx.Send(HttpMethod.Post, "/api/auth/invites/accept", new { token, fullName = "Invitee", password = Password });

    private async Task<HttpResponseMessage> BookRawAsync(Ctx c, Guid master, DateTimeOffset at, string phone = "+380501112233") =>
        await Api(HttpMethod.Post, "/api/beauty/appointments", c.Owner, new
        {
            locationId = c.Salon.LocationId, specialistId = master, serviceId = c.Salon.ServiceId, startsAt = at,
            client = new { name = "Anna", phone }, reminder = "none", paymentMethod = "cash",
        });

    private Task<HttpResponseMessage> AbsenceRawAsync(string token, Guid master, int fromAhead, int toAhead, string type = "sick",
        HttpClient? client = null) =>
        Api(HttpMethod.Post, $"/api/beauty/specialists/{master}/absences", token,
            new { type, dateFrom = Day(fromAhead), dateTo = Day(toAhead), note = "n" }, client);

    // ---------- 1. деактивація ----------

    [SkippableFact]
    public async Task deactivation_disables_user_revokes_refresh_and_pending_invites_and_blocks_actions()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c, "With Account");
        var sp = await _fx.InviteAndLoginAsync(c.Tenant, c.Owner, Roles.Specialist, master);
        var withInvite = await CreateMasterAsync(c, "With Invite");
        var invite = await J(await InviteAsync(c, withInvite));
        var inviteToken = invite.GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await SetActive(c, master, false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetActive(c, withInvite, false)).StatusCode);

        // refresh-токен відкликано, логін неможливий
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = sp.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = c.Tenant.Slug, email = sp.User.Email, password = Password })).StatusCode);
        var users = (await J(await Api(HttpMethod.Get, "/api/users", c.Owner))).EnumerateArray().ToList();
        Assert.False(users.Single(u => u.GetProperty("id").GetGuid() == sp.User.Id).GetProperty("isActive").GetBoolean());

        // pending-запрошення відкликано: прийняти його вже не можна
        Assert.NotEqual(HttpStatusCode.Created, (await Accept(inviteToken)).StatusCode);
        var invites = (await J(await Api(HttpMethod.Get, "/api/invites", c.Owner))).EnumerateArray().ToList();
        Assert.Equal("revoked", invites.Single(i => i.GetProperty("specialistId").ValueKind == JsonValueKind.String && i.GetProperty("specialistId").GetGuid() == withInvite).GetProperty("status").GetString());

        // ще чинний access-токен не дає діяти від імені деактивованого профілю
        var booking = await Api(HttpMethod.Get, "/api/beauty/appointments", sp.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, booking.StatusCode);
        Assert.Equal("specialist_inactive", await Code(booking));
        var absence = await AbsenceRawAsync(sp.AccessToken, master, 3, 3);
        Assert.Equal(HttpStatusCode.Forbidden, absence.StatusCode);
        Assert.Equal("specialist_inactive", await Code(absence));

        // керівник бачить профіль неактивним, записи не чіпаються
        Assert.False((await J(await Api(HttpMethod.Get, $"/api/beauty/specialists/{master}", c.Admin))).GetProperty("isActive").GetBoolean());
    }

    [SkippableFact]
    public async Task reactivation_does_not_enable_user_until_explicit_status_change()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);
        var sp = await _fx.InviteAndLoginAsync(c.Tenant, c.Owner, Roles.Specialist, master);

        Assert.Equal(HttpStatusCode.OK, (await SetActive(c, master, false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetActive(c, master, true)).StatusCode);

        var login = () => _fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = c.Tenant.Slug, email = sp.User.Email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, (await login()).StatusCode); // профіль активний, користувач — ні

        var enable = await Api(HttpMethod.Patch, $"/api/users/{sp.User.Id}/status", c.Owner, new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        var again = await login();
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var token = (await Read<TokenResponse>(again)).AccessToken;
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Get, "/api/beauty/appointments", token)).StatusCode);
    }

    [SkippableFact]
    public async Task deactivating_a_profile_linked_to_owner_does_not_lock_the_owner_out()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c, "Owner Profile");
        await using (var ctx = _fx.Db.CreateContext(c.Tenant.TenantId))
            await ctx.Users.Where(u => u.Id == c.Tenant.OwnerId).ExecuteUpdateAsync(s => s.SetProperty(u => u.SpecialistId, master));

        Assert.Equal(HttpStatusCode.OK, (await Api(Put, $"/api/beauty/specialists/{master}", c.Owner, new { name = "Owner Profile", isActive = false })).StatusCode);
        var login = await _fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = c.Tenant.Slug, email = c.Tenant.OwnerEmail, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [SkippableFact]
    public async Task invite_for_inactive_profile_is_409_specialist_inactive_on_both_endpoints()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await SetActive(c, master, false)).StatusCode);

        var viaStaff = await InviteAsync(c, master);
        Assert.Equal(HttpStatusCode.Conflict, viaStaff.StatusCode);
        Assert.Equal("specialist_inactive", await Code(viaStaff));

        var viaGeneric = await Api(HttpMethod.Post, "/api/invites", c.Owner,
            new { email = $"x-{Guid.NewGuid():N}"[..14] + "@" + c.Tenant.Slug + ".test", role = "specialist", specialistId = master });
        Assert.Equal(HttpStatusCode.Conflict, viaGeneric.StatusCode);
        Assert.Equal("specialist_inactive", await Code(viaGeneric));

        // після повторної активації запрошення знову можливе
        Assert.Equal(HttpStatusCode.OK, (await SetActive(c, master, true)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(c, master)).StatusCode);
    }

    // ---------- 2. одне активне запрошення ----------

    [SkippableFact]
    public async Task new_invite_revokes_previous_pending_for_the_same_specialist_and_is_not_cacheable()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);

        var first = await InviteAsync(c, master);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Contains("no-store", first.Headers.CacheControl?.ToString());
        var firstToken = (await J(first)).GetProperty("token").GetString()!;

        var second = await InviteAsync(c, master); // інший email
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Contains("no-store", second.Headers.CacheControl?.ToString());
        var secondToken = (await J(second)).GetProperty("token").GetString()!;

        var list = (await J(await Api(HttpMethod.Get, "/api/invites", c.Owner))).EnumerateArray()
            .Where(i => i.GetProperty("specialistId").ValueKind == JsonValueKind.String && i.GetProperty("specialistId").GetGuid() == master).ToList();
        Assert.Equal(2, list.Count);
        Assert.Single(list, i => i.GetProperty("status").GetString() == "pending");
        Assert.Single(list, i => i.GetProperty("status").GetString() == "revoked");

        Assert.NotEqual(HttpStatusCode.Created, (await Accept(firstToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Accept(secondToken)).StatusCode);
    }

    [SkippableFact]
    public async Task concurrent_invites_for_one_specialist_leave_exactly_one_pending()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => InviteAsync(c, master)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        var list = (await J(await Api(HttpMethod.Get, "/api/invites", c.Owner))).EnumerateArray()
            .Where(i => i.GetProperty("specialistId").ValueKind == JsonValueKind.String && i.GetProperty("specialistId").GetGuid() == master).ToList();
        Assert.Equal(6, list.Count);
        Assert.Single(list, i => i.GetProperty("status").GetString() == "pending");
    }

    [SkippableFact]
    public async Task generic_invite_endpoint_is_not_cacheable_and_lists_specialist_id()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);
        var r = await Api(HttpMethod.Post, "/api/invites", c.Owner,
            new { email = $"g-{Guid.NewGuid():N}"[..14] + "@" + c.Tenant.Slug + ".test", role = "specialist", specialistId = master });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Contains("no-store", r.Headers.CacheControl?.ToString());
        var invite = (await J(r)).GetProperty("invite");
        Assert.Equal(master, invite.GetProperty("specialistId").GetGuid());
        var listed = (await J(await Api(HttpMethod.Get, "/api/invites", c.Owner))).EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == invite.GetProperty("id").GetGuid());
        Assert.Equal(master, listed.GetProperty("specialistId").GetGuid());
    }

    // ---------- 3. публічний API однаково відповідає ----------

    [SkippableFact]
    public async Task public_api_gives_identical_response_for_busy_slot_absence_and_unassigned_service()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);
        Assert.Equal(HttpStatusCode.Created, (await AbsenceRawAsync(c.Admin, master, 6, 6)).StatusCode);

        // інша послуга, не призначена майстру
        var other = await Api(HttpMethod.Post, "/api/beauty/services", c.Owner, new { name = "Other", durationMinutes = 30 });
        var otherService = (await J(other)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK,
            (await Api(Put, $"/api/beauty/services/{otherService}/prices", c.Owner, new { locationId = (Guid?)null, price = 100m })).StatusCode);

        // зайнятий слот
        Assert.Equal(HttpStatusCode.Created, (await BookRawAsync(c, master, At(5))).StatusCode);

        async Task<(HttpStatusCode Status, string Body)> Post(Guid service, DateTimeOffset at)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{c.Tenant.Slug}/appointments")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    locationId = c.Salon.LocationId, specialistId = master, serviceId = service, startsAt = at,
                    client = new { name = "Olena Test", phone = "+380509998877" }, reminder = "none", paymentMethod = "cash",
                }, options: Json),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            var resp = await _public.SendAsync(req);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }

        var busy = await Post(c.Salon.ServiceId, At(5));
        var onAbsence = await Post(c.Salon.ServiceId, At(6));
        var unassigned = await Post(otherService, At(4));
        Assert.Equal(HttpStatusCode.Conflict, busy.Status);
        Assert.Contains("slot_unavailable", busy.Body);
        Assert.Equal(busy.Status, onAbsence.Status);
        Assert.Equal(busy.Body, onAbsence.Body);
        Assert.Equal(busy.Status, unassigned.Status);
        Assert.Equal(busy.Body, unassigned.Body);
        Assert.DoesNotContain("specialist_unavailable", onAbsence.Body + unassigned.Body);

        // staff API розрізняє причину
        var staff = await BookRawAsync(c, master, At(6));
        Assert.Equal("specialist_unavailable", await Code(staff));
    }

    // ---------- 4. advisory lock відсутність <-> запис ----------

    [SkippableFact]
    public async Task absence_and_booking_race_never_loses_a_conflict()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);

        for (var round = 0; round < 12; round++)
        {
            var ahead = 4 + round; // кожен раунд — свій день
            using var gate = new ManualResetEventSlim(false);
            var book = Task.Run(async () => { gate.Wait(); return await BookRawAsync(c, master, At(ahead), $"+38050111{round:D4}"); });
            var absence = Task.Run(async () => { gate.Wait(); return await AbsenceRawAsync(c.Admin, master, ahead, ahead); });
            gate.Set();
            var (b, a) = (await book, await absence);

            Assert.Equal(HttpStatusCode.Created, a.StatusCode);
            if (b.StatusCode == HttpStatusCode.Created)
            {
                var id = (await J(b)).GetProperty("id").GetGuid();
                var conflicts = (await J(a)).GetProperty("conflicts").EnumerateArray().Select(x => x.GetProperty("appointmentId").GetGuid()).ToList();
                Assert.Contains(id, conflicts); // запис потрапив до conflicts[] — не загубився
            }
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, b.StatusCode);
                Assert.Equal("specialist_unavailable", await Code(b));
                Assert.Empty((await J(a)).GetProperty("conflicts").EnumerateArray());
            }
        }
    }

    [SkippableFact]
    public async Task booking_reschedule_and_absence_wait_for_the_specialist_advisory_lock()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);
        var apptResp = await BookRawAsync(c, master, At(3));
        Assert.Equal(HttpStatusCode.Created, apptResp.StatusCode);
        var apptId = (await J(apptResp)).GetProperty("id").GetGuid();
        var key = $"{c.Tenant.TenantId}:specialist:{master}";

        async Task AssertBlockedUntilReleased(Func<Task<HttpResponseMessage>> call, HttpStatusCode expected)
        {
            await using var conn = new NpgsqlConnection(_fx.Db.AdminConnectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", conn, tx))
            {
                cmd.Parameters.AddWithValue("k", key);
                await cmd.ExecuteNonQueryAsync();
            }
            var pending = call();
            await Task.Delay(1500);
            Assert.False(pending.IsCompleted, "the request must wait for the specialist lock");
            await tx.CommitAsync();
            Assert.Equal(expected, (await pending.WaitAsync(TimeSpan.FromSeconds(20))).StatusCode);
        }

        await AssertBlockedUntilReleased(() => BookRawAsync(c, master, At(7), "+380501110001"), HttpStatusCode.Created);
        await AssertBlockedUntilReleased(
            () => Api(HttpMethod.Patch, $"/api/beauty/appointments/{apptId}", c.Owner, new { startsAt = At(8) }), HttpStatusCode.OK);
        await AssertBlockedUntilReleased(() => AbsenceRawAsync(c.Admin, master, 20, 20), HttpStatusCode.Created);
        var absenceId = (await J(await Api(HttpMethod.Get, $"/api/beauty/absences?specialistId={master}&from={Day(20)}&to={Day(20)}", c.Admin)))
            .EnumerateArray().Single().GetProperty("id").GetGuid();
        await AssertBlockedUntilReleased(() => Api(HttpMethod.Post, $"/api/beauty/absences/{absenceId}/cancel", c.Admin), HttpStatusCode.OK);
    }

    // ---------- 5. ліміти ----------

    [SkippableFact]
    public async Task specialist_cannot_exceed_pending_absence_request_limit()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);
        var sp = await _fx.InviteAndLoginAsync(c.Tenant, c.Owner, Roles.Specialist, master);

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Created, (await AbsenceRawAsync(sp.AccessToken, master, 3 + 2 * i, 3 + 2 * i, "day_off")).StatusCode);
        var over = await AbsenceRawAsync(sp.AccessToken, master, 40, 40, "day_off");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Equal("too_many_requests", await Code(over));

        // керівник створює approved — ліміт requested його не стосується
        Assert.Equal(HttpStatusCode.Created, (await AbsenceRawAsync(c.Admin, master, 40, 40)).StatusCode);

        // рішення/скасування звільняє місце
        var first = (await J(await Api(HttpMethod.Get, $"/api/beauty/absences?specialistId={master}&from={Day(3)}&to={Day(3)}", sp.AccessToken)))
            .EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Post, $"/api/beauty/absences/{first}/cancel", sp.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AbsenceRawAsync(sp.AccessToken, master, 42, 42, "day_off")).StatusCode);
    }

    [SkippableFact]
    public async Task absence_creation_is_rate_limited_per_user()
    {
        NeedDb();
        using var factory = _fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("Staff:AbsenceRateLimit:PermitLimit", "3");
            b.UseSetting("Staff:AbsenceRateLimit:WindowSeconds", "600");
        });
        using var client = factory.CreateClient();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Created, (await AbsenceRawAsync(c.Admin, master, 3 + 2 * i, 3 + 2 * i, client: client)).StatusCode);
        var limited = await AbsenceRawAsync(c.Admin, master, 30, 30, client: client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await Code(limited));
        // ліміт на користувача: інший користувач (owner) не страждає
        Assert.Equal(HttpStatusCode.Created, (await AbsenceRawAsync(c.Owner, master, 30, 30, client: client)).StatusCode);
    }

    // ---------- 7. хто скасував ----------

    [SkippableFact]
    public async Task cancelling_an_absence_records_who_and_when()
    {
        NeedDb();
        var c = await NewCtxAsync();
        var master = await CreateMasterAsync(c);
        var sp = await _fx.InviteAndLoginAsync(c.Tenant, c.Owner, Roles.Specialist, master);

        var approved = (await J(await AbsenceRawAsync(c.Admin, master, 3, 3))).GetProperty("id").GetGuid();
        var requested = (await J(await AbsenceRawAsync(sp.AccessToken, master, 6, 6, "day_off"))).GetProperty("id").GetGuid();
        var adminId = (await J(await Api(HttpMethod.Get, "/api/auth/me", c.Admin))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Post, $"/api/beauty/absences/{approved}/cancel", c.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Api(HttpMethod.Post, $"/api/beauty/absences/{requested}/cancel", sp.AccessToken)).StatusCode);

        await using var ctx = _fx.Db.CreateContext(c.Tenant.TenantId);
        var a = await ctx.SpecialistAbsences.AsNoTracking().SingleAsync(x => x.Id == approved);
        Assert.Equal("cancelled", a.Status);
        Assert.Equal(adminId, a.CancelledByUserId);
        Assert.NotNull(a.CancelledAt);
        var r = await ctx.SpecialistAbsences.AsNoTracking().SingleAsync(x => x.Id == requested);
        Assert.Equal(sp.User.Id, r.CancelledByUserId);
        Assert.NotNull(r.CancelledAt);
        Assert.Null(r.DecidedByUserId); // скасування не підмінює рішення
    }
}

/// <summary>TASK-696: юніт-перевірки без БД (атрибути контролерів, AbsenceService, SQL міграції).</summary>
public class StaffHardeningUnitTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 6, 8, 0, 0, TimeSpan.Zero);
    private readonly FakeStaffStore _store = new();
    private static Actor Specialist(Guid specialistId) => new(Guid.NewGuid(), Guid.NewGuid(), Roles.Specialist, specialistId);
    private static Actor Admin() => new(Guid.NewGuid(), Guid.NewGuid(), Roles.Admin, null);
    private static CreateAbsenceRequest Req(int day) => new("day_off", new DateOnly(2030, 1, day), new DateOnly(2030, 1, day), null);

    [Theory]
    [InlineData(typeof(BeautyCrm.Api.Controllers.StaffController), "Update")]
    [InlineData(typeof(BeautyCrm.Api.Controllers.StaffController), "Invite")]
    [InlineData(typeof(BeautyCrm.Api.Controllers.AbsencesController), "Create")]
    [InlineData(typeof(BeautyCrm.Api.Controllers.UsersController), "CreateInvite")]
    public void write_endpoints_have_a_request_size_limit(Type controller, string action)
    {
        var method = controller.GetMethod(action)!;
        Assert.NotNull(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false).SingleOrDefault());
    }

    [Fact]
    public async Task absence_request_above_the_limit_is_422_too_many_requests_and_configurable()
    {
        var sp = _store.AddSpecialist();
        var sut = new AbsenceService(_store, new FakeClock(Now), new StaffOptions { MaxRequestedAbsencesPerSpecialist = 2 });
        var actor = Specialist(sp.Id);
        Assert.True((await sut.CreateAsync(actor, sp.Id, Req(10), default)).IsOk);
        Assert.True((await sut.CreateAsync(actor, sp.Id, Req(12), default)).IsOk);
        var third = await sut.CreateAsync(actor, sp.Id, Req(14), default);
        Assert.Equal("too_many_requests", third.Error!.Code);
        Assert.Equal(ErrorKind.Validation, third.Error.Kind);
        // керівник створює approved — ліміт requested не діє
        Assert.True((await sut.CreateAsync(Admin(), sp.Id, Req(16), default)).IsOk);
        Assert.Equal(10, new StaffOptions().MaxRequestedAbsencesPerSpecialist);
    }

    [Fact]
    public async Task specialist_of_inactive_profile_cannot_request_absence_but_manager_can_create_for_it()
    {
        var sp = _store.AddSpecialist(active: false);
        var sut = new AbsenceService(_store, new FakeClock(Now));
        var denied = await sut.CreateAsync(Specialist(sp.Id), sp.Id, Req(10), default);
        Assert.Equal(ErrorKind.Forbidden, denied.Error!.Kind);
        Assert.Equal("specialist_inactive", denied.Error.Code);
        Assert.True((await sut.CreateAsync(Admin(), sp.Id, Req(10), default)).IsOk);
    }

    [Fact]
    public async Task cancel_passes_actor_and_time_to_the_store()
    {
        var sp = _store.AddSpecialist();
        var sut = new AbsenceService(_store, new FakeClock(Now));
        var admin = Admin();
        var created = (await sut.CreateAsync(admin, sp.Id, Req(10), default)).Value!;
        Assert.True((await sut.CancelAsync(admin, created.Id, default)).IsOk);
        Assert.Equal(("cancelled", (Guid?)admin.UserId, (DateTimeOffset?)Now), _store.LastTransition);
    }

    [Fact]
    public void hardening_migration_checks_force_rls_with_a_raise_exception()
    {
        var file = FindMigration();
        Assert.Contains("RAISE EXCEPTION", file);
        foreach (var table in new[] { "beauty_specialists", "beauty_services", "beauty_specialist_services", "beauty_specialist_absences" })
            Assert.Contains($"'{table}'", file);
        Assert.Contains("relforcerowsecurity", file);
    }

    private static string FindMigration()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var migrations = Path.Combine(dir.FullName, "BeautyCrm.Infrastructure", "Data", "Migrations");
            if (Directory.Exists(migrations))
                return File.ReadAllText(Directory.GetFiles(migrations, "*_beauty_staff_hardening.cs").Single());
        }
        throw new FileNotFoundException("migration source not found");
    }
}
