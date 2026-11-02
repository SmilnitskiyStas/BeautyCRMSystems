using System.Globalization;
using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-764 (§24.5-§24.6): фінансові поля series/network/locations і GET /analytics/network/details на реальному PostgreSQL (RLS).
/// Знімки собівартості (cost_snapshot, master_percent_snapshot, master_payout_snapshot, cost_captured_at) сидяться напряму, бо
/// їх пише TASK-763 у переході в completed.
/// </summary>
public sealed class AnalyticsDetailsApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private const string ClientName = "Zoryana Secretna";
    private const string ClientPhone = "+380509998877";
    private static readonly string[] DayKeys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
    private static readonly string AllDays =
        "{" + string.Join(",", DayKeys.Select(d => $"\"{d}\":[{{\"from\":\"09:00\",\"to\":\"18:00\"}}]")) + "}";

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    // ---------- сид ----------

    private sealed record Env(TenantSeed Tenant, string Owner, Guid Location, Guid Specialist, Guid Service, Guid ClientId)
    {
        public Guid TenantId => Tenant.TenantId;
    }

    private async Task<Env> NewEnvAsync(string tz = "UTC", string locationName = "A", string[]? modules = null)
    {
        var tenant = await fx.CreateTenantAsync(modules);
        var owner = await fx.LoginAsync(tenant.Slug, tenant.OwnerEmail);
        await using var ctx = fx.Db.CreateContext(tenant.TenantId);
        var location = new Location { Name = locationName, Timezone = tz };
        var specialist = new Specialist { FullName = "Master" };
        var service = new Service { Name = "Haircut", DurationMinutes = 60 };
        var client = new Client { FullName = ClientName, Phone = ClientPhone };
        ctx.AddRange(location, specialist, service, client);
        await ctx.SaveChangesAsync();
        ctx.Add(new SpecialistLocation { SpecialistId = specialist.Id, LocationId = location.Id, WorkingHours = AllDays });
        await ctx.SaveChangesAsync();
        return new Env(tenant, owner.AccessToken, location.Id, specialist.Id, service.Id, client.Id);
    }

    private async Task<Guid> AddLocationAsync(Env e, string name, string tz, bool active = true)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        var l = new Location { Name = name, Timezone = tz, IsActive = active };
        ctx.Add(l);
        await ctx.SaveChangesAsync();
        return l.Id;
    }

    private async Task<Guid> AddPromotionAsync(Env e, string name)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        var p = new Promotion { Name = name, DiscountType = DiscountType.Percent, DiscountValue = 10m };
        ctx.Add(p);
        await ctx.SaveChangesAsync();
        return p.Id;
    }

    private static Appointment Make(Env e, DateTimeOffset at, string status, decimal price, decimal? original, decimal? cost, decimal? percent,
        decimal? payout, Guid? location, Guid? promotion, int minutes = 60, Guid? specialist = null)
    {
        var a = new Appointment
        {
            LocationId = location ?? e.Location, SpecialistId = specialist ?? e.Specialist, ServiceId = e.Service, ClientId = e.ClientId,
            StartsAt = at.ToUniversalTime(), DurationMinutes = minutes, Source = AppointmentSource.Admin, PriceOriginal = original ?? price,
            PriceFinal = price, PromotionId = promotion,
            Status = status switch
            {
                "pending" => AppointmentStatus.Pending, "completed" => AppointmentStatus.Completed,
                "cancelled" => AppointmentStatus.Cancelled, "no_show" => AppointmentStatus.NoShow, _ => throw new ArgumentException(status),
            },
        };
        if (status == "cancelled") { a.CancelledAt = a.StartsAt; a.CancelledByType = "system"; }
        if (status == "completed")
        {
            a.CostSnapshot = cost;
            a.MasterPercentSnapshot = percent;
            a.MasterPayoutSnapshot = payout;
            if (cost is not null || percent is not null || payout is not null) a.CostCapturedAt = a.StartsAt;
        }
        return a;
    }

    private async Task<Guid> AddAsync(Env e, DateTimeOffset at, string status = "completed", decimal price = 1000m, decimal? original = null,
        decimal? cost = null, decimal? percent = null, decimal? payout = null, Guid? location = null, Guid? promotion = null, int minutes = 60)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        var a = Make(e, at, status, price, original, cost, percent, payout, location, promotion, minutes);
        ctx.Add(a);
        await ctx.SaveChangesAsync();
        return a.Id;
    }

    /// <summary>Записи по 10 хв; перетин одного майстра заборонений (EXCLUDE), тож однакові миті - різні майстри.</summary>
    private async Task AddManyAsync(Env e, IReadOnlyList<(DateTimeOffset At, Guid Specialist)> rows, decimal price = 10m, decimal? cost = 1m,
        decimal? percent = 10m, decimal? payout = 1m)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        foreach (var (t, sp) in rows) ctx.Add(Make(e, t, "completed", price, null, cost, percent, payout, null, null, 10, sp));
        await ctx.SaveChangesAsync();
    }

    private async Task<List<Guid>> AddSpecialistsAsync(Env e, int count)
    {
        await using var ctx = fx.Db.CreateContext(e.TenantId);
        var list = Enumerable.Range(0, count).Select(i => new Specialist { FullName = $"M{i}" }).ToList();
        ctx.AddRange(list);
        await ctx.SaveChangesAsync();
        return list.Select(x => x.Id).ToList();
    }

    // ---------- HTTP ----------

    private HttpClient? _client; // тест з сотнями запитів деталей підіймає ліміт (Analytics:DetailsPerUserPerMinute)

    private Task<HttpResponseMessage> Send(string token, string url) =>
        fx.Send(HttpMethod.Get, "/api/beauty/analytics/" + url, bearer: token, client: _client);

    private async Task<JsonElement> Ok(string token, string url)
    {
        var r = await Send(token, url);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await Read<JsonElement>(r);
    }

    private async Task<string> Code(string token, string url, HttpStatusCode expected)
    {
        var r = await Send(token, url);
        Assert.Equal(expected, r.StatusCode);
        return (await Read<JsonElement>(r)).GetProperty("code").GetString()!;
    }

    private static string Day(string date) => $"network/details?date={date}";
    private static string Span(string from, string to) => $"network/details?from={from}&to={to}";
    private static string SeriesUrl(string from, string to) => $"network/series?from={from}&to={to}";
    private static DateTimeOffset Utc(int y, int m, int d, int h = 10, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);
    private static DateTimeOffset Local(string tz, int y, int m, int d, int h, int min = 0) =>
        SlotCalculator.ToOffset(new DateOnly(y, m, d), new TimeOnly(h, min), TimeZoneInfo.FindSystemTimeZoneById(tz));

    private static decimal Dec(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    // =====================================================================================================================
    // формули й округлення (§24.5)
    // =====================================================================================================================

    [SkippableFact]
    public async Task summary_formulas_rounding_and_missing_cost()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var promo = await AddPromotionAsync(e, "Autumn -10%");
        // A: 333.33 x 33.33% = 111.0988 -> 111.10 (знімок), знижка 66.67 за акцією
        await AddAsync(e, Utc(2026, 6, 10, 8), price: 333.33m, original: 400m, cost: 100.10m, percent: 33.33m, payout: 111.10m, promotion: promo);
        // B: 1.00 x 0.5% = 0.005 -> 0.01
        await AddAsync(e, Utc(2026, 6, 10, 9), price: 1m, cost: 0m, percent: 0.5m, payout: 0.01m);
        // C: завершений до розгортання - без знімка
        var missing = await AddAsync(e, Utc(2026, 6, 10, 10), price: 500m);
        // нефінансові статуси
        await AddAsync(e, Utc(2026, 6, 10, 11), "cancelled", price: 700m);
        await AddAsync(e, Utc(2026, 6, 10, 12), "no_show", price: 800m);
        await AddAsync(e, Utc(2026, 6, 10, 13), "pending", price: 900m);
        // інший день
        await AddAsync(e, Utc(2026, 6, 11, 10), price: 9999m, cost: 1m, percent: 0m, payout: 0m);

        var r = await Ok(e.Owner, Day("2026-06-10"));
        var s = r.GetProperty("summary");
        Assert.Equal(6, s.GetProperty("appointments").GetInt32());
        Assert.Equal(3, s.GetProperty("completed").GetInt32());
        Assert.Equal(1, s.GetProperty("cancelled").GetInt32());
        Assert.Equal(1, s.GetProperty("noShow").GetInt32());
        Assert.Equal(834.33m, Dec(s, "revenue"));
        Assert.Equal(66.67m, Dec(s, "discountTotal"));
        Assert.Equal(100.10m, Dec(s, "cost"));
        Assert.Equal(111.11m, Dec(s, "masterPayout"));
        Assert.Equal(623.12m, Dec(s, "netIncome"));
        Assert.Equal(74.7m, Dec(s, "marginPercent")); // 623.12 / 834.33 = 74.685...
        Assert.Equal(1, s.GetProperty("withoutCost").GetInt32());
        Assert.Equal(500m, Dec(s, "revenueWithoutCost"));
        Assert.Equal("2026-06-10", r.GetProperty("fromDate").GetString());
        Assert.Equal("2026-06-10", r.GetProperty("toDate").GetString());
        Assert.Equal("UTC", r.GetProperty("timezone").GetString());
        Assert.False(r.TryGetProperty("location", out _)); // без фільтра

        var items = r.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count); // лише completed
        Assert.Equal(623.12m, items.Sum(i => Dec(i, "net")));
        var a = items[0];
        Assert.Equal("2026-06-10", a.GetProperty("localDate").GetString());
        Assert.Equal("08:00", a.GetProperty("localTime").GetString());
        Assert.Equal(400m, Dec(a, "priceOriginal"));
        Assert.Equal(66.67m, Dec(a, "discount"));
        Assert.Equal(333.33m, Dec(a, "priceFinal"));
        Assert.Equal(100.10m, Dec(a, "cost"));
        Assert.Equal(33.33m, Dec(a, "masterPercent"));
        Assert.Equal(111.10m, Dec(a, "masterPayout"));
        Assert.Equal(122.13m, Dec(a, "net"));
        Assert.False(a.GetProperty("costMissing").GetBoolean());
        Assert.Equal("Autumn -10%", a.GetProperty("promotion").GetProperty("name").GetString());
        Assert.Equal("Haircut", a.GetProperty("service").GetProperty("name").GetString());
        Assert.Equal("Master", a.GetProperty("specialist").GetProperty("name").GetString());
        Assert.Equal("A", a.GetProperty("location").GetProperty("name").GetString());
        Assert.False(items[1].TryGetProperty("promotion", out _));
        Assert.Equal(0.01m, Dec(items[1], "masterPayout"));
        var m = items[2];
        Assert.Equal(missing.ToString(), m.GetProperty("appointmentId").GetString());
        Assert.True(m.GetProperty("costMissing").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("cost").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("masterPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("masterPayout").ValueKind);
        Assert.Equal(500m, Dec(m, "net")); // без собівартості рахується з нулем

        var loc = Assert.Single(r.GetProperty("locations").EnumerateArray());
        Assert.Equal(3, loc.GetProperty("completed").GetInt32());
        Assert.Equal(623.12m, Dec(loc, "netIncome"));
        Assert.Equal(74.7m, Dec(loc, "marginPercent"));
        Assert.Equal(1, loc.GetProperty("withoutCost").GetInt32());
    }

    [SkippableFact]
    public async Task negative_net_zero_revenue_gives_null_margin_and_empty_day_is_all_zero()
    {
        NeedDb();
        var e = await NewEnvAsync();
        // акція 100%: ціна 0, собівартість 50, виплата 0 -> net = -50, виручка 0 -> маржа null
        await AddAsync(e, Utc(2026, 6, 10, 8), price: 0m, original: 300m, cost: 50m, percent: 40m, payout: 0m);
        var r = await Ok(e.Owner, Day("2026-06-10"));
        var s = r.GetProperty("summary");
        Assert.Equal(0m, Dec(s, "revenue"));
        Assert.Equal(-50m, Dec(s, "netIncome"));
        Assert.Equal(JsonValueKind.Null, s.GetProperty("marginPercent").ValueKind);
        Assert.Equal(300m, Dec(s, "discountTotal"));
        Assert.Equal(-50m, Dec(Assert.Single(r.GetProperty("items").EnumerateArray()), "net"));

        // збиток при ненульовій виручці: 100 - 150 - 0 = -50 -> -50.0 %
        await AddAsync(e, Utc(2026, 6, 12, 8), price: 100m, cost: 150m, percent: 0m, payout: 0m);
        Assert.Equal(-50m, Dec((await Ok(e.Owner, Day("2026-06-12"))).GetProperty("summary"), "marginPercent"));

        var empty = await Ok(e.Owner, Day("2026-07-01"));
        var es = empty.GetProperty("summary");
        foreach (var name in new[] { "appointments", "completed", "cancelled", "noShow", "revenue", "discountTotal", "cost", "masterPayout",
                     "netIncome", "withoutCost", "revenueWithoutCost" })
            Assert.Equal(0m, Dec(es, name));
        Assert.Equal(JsonValueKind.Null, es.GetProperty("marginPercent").ValueKind);
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        Assert.Empty(empty.GetProperty("locations").EnumerateArray());
        Assert.False(empty.GetProperty("hasMore").GetBoolean());
        Assert.False(empty.TryGetProperty("nextCursor", out _));
    }

    [SkippableFact]
    public async Task margin_rounds_half_away_from_zero_to_one_decimal()
    {
        NeedDb();
        var e = await NewEnvAsync();
        // net/revenue = 0.12345 -> 12.345 % -> 12.3; відємний: -12.35 -> -12.4 (від нуля)
        await AddAsync(e, Utc(2026, 6, 10, 8), price: 10000m, cost: 8765.50m, percent: 0m, payout: 0m); // net 1234.50 -> 12.345
        await AddAsync(e, Utc(2026, 6, 11, 8), price: 2000m, cost: 2247m, percent: 0m, payout: 0m); // net -247 -> -12.35
        Assert.Equal(12.3m, Dec((await Ok(e.Owner, Day("2026-06-10"))).GetProperty("summary"), "marginPercent"));
        Assert.Equal(-12.4m, Dec((await Ok(e.Owner, Day("2026-06-11"))).GetProperty("summary"), "marginPercent"));
    }

    // =====================================================================================================================
    // часові пояси, межі місяця, DST (Kyiv), різні зони закладів
    // =====================================================================================================================

    [SkippableFact]
    public async Task day_boundaries_use_the_local_day_of_each_location_kyiv_and_auckland()
    {
        NeedDb();
        var e = await NewEnvAsync("Europe/Kyiv", "Main");
        var auckland = await AddLocationAsync(e, "Zealand", "Pacific/Auckland");
        var late = await AddAsync(e, Local("Europe/Kyiv", 2026, 10, 31, 23, 30), price: 100m, cost: 10m, percent: 0m, payout: 0m, minutes: 30); // 31.10
        var early = await AddAsync(e, Local("Europe/Kyiv", 2026, 11, 1, 0, 15), price: 200m, cost: 10m, percent: 0m, payout: 0m); // 01.11 (31.10 22:15 UTC)
        var nz = await AddAsync(e, Local("Pacific/Auckland", 2026, 10, 1, 8), price: 300m, cost: 10m, percent: 0m, payout: 0m, location: auckland); // 30.09 UTC

        var oct31 = await Ok(e.Owner, Day("2026-10-31"));
        var only31 = Assert.Single(oct31.GetProperty("items").EnumerateArray());
        Assert.Equal(late.ToString(), only31.GetProperty("appointmentId").GetString());
        Assert.Equal("2026-10-31", only31.GetProperty("localDate").GetString());
        Assert.Equal("23:30", only31.GetProperty("localTime").GetString());
        Assert.Equal("Europe/Kyiv", oct31.GetProperty("timezone").GetString());

        var nov1 = await Ok(e.Owner, Day("2026-11-01"));
        var only1 = Assert.Single(nov1.GetProperty("items").EnumerateArray());
        Assert.Equal(early.ToString(), only1.GetProperty("appointmentId").GetString());
        Assert.Equal("2026-11-01", only1.GetProperty("localDate").GetString());
        Assert.Equal("00:15", only1.GetProperty("localTime").GetString());

        var oct1 = await Ok(e.Owner, Day("2026-10-01"));
        var nzItem = Assert.Single(oct1.GetProperty("items").EnumerateArray());
        Assert.Equal(nz.ToString(), nzItem.GetProperty("appointmentId").GetString());
        Assert.Equal("08:00", nzItem.GetProperty("localTime").GetString());
        Assert.Equal("Zealand", nzItem.GetProperty("location").GetProperty("name").GetString());
        Assert.Empty((await Ok(e.Owner, Day("2026-09-30"))).GetProperty("items").EnumerateArray());

        // фільтр закладу: підсумок і записи лише закладу; заголовок location
        var onlyNz = await Ok(e.Owner, Day("2026-10-01") + $"&locationId={auckland}");
        Assert.Equal("Zealand", onlyNz.GetProperty("location").GetProperty("name").GetString());
        Assert.Equal("Pacific/Auckland", onlyNz.GetProperty("location").GetProperty("timezone").GetString());
        Assert.Equal(300m, Dec(onlyNz.GetProperty("summary"), "revenue"));
        var wrongFilter = await Ok(e.Owner, Day("2026-10-01") + $"&locationId={e.Location}");
        Assert.Empty(wrongFilter.GetProperty("items").EnumerateArray());
        Assert.Equal(0m, Dec(wrongFilter.GetProperty("summary"), "revenue"));

        // межа місяця: жовтень включає 31.10 і виключає 01.11
        var october = await Ok(e.Owner, Span("2026-10-01", "2026-10-31"));
        Assert.Equal(400m, Dec(october.GetProperty("summary"), "revenue")); // 100 + 300
        var november = await Ok(e.Owner, Span("2026-11-01", "2026-11-30"));
        Assert.Equal(200m, Dec(november.GetProperty("summary"), "revenue"));
    }

    [SkippableFact]
    public async Task dst_both_occurrences_of_the_repeated_hour_are_in_the_day_ordered_by_utc_and_spring_day_has_no_shift()
    {
        NeedDb();
        var e = await NewEnvAsync("Europe/Kyiv");
        // 25.10.2026 перехід на зимовий час: 03:30 EEST = 00:30 UTC, 03:30 EET = 01:30 UTC
        var first = await AddAsync(e, Utc(2026, 10, 25, 0, 30), price: 100m, cost: 1m, percent: 0m, payout: 0m);
        var second = await AddAsync(e, Utc(2026, 10, 25, 1, 30), price: 200m, cost: 1m, percent: 0m, payout: 0m);
        await AddAsync(e, Local("Europe/Kyiv", 2026, 10, 24, 23, 59), price: 5m, cost: 1m, percent: 0m, payout: 0m);
        await AddAsync(e, Local("Europe/Kyiv", 2026, 10, 26, 0, 0), price: 7m, cost: 1m, percent: 0m, payout: 0m);

        var r = await Ok(e.Owner, Day("2026-10-25"));
        var items = r.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(first.ToString(), items[0].GetProperty("appointmentId").GetString());
        Assert.Equal(second.ToString(), items[1].GetProperty("appointmentId").GetString());
        Assert.All(items, i =>
        {
            Assert.Equal("03:30", i.GetProperty("localTime").GetString());
            Assert.Equal("2026-10-25", i.GetProperty("localDate").GetString());
        });
        Assert.Equal(300m, Dec(r.GetProperty("summary"), "revenue"));

        // 29.03.2026 (23 години): записи 28.03 23:30 / 29.03 12:00 / 30.03 00:10 local - у своїх днях
        var mar28 = await AddAsync(e, Local("Europe/Kyiv", 2026, 3, 28, 23, 30), price: 11m, cost: 1m, percent: 0m, payout: 0m);
        var mar29 = await AddAsync(e, Local("Europe/Kyiv", 2026, 3, 29, 12), price: 22m, cost: 1m, percent: 0m, payout: 0m);
        var mar30 = await AddAsync(e, Local("Europe/Kyiv", 2026, 3, 30, 0, 10), price: 33m, cost: 1m, percent: 0m, payout: 0m);
        foreach (var (day, id, time) in new[] { ("2026-03-28", mar28, "23:30"), ("2026-03-29", mar29, "12:00"), ("2026-03-30", mar30, "00:10") })
        {
            var d = Assert.Single((await Ok(e.Owner, Day(day))).GetProperty("items").EnumerateArray());
            Assert.Equal(id.ToString(), d.GetProperty("appointmentId").GetString());
            Assert.Equal(time, d.GetProperty("localTime").GetString());
        }
    }

    // =====================================================================================================================
    // інваріанти: details = точка series; Σ locations = summary; Σ items.net = netIncome; series = network = locations
    // =====================================================================================================================

    [SkippableFact]
    public async Task details_summary_equals_the_series_point_for_day_week_and_month_and_all_sums_agree()
    {
        NeedDb();
        using var factory = fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b => b.UseSetting("Analytics:DetailsPerUserPerMinute", "600"));
        _client = factory.CreateClient();
        var e = await NewEnvAsync("Europe/Kyiv", "Main");
        var nz = await AddLocationAsync(e, "Zealand", "Pacific/Auckland");
        var ny = await AddLocationAsync(e, "York", "America/New_York");
        var rnd = new Random(7);
        var origin = Local("Europe/Kyiv", 2026, 9, 28, 8);
        await using (var ctx = fx.Db.CreateContext(e.TenantId))
        {
            var usedMinutes = new HashSet<long>();
            for (var i = 0; i < 120; i++)
            {
                var at = origin.AddHours(rnd.Next(0, 24 * 40)).AddMinutes(rnd.Next(0, 60));
                if (!usedMinutes.Add(at.UtcTicks)) continue;
                var loc = (i % 3) switch { 0 => e.Location, 1 => nz, _ => ny };
                var status = (i % 7) switch { 0 => "cancelled", 1 => "no_show", 2 => "pending", _ => "completed" };
                var price = Math.Round((decimal)(rnd.NextDouble() * 900 + 10), 2);
                decimal? cost = i % 5 == 0 ? null : Math.Round((decimal)(rnd.NextDouble() * 200), 2);
                decimal? pct = cost is null ? null : Math.Round((decimal)(rnd.NextDouble() * 50), 2);
                decimal? payout = cost is null ? null : Math.Round(price * pct!.Value / 100m, 2, MidpointRounding.AwayFromZero);
                ctx.Add(Make(e, at, status, price, price + 25m, cost, pct, payout, loc, null, 1));
            }
            await ctx.SaveChangesAsync();
        }

        // day (<= 31 днів): кожна точка
        var series = await Ok(e.Owner, SeriesUrl("2026-10-01", "2026-10-31"));
        Assert.Equal("day", series.GetProperty("granularity").GetString());
        foreach (var p in series.GetProperty("points").EnumerateArray())
            await AssertPointEqualsDetails(e, p, $"network/details?date={p.GetProperty("from").GetString()}");

        // week (32..120 днів) і month (121+): кошики, у т.ч. обрізані; діапазон кошика <= 31 дня
        var weeks = await Ok(e.Owner, SeriesUrl("2026-09-28", "2026-11-15"));
        Assert.Equal("week", weeks.GetProperty("granularity").GetString());
        foreach (var p in weeks.GetProperty("points").EnumerateArray())
            await AssertPointEqualsDetails(e, p, Span(p.GetProperty("from").GetString()!, p.GetProperty("to").GetString()!));

        var months = await Ok(e.Owner, SeriesUrl("2026-06-15", "2026-11-15"));
        Assert.Equal("month", months.GetProperty("granularity").GetString());
        foreach (var p in months.GetProperty("points").EnumerateArray())
            await AssertPointEqualsDetails(e, p, Span(p.GetProperty("from").GetString()!, p.GetProperty("to").GetString()!));

        // Σ точок = network; Σ locations = network (фінансові поля)
        var range = "from=2026-09-28&to=2026-11-15";
        var network = await Ok(e.Owner, "network?" + range);
        var allPoints = weeks.GetProperty("points").EnumerateArray().ToList();
        foreach (var name in new[] { "revenue", "cost", "masterPayout", "netIncome" })
            Assert.Equal(Dec(network, name), allPoints.Sum(p => Dec(p, name)));
        Assert.Equal(network.GetProperty("withoutCost").GetInt32(), allPoints.Sum(p => p.GetProperty("withoutCost").GetInt32()));
        var rows = (await Ok(e.Owner, "locations?" + range)).EnumerateArray().ToList();
        foreach (var name in new[] { "revenue", "cost", "masterPayout", "netIncome" })
            Assert.Equal(Dec(network, name), rows.Sum(r => Dec(r, name)));
        Assert.Equal(network.GetProperty("withoutCost").GetInt32(), rows.Sum(r => r.GetProperty("withoutCost").GetInt32()));
        Assert.Equal(Dec(network, "netIncome"), Dec(network, "revenue") - Dec(network, "cost") - Dec(network, "masterPayout"));

        // деталі за той самий діапазон (<= 31 дня) = network
        var d = await Ok(e.Owner, Span("2026-10-01", "2026-10-31"));
        var n31 = await Ok(e.Owner, "network?from=2026-10-01&to=2026-10-31");
        foreach (var name in new[] { "revenue", "cost", "masterPayout", "netIncome", "revenueWithoutCost" })
            Assert.Equal(Dec(n31, name), Dec(d.GetProperty("summary"), name));
        Assert.Equal(n31.GetProperty("marginPercent").ValueKind == JsonValueKind.Null, d.GetProperty("summary").GetProperty("marginPercent").ValueKind == JsonValueKind.Null);
        if (n31.GetProperty("marginPercent").ValueKind != JsonValueKind.Null)
            Assert.Equal(Dec(n31, "marginPercent"), Dec(d.GetProperty("summary"), "marginPercent"));
    }

    private async Task AssertPointEqualsDetails(Env e, JsonElement point, string url)
    {
        var all = new List<JsonElement>();
        string? cursor = null;
        JsonElement last;
        do
        {
            last = await Ok(e.Owner, url + "&take=7" + (cursor is null ? "" : "&cursor=" + cursor));
            all.AddRange(last.GetProperty("items").EnumerateArray().Select(x => x.Clone()));
            cursor = last.TryGetProperty("nextCursor", out var c) ? c.GetString() : null;
        } while (last.GetProperty("hasMore").GetBoolean());

        var first = await Ok(e.Owner, url);
        var s = first.GetProperty("summary");
        foreach (var name in new[] { "appointments", "completed", "cancelled", "noShow", "withoutCost" })
            Assert.Equal(point.GetProperty(name).GetInt32(), s.GetProperty(name).GetInt32());
        foreach (var name in new[] { "revenue", "cost", "masterPayout", "netIncome" })
            Assert.Equal(Dec(point, name), Dec(s, name));
        // Σ locations = summary; Σ net записів по всіх сторінках = netIncome; кількість записів = completed
        var locs = first.GetProperty("locations").EnumerateArray().ToList();
        foreach (var name in new[] { "revenue", "cost", "masterPayout", "netIncome" })
            Assert.Equal(Dec(s, name), locs.Sum(l => Dec(l, name)));
        Assert.Equal(s.GetProperty("completed").GetInt32(), locs.Sum(l => l.GetProperty("completed").GetInt32()));
        Assert.Equal(s.GetProperty("completed").GetInt32(), all.Count);
        Assert.Equal(Dec(s, "netIncome"), all.Sum(i => Dec(i, "net")));
        Assert.Equal(Dec(s, "revenue"), all.Sum(i => Dec(i, "priceFinal")));
        Assert.Equal(s.GetProperty("withoutCost").GetInt32(), all.Count(i => i.GetProperty("costMissing").GetBoolean()));
    }

    [SkippableFact]
    public async Task series_network_and_locations_get_additive_financial_fields_and_legacy_is_unchanged()
    {
        NeedDb();
        var e = await NewEnvAsync();
        await AddAsync(e, Utc(2026, 6, 10, 8), price: 200m, cost: 50m, percent: 10m, payout: 20m);
        await AddAsync(e, Utc(2026, 6, 10, 9), price: 100m); // без собівартості
        await AddAsync(e, Utc(2026, 5, 10, 9), price: 80m, cost: 30m, percent: 0m, payout: 0m);

        var net = await Ok(e.Owner, "network?from=2026-06-01&to=2026-06-30&compare=true");
        Assert.Equal(300m, Dec(net, "revenue"));
        Assert.Equal(50m, Dec(net, "cost"));
        Assert.Equal(20m, Dec(net, "masterPayout"));
        Assert.Equal(230m, Dec(net, "netIncome"));
        Assert.Equal(76.7m, Dec(net, "marginPercent")); // 76.666...
        Assert.Equal(1, net.GetProperty("withoutCost").GetInt32());
        Assert.Equal(100m, Dec(net, "revenueWithoutCost"));
        Assert.Equal(300m, Dec(net, "revenue"));
        var prev = net.GetProperty("previous"); // 01.05-31.05 -> 05-01..05-31 (30 днів: 02.05-31.05)
        Assert.Equal(80m, Dec(prev, "revenue"));
        Assert.Equal(30m, Dec(prev, "cost"));
        Assert.Equal(50m, Dec(prev, "netIncome"));
        Assert.Equal(62.5m, Dec(prev, "marginPercent"));
        Assert.Equal(0, prev.GetProperty("withoutCost").GetInt32());

        var point = Assert.Single((await Ok(e.Owner, SeriesUrl("2026-06-10", "2026-06-10"))).GetProperty("points").EnumerateArray());
        Assert.Equal(50m, Dec(point, "cost"));
        Assert.Equal(20m, Dec(point, "masterPayout"));
        Assert.Equal(230m, Dec(point, "netIncome"));
        Assert.Equal(1, point.GetProperty("withoutCost").GetInt32());

        var row = Assert.Single((await Ok(e.Owner, "locations?from=2026-06-01&to=2026-06-30&compare=true")).EnumerateArray());
        Assert.Equal(50m, Dec(row, "cost"));
        Assert.Equal(20m, Dec(row, "masterPayout"));
        Assert.Equal(230m, Dec(row, "netIncome"));
        Assert.Equal(76.7m, Dec(row, "marginPercent"));
        Assert.Equal(1, row.GetProperty("withoutCost").GetInt32());
        Assert.Equal(50m, Dec(row.GetProperty("previous"), "netIncome"));

        // legacy (значення з часом): жодних нових полів
        var legacy = await Ok(e.Owner, "network?from=2026-06-01T00:00:00Z&to=2026-07-01T00:00:00Z");
        foreach (var name in new[] { "cost", "masterPayout", "netIncome", "marginPercent", "withoutCost", "revenueWithoutCost", "fromDate", "timezone" })
            Assert.False(legacy.TryGetProperty(name, out _), name);
        var legacyLocation = Assert.Single((await Ok(e.Owner, "locations?from=2026-06-01T00:00:00Z&to=2026-07-01T00:00:00Z")).EnumerateArray());
        foreach (var name in new[] { "cost", "masterPayout", "netIncome", "marginPercent", "withoutCost" })
            Assert.False(legacyLocation.TryGetProperty(name, out _), name);

        // breakdown/promotions без фінансових полів
        var breakdown = await Ok(e.Owner, $"locations/{e.Location}/breakdown?from=2026-06-01&to=2026-06-30");
        Assert.False(breakdown.TryGetProperty("netIncome", out _));
    }

    // =====================================================================================================================
    // пагінація
    // =====================================================================================================================

    [SkippableFact]
    public async Task pagination_450_items_pages_200_200_50_with_ties_no_duplicates_and_cursor_checks()
    {
        NeedDb();
        var e = await NewEnvAsync();
        // 450 записів, лише 10 різних starts_at (по 45 на мить) -> порядок рішає id
        var specialists = await AddSpecialistsAsync(e, 45);
        var rows = Enumerable.Range(0, 450).Select(i => (Utc(2026, 6, 10, 6).AddMinutes(i % 10 * 15), specialists[i / 10])).ToList();
        await AddManyAsync(e, rows, price: 10m, cost: 1m, percent: 10m, payout: 1m);

        var seen = new List<string>();
        var hasMore = new List<bool>();
        var sizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await Ok(e.Owner, Day("2026-06-10") + "&take=200" + (cursor is null ? "" : "&cursor=" + cursor));
            var items = page.GetProperty("items").EnumerateArray().ToList();
            sizes.Add(items.Count);
            hasMore.Add(page.GetProperty("hasMore").GetBoolean());
            seen.AddRange(items.Select(i => i.GetProperty("appointmentId").GetString()!));
            Assert.Equal(450, page.GetProperty("summary").GetProperty("completed").GetInt32()); // підсумок не залежить від сторінки
            Assert.Equal(4500m, Dec(page.GetProperty("summary"), "revenue"));
            cursor = page.TryGetProperty("nextCursor", out var c) ? c.GetString() : null;
            Assert.Equal(page.GetProperty("hasMore").GetBoolean(), cursor is not null);
        } while (cursor is not null);

        Assert.Equal([200, 200, 50], sizes);
        Assert.Equal([true, true, false], hasMore);
        Assert.Equal(450, seen.Distinct().Count());

        // детермінований порядок: ті самі сторінки при повторі, послідовність за (час, id)
        var again = await Ok(e.Owner, Day("2026-06-10") + "&take=200");
        Assert.Equal(seen.Take(200), again.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("appointmentId").GetString()));
        var firstPage = again.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("localTime").GetString()!).ToList();
        Assert.Equal(firstPage.OrderBy(x => x, StringComparer.Ordinal).ToList(), firstPage);

        // take: за замовчуванням 100, 500 -> 200, 0 -> 1
        Assert.Equal(100, (await Ok(e.Owner, Day("2026-06-10"))).GetProperty("items").GetArrayLength());
        Assert.Equal(200, (await Ok(e.Owner, Day("2026-06-10") + "&take=500")).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await Ok(e.Owner, Day("2026-06-10") + "&take=0")).GetProperty("items").GetArrayLength());

        // курсор прив'язаний до параметрів: інший заклад / інший день / сміття -> 422 invalid_cursor
        var token = (await Ok(e.Owner, Day("2026-06-10") + "&take=10")).GetProperty("nextCursor").GetString()!;
        Assert.Equal("location_not_found", await Code(e.Owner, Day("2026-06-10") + $"&locationId={Guid.NewGuid()}&cursor={token}", HttpStatusCode.NotFound));
        var other = await AddLocationForCursorAsync(e);
        Assert.Equal("invalid_cursor", await Code(e.Owner, Day("2026-06-10") + $"&locationId={other}&cursor={token}", HttpStatusCode.UnprocessableEntity));
        Assert.Equal("invalid_cursor", await Code(e.Owner, Day("2026-06-11") + $"&cursor={token}", HttpStatusCode.UnprocessableEntity));
        Assert.Equal("invalid_cursor", await Code(e.Owner, Day("2026-06-10") + "&cursor=not-a-cursor", HttpStatusCode.UnprocessableEntity));
        Assert.Equal("invalid_cursor", await Code(e.Owner, Day("2026-06-10") + "&cursor=" + Convert.ToBase64String("1|2|3"u8.ToArray()).TrimEnd('='), HttpStatusCode.UnprocessableEntity));
    }

    private async Task<Guid> AddLocationForCursorAsync(Env e) => await AddLocationAsync(e, "Other", "UTC");

    // =====================================================================================================================
    // валідація
    // =====================================================================================================================

    [SkippableFact]
    public async Task validation_invalid_date_invalid_range_and_31_day_limit()
    {
        NeedDb();
        var e = await NewEnvAsync();
        foreach (var bad in new[] { "2026-02-30", "2026-6-1", "abc", "1999-12-31", "2101-01-01", "2026-06-10T00:00:00Z" })
            Assert.Equal("invalid_date", await Code(e.Owner, Day(bad), HttpStatusCode.UnprocessableEntity));

        foreach (var url in new[]
                 {
                     "network/details", // нічого
                     "network/details?date=2026-06-10&from=2026-06-10", // date разом з from
                     "network/details?date=2026-06-10&to=2026-06-10",
                     "network/details?from=2026-06-10", // лише одна межа
                     "network/details?to=2026-06-10",
                     Span("2026-06-10", "2026-06-09"), // from > to
                     Span("2026-06-01", "2026-07-02"), // 32 дні
                     Span("2026-06-01T00:00:00Z", "2026-06-02T00:00:00Z"), // з часом
                     Span("2026-06-01", "2026-06-02T00:00:00Z"), // змішаний
                     Span("2026-02-30", "2026-03-05"),
                 })
            Assert.Equal("invalid_range", await Code(e.Owner, url, HttpStatusCode.UnprocessableEntity));

        Assert.Equal(HttpStatusCode.OK, (await Send(e.Owner, Span("2026-06-01", "2026-07-01"))).StatusCode); // 31 день
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Owner, Span("2026-06-10", "2026-06-10"))).StatusCode);
    }

    // =====================================================================================================================
    // ролі, IDOR, модуль, no-store, ліміт
    // =====================================================================================================================

    [SkippableFact]
    public async Task roles_idor_module_and_tenant_isolation()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var other = await NewEnvAsync();
        await AddAsync(e, Utc(2026, 6, 10, 10), price: 100m, cost: 10m, percent: 0m, payout: 0m);
        await AddAsync(other, Utc(2026, 6, 10, 10), price: 7000m, cost: 10m, percent: 0m, payout: 0m);
        var admin = await fx.InviteAndLoginAsync(e.Tenant, e.Owner, Roles.Admin);
        var specialist = await fx.InviteAndLoginAsync(e.Tenant, e.Owner, Roles.Specialist, e.Specialist);
        var url = Day("2026-06-10");

        Assert.Equal(HttpStatusCode.OK, (await Send(admin.AccessToken, url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(specialist.AccessToken, url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(specialist.AccessToken, "network?from=2026-06-01&to=2026-06-30")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(specialist.AccessToken, SeriesUrl("2026-06-01", "2026-06-30"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Get, "/api/beauty/analytics/" + url)).StatusCode);

        // чужий заклад -> 404 (як неіснуючий); дані tenant B не в сумах
        Assert.Equal("location_not_found", await Code(e.Owner, url + $"&locationId={other.Location}", HttpStatusCode.NotFound));
        Assert.Equal("location_not_found", await Code(e.Owner, url + $"&locationId={Guid.NewGuid()}", HttpStatusCode.NotFound));
        var mine = await Ok(e.Owner, url);
        Assert.Equal(100m, Dec(mine.GetProperty("summary"), "revenue"));
        Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Single(mine.GetProperty("locations").EnumerateArray());

        // власний неактивний заклад дозволений
        var inactive = await AddLocationAsync(e, "Old", "UTC", active: false);
        Assert.Equal(HttpStatusCode.OK, (await Send(e.Owner, url + $"&locationId={inactive}")).StatusCode);

        // вимкнений модуль
        var limited = await NewEnvAsync(modules: ["beauty_booking"]);
        Assert.Equal("module_disabled", await Code(limited.Owner, url, HttpStatusCode.Forbidden));
    }

    [SkippableFact]
    public async Task all_analytics_responses_are_no_store_including_errors()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var specialist = await fx.InviteAndLoginAsync(e.Tenant, e.Owner, Roles.Specialist, e.Specialist);
        await AddAsync(e, Utc(2026, 6, 10, 10), price: 100m, cost: 10m, percent: 0m, payout: 0m);
        var range = "from=2026-06-01&to=2026-06-30";
        var urls = new[]
        {
            "network?" + range, SeriesUrl("2026-06-01", "2026-06-30"), "locations?" + range, "promotions?" + range,
            $"locations/{e.Location}/breakdown?" + range, Day("2026-06-10"), "network/details?date=bad", "network/details",
            $"locations/{Guid.NewGuid()}/breakdown?" + range, "network?from=2026-06-30&to=2026-06-01",
        };
        foreach (var url in urls)
        {
            Assert.Equal("no-store", (await Send(e.Owner, url)).Headers.CacheControl?.ToString());
            Assert.Equal("no-store", (await Send(specialist.AccessToken, url)).Headers.CacheControl?.ToString()); // 403
            Assert.Equal("no-store", (await fx.Send(HttpMethod.Get, "/api/beauty/analytics/" + url)).Headers.CacheControl?.ToString()); // 401
        }
    }

    [SkippableFact]
    public async Task details_requests_are_rate_limited_per_user_with_retry_after_and_no_store()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var admin = await fx.InviteAndLoginAsync(e.Tenant, e.Owner, Roles.Admin);
        using var factory = fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b => b.UseSetting("Analytics:DetailsPerUserPerMinute", "10"));
        var client = factory.CreateClient();
        Task<HttpResponseMessage> Get(string token, string url) =>
            fx.Send(HttpMethod.Get, "/api/beauty/analytics/" + url, bearer: token, client: client);

        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await Get(e.Owner, Day("2026-06-10"))).StatusCode);
        var limited = await Get(e.Owner, Day("2026-06-10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", (await Read<JsonElement>(limited)).GetProperty("code").GetString());
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var ra) && int.Parse(ra.Single(), CultureInfo.InvariantCulture) >= 1);
        Assert.Equal("no-store", limited.Headers.CacheControl?.ToString());
        // інші ендпоінти аналітики й інший користувач не страждають
        Assert.Equal(HttpStatusCode.OK, (await Get(e.Owner, "network?from=2026-06-01&to=2026-06-30")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(admin.AccessToken, Day("2026-06-10"))).StatusCode);
        // невалідні запити теж витрачають ліміт (захист від перебору)
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Get(e.Owner, "network/details?date=bad")).StatusCode);
    }

    // =====================================================================================================================
    // відсутність ПД у відповідях і логах; кількість запитів
    // =====================================================================================================================

    [SkippableFact]
    public async Task responses_and_logs_never_contain_client_personal_data()
    {
        NeedDb();
        var e = await NewEnvAsync();
        var promo = await AddPromotionAsync(e, "P");
        await AddAsync(e, Utc(2026, 6, 10, 10), promotion: promo, cost: 10m, percent: 10m, payout: 100m);
        await AddAsync(e, Utc(2026, 6, 10, 11)); // без собівартості

        var logs = new CollectingLogs();
        using var factory = fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b => b.ConfigureLogging(l =>
        {
            l.AddProvider(logs);
            l.SetMinimumLevel(LogLevel.Trace);
        }));
        var client = factory.CreateClient();

        foreach (var url in new[] { Day("2026-06-10"), Span("2026-06-01", "2026-06-30"), Day("2026-06-10") + $"&locationId={e.Location}", Day("bad"),
                     "network?from=2026-06-01&to=2026-06-30&compare=true", SeriesUrl("2026-06-01", "2026-06-30"), "locations?from=2026-06-01&to=2026-06-30" })
        {
            var body = await (await fx.Send(HttpMethod.Get, "/api/beauty/analytics/" + url, bearer: e.Owner, client: client)).Content.ReadAsStringAsync();
            Assert.DoesNotContain(ClientName, body);
            Assert.DoesNotContain("Zoryana", body);
            Assert.DoesNotContain(ClientPhone, body);
            Assert.DoesNotContain("380509998877", body);
            Assert.DoesNotContain(e.ClientId.ToString(), body);
            Assert.DoesNotContain("clientId", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("phone", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("notes", body, StringComparison.OrdinalIgnoreCase);
        }

        var all = logs.Text();
        Assert.DoesNotContain("Zoryana", all);
        Assert.DoesNotContain(ClientPhone, all);
        Assert.DoesNotContain("380509998877", all);
        Assert.DoesNotContain(e.ClientId.ToString(), all);
    }

    [SkippableFact]
    public async Task details_use_a_fixed_small_number_of_queries()
    {
        NeedDb();
        var small = await NewEnvAsync();
        var big = await NewEnvAsync("Europe/Kyiv", "Main");
        for (var i = 0; i < 4; i++)
        {
            var loc = await AddLocationAsync(big, $"L{i}", i % 2 == 0 ? "Pacific/Auckland" : "America/New_York");
            await AddAsync(big, Utc(2026, 6, 10, 10 + i), location: loc, cost: 1m, percent: 1m, payout: 1m);
        }
        await AddAsync(small, Utc(2026, 6, 10, 10), cost: 1m, percent: 1m, payout: 1m);

        var counter = new CollectingLogs();
        using var factory = fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b => b.ConfigureLogging(l =>
        {
            l.AddProvider(counter);
            l.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information);
        }));
        var client = factory.CreateClient();

        async Task<int> Count(Env env, string url)
        {
            counter.ResetSql();
            var r = await fx.Send(HttpMethod.Get, "/api/beauty/analytics/" + url, bearer: env.Owner, client: client);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return counter.SqlCount;
        }

        await Count(small, Day("2026-06-10")); // прогрів
        var one = await Count(small, Day("2026-06-10"));
        var many = await Count(big, Span("2026-06-01", "2026-06-30"));
        var filtered = await Count(big, Day("2026-06-10") + $"&locationId={big.Location}");
        Assert.InRange(one, 1, 4);
        Assert.InRange(many, 1, 4);
        Assert.InRange(filtered, 1, 4);
        Assert.Equal(one, many);
    }

    private sealed class CollectingLogs : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
        private int _sql;
        public int SqlCount => Volatile.Read(ref _sql);
        public void ResetSql() => Interlocked.Exchange(ref _sql, 0);
        public string Text() => string.Join('\n', _lines);
        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
        public void Dispose() { }

        private sealed class Sink(CollectingLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception);
                owner._lines.Enqueue(text);
                // RelationalEventId.CommandExecuted = 20101: лише запити до таблиць beauty_
                if (category == "Microsoft.EntityFrameworkCore.Database.Command" && eventId.Id == 20101 && text.Contains("beauty_", StringComparison.Ordinal))
                    Interlocked.Increment(ref owner._sql);
            }
        }
    }
}
