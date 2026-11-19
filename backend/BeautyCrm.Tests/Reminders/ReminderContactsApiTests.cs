using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Reminders;

/// <summary>
/// TASK-738 (§22.6.2-§22.6.4, §22.10, §22.12 сценарії 14, 15): вподобання клієнта (переплан майбутніх), задачі «зателефонувати»
/// (життєвий цикл, гонка двох закриттів), налаштування, ролі/IDOR, no-store, відсутність ПД у логах. Реальний PostgreSQL + RLS.
/// </summary>
public sealed class ReminderContactsApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string Phone = "+380671234567";
    private const string ClientName = "Оксана Приватна";
    private const string SecretNote = "секретна-нотатка-щодо-клієнтки";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed class CapturingProvider : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    private sealed record Env(HttpClient Client, CapturingProvider Logs);
    private sealed record Tn(TenantSeed Seed, string Owner, string Admin, string Spec, BeautySeed B, Guid OwnerId, Guid AdminId)
    {
        public Guid Id => Seed.TenantId;
    }

    private Env NewEnv(int? maxReplan = null)
    {
        var logs = new CapturingProvider();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            if (maxReplan is { } m) b.UseSetting("Reminders:MaxUpcomingReplan", m.ToString());
            b.ConfigureTestServices(s => s.AddLogging(l => l.SetMinimumLevel(LogLevel.Information).AddProvider(logs)));
        });
        _disposables.Add(f);
        return new Env(f.CreateClient(), logs);
    }

    private async Task<Tn> NewTenantAsync()
    {
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var c = await ctx.Clients.SingleAsync(x => x.Id == seed.ClientId);
            c.FullName = ClientName;
            c.Phone = Phone;
            await ctx.SaveChangesAsync();
        }
        return new Tn(t, owner.AccessToken, admin.AccessToken, spec.AccessToken, seed, t.OwnerId, await UserIdAsync(t.TenantId, admin.AccessToken));
    }

    private static Task<Guid> UserIdAsync(Guid _, string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return Task.FromResult(Guid.Parse(doc.RootElement.GetProperty("sub").GetString()!));
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, string? token, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;
    private static string Prefs(Guid clientId) => $"/api/beauty/clients/{clientId}/contact-preferences";
    private const string Tasks = "/api/beauty/call-tasks";
    private const string Settings = "/api/beauty/settings/reminders";

    private async Task<Guid> AddTaskAsync(Tn t, Guid appointmentId, DateTimeOffset due, string reason = "reminder", Guid? locationId = null, string status = "open", Guid? clientId = null)
    {
        await using var ctx = fx.Db.CreateContext(t.Id);
        var task = new CallTask
        {
            AppointmentId = appointmentId, ClientId = clientId ?? t.B.ClientId, LocationId = locationId ?? t.B.LocationId, Reason = reason, DueAt = due.ToUniversalTime(), Status = status,
            OffsetMinutes = reason == "reminder" ? (short)120 : null,
        };
        ctx.Add(task);
        await ctx.SaveChangesAsync();
        return task.Id;
    }

    // ------------------------------------------------------------------ ролі, no-store, IDOR

    [SkippableFact]
    public async Task endpoints_anonymous_401_specialist_403_forbidden_role_admin_ok_settings_put_owner_only_and_no_store()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        var taskId = await AddTaskAsync(t, t.B.AppointmentA, DateTimeOffset.UtcNow, "delivery_failed"); // PUT вподобань закрив би reminder-задачу
        var calls = new (HttpMethod M, string Url, object? Body)[]
        {
            (HttpMethod.Get, Prefs(t.B.ClientId), null),
            (HttpMethod.Put, Prefs(t.B.ClientId), new { method = "none", offsetsMinutes = Array.Empty<int>() }),
            (HttpMethod.Get, Tasks, null),
            (HttpMethod.Patch, $"{Tasks}/{taskId}", new { status = "done" }),
            (HttpMethod.Get, Settings, null),
            (HttpMethod.Put, Settings, new { telegramEnabled = true, callEnabled = true, quietHoursStart = "22:00", quietHoursEnd = "08:00" }),
        };
        foreach (var (m, url, body) in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await e.Client.SendAsync(Req(m, url, null, body))).StatusCode);
            var spec = await e.Client.SendAsync(Req(m, url, t.Spec, body));
            Assert.Equal(HttpStatusCode.Forbidden, spec.StatusCode);
            Assert.Equal("forbidden_role", await Code(spec));
        }
        // specialist із зламаним тілом теж 403, а не 422/400
        var broken = Req(HttpMethod.Put, Prefs(t.B.ClientId), t.Spec);
        broken.Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Client.SendAsync(broken)).StatusCode);

        foreach (var (m, url, body) in calls.Where(c => !(c.M == HttpMethod.Put && c.Url == Settings)))
        {
            var admin = await e.Client.SendAsync(Req(m, url, t.Admin, body));
            Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
            Assert.Equal("no-store", admin.Headers.CacheControl?.ToString());
        }
        var adminPut = await e.Client.SendAsync(Req(HttpMethod.Put, Settings, t.Admin, calls[5].Body));
        Assert.Equal(HttpStatusCode.Forbidden, adminPut.StatusCode);
        Assert.Equal("forbidden_role", await Code(adminPut));
        var ownerPut = await e.Client.SendAsync(Req(HttpMethod.Put, Settings, t.Owner, calls[5].Body));
        Assert.Equal(HttpStatusCode.OK, ownerPut.StatusCode);
        Assert.Equal("no-store", ownerPut.Headers.CacheControl?.ToString());
    }

    [SkippableFact]
    public async Task disabled_modules_are_403_module_disabled()
    {
        NeedDb();
        var e = NewEnv();
        var t = await fx.CreateTenantAsync(["beauty_booking"]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Client.SendAsync(Req(HttpMethod.Get, Tasks, owner.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Client.SendAsync(Req(HttpMethod.Get, Prefs(Guid.NewGuid()), owner.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Req(HttpMethod.Get, Settings, owner.AccessToken))).StatusCode);
    }

    [SkippableFact]
    public async Task idor_foreign_tenant_ids_are_404_and_nothing_changes()
    {
        NeedDb();
        var e = NewEnv(); var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var foreignTask = await AddTaskAsync(b, b.B.AppointmentA, DateTimeOffset.UtcNow);

        var get = await e.Client.SendAsync(Req(HttpMethod.Get, Prefs(b.B.ClientId), a.Owner));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("client_not_found", await Code(get));
        Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
        var put = await e.Client.SendAsync(Req(HttpMethod.Put, Prefs(b.B.ClientId), a.Owner, new { method = "call", offsetsMinutes = new[] { 60 } }));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        var patch = await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{foreignTask}", a.Owner, new { status = "done" }));
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal("call_task_not_found", await Code(patch));

        await using var ctx = fx.Db.CreateContext(b.Id);
        Assert.Equal("open", (await ctx.CallTasks.SingleAsync(x => x.Id == foreignTask)).Status);
        Assert.Empty(await ctx.ClientContactPreferences.ToListAsync());
        // список задач чужого tenant не показується
        var list = await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"{Tasks}?status=all", a.Owner)));
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
    }

    [SkippableFact]
    public async Task deleted_client_is_404_and_body_limits_are_declared()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            var c = new Client { FullName = "Видалений" };
            ctx.Add(c);
            await ctx.SaveChangesAsync();
            c.DeletedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.NotFound, (await e.Client.SendAsync(Req(HttpMethod.Get, Prefs(c.Id), t.Owner))).StatusCode);
        }
        // TestServer не примушує ліміт тіла (його виконує Kestrel): перевіряємо наявність атрибутів
        Assert.Single(typeof(BeautyCrm.Api.Controllers.ContactPreferencesController).GetMethod("Put")!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false));
        Assert.Single(typeof(BeautyCrm.Api.Controllers.CallTasksController).GetMethod("Patch")!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false));
    }

    // ------------------------------------------------------------------ вподобання (§22.12 тест 14)

    [SkippableFact]
    public async Task preferences_unset_then_call_replans_upcoming_then_none_cancels_the_plan()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        var unset = await J(await e.Client.SendAsync(Req(HttpMethod.Get, Prefs(t.B.ClientId), t.Admin)));
        Assert.Equal("unset", unset.GetProperty("state").GetString());
        Assert.False(unset.TryGetProperty("method", out _));
        Assert.True(unset.GetProperty("phoneAvailable").GetBoolean());
        Assert.False(unset.GetProperty("telegramAvailable").GetBoolean());
        Assert.True(unset.GetProperty("telegramEnabled").GetBoolean());

        var res = await e.Client.SendAsync(Req(HttpMethod.Put, Prefs(t.B.ClientId), t.Admin, new { method = "call", offsetsMinutes = new[] { 120 } }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var set = await J(res);
        Assert.Equal("set", set.GetProperty("state").GetString());
        Assert.Equal("call", set.GetProperty("method").GetString());
        Assert.Equal("admin", set.GetProperty("source").GetString());
        Assert.Equal(2, set.GetProperty("replannedAppointments").GetInt32());
        Assert.Equal(t.AdminId, set.GetProperty("updatedBy").GetProperty("id").GetGuid());
        Assert.Equal(new[] { 120 }, set.GetProperty("offsetsMinutes").EnumerateArray().Select(x => x.GetInt32()));

        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            var pref = await ctx.ClientContactPreferences.SingleAsync();
            Assert.Equal(["call", "admin"], [pref.Method, pref.Source]);
            Assert.Equal(t.AdminId, pref.UpdatedByUserId);
            var appts = await ctx.Appointments.Where(a => a.ClientId == t.B.ClientId).ToListAsync();
            Assert.All(appts, a => { Assert.Equal("call", a.ReminderMethod); Assert.Equal(new short[] { 120 }, a.ReminderOffsets); });
            var tasks = await ctx.CallTasks.Where(x => x.Status == "open").ToListAsync();
            Assert.Equal(2, tasks.Count);
            Assert.All(tasks, x => Assert.Equal("reminder", x.Reason));
        }

        // none: открыті задачі й нагадування закриваються (replanned), вподобання зберігається
        var none = await J(await e.Client.SendAsync(Req(HttpMethod.Put, Prefs(t.B.ClientId), t.Owner, new { method = "none", offsetsMinutes = Array.Empty<int>() })));
        Assert.Equal(2, none.GetProperty("replannedAppointments").GetInt32());
        Assert.Equal(t.OwnerId, none.GetProperty("updatedBy").GetProperty("id").GetGuid());
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            Assert.Empty(await ctx.CallTasks.Where(x => x.Status == "open").ToListAsync());
            var cancelled = await ctx.CallTasks.Where(x => x.Status == "cancelled").ToListAsync();
            Assert.Equal(2, cancelled.Count);
            Assert.All(cancelled, x => Assert.Equal("replanned", x.ClosedReason));
            Assert.All(await ctx.Appointments.ToListAsync(), a => Assert.Equal("none", a.ReminderMethod));
        }
    }

    [SkippableFact]
    public async Task apply_to_upcoming_false_keeps_appointments_and_max_upcoming_replan_limits_the_count()
    {
        NeedDb();
        var e = NewEnv(maxReplan: 1); var t = await NewTenantAsync();
        var keep = await J(await e.Client.SendAsync(Req(HttpMethod.Put, Prefs(t.B.ClientId), t.Owner,
            new { method = "call", offsetsMinutes = new[] { 60 }, applyToUpcoming = false })));
        Assert.Equal(0, keep.GetProperty("replannedAppointments").GetInt32());
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            Assert.All(await ctx.Appointments.ToListAsync(), a => Assert.Equal("none", a.ReminderMethod));
            Assert.Empty(await ctx.CallTasks.ToListAsync());
            Assert.Equal("call", (await ctx.ClientContactPreferences.SingleAsync()).Method);
        }
        var limited = await J(await e.Client.SendAsync(Req(HttpMethod.Put, Prefs(t.B.ClientId), t.Owner, new { method = "call", offsetsMinutes = new[] { 60 } })));
        Assert.Equal(1, limited.GetProperty("replannedAppointments").GetInt32());
        await using var ctx2 = fx.Db.CreateContext(t.Id);
        Assert.Equal(1, await ctx2.Appointments.CountAsync(a => a.ReminderMethod == "call"));
    }

    [SkippableFact]
    public async Task preferences_validation_422_and_conflicts_409()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        async Task<(HttpStatusCode, string)> Put(object body, Guid? client = null)
        {
            var r = await e.Client.SendAsync(Req(HttpMethod.Put, Prefs(client ?? t.B.ClientId), t.Owner, body));
            return (r.StatusCode, await Code(r));
        }
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_offsets"), await Put(new { method = "call", offsetsMinutes = new[] { 60, 120 } }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_offsets"), await Put(new { method = "call", offsetsMinutes = Array.Empty<int>() }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_offsets"), await Put(new { method = "telegram", offsetsMinutes = Array.Empty<int>() }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_offsets"), await Put(new { method = "none", offsetsMinutes = new[] { 60 } }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_offsets"), await Put(new { method = "call", offsetsMinutes = new[] { 90 } }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_method"), await Put(new { method = "sms", offsetsMinutes = new[] { 60 } }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_method"), await Put(new { method = "auto", offsetsMinutes = new[] { 60 } }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_method"), await Put(new { offsetsMinutes = new[] { 60 } }));

        // telegram без розмови в активному Telegram-каналі
        Assert.Equal((HttpStatusCode.Conflict, "telegram_unavailable"), await Put(new { method = "telegram", offsetsMinutes = new[] { 120 } }));
        // call без телефону
        Guid noPhone;
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            var c = new Client { FullName = "Без телефону" };
            ctx.Add(c);
            await ctx.SaveChangesAsync();
            noPhone = c.Id;
        }
        Assert.Equal((HttpStatusCode.Conflict, "phone_required"), await Put(new { method = "call", offsetsMinutes = new[] { 60 } }, noPhone));
        // нічого не збережено
        await using (var ctx = fx.Db.CreateContext(t.Id)) Assert.Empty(await ctx.ClientContactPreferences.ToListAsync());

        // вимкнені канали
        var off = await e.Client.SendAsync(Req(HttpMethod.Put, Settings, t.Owner,
            new { telegramEnabled = false, callEnabled = false, quietHoursStart = "22:00", quietHoursEnd = "08:00" }));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, "call_reminders_disabled"), await Put(new { method = "call", offsetsMinutes = new[] { 60 } }));
        Assert.Equal((HttpStatusCode.Conflict, "telegram_reminders_disabled"), await Put(new { method = "telegram", offsetsMinutes = new[] { 60 } }));
        var state = await J(await e.Client.SendAsync(Req(HttpMethod.Get, Prefs(t.B.ClientId), t.Owner)));
        Assert.False(state.GetProperty("callEnabled").GetBoolean());
        Assert.False(state.GetProperty("telegramEnabled").GetBoolean());
        // none дозволено завжди
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Req(HttpMethod.Put, Prefs(t.B.ClientId), t.Owner, new { method = "none", offsetsMinutes = Array.Empty<int>() }))).StatusCode);
    }

    [SkippableFact]
    public async Task telegram_preference_with_active_telegram_conversation_plans_message_rows_and_remembers_the_chat()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        Guid convId;
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            var ch = new Channel { Type = ChannelType.Telegram, Name = "bot", LocationId = t.B.LocationId, IsActive = true };
            ctx.Add(ch);
            await ctx.SaveChangesAsync();
            var conv = new Conversation { ChannelId = ch.Id, ClientId = t.B.ClientId, ExternalChatId = "5550001111", LastMessageAt = DateTimeOffset.UtcNow };
            ctx.Add(conv);
            await ctx.SaveChangesAsync();
            convId = conv.Id;
        }
        var res = await e.Client.SendAsync(Req(HttpMethod.Put, Prefs(t.B.ClientId), t.Owner, new { method = "telegram", offsetsMinutes = new[] { 1440, 120, 120 } }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await J(res);
        Assert.True(body.GetProperty("telegramAvailable").GetBoolean());
        Assert.Equal(new[] { 120, 1440 }, body.GetProperty("offsetsMinutes").EnumerateArray().Select(x => x.GetInt32()));
        Assert.DoesNotContain("5550001111", body.GetRawText());
        await using var ctx2 = fx.Db.CreateContext(t.Id);
        Assert.Equal(convId, (await ctx2.ClientContactPreferences.SingleAsync()).TelegramConversationId);
        var rows = await ctx2.Reminders.Where(r => r.Status == ReminderStatus.Scheduled && r.AppointmentId == t.B.AppointmentA).ToListAsync();
        Assert.Equal(new short?[] { 120, 1440 }, rows.Select(r => r.OffsetMinutes).OrderBy(x => x).ToArray());
        Assert.All(rows, r => Assert.Equal("telegram", r.Method));
    }

    [SkippableFact]
    public async Task concurrent_puts_for_one_client_do_not_break_the_plan()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        var bodies = new object[]
        {
            new { method = "call", offsetsMinutes = new[] { 60 } }, new { method = "call", offsetsMinutes = new[] { 120 } },
            new { method = "call", offsetsMinutes = new[] { 1440 } }, new { method = "none", offsetsMinutes = Array.Empty<int>() },
        };
        var results = await Task.WhenAll(bodies.Select(b => e.Client.SendAsync(Req(HttpMethod.Put, Prefs(t.B.ClientId), t.Owner, b))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await using var ctx = fx.Db.CreateContext(t.Id);
        var pref = await ctx.ClientContactPreferences.SingleAsync();
        // кожен запис узгоджений з підсумковим вподобанням, відкритих задач не більше однієї на запис
        Assert.All(await ctx.Appointments.ToListAsync(), a => { Assert.Equal(pref.Method, a.ReminderMethod); Assert.Equal(pref.OffsetsMinutes, a.ReminderOffsets); });
        Assert.Equal(pref.Method == "call" ? 2 : 0, await ctx.CallTasks.CountAsync(x => x.Status == "open"));
    }

    // ------------------------------------------------------------------ call-tasks (§22.12 тест 15)

    [SkippableFact]
    public async Task call_tasks_list_filters_by_local_date_in_each_locations_zone_and_counts_ignore_the_status_filter()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        Guid tokyo, tokyoAppt;
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            var loc = new Location { Name = "Tokyo", Timezone = "Asia/Tokyo" };
            ctx.Add(loc);
            await ctx.SaveChangesAsync();
            var appt = new Appointment
            {
                LocationId = loc.Id, SpecialistId = t.B.SpecialistB, ServiceId = t.B.ServiceId, ClientId = t.B.ClientId,
                StartsAt = new DateTimeOffset(2031, 6, 20, 5, 0, 0, TimeSpan.Zero), DurationMinutes = 60, Status = AppointmentStatus.Confirmed,
                Source = AppointmentSource.Admin, PriceOriginal = 1m, PriceFinal = 1m,
            };
            ctx.Add(appt);
            await ctx.SaveChangesAsync();
            tokyo = loc.Id;
            tokyoAppt = appt.Id;
        }
        // 2031-06-10 20:00Z = 23:00 10.06 у Києві, але 05:00 11.06 у Токіо
        var due = new DateTimeOffset(2031, 6, 10, 20, 0, 0, TimeSpan.Zero);
        var kyivTask = await AddTaskAsync(t, t.B.AppointmentA, due);
        var tokyoTask = await AddTaskAsync(t, tokyoAppt, due, "delivery_failed", tokyo);
        var doneTask = await AddTaskAsync(t, t.B.AppointmentB, due.AddHours(-3), "reminder", null, "cancelled");

        async Task<JsonElement> List(string q) => await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"{Tasks}?{q}", t.Owner)));
        static string[] Ids(JsonElement l) => l.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();

        var d10 = await List("day=2031-06-10&status=all");
        Assert.Equal(new[] { kyivTask.ToString(), doneTask.ToString() }.Order(), Ids(d10).Order());
        var d11 = await List("day=2031-06-11&status=all");
        Assert.Equal([tokyoTask.ToString()], Ids(d11));
        var item = d11.GetProperty("items")[0];
        Assert.Equal("05:00", item.GetProperty("localDueTime").GetString());
        Assert.Equal("Asia/Tokyo", item.GetProperty("location").GetProperty("timezone").GetString());
        Assert.Equal("delivery_failed", item.GetProperty("reason").GetString());
        Assert.Equal("2031-06-20T14:00", item.GetProperty("appointment").GetProperty("localStartsAt").GetString());
        Assert.Equal("Haircut", item.GetProperty("appointment").GetProperty("serviceName").GetString());
        Assert.Equal(Phone, item.GetProperty("client").GetProperty("phone").GetString());
        Assert.False(item.TryGetProperty("completedAt", out _));

        // за замовчуванням status=open; cancelled не потрапляє в open, але фільтр cancelled його показує
        Assert.Equal([kyivTask.ToString()], Ids(await List("day=2031-06-10")));
        Assert.Equal([doneTask.ToString()], Ids(await List("day=2031-06-10&status=cancelled")));
        var counts = (await List("day=2031-06-10&status=done")).GetProperty("counts");
        Assert.Equal((1, 0, 0), (counts.GetProperty("open").GetInt32(), counts.GetProperty("done").GetInt32(), counts.GetProperty("skipped").GetInt32()));
        // діапазон і locationId
        Assert.Equal(2, Ids(await List("day=2031-06-10&to=2031-06-11&status=open")).Length);
        Assert.Equal([tokyoTask.ToString()], Ids(await List($"day=2031-06-10&to=2031-06-11&status=open&locationId={tokyo}")));
        Assert.Single((await List("day=2031-06-10&to=2031-06-11&status=open&take=1")).GetProperty("items").EnumerateArray());
        // сортування за due_at
        var all = (await List("day=2031-06-10&to=2031-06-11&status=all")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("dueAt").GetDateTimeOffset()).ToArray();
        Assert.Equal(all.Order(), all);
    }

    [SkippableFact]
    public async Task call_tasks_today_and_tomorrow_use_the_zone_of_the_location_and_phone_follows_the_client()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        var kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var nowKyiv = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, kyiv);
        var tomorrowNoon = new DateTimeOffset(nowKyiv.Date.AddDays(1).AddHours(12), kyiv.GetUtcOffset(nowKyiv.Date.AddDays(1).AddHours(12)));
        var tomorrowTask = await AddTaskAsync(t, t.B.AppointmentA, tomorrowNoon);
        var todayTask = await AddTaskAsync(t, t.B.AppointmentB, new DateTimeOffset(nowKyiv.Date.AddHours(12), kyiv.GetUtcOffset(nowKyiv.Date.AddHours(12))), "delivery_failed");

        async Task<string[]> Ids(string day) =>
            (await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"{Tasks}?day={day}", t.Owner)))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();
        Assert.Equal([todayTask.ToString()], await Ids("today"));
        Assert.Equal([tomorrowTask.ToString()], await Ids("tomorrow"));
        Assert.Equal([todayTask.ToString()], await Ids("")); // за замовчуванням today

        // телефон береться з клієнта: зміна видна в задачі, у задачі його немає
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            (await ctx.Clients.SingleAsync(c => c.Id == t.B.ClientId)).Phone = "+380509998877";
            await ctx.SaveChangesAsync();
        }
        var list = await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"{Tasks}?day=today", t.Admin)));
        Assert.Equal("+380509998877", list.GetProperty("items")[0].GetProperty("client").GetProperty("phone").GetString());
        Assert.DoesNotContain(typeof(CallTask).GetProperties(), p => p.Name.Contains("Phone", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task call_tasks_query_validation_422()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        foreach (var (q, code) in new[]
        {
            ("day=yesterday", "invalid_day"), ("day=2031-13-40", "invalid_day"), ("day=today&to=2031-06-10", "invalid_day"),
            ("day=2031-06-10&to=2031-06-09", "invalid_day"), ("day=2031-01-01&to=2031-03-01", "invalid_day"), ("day=2031-06-10&to=junk", "invalid_day"),
            ("status=weird", "invalid_status"),
        })
        {
            var r = await e.Client.SendAsync(Req(HttpMethod.Get, $"{Tasks}?{q}", t.Owner));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal(code, await Code(r));
        }
        // take обмежується 200 і не падає
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Req(HttpMethod.Get, $"{Tasks}?take=100000", t.Owner))).StatusCode);
    }

    [SkippableFact]
    public async Task call_task_lifecycle_done_with_note_then_second_close_is_409_with_author_open_clears_author()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        var id = await AddTaskAsync(t, t.B.AppointmentA, DateTimeOffset.UtcNow);

        var done = await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{id}", t.Admin, new { status = "done", note = "  " + SecretNote + "  " }));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var dj = await J(done);
        Assert.Equal("done", dj.GetProperty("status").GetString());
        Assert.Equal(t.AdminId, dj.GetProperty("completedBy").GetProperty("id").GetGuid());
        Assert.True(dj.TryGetProperty("completedAt", out _));
        Assert.Equal(SecretNote, dj.GetProperty("note").GetString());

        // другий адміністратор: 409 task_already_closed з хто/коли
        var second = await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{id}", t.Owner, new { status = "skipped" }));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var sj = await J(second);
        Assert.Equal("task_already_closed", sj.GetProperty("code").GetString());
        Assert.Equal("done", sj.GetProperty("task").GetProperty("status").GetString());
        Assert.Equal(t.AdminId, sj.GetProperty("task").GetProperty("completedBy").GetProperty("id").GetGuid());
        Assert.True(sj.GetProperty("task").TryGetProperty("completedAt", out _));
        Assert.Equal("no-store", second.Headers.CacheControl?.ToString());

        // «повернути» очищає автора й час, нотатка лишається
        var reopened = await J(await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{id}", t.Owner, new { status = "open" })));
        Assert.Equal("open", reopened.GetProperty("status").GetString());
        Assert.False(reopened.TryGetProperty("completedBy", out _));
        Assert.False(reopened.TryGetProperty("completedAt", out _));
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            var row = await ctx.CallTasks.SingleAsync(x => x.Id == id);
            Assert.Equal("open", row.Status);
            Assert.Null(row.CompletedByUserId);
            Assert.Null(row.CompletedAt);
        }
        // повторне open ідемпотентне, skipped з новою нотаткою
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{id}", t.Owner, new { status = "open" }))).StatusCode);
        var skipped = await J(await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{id}", t.Owner, new { status = "skipped", note = "не відповіла" })));
        Assert.Equal("skipped", skipped.GetProperty("status").GetString());
        Assert.Equal(t.OwnerId, skipped.GetProperty("completedBy").GetProperty("id").GetGuid());
        Assert.Equal("не відповіла", skipped.GetProperty("note").GetString());
    }

    [SkippableFact]
    public async Task two_concurrent_closes_have_exactly_one_winner_and_the_loser_sees_the_winner()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        for (var round = 0; round < 5; round++)
        {
            var id = await AddTaskAsync(t, round % 2 == 0 ? t.B.AppointmentA : t.B.AppointmentB, DateTimeOffset.UtcNow, "delivery_failed");
            var tokens = new[] { t.Owner, t.Admin };
            var responses = await Task.WhenAll(tokens.Select(tok => e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{id}", tok, new { status = "done" }))));
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            var lj = await J(loser);
            Assert.Equal("task_already_closed", lj.GetProperty("code").GetString());
            var winnerId = Guid.Parse(lj.GetProperty("task").GetProperty("completedBy").GetProperty("id").GetString()!);
            await using var ctx = fx.Db.CreateContext(t.Id);
            Assert.Equal(winnerId, (await ctx.CallTasks.SingleAsync(x => x.Id == id)).CompletedByUserId);
        }
    }

    [SkippableFact]
    public async Task call_task_validation_cancelled_and_reopen_conflict()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        var id = await AddTaskAsync(t, t.B.AppointmentA, DateTimeOffset.UtcNow);
        async Task<(HttpStatusCode, string)> Patch(Guid task, object body)
        {
            var r = await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{task}", t.Owner, body));
            return (r.StatusCode, await Code(r));
        }
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_status"), await Patch(id, new { status = "cancelled" }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_status"), await Patch(id, new { note = "x" }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_note"), await Patch(id, new { status = "done", note = new string('я', 301) }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_note"), await Patch(id, new { status = "done", note = "a\u0007b" }));
        Assert.Equal((HttpStatusCode.NotFound, "call_task_not_found"), await Patch(Guid.NewGuid(), new { status = "done" }));
        // межа 300 проходить
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{id}", t.Owner, new { status = "done", note = new string('я', 300) }))).StatusCode);

        // cancelled - системний фінальний стан
        var cancelled = await AddTaskAsync(t, t.B.AppointmentB, DateTimeOffset.UtcNow, "delivery_failed", null, "cancelled");
        var r409 = await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{cancelled}", t.Owner, new { status = "open" }));
        Assert.Equal(HttpStatusCode.Conflict, r409.StatusCode);
        var j = await J(r409);
        Assert.Equal("task_cancelled", j.GetProperty("code").GetString());
        Assert.Equal("cancelled", j.GetProperty("task").GetProperty("status").GetString());
        Assert.Equal((HttpStatusCode.Conflict, "task_cancelled"), await Patch(cancelled, new { status = "done" }));

        // повернути закриту задачу, коли для запису вже є інша відкрита reminder-задача -> 409 task_superseded
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            var old = await AddTaskAsync(t, t.B.AppointmentB, DateTimeOffset.UtcNow, "reminder", null, "open");
            Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{old}", t.Owner, new { status = "done" }))).StatusCode);
            await AddTaskAsync(t, t.B.AppointmentB, DateTimeOffset.UtcNow, "reminder", null, "open");
            Assert.Equal((HttpStatusCode.Conflict, "task_superseded"), await Patch(old, new { status = "open" }));
            Assert.Equal("done", (await ctx.CallTasks.SingleAsync(x => x.Id == old)).Status);
        }
    }

    // ------------------------------------------------------------------ settings/reminders (§22.6.4)

    [SkippableFact]
    public async Task settings_defaults_put_validation_and_effect_on_new_plans()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        var def = await J(await e.Client.SendAsync(Req(HttpMethod.Get, Settings, t.Admin)));
        Assert.True(def.GetProperty("telegramEnabled").GetBoolean());
        Assert.True(def.GetProperty("callEnabled").GetBoolean());
        Assert.Equal("22:00", def.GetProperty("quietHoursStart").GetString());
        Assert.Equal("08:00", def.GetProperty("quietHoursEnd").GetString());

        async Task<(HttpStatusCode, string)> Put(object body)
        {
            var r = await e.Client.SendAsync(Req(HttpMethod.Put, Settings, t.Owner, body));
            return (r.StatusCode, r.IsSuccessStatusCode ? "ok" : await Code(r));
        }
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "settings_incomplete"), await Put(new { telegramEnabled = true, callEnabled = true, quietHoursStart = "22:00" }));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "settings_incomplete"), await Put(new { callEnabled = true, quietHoursStart = "22:00", quietHoursEnd = "08:00" }));
        foreach (var (s, en) in new[] { ("22:00", "22:00"), ("8:00", "22:00"), ("25:00", "08:00"), ("22:00", "junk"), ("08:00", "21:00"), ("22:00", "10:01") })
            Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_quiet_hours"), await Put(new { telegramEnabled = true, callEnabled = true, quietHoursStart = s, quietHoursEnd = en }));
        await using (var ctx = fx.Db.CreateContext(t.Id)) Assert.Empty(await ctx.ReminderSettings.ToListAsync());

        // межі: рівно 12 год і перехід через північ допустимі
        Assert.Equal((HttpStatusCode.OK, "ok"), await Put(new { telegramEnabled = true, callEnabled = false, quietHoursStart = "20:00", quietHoursEnd = "08:00" }));
        Assert.Equal((HttpStatusCode.OK, "ok"), await Put(new { telegramEnabled = false, callEnabled = true, quietHoursStart = "00:00", quietHoursEnd = "06:30" }));
        var saved = await J(await e.Client.SendAsync(Req(HttpMethod.Get, Settings, t.Owner)));
        Assert.False(saved.GetProperty("telegramEnabled").GetBoolean());
        Assert.Equal("00:00", saved.GetProperty("quietHoursStart").GetString());
        Assert.Equal("06:30", saved.GetProperty("quietHoursEnd").GetString());
        await using var ctx2 = fx.Db.CreateContext(t.Id);
        var row = await ctx2.ReminderSettings.SingleAsync();
        Assert.Equal(t.OwnerId, row.UpdatedByUserId);
        Assert.Equal(new TimeOnly(0, 0), row.QuietStart);
    }

    [SkippableFact]
    public async Task settings_are_isolated_per_tenant()
    {
        NeedDb();
        var e = NewEnv(); var a = await NewTenantAsync(); var b = await NewTenantAsync();
        Assert.Equal(HttpStatusCode.OK, (await e.Client.SendAsync(Req(HttpMethod.Put, Settings, a.Owner,
            new { telegramEnabled = false, callEnabled = false, quietHoursStart = "21:00", quietHoursEnd = "07:00" }))).StatusCode);
        var other = await J(await e.Client.SendAsync(Req(HttpMethod.Get, Settings, b.Owner)));
        Assert.True(other.GetProperty("telegramEnabled").GetBoolean());
        Assert.Equal("22:00", other.GetProperty("quietHoursStart").GetString());
    }

    // ------------------------------------------------------------------ ПД у логах

    [SkippableFact]
    public async Task logs_contain_no_phone_note_names_or_chat_ids()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        var id = await AddTaskAsync(t, t.B.AppointmentA, DateTimeOffset.UtcNow, "delivery_failed");
        await e.Client.SendAsync(Req(HttpMethod.Put, Prefs(t.B.ClientId), t.Owner, new { method = "call", offsetsMinutes = new[] { 120 } }));
        await e.Client.SendAsync(Req(HttpMethod.Put, Prefs(t.B.ClientId), t.Owner, new { method = "telegram", offsetsMinutes = new[] { 120 } })); // 409
        await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{id}", t.Admin, new { status = "done", note = SecretNote }));
        await e.Client.SendAsync(Req(HttpMethod.Patch, $"{Tasks}/{id}", t.Owner, new { status = "skipped", note = SecretNote }));
        await e.Client.SendAsync(Req(HttpMethod.Get, $"{Tasks}?status=all", t.Owner));
        await e.Client.SendAsync(Req(HttpMethod.Put, Settings, t.Owner, new { telegramEnabled = true, callEnabled = true, quietHoursStart = "22:00", quietHoursEnd = "08:00" }));

        var lines = e.Logs.Lines.ToArray();
        Assert.Contains(lines, l => l.Contains("contact_prefs_updated client=") && l.Contains("method=call") && l.Contains("replanned=2"));
        Assert.Contains(lines, l => l.Contains("call_task_updated task=") && l.Contains("status=done"));
        foreach (var secret in new[] { Phone.TrimStart('+'), SecretNote, "Оксана", "Приватна" })
            Assert.DoesNotContain(lines, l => l.Contains(secret));
    }
}
