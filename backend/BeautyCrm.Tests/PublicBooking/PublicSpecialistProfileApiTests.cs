using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.PublicBooking;

/// <summary>
/// Мобільний потік: профіль майстра, відгуки й календар доступності в публічному API на реальному PostgreSQL
/// (роль без BYPASSRLS, FORCE RLS). Без БД пропускаються; BEAUTY_TEST_REQUIRE_DB=1 -> помилка.
/// </summary>
public sealed class PublicSpecialistProfileApiTests : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string AllDays =
        """{"mon":[{"from":"08:00","to":"20:00"}],"tue":[{"from":"08:00","to":"20:00"}],"wed":[{"from":"08:00","to":"20:00"}],"thu":[{"from":"08:00","to":"20:00"}],"fri":[{"from":"08:00","to":"20:00"}],"sat":[{"from":"08:00","to":"20:00"}],"sun":[{"from":"08:00","to":"20:00"}]}""";

    private readonly AuthApiFixture _fx;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly HttpClient _client = null!;

    public PublicSpecialistProfileApiTests(AuthApiFixture fx)
    {
        _fx = fx;
        if (fx.SkipReason is null)
        {
            var f = fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b =>
            {
                b.UseSetting("PublicBooking:RateLimit:ReadPermit", "10000");
                b.UseSetting("PublicBooking:RateLimit:TokenPermit", "10000");
                b.UseSetting("PublicBooking:RateLimit:WritePermit", "10000");
            });
            _factories.Add(f);
            _client = f.CreateClient();
        }
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    private sealed record Seed(TenantSeed Tenant, Guid LocationId, Guid SpecialistId, Guid ServiceId, Guid ClientId)
    {
        public string Slug => Tenant.Slug;
    }

    private static int Year => DateTime.UtcNow.Year;

    private async Task<Seed> SeedAsync(Action<Specialist>? configure = null, string[]? closedWeekdays = null)
    {
        var t = await _fx.CreateTenantAsync();
        await using var ctx = _fx.Db.CreateContext(t.TenantId);
        var loc = new Location { Name = "Main", Address = "Street 1", Phone = "+380440000000", Timezone = "Europe/Kyiv", ClosedWeekdays = closedWeekdays ?? [] };
        var sp = new Specialist { FullName = "Master A", Title = "Stylist", Email = "private@x.test", Phone = "+380991112233" };
        configure?.Invoke(sp);
        var svc = new Service { Name = "Haircut", Description = "Cut", Category = "Hair", DurationMinutes = 60 };
        var client = new Client { FullName = "Olena Kovalenko", Phone = "+380501234567", Email = "olena@x.test" };
        ctx.AddRange(loc, sp, svc, client);
        await ctx.SaveChangesAsync();
        ctx.Add(new SpecialistLocation { SpecialistId = sp.Id, LocationId = loc.Id, WorkingHours = AllDays });
        ctx.Add(new SpecialistServiceLink { SpecialistId = sp.Id, ServiceId = svc.Id });
        ctx.Add(new ServicePrice { ServiceId = svc.Id, LocationId = null, Price = 500m });
        await ctx.SaveChangesAsync();
        return new Seed(t, loc.Id, sp.Id, svc.Id, client.Id);
    }

    private async Task AddReviewAsync(Seed s, int rating, string? text = null, bool published = true, string author = "Olena K.",
        DateTimeOffset? createdAt = null, bool withClient = true)
    {
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        ctx.Add(new SpecialistReview
        {
            SpecialistId = s.SpecialistId, ClientId = withClient ? s.ClientId : null, AuthorName = author, Rating = (short)rating,
            Text = text, IsPublished = published, CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<JsonElement> GetJson(string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var r = await _client.GetAsync(url);
        Assert.Equal(expected, r.StatusCode);
        return await Read<JsonElement>(r);
    }

    // ---------- спеціалісти: профіль, рейтинг ----------

    [SkippableFact]
    public async Task specialists_expose_profile_and_rating_and_count_only_published_reviews()
    {
        NeedDb();
        var s = await SeedAsync(sp =>
        {
            sp.PhotoUrl = "https://cdn.example.com/a.jpg";
            sp.PracticeStartYear = Year - 5;
            sp.Bio = "Colorist";
        });
        await AddReviewAsync(s, 5);
        await AddReviewAsync(s, 4);
        await AddReviewAsync(s, 4);
        await AddReviewAsync(s, 1, published: false); // не входить ні в середнє, ні в кількість

        var list = await GetJson($"/api/public/{s.Slug}/locations/{s.LocationId}/specialists?serviceId={s.ServiceId}");
        var m = Assert.Single(list.EnumerateArray().ToList());
        Assert.Equal(s.SpecialistId, m.GetProperty("id").GetGuid());
        Assert.Equal("Master A", m.GetProperty("name").GetString());
        Assert.Equal("Stylist", m.GetProperty("title").GetString());
        Assert.Equal("https://cdn.example.com/a.jpg", m.GetProperty("photoUrl").GetString());
        Assert.Equal(5, m.GetProperty("experienceYears").GetInt32());
        Assert.Equal("Colorist", m.GetProperty("bio").GetString());
        Assert.Equal(4.3, m.GetProperty("rating").GetDouble(), 5);
        Assert.Equal(3, m.GetProperty("reviewsCount").GetInt32());

        var raw = list.GetRawText();
        Assert.DoesNotContain("380991112233", raw);
        Assert.DoesNotContain("private@x.test", raw);
    }

    [SkippableFact]
    public async Task specialist_without_profile_or_reviews_has_nulls_and_zero_count()
    {
        NeedDb();
        var s = await SeedAsync();
        await AddReviewAsync(s, 3, published: false); // лише неопубліковані = немає рейтингу

        var m = (await GetJson($"/api/public/{s.Slug}/locations/{s.LocationId}/specialists")).EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, m.GetProperty("rating").ValueKind);
        Assert.Equal(0, m.GetProperty("reviewsCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("experienceYears").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("bio").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("photoUrl").ValueKind);
    }

    [SkippableFact]
    public async Task experience_is_never_negative_and_zero_in_the_start_year()
    {
        NeedDb();
        var s = await SeedAsync(sp => sp.PracticeStartYear = Year);
        var m = (await GetJson($"/api/public/{s.Slug}/locations/{s.LocationId}/specialists")).EnumerateArray().Single();
        Assert.Equal(0, m.GetProperty("experienceYears").GetInt32());
    }

    // ---------- відгуки ----------

    [SkippableFact]
    public async Task reviews_are_published_only_newest_first_paged_and_free_of_pii()
    {
        NeedDb();
        var s = await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        await AddReviewAsync(s, 5, "Old", createdAt: now.AddDays(-3), author: "Anna S.");
        await AddReviewAsync(s, 4, "Newest", createdAt: now.AddDays(-1));
        await AddReviewAsync(s, 2, "Hidden", published: false, createdAt: now);
        await AddReviewAsync(s, 3, null, createdAt: now.AddDays(-2), withClient: false);

        var page = await GetJson($"/api/public/{s.Slug}/specialists/{s.SpecialistId}/reviews");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Newest", null, "Old"], items.Select(i => i.GetProperty("text").GetString()));
        Assert.Equal("Olena K.", items[0].GetProperty("authorName").GetString());
        Assert.Equal(4, items[0].GetProperty("rating").GetInt32());
        Assert.Equal(["authorName", "createdAt", "id", "rating", "text"],
            items[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        var raw = page.GetRawText();
        foreach (var forbidden in new[] { "380501234567", "olena@x.test", "Kovalenko", "clientId", "appointmentId", "isPublished", "Hidden" })
            Assert.DoesNotContain(forbidden, raw);

        var second = await GetJson($"/api/public/{s.Slug}/specialists/{s.SpecialistId}/reviews?limit=1&offset=1");
        Assert.Equal(3, second.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("items")[0].GetProperty("text").ValueKind); // другий за новизною
        Assert.Single(second.GetProperty("items").EnumerateArray().ToList());

        var beyond = await GetJson($"/api/public/{s.Slug}/specialists/{s.SpecialistId}/reviews?offset=10");
        Assert.Equal(3, beyond.GetProperty("total").GetInt32());
        Assert.Empty(beyond.GetProperty("items").EnumerateArray().ToList());
    }

    [SkippableFact]
    public async Task reviews_limit_and_offset_are_validated()
    {
        NeedDb();
        var s = await SeedAsync();
        var url = $"/api/public/{s.Slug}/specialists/{s.SpecialistId}/reviews";
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(url + "?limit=50")).StatusCode);
        foreach (var bad in new[] { "?limit=51", "?limit=0", "?limit=-1", "?offset=-1" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _client.GetAsync(url + bad)).StatusCode);
    }

    [SkippableFact]
    public async Task reviews_of_another_tenant_or_unknown_specialist_are_404_and_never_leak()
    {
        NeedDb();
        var a = await SeedAsync();
        var b = await SeedAsync();
        await AddReviewAsync(b, 5, "Secret of B");

        // чужий майстер під slug tenant-а A і неіснуючий — однакова 404
        var cross = await _client.GetAsync($"/api/public/{a.Slug}/specialists/{b.SpecialistId}/reviews");
        var unknown = await _client.GetAsync($"/api/public/{a.Slug}/specialists/{Guid.NewGuid()}/reviews");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain("Secret", await cross.Content.ReadAsStringAsync());

        // відгуки A не містять відгуків B, а рейтинг B не потрапляє у список A
        var own = await GetJson($"/api/public/{a.Slug}/specialists/{a.SpecialistId}/reviews");
        Assert.Equal(0, own.GetProperty("total").GetInt32());
        var aList = (await GetJson($"/api/public/{a.Slug}/locations/{a.LocationId}/specialists")).EnumerateArray().Single();
        Assert.Equal(0, aList.GetProperty("reviewsCount").GetInt32());
    }

    [SkippableFact]
    public async Task inactive_specialist_reviews_are_not_public()
    {
        NeedDb();
        var s = await SeedAsync(sp => sp.IsActive = false);
        await AddReviewAsync(s, 5, "x");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/public/{s.Slug}/specialists/{s.SpecialistId}/reviews")).StatusCode);
    }

    // ---------- обмеження БД ----------

    [SkippableFact]
    public async Task database_rejects_rating_out_of_range_and_cross_tenant_rows()
    {
        NeedDb();
        var a = await SeedAsync();
        var b = await SeedAsync();
        foreach (var bad in new[] { 0, 6 })
            await Assert.ThrowsAsync<DbUpdateException>(() => AddReviewAsync(a, bad));

        // RLS WITH CHECK / складений FK: відгук tenant-а A на майстра tenant-а B неможливий
        await using var ctx = _fx.Db.CreateContext(a.Tenant.TenantId);
        ctx.Add(new SpecialistReview { SpecialistId = b.SpecialistId, AuthorName = "X Y.", Rating = 5 });
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    // ---------- календар доступності ----------

    private static DateOnly KyivToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")).DateTime);

    [SkippableFact]
    public async Task availability_returns_statuses_closed_past_and_available_with_exact_shape()
    {
        NeedDb();
        var today = KyivToday();
        var closedDay = today.AddDays(2);
        var s = await SeedAsync(closedWeekdays: [SlotCalculator.DayKey(closedDay.DayOfWeek)]);

        var from = today.AddDays(-1);
        var body = await GetJson($"/api/public/{s.Slug}/availability?locationId={s.LocationId}&serviceId={s.ServiceId}&from={from:yyyy-MM-dd}&days=4");
        Assert.Equal(["days"], body.EnumerateObject().Select(p => p.Name));
        var days = body.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(4, days.Count);
        Assert.Equal(["date", "freeSlots", "status"], days[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(from.ToString("yyyy-MM-dd"), days[0].GetProperty("date").GetString());
        Assert.Equal("past", days[0].GetProperty("status").GetString());
        Assert.Equal(0, days[0].GetProperty("freeSlots").GetInt32());
        // сьогодні: available або full (залежить від часу доби), але не closed/past
        Assert.Contains(days[1].GetProperty("status").GetString(), new[] { "available", "full" });
        Assert.Equal("available", days[2].GetProperty("status").GetString()); // завтра: графік 08:00-20:00, записів немає
        Assert.True(days[2].GetProperty("freeSlots").GetInt32() > 0);
        Assert.Equal(closedDay.ToString("yyyy-MM-dd"), days[3].GetProperty("date").GetString());
        Assert.Equal("closed", days[3].GetProperty("status").GetString()); // щотижневий вихідний закладу
        Assert.Equal(0, days[3].GetProperty("freeSlots").GetInt32());
    }

    [SkippableFact]
    public async Task availability_defaults_to_31_days_and_validates_parameters()
    {
        NeedDb();
        var s = await SeedAsync();
        var q = $"locationId={s.LocationId}&serviceId={s.ServiceId}";
        var def = await GetJson($"/api/public/{s.Slug}/availability?{q}");
        var days = def.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(31, days.Count);
        Assert.Equal(KyivToday().ToString("yyyy-MM-dd"), days[0].GetProperty("date").GetString());

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/public/{s.Slug}/availability?{q}&days=62")).StatusCode);
        foreach (var bad in new[] { "&days=0", "&days=63", "&from=2001-01-01" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _client.GetAsync($"/api/public/{s.Slug}/availability?{q}{bad}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/public/{s.Slug}/availability?locationId={s.LocationId}&serviceId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/public/{s.Slug}/availability?locationId={Guid.NewGuid()}&serviceId={s.ServiceId}")).StatusCode);
    }

    [SkippableFact]
    public async Task availability_with_and_without_specialist_agree_and_foreign_specialist_yields_closed_days()
    {
        NeedDb();
        var s = await SeedAsync();
        var q = $"/api/public/{s.Slug}/availability?locationId={s.LocationId}&serviceId={s.ServiceId}&from={KyivToday().AddDays(1):yyyy-MM-dd}&days=5";
        var any = (await GetJson(q)).GetProperty("days").GetRawText();
        var one = (await GetJson($"{q}&specialistId={s.SpecialistId}")).GetProperty("days").GetRawText();
        Assert.Equal(any, one);

        var other = (await GetJson($"{q}&specialistId={Guid.NewGuid()}")).GetProperty("days").EnumerateArray().ToList();
        Assert.All(other, d => Assert.Equal("closed", d.GetProperty("status").GetString()));
    }

    [SkippableFact]
    public async Task availability_for_31_days_is_one_pass_and_fast_enough()
    {
        NeedDb();
        var s = await SeedAsync();
        var url = $"/api/public/{s.Slug}/availability?locationId={s.LocationId}&serviceId={s.ServiceId}&days=31";
        await _client.GetAsync(url); // прогрів
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await _client.GetAsync(url);
        sw.Stop();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"availability(31 days) took {sw.ElapsedMilliseconds} ms");
    }
}
