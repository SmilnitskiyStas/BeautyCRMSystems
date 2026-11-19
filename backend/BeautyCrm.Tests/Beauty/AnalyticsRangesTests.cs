using System.Globalization;
using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-706 (§19): аналітика - діапазони, динаміка, порівняння, завантаження. Повний HTTP-конвеєр на реальному PostgreSQL
/// (роль без BYPASSRLS). Дані пишуться напряму через DbContext під RLS tenant (записи в минулому API не дозволяє).
/// Графік майстра за замовчуванням 09:00-18:00 щодня.
/// </summary>
public sealed class AnalyticsRangesApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private const string ClientName = "Zoryana Secretna";
    private const string ClientPhone = "+380509998877";
    private static readonly string[] DayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    // ---------- сид ----------

    private sealed record Env(TenantSeed Tenant, string Owner, Guid Location, Guid Specialist, Guid Service, Guid ClientId)
    {
        public Guid TenantId => Tenant.TenantId;
    }

    private static string Hours(string from, string to, params string[] skipDays) =>
        "{" + string.Join(",", DayKeys.Where(d => !skipDays.Contains(d)).Select(d => $"\"{d}\":[{{\"from\":\"{from}\",\"to\":\"{to}\"}}]")) + "}";

    private static readonly string AllDays = Hours("09:00", "18:00");

    private async Task<Env> NewEnvAsync(string tz = "UTC", string locationName = "A", string? specialistHours = null, string? locationHours = null,
        string[]? closedWeekdays = null, string[]? modules = null)
    {
        var tenant = await fx.CreateTenantAsync(modules);
        var owner = await fx.LoginAsync(tenant.Slug, tenant.OwnerEmail);
        await using var ctx = fx.Db.CreateContext(tenant.TenantId);
        var location = new Location { Name = locationName, Timezone = tz, WorkingHours = locationHours, ClosedWeekdays = closedWeekdays ?? [] };
        var specialist = new Specialist { FullName = "Master" };
        var service = new Service { Name = "Haircut", DurationMinutes = 60 };
        var client = new Client { FullName = ClientName, Phone = ClientPhone };
        ctx.AddRange(location, specialist, service, client);
        await ctx.SaveChangesAsync();
        ctx.Add(new SpecialistLocation { SpecialistId = specialist.Id, LocationId = location.Id, WorkingHours = specialistHours ?? AllDays });
        await ctx.SaveChangesAsync();
        return new Env(tenant, owner.AccessToken, location.Id, specialist.Id, service.Id, client.Id);
    }

    private async Task<Guid> AddLocationAsync(Env e, string name, string tz, bool active = true, string? hours = null)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        var l = new Location { Name = name, Timezone = tz, IsActive = active, WorkingHours = hours };
        ctx.Add(l);
        await ctx.SaveChangesAsync();
        return l.Id;
    }

    private async Task<Guid> AddSpecialistAsync(Env e, string name, Guid? location = null, string? hours = null, bool active = true, bool linkActive = true)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        var s = new Specialist { FullName = name, IsActive = active };
        ctx.Add(s);
        await ctx.SaveChangesAsync();
        ctx.Add(new SpecialistLocation { SpecialistId = s.Id, LocationId = location ?? e.Location, WorkingHours = hours ?? AllDays, IsActive = linkActive });
        await ctx.SaveChangesAsync();
        return s.Id;
    }

    private async Task<Guid> AddApptAsync(Env e, DateTimeOffset startsAt, string status = "completed", int minutes = 60, decimal price = 1000m,
        Guid? location = null, Guid? specialist = null, Guid? promotion = null, decimal? original = null)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        var a = new Appointment
        {
            LocationId = location ?? e.Location, SpecialistId = specialist ?? e.Specialist, ServiceId = e.Service, ClientId = e.ClientId,
            StartsAt = startsAt.ToUniversalTime(), DurationMinutes = minutes, Source = AppointmentSource.Admin, PriceOriginal = original ?? price,
            PriceFinal = price, PromotionId = promotion,
            Status = status switch
            {
                "pending" => AppointmentStatus.Pending, "confirmed" => AppointmentStatus.Confirmed, "completed" => AppointmentStatus.Completed,
                "cancelled" => AppointmentStatus.Cancelled, "no_show" => AppointmentStatus.NoShow, _ => throw new ArgumentException(status),
            },
        };
        if (status == "cancelled") { a.CancelledAt = startsAt.ToUniversalTime(); a.CancelledByType = "system"; }
        ctx.Add(a);
        await ctx.SaveChangesAsync();
        return a.Id;
    }

    private async Task<Guid> AddPromotionAsync(Env e, string name)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        var p = new Promotion { Name = name, DiscountType = DiscountType.Percent, DiscountValue = 10m };
        ctx.Add(p);
        await ctx.SaveChangesAsync();
        return p.Id;
    }

    private async Task AddAbsenceAsync(Env e, Guid specialist, DateOnly from, DateOnly to, string status)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        ctx.Add(new SpecialistAbsence
        {
            SpecialistId = specialist, Type = "other", DateFrom = from, DateTo = to, Status = status, RequestedByUserId = e.Tenant.OwnerId,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task AddClosureAsync(Env e, DateOnly from, DateOnly to)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        ctx.Add(new LocationClosure { LocationId = e.Location, DateFrom = from, DateTo = to });
        await ctx.SaveChangesAsync();
    }

    private async Task AddClientAsync(Env e, DateTimeOffset createdAt)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        ctx.Add(new Client { FullName = "New", Phone = "+3805" + Random.Shared.Next(10_000_000, 99_999_999), CreatedAt = createdAt });
        await ctx.SaveChangesAsync();
    }

    // ---------- HTTP ----------

    private Task<HttpResponseMessage> Send(string token, string url) => fx.Send(HttpMethod.Get, "/api/beauty/analytics/" + url, bearer: token);

    private async Task<JsonElement> Ok(string token, string url)
    {
        var r = await Send(token, url);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await Read<JsonElement>(r);
    }

    private async Task AssertInvalidRange(string token, string url)
    {
        var r = await Send(token, url);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("invalid_range", (await Read<JsonElement>(r)).GetProperty("code").GetString());
    }

    private static string R(string from, string to) => $"from={from}&to={to}";
    private static DateOnly D(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTimeOffset Utc(int y, int m, int d, int h = 10, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);
    private static DateTimeOffset Local(string tz, int y, int m, int d, int h, int min = 0) =>
        SlotCalculator.ToOffset(new DateOnly(y, m, d), new TimeOnly(h, min), TimeZoneInfo.FindSystemTimeZoneById(tz));

    // =====================================================================================================================
    // §19.1 діапазони, валідація, legacy
    // =====================================================================================================================

    [SkippableFact]
    public async Task one_day_range_is_valid_and_previous_period_is_the_day_before()
    {
        NeedDb();
        var e = await NewEnvAsync("Europe/Kyiv");
        await AddApptAsync(e, Utc(2026, 6, 10, 8));

        var series = await Ok(e.Owner, "network/series?" + R("2026-06-10", "2026-06-10"));
        Assert.Equal("day", series.GetProperty("granularity").GetString());
        var points = series.GetProperty("points").EnumerateArray().ToList();
        var p = Assert.Single(points);
        Assert.Equal("2026-06-10", p.GetProperty("from").GetString());
        Assert.Equal("2026-06-10", p.GetProperty("to").GetString());
        Assert.Equal(1, p.GetProperty("appointments").GetInt32());
        Assert.Equal(1000m, p.GetProperty("revenue").GetDecimal());

        var net = await Ok(e.Owner, "network?" + R("2026-06-10", "2026-06-10") + "&compare=true");
        Assert.Equal("2026-06-10", net.GetProperty("fromDate").GetString());
        Assert.Equal("2026-06-10", net.GetProperty("toDate").GetString());
        Assert.Equal("Europe/Kyiv", net.GetProperty("timezone").GetString());
        var prev = net.GetProperty("previous");
        Assert.Equal("2026-06-09", prev.GetProperty("fromDate").GetString());
        Assert.Equal("2026-06-09", prev.GetProperty("toDate").GetString());
        Assert.Equal(0, prev.GetProperty("appointments").GetInt32());
        // еталонні миттєвості: 00:00 першого дня і 00:00 наступного (to виключно) у зоні закладу
        Assert.Equal(Local("Europe/Kyiv", 2026, 6, 10, 0), net.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(Local("Europe/Kyiv", 2026, 6, 11, 0), net.GetProperty("to").GetDateTimeOffset());
    }

    [SkippableFact]
    public async Task range_validation_366_days_ok_and_everything_else_is_422_invalid_range()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var breakdown = $"locations/{e.Location}/breakdown";
        foreach (var path in new[] { "network", "network/series", "locations", "promotions", breakdown })
        {
            Assert.Equal(HttpStatusCode.OK, (await Send(e.Owner, $"{path}?" + R("2024-01-01", "2024-12-31"))).StatusCode); // 366 днів (високосний)
            await AssertInvalidRange(e.Owner, $"{path}?" + R("2024-01-01", "2025-01-01")); // 367
            await AssertInvalidRange(e.Owner, $"{path}?" + R("2026-06-10", "2026-06-09")); // from > to
            await AssertInvalidRange(e.Owner, $"{path}?" + R("2026-02-30", "2026-03-05")); // неіснуюча дата
            await AssertInvalidRange(e.Owner, $"{path}?" + R("2026-06-01", "2026-06-10T00:00:00Z")); // змішаний формат
            await AssertInvalidRange(e.Owner, $"{path}?" + R("2026-06-01T00:00:00Z", "2026-06-10")); // змішаний формат
            await AssertInvalidRange(e.Owner, $"{path}?" + R("1999-12-31", "2000-01-02")); // рік поза 2000..2100
            await AssertInvalidRange(e.Owner, $"{path}?" + R("2100-12-30", "2101-01-02"));
            await AssertInvalidRange(e.Owner, $"{path}?" + R("abc", "2026-06-10"));
        }
        // legacy: ліміт 366 днів, from >= to; серія/розбивка приймають лише датний режим
        foreach (var path in new[] { "network", "locations", "promotions" })
        {
            await AssertInvalidRange(e.Owner, $"{path}?" + R("2025-01-01T00:00:00Z", "2026-06-01T00:00:00Z"));
            await AssertInvalidRange(e.Owner, $"{path}?" + R("2026-06-01T00:00:00Z", "2026-06-01T00:00:00Z"));
            Assert.Equal(HttpStatusCode.OK, (await Send(e.Owner, $"{path}?" + R("2026-01-01T00:00:00Z", "2026-12-31T00:00:00Z"))).StatusCode);
        }
        await AssertInvalidRange(e.Owner, "network/series?" + R("2026-06-01T00:00:00Z", "2026-06-10T00:00:00Z"));
        await AssertInvalidRange(e.Owner, $"{breakdown}?" + R("2026-06-01T00:00:00Z", "2026-06-10T00:00:00Z"));
    }

    [SkippableFact]
    public async Task defaults_are_the_last_30_local_days_including_today()
    {
        NeedDb();
        var e = await NewEnvAsync("Pacific/Auckland");
        var net = await Ok(e.Owner, "network");
        var to = D(net.GetProperty("toDate").GetString()!);
        var from = D(net.GetProperty("fromDate").GetString()!);
        Assert.Equal(29, to.DayNumber - from.DayNumber);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland")).DateTime);
        Assert.True(Math.Abs(to.DayNumber - today.DayNumber) <= 1, $"to={to} today(NZ)={today}"); // допуск на північ під час виконання
        Assert.Equal("Pacific/Auckland", net.GetProperty("timezone").GetString());

        var onlyFrom = await Ok(e.Owner, "network?from=2026-01-01");
        Assert.Equal("2026-01-01", onlyFrom.GetProperty("fromDate").GetString());
        Assert.Equal(Iso(to), onlyFrom.GetProperty("toDate").GetString());
        var onlyTo = await Ok(e.Owner, "network?to=2026-03-31");
        Assert.Equal("2026-03-02", onlyTo.GetProperty("fromDate").GetString());
    }

    [SkippableFact]
    public async Task legacy_mode_keeps_the_v0_19_response_shape_and_utc_semantics()
    {
        NeedDb();
        var e = await NewEnvAsync("Europe/Kyiv");
        var promo = await AddPromotionAsync(e, "Promo");
        await AddApptAsync(e, Utc(2026, 6, 10, 10), promotion: promo, original: 1200m);
        await AddApptAsync(e, Utc(2026, 6, 10, 11), "cancelled");

        // [from, to) за UTC-миттєвостями: 10:00Z входить, 11:00Z (скасований) - ні
        var net = await Ok(e.Owner, "network?from=2026-06-10T10:00:00Z&to=2026-06-10T11:00:00Z&compare=true");
        Assert.Equal(["from", "to", "appointments", "completed", "cancelled", "noShow", "revenue", "averageCheck", "cancellationRate", "newClients"],
            net.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, net.GetProperty("appointments").GetInt32());
        Assert.Equal(0, net.GetProperty("cancelled").GetInt32());
        Assert.Equal(Utc(2026, 6, 10, 10), net.GetProperty("from").GetDateTimeOffset());

        var locations = await Ok(e.Owner, "locations?from=2026-06-10T00:00:00Z&to=2026-06-11T00:00:00Z&compare=true");
        var row = Assert.Single(locations.EnumerateArray());
        Assert.Equal(["locationId", "name", "appointments", "completed", "cancelled", "revenue"], row.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(2, row.GetProperty("appointments").GetInt32());

        var promos = await Ok(e.Owner, "promotions?from=2026-06-10T00:00:00Z&to=2026-06-11T00:00:00Z&compare=true");
        var pr = Assert.Single(promos.EnumerateArray());
        Assert.Equal(["promotionId", "name", "uses", "discountTotal", "revenue"], pr.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(200m, pr.GetProperty("discountTotal").GetDecimal());
    }

    [SkippableFact]
    public async Task date_mode_new_fields_are_always_present_and_previous_only_with_compare()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var net = await Ok(e.Owner, "network?" + R("2026-06-01", "2026-06-10"));
        Assert.False(net.TryGetProperty("previous", out _));
        foreach (var f in new[] { "fromDate", "toDate", "timezone", "from", "to", "newClients" }) Assert.True(net.TryGetProperty(f, out _), f);

        var locs = await Ok(e.Owner, "locations?" + R("2026-06-01", "2026-06-10"));
        var row = Assert.Single(locs.EnumerateArray());
        foreach (var f in new[] { "noShow", "timezone", "isActive", "busyMinutes", "availableMinutes", "load" }) Assert.True(row.TryGetProperty(f, out _), f);
        Assert.False(row.TryGetProperty("previous", out _));
    }

    // =====================================================================================================================
    // §19.2 series
    // =====================================================================================================================

    [SkippableTheory]
    [InlineData(31, "day", 31, 31)]
    [InlineData(32, "week", 5, 5)]
    [InlineData(120, "week", 18, 18)]
    [InlineData(121, "month", 5, 5)]
    [InlineData(366, "month", 12, 13)]
    public async Task series_granularity_thresholds_and_bucket_clipping(int days, string granularity, int minPoints, int maxPoints)
    {
        NeedDb();
        var e = await NewEnvAsync();
        var from = new DateOnly(2026, 1, 1); // четвер
        var to = from.AddDays(days - 1);
        var series = await Ok(e.Owner, "network/series?" + R(Iso(from), Iso(to)));
        Assert.Equal(granularity, series.GetProperty("granularity").GetString());
        var pts = series.GetProperty("points").EnumerateArray().Select(p => (From: D(p.GetProperty("from").GetString()!), To: D(p.GetProperty("to").GetString()!))).ToList();
        Assert.InRange(pts.Count, minPoints, maxPoints);
        Assert.Equal(from, pts[0].From);
        Assert.Equal(to, pts[^1].To);
        for (var i = 1; i < pts.Count; i++) Assert.Equal(pts[i - 1].To.AddDays(1), pts[i].From); // без пропусків
        if (granularity == "day") Assert.All(pts, p => Assert.Equal(p.From, p.To));
        if (granularity == "week")
        {
            Assert.Equal(new DateOnly(2026, 1, 4), pts[0].To); // перший тиждень обрізаний межею (чт-нд)
            foreach (var p in pts.Skip(1)) Assert.Equal(DayOfWeek.Monday, p.From.DayOfWeek);
            foreach (var p in pts.Take(pts.Count - 1)) Assert.Equal(DayOfWeek.Sunday, p.To.DayOfWeek);
        }
        if (granularity == "month")
        {
            foreach (var p in pts.Skip(1)) Assert.Equal(1, p.From.Day);
            foreach (var p in pts.Take(pts.Count - 1)) Assert.Equal(DateTime.DaysInMonth(p.To.Year, p.To.Month), p.To.Day);
        }
    }

    [SkippableFact]
    public async Task series_buckets_by_the_locations_local_date_across_month_and_week_boundaries_in_kyiv()
    {
        NeedDb();
        var e = await NewEnvAsync("Europe/Kyiv");
        // 31.10 23:30 local (+02:00 = 21:30Z) -> жовтень; 01.11 00:15 local (= 22:15Z 31.10) -> листопад
        await AddApptAsync(e, Local("Europe/Kyiv", 2026, 10, 31, 23, 30), minutes: 30);
        await AddApptAsync(e, Local("Europe/Kyiv", 2026, 11, 1, 0, 15), minutes: 30);
        Assert.Equal(Utc(2026, 10, 31, 22, 15), Local("Europe/Kyiv", 2026, 11, 1, 0, 15).ToUniversalTime());

        var month = (await Ok(e.Owner, "network/series?" + R("2026-09-01", "2026-12-31"))).GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(4, month.Count);
        Assert.Equal([0, 1, 1, 0], month.Select(p => p.GetProperty("appointments").GetInt32()).ToArray());

        var week = (await Ok(e.Owner, "network/series?" + R("2026-09-28", "2026-11-01"))).GetProperty("points").EnumerateArray().ToList();
        Assert.Equal("week", (await Ok(e.Owner, "network/series?" + R("2026-09-28", "2026-11-01"))).GetProperty("granularity").GetString());
        var last = week[^1];
        Assert.Equal("2026-10-26", last.GetProperty("from").GetString());
        Assert.Equal("2026-11-01", last.GetProperty("to").GetString());
        Assert.Equal(2, last.GetProperty("appointments").GetInt32());

        var days = (await Ok(e.Owner, "network/series?" + R("2026-10-31", "2026-11-01"))).GetProperty("points").EnumerateArray().ToList();
        Assert.Equal([1, 1], days.Select(p => p.GetProperty("appointments").GetInt32()).ToArray());
    }

    [SkippableFact]
    public async Task february_ranges_have_28_or_29_days_and_previous_period_precedes_them()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var feb = await Ok(e.Owner, "network?" + R("2026-02-01", "2026-02-28") + "&compare=true");
        Assert.Equal("2026-01-04", feb.GetProperty("previous").GetProperty("fromDate").GetString());
        Assert.Equal("2026-01-31", feb.GetProperty("previous").GetProperty("toDate").GetString());
        var leap = await Ok(e.Owner, "network/series?" + R("2028-02-01", "2028-02-29"));
        Assert.Equal(29, leap.GetProperty("points").GetArrayLength());
    }

    [SkippableFact]
    public async Task dst_days_in_kyiv_keep_buckets_and_availability_in_wall_clock_minutes()
    {
        NeedDb();
        var e = await NewEnvAsync("Europe/Kyiv");
        await AddApptAsync(e, Local("Europe/Kyiv", 2026, 3, 28, 23, 30), minutes: 30);
        // 25.10.2026: година 03:00-04:00 повторюється; обидва входження належать дню 25.10
        await AddApptAsync(e, Utc(2026, 10, 25, 0, 30), minutes: 30);
        await AddApptAsync(e, Utc(2026, 10, 25, 1, 30), minutes: 30);

        var spring = (await Ok(e.Owner, "network/series?" + R("2026-03-26", "2026-04-01"))).GetProperty("points").EnumerateArray().ToList();
        Assert.Equal([0, 0, 1, 0, 0, 0, 0], spring.Select(p => p.GetProperty("appointments").GetInt32()).ToArray());
        Assert.Equal("2026-03-28", spring[2].GetProperty("from").GetString());
        var autumn = (await Ok(e.Owner, "network/series?" + R("2026-10-22", "2026-10-28"))).GetProperty("points").EnumerateArray().ToList();
        Assert.Equal([0, 0, 0, 2, 0, 0, 0], autumn.Select(p => p.GetProperty("appointments").GetInt32()).ToArray());

        foreach (var day in new[] { "2026-03-29", "2026-10-25" }) // 23- і 25-годинна доба: 09:00-18:00 = 540
        {
            var b = await Ok(e.Owner, $"locations/{e.Location}/breakdown?" + R(day, day));
            Assert.Equal(540, b.GetProperty("availableMinutes").GetInt32());
        }
        var oct25 = await Ok(e.Owner, $"locations/{e.Location}/breakdown?" + R("2026-10-25", "2026-10-25"));
        Assert.Equal(60, oct25.GetProperty("busyMinutes").GetInt32()); // за duration_minutes, не за настінним часом
    }

    [SkippableFact]
    public async Task series_sum_equals_network_and_location_revenues_sum_to_network()
    {
        NeedDb();
        var e = await NewEnvAsync("Europe/Kyiv", "A");
        var b = await AddLocationAsync(e, "B", "Pacific/Auckland");
        var second = await AddSpecialistAsync(e, "Second", b);
        var statuses = new[] { "completed", "cancelled", "no_show", "pending", "confirmed", "completed" };
        for (var i = 0; i < 40; i++)
        {
            var day = new DateOnly(2026, 5, 1).AddDays(i * 2);
            var at = SlotCalculator.ToOffset(day, new TimeOnly(9 + i % 8, 0), TimeZoneInfo.FindSystemTimeZoneById(i % 3 == 0 ? "Pacific/Auckland" : "Europe/Kyiv"));
            await AddApptAsync(e, at, statuses[i % statuses.Length], price: 100 + i, location: i % 3 == 0 ? b : e.Location, specialist: i % 3 == 0 ? second : e.Specialist);
        }
        foreach (var range in new[] { R("2026-05-01", "2026-07-20"), R("2026-05-01", "2026-05-31"), R("2026-04-01", "2026-12-31") })
        {
            var net = await Ok(e.Owner, "network?" + range);
            var series = await Ok(e.Owner, "network/series?" + range);
            foreach (var f in new[] { "appointments", "completed", "cancelled", "noShow" })
                Assert.Equal(net.GetProperty(f).GetInt32(), series.GetProperty("points").EnumerateArray().Sum(p => p.GetProperty(f).GetInt32()));
            Assert.Equal(net.GetProperty("revenue").GetDecimal(), series.GetProperty("points").EnumerateArray().Sum(p => p.GetProperty("revenue").GetDecimal()));
            var locs = (await Ok(e.Owner, "locations?" + range)).EnumerateArray().ToList();
            Assert.Equal(net.GetProperty("revenue").GetDecimal(), locs.Sum(l => l.GetProperty("revenue").GetDecimal()));
            Assert.Equal(net.GetProperty("appointments").GetInt32(), locs.Sum(l => l.GetProperty("appointments").GetInt32()));
        }
    }

    // =====================================================================================================================
    // §19.1 зони, newClients
    // =====================================================================================================================

    [SkippableFact]
    public async Task appointment_belongs_to_the_local_day_of_its_own_location_and_new_clients_use_the_reference_zone()
    {
        NeedDb();
        var e = await NewEnvAsync("Europe/Kyiv", "A"); // еталон: перший активний за назвою
        var auckland = await AddLocationAsync(e, "B", "Pacific/Auckland");
        var second = await AddSpecialistAsync(e, "NZ master", auckland);
        // 01.10 08:00 NZDT (+13) = 30.09 19:00Z
        var at = Local("Pacific/Auckland", 2026, 10, 1, 8);
        Assert.Equal(Utc(2026, 9, 30, 19), at.ToUniversalTime());
        await AddApptAsync(e, at, location: auckland, specialist: second);
        // 30.09 22:30Z = 01.10 01:30 Kyiv (+03:00) -> 01.10 за еталонною зоною
        await AddClientAsync(e, Utc(2026, 9, 30, 22, 30));

        var oct1 = await Ok(e.Owner, "network?" + R("2026-10-01", "2026-10-01"));
        Assert.Equal(1, oct1.GetProperty("appointments").GetInt32());
        Assert.Equal(1, oct1.GetProperty("newClients").GetInt32());
        var sep30 = await Ok(e.Owner, "network?" + R("2026-09-30", "2026-09-30"));
        Assert.Equal(0, sep30.GetProperty("appointments").GetInt32());
        Assert.Equal(0, sep30.GetProperty("newClients").GetInt32());

        var rows = (await Ok(e.Owner, "locations?" + R("2026-10-01", "2026-10-01"))).EnumerateArray().ToList();
        var nz = rows.Single(r => r.GetProperty("name").GetString() == "B");
        Assert.Equal("Pacific/Auckland", nz.GetProperty("timezone").GetString());
        Assert.Equal(1, nz.GetProperty("appointments").GetInt32());
        Assert.Equal("Europe/Kyiv", oct1.GetProperty("timezone").GetString());
    }

    [SkippableFact]
    public async Task reference_zone_skips_inactive_locations_and_defaults_to_utc_without_locations()
    {
        NeedDb();
        var e = await NewEnvAsync("Europe/Kyiv", "B");
        await AddLocationAsync(e, "A", "Pacific/Auckland", active: false); // неактивний "A" не стає еталоном
        Assert.Equal("Europe/Kyiv", (await Ok(e.Owner, "network?" + R("2026-06-01", "2026-06-02"))).GetProperty("timezone").GetString());

        var tenant = await fx.CreateTenantAsync();
        var owner = (await fx.LoginAsync(tenant.Slug, tenant.OwnerEmail)).AccessToken;
        var net = await Ok(owner, "network?" + R("2026-06-01", "2026-06-02"));
        Assert.Equal("UTC", net.GetProperty("timezone").GetString());
        Assert.Equal(0, (await Ok(owner, "locations?" + R("2026-06-01", "2026-06-02"))).GetArrayLength());
        var missing = await Send(owner, $"locations/{Guid.NewGuid()}/breakdown?" + R("2026-06-01", "2026-06-02"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("location_not_found", (await Read<JsonElement>(missing)).GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task empty_period_has_all_points_with_zeros_and_load_is_null_without_schedules_but_zero_with_one()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var noSchedule = await AddLocationAsync(e, "B", "UTC");
        var net = await Ok(e.Owner, "network?" + R("2026-06-01", "2026-06-07"));
        Assert.Equal(0, net.GetProperty("appointments").GetInt32());
        Assert.Equal(0m, net.GetProperty("averageCheck").GetDecimal());
        Assert.Equal(0m, net.GetProperty("cancellationRate").GetDecimal());
        var series = (await Ok(e.Owner, "network/series?" + R("2026-06-01", "2026-06-07"))).GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(7, series.Count);
        Assert.All(series, p => { Assert.Equal(0, p.GetProperty("appointments").GetInt32()); Assert.Equal(0m, p.GetProperty("revenue").GetDecimal()); });

        var rows = (await Ok(e.Owner, "locations?" + R("2026-06-01", "2026-06-07"))).EnumerateArray().ToList();
        Assert.Equal(2, rows.Count); // усі активні заклади, також без записів
        var a = rows.Single(r => r.GetProperty("name").GetString() == "A");
        Assert.Equal(7 * 540, a.GetProperty("availableMinutes").GetInt32());
        Assert.Equal(0m, a.GetProperty("load").GetDecimal());
        var b = rows.Single(r => r.GetProperty("locationId").GetGuid() == noSchedule);
        Assert.Equal(0, b.GetProperty("availableMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("load").ValueKind);
    }

    // =====================================================================================================================
    // §19.4 завантаження
    // =====================================================================================================================

    [SkippableFact]
    public async Task load_formula_intersects_hours_and_skips_closures_weekly_closed_days_and_approved_absences()
    {
        NeedDb();
        // графік 10:00-18:00 ∩ години закладу 09:00-17:00 = 420 хв/день
        var e = await NewEnvAsync(specialistHours: Hours("10:00", "18:00"), locationHours: Hours("09:00", "17:00"), closedWeekdays: ["sun"]);
        var second = await AddSpecialistAsync(e, "Second", hours: Hours("09:00", "10:00")); // 60 хв/день
        // 01.06.2026 - понеділок; діапазон 01..14.06 (14 днів), неділі 07 і 14
        await AddClosureAsync(e, new DateOnly(2026, 6, 3), new DateOnly(2026, 6, 3));
        await AddAbsenceAsync(e, e.Specialist, new DateOnly(2026, 6, 9), new DateOnly(2026, 6, 10), "approved");
        await AddAbsenceAsync(e, e.Specialist, new DateOnly(2026, 6, 11), new DateOnly(2026, 6, 11), "requested"); // не зменшує
        await AddAbsenceAsync(e, e.Specialist, new DateOnly(2026, 6, 12), new DateOnly(2026, 6, 12), "rejected"); // не зменшує
        await AddApptAsync(e, Utc(2026, 6, 1, 10), "pending");
        await AddApptAsync(e, Utc(2026, 6, 2, 10), "confirmed");
        await AddApptAsync(e, Utc(2026, 6, 4, 10), "completed");
        await AddApptAsync(e, Utc(2026, 6, 5, 10), "cancelled");
        await AddApptAsync(e, Utc(2026, 6, 8, 10), "no_show");
        await AddApptAsync(e, Utc(2026, 6, 2, 9), "completed", specialist: second);

        // закритих днів: 03.06 (закриття), 07 і 14 (неділі) -> 11 днів у закладі; у першого ще 09 і 10 (approved) -> 9 днів
        var expectedFirst = 9 * 420;
        var expectedSecond = 11 * 60;
        var range = R("2026-06-01", "2026-06-14");
        var loc = Assert.Single((await Ok(e.Owner, "locations?" + range)).EnumerateArray());
        Assert.Equal(expectedFirst + expectedSecond, loc.GetProperty("availableMinutes").GetInt32());
        Assert.Equal(240, loc.GetProperty("busyMinutes").GetInt32()); // 3 + 1 (другий майстер); cancelled/no_show не входять
        Assert.Equal(Math.Round(240m / (expectedFirst + expectedSecond), 4), loc.GetProperty("load").GetDecimal());
        Assert.Equal(6, loc.GetProperty("appointments").GetInt32());
        Assert.Equal(1, loc.GetProperty("noShow").GetInt32());

        var b = await Ok(e.Owner, $"locations/{e.Location}/breakdown?" + range);
        var specs = b.GetProperty("specialists").EnumerateArray().ToList();
        Assert.Equal(2, specs.Count);
        Assert.Equal(loc.GetProperty("availableMinutes").GetInt32(), specs.Sum(s => s.GetProperty("availableMinutes").GetInt32()));
        Assert.Equal(loc.GetProperty("busyMinutes").GetInt32(), specs.Sum(s => s.GetProperty("busyMinutes").GetInt32()));
        Assert.Equal(loc.GetProperty("availableMinutes").GetInt32(), b.GetProperty("availableMinutes").GetInt32());
        Assert.Equal(loc.GetProperty("load").GetDecimal(), b.GetProperty("load").GetDecimal());
        var first = specs.Single(s => s.GetProperty("specialistId").GetGuid() == e.Specialist);
        Assert.Equal(expectedFirst, first.GetProperty("availableMinutes").GetInt32());
        Assert.Equal(180, first.GetProperty("busyMinutes").GetInt32());
        Assert.Equal(Math.Round(180m / expectedFirst, 4), first.GetProperty("load").GetDecimal());
        Assert.Equal(5, first.GetProperty("appointments").GetInt32());
        Assert.Equal(1, first.GetProperty("cancelled").GetInt32());
        // revenue desc: перший (1000+1000 completed: 04.06) і другий (1000)
        Assert.Equal(e.Specialist, specs[0].GetProperty("specialistId").GetGuid());
        Assert.Equal(1000m, first.GetProperty("revenue").GetDecimal());
    }

    [SkippableFact]
    public async Task load_is_null_when_nothing_is_available_even_if_there_are_bookings_and_is_clamped_to_one_when_overbooked()
    {
        NeedDb();
        // (a) усі дні тижня вихідні, записи є
        var closed = await NewEnvAsync(closedWeekdays: DayKeys);
        await AddApptAsync(closed, Utc(2026, 6, 10, 10));
        var row = Assert.Single((await Ok(closed.Owner, "locations?" + R("2026-06-08", "2026-06-12"))).EnumerateArray());
        Assert.Equal(0, row.GetProperty("availableMinutes").GetInt32());
        Assert.Equal(60, row.GetProperty("busyMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("load").ValueKind);

        // (b) approved-відсутність на весь діапазон
        var absent = await NewEnvAsync();
        await AddAbsenceAsync(absent, absent.Specialist, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), "approved");
        await AddApptAsync(absent, Utc(2026, 6, 10, 10));
        var absentRow = Assert.Single((await Ok(absent.Owner, "locations?" + R("2026-06-08", "2026-06-12"))).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, absentRow.GetProperty("load").ValueKind);

        // (c) години закладу не перетинаються з графіком
        var disjoint = await NewEnvAsync(specialistHours: Hours("09:00", "12:00"), locationHours: Hours("13:00", "18:00"));
        var disjointRow = Assert.Single((await Ok(disjoint.Owner, "locations?" + R("2026-06-08", "2026-06-12"))).EnumerateArray());
        Assert.Equal(0, disjointRow.GetProperty("availableMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, disjointRow.GetProperty("load").ValueKind);

        // (d) busy > avail: години закладу 10:00-11:00 (60 хв), записи 09:00 і 12:00 -> busy 120, load = 1
        var over = await NewEnvAsync(locationHours: Hours("10:00", "11:00"));
        await AddApptAsync(over, Utc(2026, 6, 10, 9));
        await AddApptAsync(over, Utc(2026, 6, 10, 12));
        var overRow = Assert.Single((await Ok(over.Owner, "locations?" + R("2026-06-10", "2026-06-10"))).EnumerateArray());
        Assert.Equal(60, overRow.GetProperty("availableMinutes").GetInt32());
        Assert.Equal(120, overRow.GetProperty("busyMinutes").GetInt32());
        Assert.Equal(1m, overRow.GetProperty("load").GetDecimal());
        var overBreakdown = await Ok(over.Owner, $"locations/{over.Location}/breakdown?" + R("2026-06-10", "2026-06-10"));
        Assert.Equal(1m, overBreakdown.GetProperty("specialists")[0].GetProperty("load").GetDecimal());
    }

    [SkippableFact]
    public async Task future_days_of_an_inactive_profile_or_link_give_no_availability_but_past_days_keep_the_stored_schedule()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var inactive = await AddSpecialistAsync(e, "Inactive", active: false);
        var inactiveLink = await AddSpecialistAsync(e, "Link off", linkActive: false);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var past = await Ok(e.Owner, $"locations/{e.Location}/breakdown?" + R(Iso(today.AddDays(-9)), Iso(today.AddDays(-5))));
        Assert.Equal(3 * 5 * 540, past.GetProperty("availableMinutes").GetInt32());

        // сьогодні + 2 дні вперед: активний -> 3 доби; неактивні -> лише сьогодні
        var forward = await Ok(e.Owner, $"locations/{e.Location}/breakdown?" + R(Iso(today), Iso(today.AddDays(2))));
        var bySpec = forward.GetProperty("specialists").EnumerateArray().ToDictionary(s => s.GetProperty("specialistId").GetGuid(), s => s.GetProperty("availableMinutes").GetInt32());
        Assert.Equal(3 * 540, bySpec[e.Specialist]);
        Assert.Equal(540, bySpec[inactive]);
        Assert.Equal(540, bySpec[inactiveLink]);

        var onlyFuture = await Ok(e.Owner, $"locations/{e.Location}/breakdown?" + R(Iso(today.AddDays(1)), Iso(today.AddDays(2))));
        var future = onlyFuture.GetProperty("specialists").EnumerateArray().Select(s => s.GetProperty("specialistId").GetGuid()).ToList();
        Assert.Equal([e.Specialist], future); // неактивні без записів і без доступних хвилин у список не потрапляють
    }

    [SkippableFact]
    public async Task breakdown_lists_specialists_with_bookings_or_availability_and_top_services_by_revenue()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var other = await AddSpecialistAsync(e, "Aaa idle", hours: "{}");           // без графіка і без записів -> не потрапляє
        var booked = await AddSpecialistAsync(e, "Zed booked", hours: "{}");        // без графіка, але є запис -> потрапляє
        await using (var ctx = fx.Db.CreateContext(e.TenantId))
        {
            var cheap = new Service { Name = "Cheap", DurationMinutes = 30 };
            ctx.Add(cheap);
            await ctx.SaveChangesAsync();
            ctx.Add(new Appointment
            {
                LocationId = e.Location, SpecialistId = booked, ServiceId = cheap.Id, ClientId = e.ClientId, StartsAt = Utc(2026, 6, 10, 12),
                DurationMinutes = 30, Source = AppointmentSource.Admin, PriceOriginal = 50m, PriceFinal = 50m, Status = AppointmentStatus.Completed,
            });
            await ctx.SaveChangesAsync();
        }
        await AddApptAsync(e, Utc(2026, 6, 10, 10));
        await AddApptAsync(e, Utc(2026, 6, 11, 10), "cancelled");

        var b = await Ok(e.Owner, $"locations/{e.Location}/breakdown?" + R("2026-06-10", "2026-06-11"));
        var ids = b.GetProperty("specialists").EnumerateArray().Select(s => s.GetProperty("specialistId").GetGuid()).ToList();
        Assert.Equal([e.Specialist, booked], ids); // revenue desc
        Assert.DoesNotContain(other, ids);
        var services = b.GetProperty("services").EnumerateArray().ToList();
        Assert.Equal(["Haircut", "Cheap"], services.Select(s => s.GetProperty("name").GetString()).ToArray()); // лише completed, revenue desc
        Assert.Equal(1, services[0].GetProperty("completed").GetInt32());
        Assert.Equal("Master", b.GetProperty("specialists")[0].GetProperty("name").GetString());
        Assert.Equal("UTC", b.GetProperty("timezone").GetString());
        Assert.Equal("2026-06-10", b.GetProperty("fromDate").GetString());
    }

    [SkippableFact]
    public async Task breakdown_of_an_inactive_own_location_is_allowed()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var inactive = await AddLocationAsync(e, "Old", "UTC", active: false);
        var b = await Send(e.Owner, $"locations/{inactive}/breakdown?" + R("2026-06-01", "2026-06-02"));
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        // у списку закладів неактивний без записів не показується; із записом - показується
        var rows = (await Ok(e.Owner, "locations?" + R("2026-06-01", "2026-06-30"))).EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("Old", rows);
        var spec = await AddSpecialistAsync(e, "Old master", inactive);
        await AddApptAsync(e, Utc(2026, 6, 10, 10), location: inactive, specialist: spec);
        var withRows = (await Ok(e.Owner, "locations?" + R("2026-06-01", "2026-06-30"))).EnumerateArray().ToList();
        var old = withRows.Single(r => r.GetProperty("name").GetString() == "Old");
        Assert.False(old.GetProperty("isActive").GetBoolean());
    }

    // =====================================================================================================================
    // §19.3 compare
    // =====================================================================================================================

    [SkippableFact]
    public async Task compare_adds_previous_with_zero_filled_union_of_locations_and_promotions()
    {
        NeedDb();
        var e = await NewEnvAsync("UTC", "A");
        var oldLocation = await AddLocationAsync(e, "Closed B", "UTC", active: false);
        var oldSpec = await AddSpecialistAsync(e, "Old", oldLocation);
        var idle = await AddLocationAsync(e, "Idle C", "UTC");
        var onlyPrev = await AddPromotionAsync(e, "Only previous");
        var both = await AddPromotionAsync(e, "Both");
        // поточний 2026-10-01..24, попередній 2026-09-07..30
        await AddApptAsync(e, Utc(2026, 10, 5, 10), price: 900m, original: 1000m, promotion: both);
        await AddApptAsync(e, Utc(2026, 9, 8, 10), price: 450m, original: 500m, promotion: both);
        await AddApptAsync(e, Utc(2026, 9, 9, 10), price: 270m, original: 300m, promotion: onlyPrev);
        await AddApptAsync(e, Utc(2026, 9, 10, 10), price: 700m, location: oldLocation, specialist: oldSpec);
        await AddApptAsync(e, Utc(2026, 9, 6, 10), price: 5000m); // 06.09 - поза обома періодами

        var range = R("2026-10-01", "2026-10-24");
        var net = await Ok(e.Owner, "network?" + range + "&compare=true");
        var prev = net.GetProperty("previous");
        Assert.Equal("2026-09-07", prev.GetProperty("fromDate").GetString());
        Assert.Equal("2026-09-30", prev.GetProperty("toDate").GetString());
        Assert.Equal(3, prev.GetProperty("appointments").GetInt32());
        Assert.Equal(450m + 270m + 700m, prev.GetProperty("revenue").GetDecimal());
        Assert.Equal(1, net.GetProperty("appointments").GetInt32());

        var locs = (await Ok(e.Owner, "locations?" + range + "&compare=true")).EnumerateArray().ToList();
        Assert.Equal(["A", "Closed B", "Idle C"], locs.Select(l => l.GetProperty("name").GetString()).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        var closedB = locs.Single(l => l.GetProperty("name").GetString() == "Closed B");
        Assert.Equal(0, closedB.GetProperty("appointments").GetInt32());
        Assert.Equal(1, closedB.GetProperty("previous").GetProperty("appointments").GetInt32());
        Assert.Equal(700m, closedB.GetProperty("previous").GetProperty("revenue").GetDecimal());
        var idleC = locs.Single(l => l.GetProperty("name").GetString() == "Idle C");
        Assert.Equal(0, idleC.GetProperty("previous").GetProperty("appointments").GetInt32());
        Assert.Equal(0, idleC.GetProperty("previous").GetProperty("availableMinutes").GetInt32()); // без майстрів
        Assert.Equal(JsonValueKind.Null, idleC.GetProperty("previous").GetProperty("load").ValueKind);
        var a = locs.Single(l => l.GetProperty("name").GetString() == "A");
        Assert.Equal(24 * 540, a.GetProperty("previous").GetProperty("availableMinutes").GetInt32());
        Assert.Equal(24 * 540, a.GetProperty("availableMinutes").GetInt32());

        var promos = (await Ok(e.Owner, "promotions?" + range + "&compare=true")).EnumerateArray().ToList();
        Assert.Equal(["Both", "Only previous"], promos.Select(p => p.GetProperty("name").GetString()).ToArray()); // за поточним uses
        var onlyPrevRow = promos[1];
        Assert.Equal(0, onlyPrevRow.GetProperty("uses").GetInt32());
        Assert.Equal(1, onlyPrevRow.GetProperty("previous").GetProperty("uses").GetInt32());
        Assert.Equal(30m, onlyPrevRow.GetProperty("previous").GetProperty("discountTotal").GetDecimal());
        Assert.Equal(100m, promos[0].GetProperty("discountTotal").GetDecimal());
        Assert.Equal(50m, promos[0].GetProperty("previous").GetProperty("discountTotal").GetDecimal());

        // без compare поля previous відсутні
        Assert.False((await Ok(e.Owner, "network?" + range)).TryGetProperty("previous", out _));
        Assert.All((await Ok(e.Owner, "locations?" + range)).EnumerateArray(), l => Assert.False(l.TryGetProperty("previous", out _)));
        Assert.All((await Ok(e.Owner, "promotions?" + range)).EnumerateArray(), l => Assert.False(l.TryGetProperty("previous", out _)));
    }

    // =====================================================================================================================
    // доступ, IDOR, RLS, відсутність даних клієнтів
    // =====================================================================================================================

    [SkippableFact]
    public async Task roles_modules_idor_and_tenant_isolation()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var other = await NewEnvAsync();
        await AddApptAsync(e, Utc(2026, 6, 10, 10), price: 100m);
        await AddApptAsync(other, Utc(2026, 6, 10, 10), price: 7000m);
        var admin = await fx.InviteAndLoginAsync(e.Tenant, e.Owner, Roles.Admin);
        var specialist = await fx.InviteAndLoginAsync(e.Tenant, e.Owner, Roles.Specialist, e.Specialist);
        var range = R("2026-06-01", "2026-06-30");
        var urls = new[]
        {
            "network?" + range, "network/series?" + range, "locations?" + range, "promotions?" + range,
            $"locations/{e.Location}/breakdown?" + range,
        };
        foreach (var url in urls)
        {
            Assert.Equal(HttpStatusCode.OK, (await Send(admin.AccessToken, url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(specialist.AccessToken, url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Get, "/api/beauty/analytics/" + url)).StatusCode);
        }

        // чужий заклад -> 404 (як неіснуючий), чужі дані в сумах відсутні (RLS)
        var foreign = await Send(e.Owner, $"locations/{other.Location}/breakdown?" + range);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("location_not_found", (await Read<JsonElement>(foreign)).GetProperty("code").GetString());
        Assert.Equal(100m, (await Ok(e.Owner, "network?" + range)).GetProperty("revenue").GetDecimal());
        Assert.Equal(100m, (await Ok(e.Owner, "network/series?" + range)).GetProperty("points").EnumerateArray().Sum(p => p.GetProperty("revenue").GetDecimal()));
        Assert.Equal(100m, Assert.Single((await Ok(e.Owner, "locations?" + range)).EnumerateArray()).GetProperty("revenue").GetDecimal());

        // вимкнений модуль
        var limited = await NewEnvAsync(modules: ["beauty_booking"]);
        foreach (var url in urls.Take(4).Append($"locations/{limited.Location}/breakdown?" + range))
        {
            var r = await Send(limited.Owner, url);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("module_disabled", (await Read<JsonElement>(r)).GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task responses_never_contain_client_personal_data()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var promo = await AddPromotionAsync(e, "P");
        await AddApptAsync(e, Utc(2026, 6, 10, 10), promotion: promo);
        var range = R("2026-06-01", "2026-06-30");
        foreach (var url in new[] { "network?" + range + "&compare=true", "network/series?" + range, "locations?" + range + "&compare=true",
                     "promotions?" + range + "&compare=true", $"locations/{e.Location}/breakdown?" + range })
        {
            var body = await (await Send(e.Owner, url)).Content.ReadAsStringAsync();
            Assert.DoesNotContain(ClientName, body);
            Assert.DoesNotContain("Zoryana", body);
            Assert.DoesNotContain(ClientPhone, body);
            Assert.DoesNotContain(e.ClientId.ToString(), body);
            Assert.DoesNotContain("clientId", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("phone", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // =====================================================================================================================
    // продуктивність: кількість запитів не залежить від довжини діапазону й кількості закладів/майстрів (немає N+1)
    // =====================================================================================================================

    [SkippableFact]
    public async Task query_count_does_not_depend_on_range_length_or_on_number_of_locations_and_specialists()
    {
        NeedDb();
        var small = await NewEnvAsync();
        var big = await NewEnvAsync("Europe/Kyiv", "Main");
        for (var i = 0; i < 5; i++)
        {
            var loc = await AddLocationAsync(big, $"L{i}", i % 2 == 0 ? "Pacific/Auckland" : "America/New_York");
            for (var j = 0; j < 3; j++)
            {
                var spec = await AddSpecialistAsync(big, $"S{i}{j}", loc);
                await AddApptAsync(big, Utc(2026, 3, 1 + i * 3 + j, 10), location: loc, specialist: spec);
            }
        }

        var counter = new SqlCounter();
        using var factory = fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b => b.ConfigureLogging(l =>
        {
            l.AddProvider(counter);
            l.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information);
        }));
        var client = factory.CreateClient();

        async Task<int> Count(Env env, string url)
        {
            counter.Reset();
            var r = await fx.Send(HttpMethod.Get, "/api/beauty/analytics/" + url, bearer: env.Owner, client: client);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return counter.Value;
        }

        foreach (var (path, compare) in new[] { ("network", "&compare=true"), ("network/series", ""), ("locations", "&compare=true"), ("promotions", "&compare=true") })
        {
            await Count(small, $"{path}?" + R("2026-06-01", "2026-06-02") + compare); // прогрів
            var shortSmall = await Count(small, $"{path}?" + R("2026-06-01", "2026-06-02") + compare);
            var longSmall = await Count(small, $"{path}?" + R("2025-06-01", "2026-05-31") + compare);
            var longBig = await Count(big, $"{path}?" + R("2025-06-01", "2026-05-31") + compare);
            Assert.True(shortSmall == longSmall && longSmall == longBig, $"{path}: short={shortSmall}, long={longSmall}, long+many={longBig}");
            Assert.InRange(longBig, 1, 14);
        }
        var bShort = await Count(big, $"locations/{big.Location}/breakdown?" + R("2026-06-01", "2026-06-02"));
        var bLong = await Count(big, $"locations/{big.Location}/breakdown?" + R("2025-06-01", "2026-05-31"));
        Assert.Equal(bShort, bLong);
        Assert.InRange(bLong, 1, 14);
    }

    private sealed class SqlCounter : ILoggerProvider
    {
        private int _count;
        public int Value => Volatile.Read(ref _count);
        public void Reset() => Interlocked.Exchange(ref _count, 0);
        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
        public void Dispose() { }

        private sealed class Sink(SqlCounter owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                // RelationalEventId.CommandExecuted = 20101: лише запити до таблиць аналітики (не auth/tenant)
                if (category != "Microsoft.EntityFrameworkCore.Database.Command" || eventId.Id != 20101) return;
                var text = formatter(state, exception);
                if (text.Contains("beauty_", StringComparison.Ordinal)) Interlocked.Increment(ref owner._count);
            }
        }
    }
}
