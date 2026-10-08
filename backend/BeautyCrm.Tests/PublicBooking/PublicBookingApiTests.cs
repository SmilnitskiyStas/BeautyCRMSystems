using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.PublicBooking;

/// <summary>
/// TASK-688: публічний API запису /api/public/{tenantSlug}/... на реальному PostgreSQL (роль без BYPASSRLS)
/// через повний HTTP-конвеєр. Без БД пропускаються; BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку.
/// </summary>
public sealed class PublicBookingApiTests : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string Phone = "+380501234567";
    private readonly AuthApiFixture _fx;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly HttpClient _client = null!;

    public PublicBookingApiTests(AuthApiFixture fx)
    {
        _fx = fx;
        if (fx.SkipReason is null) _client = NewClient();
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    /// <summary>Окремий хост зі своїми налаштуваннями; ліміти за замовчуванням послаблені, щоб тести не заважали одне одному.</summary>
    private HttpClient NewClient(Action<IWebHostBuilder>? configure = null, int writePermit = 10_000, int tokenPermit = 10_000)
    {
        var f = _fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("PublicBooking:RateLimit:ReadPermit", "10000");
            b.UseSetting("PublicBooking:RateLimit:TokenPermit", tokenPermit.ToString());
            b.UseSetting("PublicBooking:RateLimit:WritePermit", writePermit.ToString());
            configure?.Invoke(b);
        });
        _factories.Add(f);
        return f.CreateClient();
    }

    // ---------- seed / helpers ----------

    private sealed record Seed(TenantSeed Tenant, Guid LocationId, Guid SpecialistId, Guid ServiceId)
    {
        public string Slug => Tenant.Slug;
    }

    private const string Hours =
        """{"mon":[{"from":"08:00","to":"20:00"}],"tue":[{"from":"08:00","to":"20:00"}],"wed":[{"from":"08:00","to":"20:00"}],"thu":[{"from":"08:00","to":"20:00"}],"fri":[{"from":"08:00","to":"20:00"}],"sat":[{"from":"08:00","to":"20:00"}],"sun":[{"from":"08:00","to":"20:00"}]}""";

    private async Task<Seed> SeedAsync(string[]? modules = null, decimal network = 500m, decimal? locationPrice = null, int? promoPercent = null)
    {
        var t = await _fx.CreateTenantAsync(modules);
        await using var ctx = _fx.Db.CreateContext(t.TenantId);
        var loc = new Location { Name = "Main", Address = "Street 1", Phone = "+380440000000", Timezone = "Europe/Kyiv" };
        var sp = new Specialist { FullName = "Master A", Title = "Stylist", Email = "private@x.test", Phone = "+380991112233" };
        var svc = new Service { Name = "Haircut", Description = "Cut", Category = "Hair", DurationMinutes = 60 };
        ctx.AddRange(loc, sp, svc);
        await ctx.SaveChangesAsync();
        ctx.Add(new SpecialistLocation { SpecialistId = sp.Id, LocationId = loc.Id, WorkingHours = Hours });
        ctx.Add(new SpecialistServiceLink { SpecialistId = sp.Id, ServiceId = svc.Id }); // TASK-691: майстер пропонує лише призначені послуги
        ctx.Add(new ServicePrice { ServiceId = svc.Id, LocationId = null, Price = network });
        if (locationPrice is { } lp) ctx.Add(new ServicePrice { ServiceId = svc.Id, LocationId = loc.Id, Price = lp });
        if (promoPercent is { } pp)
        {
            var promo = new Promotion { Name = "Spring", DiscountType = DiscountType.Percent, DiscountValue = pp, IsActive = true };
            ctx.Add(promo);
            await ctx.SaveChangesAsync();
            ctx.Add(new PromotionService { PromotionId = promo.Id, ServiceId = svc.Id });
        }
        await ctx.SaveChangesAsync();
        return new Seed(t, loc.Id, sp.Id, svc.Id);
    }

    private static string Date(int daysAhead = 2) => DateTime.UtcNow.AddDays(daysAhead).ToString("yyyy-MM-dd");

    private async Task<List<DateTimeOffset>> SlotStartsAsync(Seed s, int daysAhead = 2, HttpClient? c = null)
    {
        var r = await (c ?? _client).GetAsync($"/api/public/{s.Slug}/slots?locationId={s.LocationId}&serviceId={s.ServiceId}&date={Date(daysAhead)}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var arr = await Read<JsonElement>(r);
        return arr.EnumerateArray().Select(e => e.GetProperty("startsAt").GetDateTimeOffset()).ToList();
    }

    private static object Body(Seed s, DateTimeOffset startsAt, string phone = Phone, string name = "Olena Test",
        string payment = "card", string reminder = "1h") => new
    {
        locationId = s.LocationId, specialistId = s.SpecialistId, serviceId = s.ServiceId, startsAt,
        client = new { name, phone }, reminder, paymentMethod = payment,
    };

    private Task<HttpResponseMessage> PostAsync(string slug, object body, string? key = "auto", HttpClient? c = null, string? captcha = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{slug}/appointments") { Content = JsonContent.Create(body, options: Json) };
        if (key is not null) req.Headers.Add("Idempotency-Key", key == "auto" ? Guid.NewGuid().ToString("N") : key);
        if (captcha is not null) req.Headers.Add("X-Captcha-Token", captcha);
        return (c ?? _client).SendAsync(req);
    }

    private async Task<(string Token, JsonElement Appointment)> BookAsync(Seed s, DateTimeOffset at, string phone = Phone, string payment = "card")
    {
        var r = await PostAsync(s.Slug, Body(s, at, phone, payment: payment));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await Read<JsonElement>(r);
        return (j.GetProperty("publicToken").GetString()!, j.GetProperty("appointment"));
    }

    private static async Task<JsonElement> Err(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        Assert.Equal(status, r.StatusCode);
        var j = await Read<JsonElement>(r);
        Assert.Equal(code, j.GetProperty("code").GetString());
        return j;
    }

    // ---------- основний потік ----------

    [SkippableFact]
    public async Task anonymous_creates_appointment_and_views_it_by_token()
    {
        NeedDb();
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s);
        Assert.NotEmpty(starts);

        var r = await PostAsync(s.Slug, Body(s, starts[2]));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode); // без Authorization
        var raw = await r.Content.ReadAsStringAsync();
        var created = JsonDocument.Parse(raw).RootElement;
        var token = created.GetProperty("publicToken").GetString()!;
        Assert.Equal(PublicTokenService.TokenLength, token.Length);
        var a = created.GetProperty("appointment");
        Assert.Equal("confirmed", a.GetProperty("status").GetString());
        Assert.Equal("paid", a.GetProperty("paymentStatus").GetString());
        Assert.Equal(60, a.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(500m, a.GetProperty("priceFinal").GetDecimal());
        Assert.Equal(12, a.GetProperty("cancellation").GetProperty("windowHours").GetInt32());

        var get = await _client.GetAsync($"/api/public/{s.Slug}/appointments/{token}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
        var view = await Read<JsonElement>(get);
        Assert.Equal(starts[2], view.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal("Master A", view.GetProperty("specialistName").GetString());

        // мінімум PII: ні імені, ні телефону, ні внутрішніх id клієнта/запису
        var viewRaw = await get.Content.ReadAsStringAsync();
        foreach (var text in new[] { raw, viewRaw })
        {
            Assert.DoesNotContain("Olena", text);
            Assert.DoesNotContain("380501234567", text);
            Assert.DoesNotContain("clientId", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private@x.test", text);
            Assert.DoesNotContain("source", text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(view.TryGetProperty("id", out _));

        // у БД: source=online, токен лише як SHA-256
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var row = await ctx.Appointments.SingleAsync();
        Assert.Equal(AppointmentSource.Online, row.Source);
        Assert.Equal(PublicTokenService.Hash(token), row.PublicTokenHash);
        Assert.NotEqual(token, row.PublicTokenHash);
        Assert.Equal(1, await ctx.Payments.CountAsync(p => p.AppointmentId == row.Id && p.Status == PaymentStatus.Paid));
        Assert.Equal(1, await ctx.Reminders.CountAsync(x => x.AppointmentId == row.Id));
    }

    [SkippableFact]
    public async Task client_is_deduplicated_by_phone_within_tenant_and_existing_name_is_not_overwritten()
    {
        NeedDb();
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s);
        await BookAsync(s, starts[0], "050 123 45 67");
        var r = await PostAsync(s.Slug, Body(s, starts[4], "+38 (050) 123-45-67", name: "Someone Else", payment: "cash"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var clients = await ctx.Clients.ToListAsync();
        var client = Assert.Single(clients);
        Assert.Equal("+380501234567", client.Phone);
        Assert.Equal("Olena Test", client.FullName);
        Assert.Equal(2, await ctx.Appointments.CountAsync(a => a.ClientId == client.Id));
    }

    [SkippableFact]
    public async Task booked_slot_is_no_longer_offered_and_overlapping_start_is_rejected()
    {
        NeedDb();
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s);
        var taken = starts[4];
        await BookAsync(s, taken, payment: "cash");

        var after = await SlotStartsAsync(s);
        Assert.DoesNotContain(taken, after);
        // 60-хв послуга: слоти на :15, :30, :45 до кінця й за 45 хв до початку перетинаються
        Assert.DoesNotContain(taken.AddMinutes(30), after);
        Assert.DoesNotContain(taken.AddMinutes(-30), after);
        Assert.Contains(taken.AddMinutes(60), after);
        Assert.Contains(taken.AddMinutes(-60), after);

        await Err(await PostAsync(s.Slug, Body(s, taken.AddMinutes(30), "+380671111111", payment: "cash")),
            HttpStatusCode.Conflict, "slot_unavailable");
        await Err(await PostAsync(s.Slug, Body(s, taken, "+380672222222", payment: "cash")), HttpStatusCode.Conflict, "slot_unavailable");
    }

    [SkippableFact]
    public async Task catalog_returns_override_price_promotion_duration_and_cancellation_terms()
    {
        NeedDb();
        var s = await SeedAsync(network: 500m, locationPrice: 400m, promoPercent: 25);

        var locs = await Read<JsonElement>(await _client.GetAsync($"/api/public/{s.Slug}/locations"));
        var loc = Assert.Single(locs.EnumerateArray());
        Assert.Equal(s.LocationId, loc.GetProperty("id").GetGuid());
        Assert.Equal("Europe/Kyiv", loc.GetProperty("timezone").GetString());

        var specs = await Read<JsonElement>(await _client.GetAsync($"/api/public/{s.Slug}/locations/{s.LocationId}/specialists"));
        var sp = Assert.Single(specs.EnumerateArray());
        Assert.Equal("Master A", sp.GetProperty("name").GetString());
        Assert.False(sp.TryGetProperty("email", out _));
        Assert.False(sp.TryGetProperty("phone", out _));

        var svcs = await Read<JsonElement>(await _client.GetAsync($"/api/public/{s.Slug}/locations/{s.LocationId}/services"));
        var svc = Assert.Single(svcs.EnumerateArray());
        Assert.Equal(60, svc.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(400m, svc.GetProperty("priceOriginal").GetDecimal()); // override закладу
        Assert.Equal(300m, svc.GetProperty("priceFinal").GetDecimal()); // -25%
        Assert.Equal("Spring", svc.GetProperty("promotionName").GetString());

        var slots = await Read<JsonElement>(await _client.GetAsync(
            $"/api/public/{s.Slug}/slots?locationId={s.LocationId}&serviceId={s.ServiceId}&date={Date()}&specialistId={s.SpecialistId}"));
        var first = slots.EnumerateArray().First();
        Assert.Equal(12, first.GetProperty("cancellation").GetProperty("windowHours").GetInt32());
        Assert.Equal(50, first.GetProperty("cancellation").GetProperty("refundPercentInWindow").GetInt32());

        // ціна запису = ціна з каталогу
        var (_, appt) = await BookAsync(s, first.GetProperty("startsAt").GetDateTimeOffset(), payment: "cash");
        Assert.Equal(400m, appt.GetProperty("priceOriginal").GetDecimal());
        Assert.Equal(300m, appt.GetProperty("priceFinal").GetDecimal());
    }

    // ---------- tenant / модулі ----------

    [SkippableFact]
    public async Task tenants_are_isolated_by_slug()
    {
        NeedDb();
        var a = await SeedAsync();
        var b = await SeedAsync();
        var (tokenA, _) = await BookAsync(a, (await SlotStartsAsync(a))[2], payment: "cash");

        var locsB = await Read<JsonElement>(await _client.GetAsync($"/api/public/{b.Slug}/locations"));
        Assert.Equal(b.LocationId, Assert.Single(locsB.EnumerateArray()).GetProperty("id").GetGuid());

        // id закладу/послуги tenant-а A під slug B недоступні
        await Err(await _client.GetAsync($"/api/public/{b.Slug}/locations/{a.LocationId}/specialists"), HttpStatusCode.NotFound, "location_not_found");
        await Err(await _client.GetAsync($"/api/public/{b.Slug}/locations/{a.LocationId}/services"), HttpStatusCode.NotFound, "location_not_found");
        await Err(await _client.GetAsync($"/api/public/{b.Slug}/slots?locationId={a.LocationId}&serviceId={a.ServiceId}&date={Date()}"),
            HttpStatusCode.NotFound, "service_not_found");
        // токен A під slug B — така сама 404, як і для неіснуючого
        await Err(await _client.GetAsync($"/api/public/{b.Slug}/appointments/{tokenA}"), HttpStatusCode.NotFound, "not_found");
        await Err(await _client.PostAsync($"/api/public/{b.Slug}/appointments/{tokenA}/cancel", null), HttpStatusCode.NotFound, "not_found");
        // а під своїм slug працює
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/public/{a.Slug}/appointments/{tokenA}")).StatusCode);

        // створити запис у B з id A не можна
        var bad = Body(a, (await SlotStartsAsync(b))[0], "+380673333333", payment: "cash");
        var r = await PostAsync(b.Slug, bad);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        await using var ctxB = _fx.Db.CreateContext(b.Tenant.TenantId);
        Assert.Equal(0, await ctxB.Appointments.CountAsync());
    }

    [SkippableFact]
    public async Task unknown_suspended_and_module_disabled_tenants_are_indistinguishable_404()
    {
        NeedDb();
        var noModule = await SeedAsync(modules: ["beauty_catalog"]);
        var suspended = await SeedAsync();
        var patch = await _fx.Send(HttpMethod.Patch, $"/api/platform/tenants/{suspended.Tenant.TenantId}", new { status = "suspended" },
            platformKey: PlatformKey);
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        string? expected = null;
        foreach (var slug in new[] { "no-such-salon-0000", noModule.Slug, suspended.Slug, "UPPER_and-bad", "x" })
        {
            var r = await _client.GetAsync($"/api/public/{slug}/locations");
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            var body = await r.Content.ReadAsStringAsync();
            expected ??= body;
            Assert.Equal(expected, body);
        }
        Assert.Contains("not_found", expected);
    }

    [SkippableFact]
    public async Task public_endpoints_do_not_open_authenticated_api_or_rls()
    {
        NeedDb();
        var s = await SeedAsync();
        await BookAsync(s, (await SlotStartsAsync(s))[0], payment: "cash");

        // staff-API лишається закритим, X-Tenant-Id поза Development ігнорується
        var staff = new HttpRequestMessage(HttpMethod.Get, "/api/beauty/appointments");
        staff.Headers.Add("X-Tenant-Id", s.Tenant.TenantId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(staff)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/beauty/slots")).StatusCode);

        // без app.tenant_id роль застосунку не бачить жодного запису (політики не розширені)
        await using var ctx = _fx.Db.CreateContext(null);
        Assert.Equal(0, await ctx.Appointments.CountAsync());
        Assert.Equal(0, await ctx.Clients.CountAsync());
    }

    // ---------- токен ----------

    [SkippableFact]
    public async Task unknown_malformed_and_foreign_tokens_return_identical_404()
    {
        NeedDb();
        var s = await SeedAsync();
        var other = await SeedAsync();
        var (foreign, _) = await BookAsync(other, (await SlotStartsAsync(other))[0], payment: "cash");
        var (own, _) = await BookAsync(s, (await SlotStartsAsync(s))[0], payment: "cash");

        var random = new string('A', PublicTokenService.TokenLength);
        var flipped = own[..^1] + (own[^1] == 'A' ? 'B' : 'A');
        var bodies = new HashSet<string>();
        foreach (var token in new[] { random, flipped, foreign, "short", "12345", new string('x', 500), Guid.NewGuid().ToString() })
        {
            var g = await _client.GetAsync($"/api/public/{s.Slug}/appointments/{token}");
            Assert.Equal(HttpStatusCode.NotFound, g.StatusCode);
            bodies.Add(await g.Content.ReadAsStringAsync());
            var c = await _client.PostAsync($"/api/public/{s.Slug}/appointments/{token}/cancel", null);
            Assert.Equal(HttpStatusCode.NotFound, c.StatusCode);
            bodies.Add(await c.Content.ReadAsStringAsync());
        }
        Assert.Single(bodies);
        // власний токен не постраждав
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/public/{s.Slug}/appointments/{own}")).StatusCode);
    }

    // ---------- скасування ----------

    [SkippableFact]
    public async Task cancel_outside_window_refunds_full_and_frees_slot_and_second_cancel_conflicts()
    {
        NeedDb();
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s, daysAhead: 3);
        var (token, _) = await BookAsync(s, starts[3]); // картка, 500, >12 год

        var r = await _client.PostAsync($"/api/public/{s.Slug}/appointments/{token}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Read<JsonElement>(r);
        Assert.Equal(500m, j.GetProperty("refundAmount").GetDecimal());
        Assert.Equal(100, j.GetProperty("refundPercent").GetInt32());
        Assert.Equal("cancelled", j.GetProperty("appointment").GetProperty("status").GetString());
        Assert.Equal("refunded", j.GetProperty("appointment").GetProperty("paymentStatus").GetString());

        await Err(await _client.PostAsync($"/api/public/{s.Slug}/appointments/{token}/cancel", null), HttpStatusCode.Conflict, "already_cancelled");
        Assert.Contains(starts[3], await SlotStartsAsync(s, daysAhead: 3)); // слот знову вільний
        var view = await Read<JsonElement>(await _client.GetAsync($"/api/public/{s.Slug}/appointments/{token}"));
        Assert.Equal("cancelled", view.GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task cancel_follows_tenant_policy_window_percent_and_fee()
    {
        NeedDb();
        var s = await SeedAsync();
        var owner = await _fx.LoginAsync(s.Slug, s.Tenant.OwnerEmail);
        var put = await _fx.Send(HttpMethod.Put, "/api/beauty/settings/cancellation",
            new { windowHours = 720, refundPercentInWindow = 40, refundPercentOutside = 90, deductFee = true, feePercent = 10 },
            bearer: owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var starts = await SlotStartsAsync(s);
        var (token, appt) = await BookAsync(s, starts[1]);
        Assert.Equal(720, appt.GetProperty("cancellation").GetProperty("windowHours").GetInt32());
        Assert.Equal(10, appt.GetProperty("cancellation").GetProperty("feePercent").GetInt32());

        var j = await Read<JsonElement>(await _client.PostAsync($"/api/public/{s.Slug}/appointments/{token}/cancel", null));
        // у вікні 720 год: 500 * 40% * 90% = 180.00
        Assert.Equal(180m, j.GetProperty("refundAmount").GetDecimal());
        Assert.Equal(40, j.GetProperty("refundPercent").GetInt32());
        Assert.Equal(10, j.GetProperty("feePercent").GetInt32());
    }

    [SkippableFact]
    public async Task cancel_cash_appointment_refunds_nothing()
    {
        NeedDb();
        var s = await SeedAsync();
        var (token, _) = await BookAsync(s, (await SlotStartsAsync(s))[1], payment: "cash");
        var j = await Read<JsonElement>(await _client.PostAsync($"/api/public/{s.Slug}/appointments/{token}/cancel", null));
        Assert.Equal(0m, j.GetProperty("refundAmount").GetDecimal());
        Assert.Equal("cancelled", j.GetProperty("appointment").GetProperty("status").GetString());
    }

    // ---------- ідемпотентність ----------

    [SkippableFact]
    public async Task same_idempotency_key_replays_same_booking_without_duplicate_or_second_charge()
    {
        NeedDb();
        var s = await SeedAsync();
        var at = (await SlotStartsAsync(s))[2];
        var key = "key-" + Guid.NewGuid().ToString("N");

        var first = await PostAsync(s.Slug, Body(s, at), key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await PostAsync(s.Slug, Body(s, at), key);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal((await Read<JsonElement>(first)).GetProperty("publicToken").GetString(),
            (await Read<JsonElement>(second)).GetProperty("publicToken").GetString());

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.Equal(1, await ctx.Appointments.CountAsync());
        Assert.Equal(1, await ctx.Payments.CountAsync());
        // ключ у БД лише як хеш
        Assert.DoesNotContain(await ctx.Appointments.Select(a => a.IdempotencyKeyHash).ToListAsync(), k => k == key);

        // той самий ключ з іншим тілом
        await Err(await PostAsync(s.Slug, Body(s, at.AddHours(1)), key), HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
    }

    [SkippableFact]
    public async Task concurrent_requests_with_same_key_create_exactly_one_appointment()
    {
        NeedDb();
        var s = await SeedAsync();
        var at = (await SlotStartsAsync(s))[2];
        var key = "race-" + Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostAsync(s.Slug, Body(s, at), key)));
        foreach (var r in responses)
            Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode + " " + await r.Content.ReadAsStringAsync());
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Created);
        var tokens = new HashSet<string?>();
        foreach (var r in responses) tokens.Add((await Read<JsonElement>(r)).GetProperty("publicToken").GetString());
        Assert.Single(tokens);

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.Equal(1, await ctx.Appointments.CountAsync());
        Assert.Equal(1, await ctx.Payments.CountAsync(p => p.Status == PaymentStatus.Paid));
    }

    [SkippableFact]
    public async Task idempotency_key_is_required_and_validated()
    {
        NeedDb();
        var s = await SeedAsync();
        var body = Body(s, (await SlotStartsAsync(s))[0]);
        await Err(await PostAsync(s.Slug, body, key: null), HttpStatusCode.UnprocessableEntity, "idempotency_key_required");
        await Err(await PostAsync(s.Slug, body, key: "short"), HttpStatusCode.UnprocessableEntity, "invalid_idempotency_key");
        await Err(await PostAsync(s.Slug, body, key: "has spaces and <script> 0123456789"), HttpStatusCode.UnprocessableEntity, "invalid_idempotency_key");
    }

    // ---------- валідація / зловживання ----------

    [SkippableTheory]
    [InlineData("12345", "Olena", "invalid_phone")]
    [InlineData("abc", "Olena", "invalid_phone")]
    [InlineData("+380501234567", "O", "invalid_name")]
    [InlineData("+380501234567", "   ", "invalid_name")]
    public async Task invalid_client_is_rejected(string phone, string name, string code)
    {
        NeedDb();
        var s = await SeedAsync();
        await Err(await PostAsync(s.Slug, Body(s, (await SlotStartsAsync(s))[0], phone, name)), HttpStatusCode.UnprocessableEntity, code);
    }

    [SkippableFact]
    public async Task invalid_request_fields_are_rejected()
    {
        NeedDb();
        var s = await SeedAsync();
        var at = (await SlotStartsAsync(s))[0];
        await Err(await PostAsync(s.Slug, Body(s, at, reminder: "3h")), HttpStatusCode.UnprocessableEntity, "invalid_reminder");
        await Err(await PostAsync(s.Slug, Body(s, at, payment: "bitcoin")), HttpStatusCode.UnprocessableEntity, "invalid_payment_method");
        await Err(await PostAsync(s.Slug, Body(s, at.AddMinutes(7))), HttpStatusCode.UnprocessableEntity, "invalid_start");
        await Err(await PostAsync(s.Slug, Body(s, new DateTimeOffset(DateTime.UtcNow.Date.AddDays(400), TimeSpan.Zero))), HttpStatusCode.UnprocessableEntity, "invalid_start");
        await Err(await PostAsync(s.Slug, new { locationId = s.LocationId }), HttpStatusCode.UnprocessableEntity, "invalid_request");
        await Err(await PostAsync(s.Slug, Body(s, new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddHours(-3))), HttpStatusCode.UnprocessableEntity, "slot_in_past");
        // за межами робочих годин (23:00 Kyiv)
        var night = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2).AddHours(21), TimeSpan.Zero); // 23:00/24:00 Kyiv
        await Err(await PostAsync(s.Slug, Body(s, night)), HttpStatusCode.UnprocessableEntity, "outside_working_hours");
        await Err(await _client.GetAsync($"/api/public/{s.Slug}/slots?locationId={s.LocationId}&serviceId={s.ServiceId}&date={Date(400)}"),
            HttpStatusCode.UnprocessableEntity, "invalid_date");
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.Equal(0, await ctx.Appointments.CountAsync());
    }

    [SkippableFact]
    public async Task limit_of_active_bookings_per_phone_is_enforced()
    {
        NeedDb();
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s);
        for (var i = 0; i < 3; i++) await BookAsync(s, starts[i * 4], "+380509990000", payment: "cash");
        await Err(await PostAsync(s.Slug, Body(s, starts[12], "+380509990000", payment: "cash")),
            HttpStatusCode.UnprocessableEntity, "booking_limit_reached");
        // інший телефон не обмежений
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s.Slug, Body(s, starts[12], "+380509990001", payment: "cash"))).StatusCode);
    }

    [SkippableFact]
    public async Task rate_limit_is_stricter_on_post_and_returns_429()
    {
        NeedDb();
        var c = NewClient(writePermit: 3, tokenPermit: 2);
        var s = await SeedAsync();
        for (var i = 0; i < 3; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostAsync(s.Slug, new { }, c: c)).StatusCode);
        var limited = await PostAsync(s.Slug, new { }, c: c);
        await Err(limited, HttpStatusCode.TooManyRequests, "rate_limited");
        // читання має окремий (вільніший) ліміт
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/public/{s.Slug}/locations")).StatusCode);
        // скасування — теж у write-ліміті
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsync($"/api/public/{s.Slug}/appointments/x/cancel", null)).StatusCode);

        // перегляд за токеном — суворіший за читання каталогу: 2 запити, третій — 429
        var tokenUrl = $"/api/public/{s.Slug}/appointments/{new string('A', PublicTokenService.TokenLength)}";
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(tokenUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(tokenUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.GetAsync(tokenUrl)).StatusCode);
    }

    // ---------- CAPTCHA-хук ----------

    private sealed class FakeCaptcha : ICaptchaVerifier
    {
        public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct) => Task.FromResult(token == "good-token");
    }

    [SkippableFact]
    public async Task captcha_hook_is_enforced_when_required()
    {
        NeedDb();
        var c = NewClient(b =>
        {
            b.UseSetting("PublicBooking:CaptchaRequired", "true");
            b.ConfigureTestServices(sv => sv.AddSingleton<ICaptchaVerifier, FakeCaptcha>());
        });
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s, c: c);
        await Err(await PostAsync(s.Slug, Body(s, starts[0], payment: "cash"), c: c), HttpStatusCode.UnprocessableEntity, "captcha_failed");
        await Err(await PostAsync(s.Slug, Body(s, starts[0], payment: "cash"), c: c, captcha: "bad"), HttpStatusCode.UnprocessableEntity, "captcha_failed");
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s.Slug, Body(s, starts[0], payment: "cash"), c: c, captcha: "good-token")).StatusCode);
        // токен можна передати й у тілі
        var inBody = new
        {
            locationId = s.LocationId, specialistId = s.SpecialistId, serviceId = s.ServiceId, startsAt = starts[4],
            client = new { name = "Olena Test", phone = "+380509990002" }, reminder = "none", paymentMethod = "cash", captchaToken = "good-token",
        };
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s.Slug, inBody, c: c)).StatusCode);
    }

    [SkippableFact]
    public async Task captcha_required_without_provider_fails_closed()
    {
        NeedDb();
        var c = NewClient(b => b.UseSetting("PublicBooking:CaptchaRequired", "true"));
        var s = await SeedAsync();
        var at = (await SlotStartsAsync(s, c: c))[0];
        await Err(await PostAsync(s.Slug, Body(s, at, payment: "cash"), c: c, captcha: "anything"), HttpStatusCode.UnprocessableEntity, "captcha_failed");
    }

    // ---------- вимоги security-reviewer ----------

    [SkippableFact]
    public async Task honeypot_source_and_marketing_consent_are_server_controlled()
    {
        NeedDb();
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s);
        object Make(DateTimeOffset at, string phone, object? extra = null, bool? consent = null, string? website = null) => new
        {
            locationId = s.LocationId, specialistId = s.SpecialistId, serviceId = s.ServiceId, startsAt = at,
            client = new { name = "Olena Test", phone, marketingConsent = consent }, reminder = "none", paymentMethod = "cash",
            source = "admin", status = "completed", priceFinal = 1, clientId = Guid.NewGuid(), website,
        };

        await Err(await PostAsync(s.Slug, Make(starts[0], "+380501110001", website: "http://spam")), HttpStatusCode.UnprocessableEntity, "invalid_request");
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s.Slug, Make(starts[0], "+380501110001"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s.Slug, Make(starts[4], "+380501110002", consent: true))).StatusCode);

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.All(await ctx.Appointments.ToListAsync(), a =>
        {
            Assert.Equal(AppointmentSource.Online, a.Source);
            Assert.Equal(500m, a.PriceFinal);
            Assert.NotEqual(AppointmentStatus.Completed, a.Status);
        });
        Assert.False((await ctx.Clients.SingleAsync(c => c.Phone == "+380501110001")).MarketingConsent); // без поля = немає згоди
        Assert.True((await ctx.Clients.SingleAsync(c => c.Phone == "+380501110002")).MarketingConsent);

        // наявний клієнт: згода й профіль не змінюються анонімним запитом
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s.Slug, Make(starts[8], "+380501110001", consent: true))).StatusCode);
        Assert.False((await ctx.Clients.AsNoTracking().SingleAsync(c => c.Phone == "+380501110001")).MarketingConsent);
    }

    [SkippableFact]
    public async Task per_phone_hourly_creation_limit_applies_even_to_cancelled_bookings()
    {
        NeedDb();
        var c = NewClient(b => b.UseSetting("PublicBooking:MaxCreatesPerPhonePerHour", "2"));
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s, c: c);
        for (var i = 0; i < 2; i++)
        {
            var r = await PostAsync(s.Slug, Body(s, starts[i * 4], "+380509998877", payment: "cash"), c: c);
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            var token = (await Read<JsonElement>(r)).GetProperty("publicToken").GetString();
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/public/{s.Slug}/appointments/{token}/cancel", null)).StatusCode);
        }
        await Err(await PostAsync(s.Slug, Body(s, starts[12], "+380509998877", payment: "cash"), c: c),
            HttpStatusCode.UnprocessableEntity, "booking_limit_reached");
    }
}
