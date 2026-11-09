using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.PublicBooking;

/// <summary>
/// Публічний запис: нагадування 24h, необов'язковий email клієнта (валідація, збереження без затирання) і лист-підтвердження
/// в outbox (beauty_messages, channel email). Реальний PostgreSQL + повний HTTP-конвеєр; без БД пропускається.
/// </summary>
public sealed class PublicBookingEmailReminderTests : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string Phone = "+380501234567";
    private readonly AuthApiFixture _fx;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly HttpClient _client = null!;

    public PublicBookingEmailReminderTests(AuthApiFixture fx)
    {
        _fx = fx;
        if (fx.SkipReason is null)
        {
            var f = _fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b =>
            {
                b.UseSetting("PublicBooking:RateLimit:ReadPermit", "10000");
                b.UseSetting("PublicBooking:RateLimit:TokenPermit", "10000");
                b.UseSetting("PublicBooking:RateLimit:WritePermit", "10000");
                b.UseSetting("PublicBooking:MaxActiveBookingsPerPhone", "100");
                b.UseSetting("PublicBooking:MaxCreatesPerPhonePerHour", "100");
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

    private sealed record Seed(TenantSeed Tenant, Guid LocationId, Guid SpecialistId, Guid ServiceId)
    {
        public string Slug => Tenant.Slug;
    }

    private const string Hours =
        """{"mon":[{"from":"08:00","to":"20:00"}],"tue":[{"from":"08:00","to":"20:00"}],"wed":[{"from":"08:00","to":"20:00"}],"thu":[{"from":"08:00","to":"20:00"}],"fri":[{"from":"08:00","to":"20:00"}],"sat":[{"from":"08:00","to":"20:00"}],"sun":[{"from":"08:00","to":"20:00"}]}""";

    private async Task<Seed> SeedAsync()
    {
        var t = await _fx.CreateTenantAsync(null);
        await using var ctx = _fx.Db.CreateContext(t.TenantId);
        var loc = new Location { Name = "Main", Address = "Street 1", Phone = "+380440000000", Timezone = "Europe/Kyiv" };
        var sp = new Specialist { FullName = "Master A", Title = "Stylist" };
        var svc = new Service { Name = "Haircut", Description = "Cut", Category = "Hair", DurationMinutes = 60 };
        ctx.AddRange(loc, sp, svc);
        await ctx.SaveChangesAsync();
        ctx.Add(new SpecialistLocation { SpecialistId = sp.Id, LocationId = loc.Id, WorkingHours = Hours });
        ctx.Add(new SpecialistServiceLink { SpecialistId = sp.Id, ServiceId = svc.Id });
        ctx.Add(new ServicePrice { ServiceId = svc.Id, LocationId = null, Price = 500m });
        await ctx.SaveChangesAsync();
        return new Seed(t, loc.Id, sp.Id, svc.Id);
    }

    private async Task<List<DateTimeOffset>> SlotStartsAsync(Seed s, int daysAhead = 3)
    {
        var date = DateTime.UtcNow.AddDays(daysAhead).ToString("yyyy-MM-dd");
        var r = await _client.GetAsync($"/api/public/{s.Slug}/slots?locationId={s.LocationId}&serviceId={s.ServiceId}&date={date}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Read<JsonElement>(r)).EnumerateArray().Select(e => e.GetProperty("startsAt").GetDateTimeOffset()).ToList();
    }

    private Task<HttpResponseMessage> PostAsync(Seed s, DateTimeOffset startsAt, string? email = null, string reminder = "none",
        string phone = Phone, string? key = null, bool omitEmail = false)
    {
        object client = omitEmail ? new { name = "Olena Test", phone } : new { name = "Olena Test", phone, email };
        var body = new
        {
            locationId = s.LocationId, specialistId = s.SpecialistId, serviceId = s.ServiceId, startsAt,
            client, reminder, paymentMethod = "cash",
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{s.Slug}/appointments") { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return _client.SendAsync(req);
    }

    // ---------- 24h ----------

    [SkippableTheory]
    [InlineData("none")]
    [InlineData("1h")]
    [InlineData("2h")]
    [InlineData("24h")]
    public async Task all_reminder_options_are_accepted_and_stored(string option)
    {
        NeedDb();
        var s = await SeedAsync();
        var start = (await SlotStartsAsync(s))[2];
        var r = await PostAsync(s, start, reminder: option);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(option, (await Read<JsonElement>(r)).GetProperty("appointment").GetProperty("reminderOption").GetString());

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var appt = await ctx.Appointments.AsNoTracking().SingleAsync();
        var expected = option switch { "1h" => 1, "2h" => 2, "24h" => 24, _ => 0 };
        if (expected == 0) Assert.Empty(await ctx.Reminders.AsNoTracking().ToListAsync());
        else
        {
            var rem = Assert.Single(await ctx.Reminders.AsNoTracking().Where(x => x.AppointmentId == appt.Id).ToListAsync());
            Assert.Equal(start.AddHours(-expected), rem.ScheduledAt);
            Assert.Equal(ReminderStatus.Scheduled, rem.Status);
        }
    }

    [SkippableFact]
    public async Task unknown_reminder_is_rejected_with_updated_message()
    {
        NeedDb();
        var s = await SeedAsync();
        var r = await PostAsync(s, (await SlotStartsAsync(s))[0], reminder: "48h");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var j = await Read<JsonElement>(r);
        Assert.Equal("invalid_reminder", j.GetProperty("code").GetString());
        Assert.Contains("24h", await r.Content.ReadAsStringAsync());
    }

    // ---------- email ----------

    [SkippableTheory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("a b@c.com")]
    [InlineData("a@@c.com")]
    [InlineData("@c.com")]
    [InlineData("user@.com")]
    [InlineData("user@c..com")]
    public async Task invalid_email_is_rejected_with_422(string email)
    {
        NeedDb();
        var s = await SeedAsync();
        var r = await PostAsync(s, (await SlotStartsAsync(s))[0], email);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("invalid_email", (await Read<JsonElement>(r)).GetProperty("code").GetString());
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.Empty(await ctx.Appointments.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task email_longer_than_254_is_rejected()
    {
        NeedDb();
        var s = await SeedAsync();
        var email = new string('a', 250) + "@x.com"; // 256
        var r = await PostAsync(s, (await SlotStartsAsync(s))[0], email);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("invalid_email", (await Read<JsonElement>(r)).GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task email_is_trimmed_lowercased_stored_and_not_echoed_back()
    {
        NeedDb();
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s);
        var r = await PostAsync(s, starts[0], "  Olena.Test@Example.COM ");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var raw = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("example.com", raw, StringComparison.OrdinalIgnoreCase);

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.Equal("olena.test@example.com", (await ctx.Clients.AsNoTracking().SingleAsync()).Email);
    }

    [SkippableFact]
    public async Task existing_client_email_is_filled_when_empty_never_wiped_and_not_overwritten()
    {
        NeedDb();
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s);

        // 1) без email -> клієнт без email; порожній email нічого не змінює
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, starts[0], omitEmail: true)).StatusCode);
        await using (var ctx = _fx.Db.CreateContext(s.Tenant.TenantId))
            Assert.Null((await ctx.Clients.AsNoTracking().SingleAsync()).Email);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, starts[4], "   ")).StatusCode);
        await using (var ctx = _fx.Db.CreateContext(s.Tenant.TenantId))
            Assert.Null((await ctx.Clients.AsNoTracking().SingleAsync()).Email);

        // 2) наявному клієнту без email адреса доповнюється
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, starts[8], "first@example.com")).StatusCode);
        await using (var ctx = _fx.Db.CreateContext(s.Tenant.TenantId))
            Assert.Equal("first@example.com", (await ctx.Clients.AsNoTracking().SingleAsync()).Email);

        // 3) порожній email не затирає; інший email не перезаписує наявний (анонімний виклик не підміняє адресу)
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, starts[12], omitEmail: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, starts[16], "other@example.com")).StatusCode);
        await using (var ctx = _fx.Db.CreateContext(s.Tenant.TenantId))
        {
            var c = await ctx.Clients.AsNoTracking().SingleAsync();
            Assert.Equal("first@example.com", c.Email);
            Assert.Equal(5, await ctx.Appointments.CountAsync(a => a.ClientId == c.Id));
        }
    }

    // ---------- лист-підтвердження (outbox) ----------

    [SkippableFact]
    public async Task confirmation_email_is_queued_once_without_cancel_token()
    {
        NeedDb();
        var s = await SeedAsync();
        var start = (await SlotStartsAsync(s))[2];
        var key = Guid.NewGuid().ToString("N");
        var r = await PostAsync(s, start, "Olena@Example.com", key: key);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var token = (await Read<JsonElement>(r)).GetProperty("publicToken").GetString()!;

        // повтор того самого Idempotency-Key (replay 200) не створює другого листа
        var replay = await PostAsync(s, start, "Olena@Example.com", key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var appt = await ctx.Appointments.AsNoTracking().SingleAsync();
        var msg = await ctx.Messages.AsNoTracking().SingleAsync();
        Assert.Equal($"confirmation:{appt.Id}", msg.IdempotencyKey);
        Assert.Equal("pending", msg.Status);
        Assert.Equal(MessageDirection.Outbound, msg.Direction);
        Assert.Equal(MessageSenderType.System, msg.SenderType);
        Assert.Contains("Main", msg.Body);
        Assert.Contains("Haircut", msg.Body);
        Assert.Contains("Master A", msg.Body);
        Assert.Contains("Street 1", msg.Body);
        Assert.Contains("Europe/Kyiv", msg.Body);
        Assert.DoesNotContain(token, msg.Body);
        Assert.DoesNotContain("cancel", msg.Body, StringComparison.OrdinalIgnoreCase);

        var conv = await ctx.Conversations.AsNoTracking().Include(c => c.Channel).SingleAsync(c => c.Id == msg.ConversationId);
        Assert.Equal("olena@example.com", conv.ExternalChatId);
        Assert.Equal(ChannelType.Email, conv.Channel!.Type);
        Assert.Equal(appt.ClientId, conv.ClientId);
        Assert.Null(conv.LastMessageAt); // месенджер-розмова клієнта (з last_message_at) лишається пріоритетною для worker
        Assert.True(conv.Channel.IsActive);
    }

    [SkippableFact]
    public async Task second_booking_reuses_email_channel_and_conversation_and_no_email_means_no_message()
    {
        NeedDb();
        var s = await SeedAsync();
        var starts = await SlotStartsAsync(s);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, starts[0], "a@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, starts[4], null, omitEmail: true)).StatusCode); // email уже в клієнта -> лист є
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, starts[8], "a@example.com", phone: "+380671112233")).StatusCode);

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.Equal(1, await ctx.Channels.CountAsync(c => c.Type == ChannelType.Email));
        Assert.Equal(1, await ctx.Conversations.CountAsync());
        Assert.Equal(3, await ctx.Messages.CountAsync());
    }

    [SkippableFact]
    public async Task booking_without_any_email_queues_nothing()
    {
        NeedDb();
        var s = await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, (await SlotStartsAsync(s))[0], omitEmail: true)).StatusCode);
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.Empty(await ctx.Messages.AsNoTracking().ToListAsync());
        Assert.Empty(await ctx.Channels.AsNoTracking().ToListAsync());
    }
}
