using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// Інтеграційні тести TASK-684 на реальному PostgreSQL (RLS, роль без BYPASSRLS) через повний HTTP-конвеєр.
/// Без БД пропускаються; у CI BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку.
/// </summary>
public sealed class AuthApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private static async Task<string> Code(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonElementAsync()).GetProperty("code").GetString()!;

    // ---------- оператор платформи ----------

    [SkippableFact]
    public async Task createTenant_requires_platform_key_and_creates_tenant_with_owner()
    {
        NeedDb();
        var body = new { name = "S", slug = Unique("salon"), owner = new { email = "o@x.test", fullName = "O", password = Password } };

        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Post, "/api/platform/tenants", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fx.Send(HttpMethod.Post, "/api/platform/tenants", body, platformKey: PlatformKey + "x")).StatusCode);

        var ok = await fx.Send(HttpMethod.Post, "/api/platform/tenants", body, platformKey: PlatformKey);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var dto = await Read<TenantCreatedDto>(ok);
        Assert.Equal(BeautyModules.All.Count, dto.Modules.Count); // за замовчуванням усі beauty-модулі
        var login = await fx.LoginAsync(body.slug, "o@x.test");
        Assert.Equal(Roles.Owner, login.User.Role);

        var dup = await fx.Send(HttpMethod.Post, "/api/platform/tenants", body, platformKey: PlatformKey);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("tenant_slug_taken", await Code(dup));
    }

    [SkippableFact]
    public async Task platformEndpoints_reject_user_jwt_and_short_key_config_fails_closed()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var body = new { name = "S", slug = Unique("salon"), owner = new { email = "o@x.test", fullName = "O", password = Password } };

        // JWT власника не відкриває операторські ендпоінти
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fx.Send(HttpMethod.Post, "/api/platform/tenants", body, bearer: owner.AccessToken)).StatusCode);

        // ключ платформи не задано/короткий -> ендпоінти вимкнені, навіть якщо передати той самий короткий ключ
        using var weak = fx.Factory!.WithWebHostBuilder(b => b.UseSetting("Auth:PlatformKey", "short"));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fx.Send(HttpMethod.Post, "/api/platform/tenants", body, platformKey: "short", client: weak.CreateClient())).StatusCode);
    }

    [SkippableFact]
    public async Task createTenant_validates_slug_modules_and_password()
    {
        NeedDb();
        async Task<HttpStatusCode> Try(string slug, string[]? modules, string password) =>
            (await fx.Send(HttpMethod.Post, "/api/platform/tenants",
                new { name = "S", slug, modules, owner = new { email = "o@x.test", fullName = "O", password } }, platformKey: PlatformKey)).StatusCode;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try("Bad Slug!", null, Password));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Unique("salon"), ["no_such_module"], Password));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Unique("salon"), null, "short"));
    }

    // ---------- логін ----------

    [SkippableFact]
    public async Task login_returns_tokens_with_tenant_and_role_claims()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var login = await fx.LoginAsync(t.Slug, t.OwnerEmail.ToUpperInvariant()); // email нечутливий до регістру
        Assert.Equal(t.OwnerId, login.User.Id);
        Assert.True(login.ExpiresInSeconds > 0);

        var me = await fx.Send(HttpMethod.Get, "/api/auth/me", bearer: login.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(t.OwnerId, (await Read<UserDto>(me)).Id);
        Assert.DoesNotContain("password", await me.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task login_wrong_password_unknown_user_and_unknown_tenant_are_indistinguishable_401()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var wrong = await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = t.Slug, email = t.OwnerEmail, password = "Wrong-Password-123" });
        var noUser = await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = t.Slug, email = "nobody@x.test", password = Password });
        var noTenant = await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = "no-such-salon", email = t.OwnerEmail, password = Password });

        foreach (var r in new[] { wrong, noUser, noTenant })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("invalid_credentials", await Code(r));
        }
    }

    [SkippableFact]
    public async Task login_locks_account_after_max_failed_attempts_even_for_correct_password()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        for (var i = 0; i < new AuthOptions().MaxFailedAttempts; i++)
        {
            var bad = await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = t.Slug, email = t.OwnerEmail, password = "Wrong-Password-123" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        var locked = await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = t.Slug, email = t.OwnerEmail, password = Password });
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
        Assert.Equal("account_locked", await Code(locked));

        // блокування одного tenant не зачіпає інших
        var other = await fx.CreateTenantAsync();
        await fx.LoginAsync(other.Slug, other.OwnerEmail);
    }

    [SkippableFact]
    public async Task login_successful_attempt_resets_failure_counter()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 3; i++)
                await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = t.Slug, email = t.OwnerEmail, password = "Wrong-Password-123" });
            await fx.LoginAsync(t.Slug, t.OwnerEmail); // 3 + 3 помилок загалом, але успіх між ними обнуляє лічильник
        }
    }

    [SkippableFact]
    public async Task login_rejects_invalid_body_with_422_and_is_rate_limited_with_429()
    {
        NeedDb();
        var bad = await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = "x", email = "not-an-email", password = "p" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        using var limited = fx.CreateFactory(permitLimit: 3);
        var client = limited.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            codes.Add((await fx.Send(HttpMethod.Post, "/api/auth/login",
                new { tenant = "nope", email = "a@b.test", password = Password }, client: client)).StatusCode);
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    // ---------- refresh ----------

    [SkippableFact]
    public async Task refresh_rotates_token_and_reuse_of_old_token_revokes_all_sessions()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var first = await fx.LoginAsync(t.Slug, t.OwnerEmail);

        var r1 = await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var second = await Read<TokenResponse>(r1);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // повторне використання вже ротованого токена -> 401 і відкликання новоствореного
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = first.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = second.RefreshToken })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = "garbage" })).StatusCode);
    }

    [SkippableFact]
    public async Task logout_revokes_refresh_token()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var login = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        Assert.Equal(HttpStatusCode.NoContent, (await fx.Send(HttpMethod.Post, "/api/auth/logout", new { refreshToken = login.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = login.RefreshToken })).StatusCode);
    }

    // ---------- 401 / 403 ----------

    [SkippableFact]
    public async Task beautyEndpoints_return_401_without_valid_token_and_ignore_tenant_header_outside_development()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Get, "/api/beauty/services")).StatusCode);

        var withHeader = Req(HttpMethod.Get, "/api/beauty/services");
        withHeader.Headers.Add("X-Tenant-Id", t.TenantId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client.SendAsync(withHeader)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Get, "/api/beauty/services", bearer: "not.a.jwt")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Get, "/api/users")).StatusCode);
    }

    [SkippableFact]
    public async Task tamperedToken_with_other_tenant_claim_is_rejected()
    {
        NeedDb();
        var a = await fx.CreateTenantAsync();
        var b = await fx.CreateTenantAsync();
        var token = (await fx.LoginAsync(a.Slug, a.OwnerEmail)).AccessToken;
        var parts = token.Split('.');
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1]))).Replace(a.TenantId.ToString(), b.TenantId.ToString())))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var forged = $"{parts[0]}.{payload}.{parts[2]}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Get, "/api/beauty/services", bearer: forged)).StatusCode);

        static string Pad(string s) => s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4);
    }

    [SkippableFact]
    public async Task roleGuard_specialist_gets_403_on_management_endpoints_admin_gets_200()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var specialist = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);

        foreach (var url in new[] { "/api/beauty/clients", "/api/beauty/analytics/network", "/api/beauty/channels", "/api/beauty/ai/actions", "/api/users", "/api/invites" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await fx.Send(HttpMethod.Get, url, bearer: specialist.AccessToken)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, url, bearer: admin.AccessToken)).StatusCode);
        }

        // каталог: читання для всіх ролей персоналу, зміни — лише керівництво
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, "/api/beauty/services", bearer: specialist.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Send(HttpMethod.Post, "/api/beauty/services",
            new { name = "X", durationMinutes = 30 }, specialist.AccessToken)).StatusCode);
    }

    [SkippableFact]
    public async Task requireModule_uses_tenants_modules_and_suspended_tenant_loses_access()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync(modules: ["beauty_booking"]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);

        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, "/api/beauty/appointments", bearer: owner.AccessToken)).StatusCode);
        var disabled = await fx.Send(HttpMethod.Get, "/api/beauty/services", bearer: owner.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("module_disabled", await Code(disabled));

        var enable = await fx.Send(HttpMethod.Patch, $"/api/platform/tenants/{t.TenantId}",
            new { modules = new[] { "beauty_booking", "beauty_catalog" } }, platformKey: PlatformKey);
        Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, "/api/beauty/services", bearer: owner.AccessToken)).StatusCode);

        var suspend = await fx.Send(HttpMethod.Patch, $"/api/platform/tenants/{t.TenantId}", new { status = "suspended" }, platformKey: PlatformKey);
        Assert.Equal(HttpStatusCode.NoContent, suspend.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Send(HttpMethod.Get, "/api/beauty/appointments", bearer: owner.AccessToken)).StatusCode);
        var login = await fx.Send(HttpMethod.Post, "/api/auth/login", new { tenant = t.Slug, email = t.OwnerEmail, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = owner.RefreshToken })).StatusCode);
    }

    // ---------- спеціаліст бачить лише свої записи ----------

    [SkippableFact]
    public async Task specialist_sees_only_own_appointments_and_cannot_touch_others()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var specialist = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        Assert.Equal(seed.SpecialistA, specialist.User.SpecialistId);
        var tok = specialist.AccessToken;

        // власник бачить обидва
        var all = await Read<List<AppointmentDto>>(await fx.Send(HttpMethod.Get, "/api/beauty/appointments?from=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o")), bearer: owner.AccessToken));
        Assert.Equal(2, all.Count);

        // спеціаліст — лише свій, навіть якщо просить чужого через specialistId
        foreach (var query in new[] { "", $"&specialistId={seed.SpecialistB}" })
        {
            var mine = await Read<List<AppointmentDto>>(await fx.Send(HttpMethod.Get,
                "/api/beauty/appointments?from=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("o")) + query, bearer: tok));
            Assert.Equal(seed.AppointmentA, Assert.Single(mine).Id);
        }

        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, $"/api/beauty/appointments/{seed.AppointmentA}", bearer: tok)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Send(HttpMethod.Get, $"/api/beauty/appointments/{seed.AppointmentB}", bearer: tok)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await fx.Send(HttpMethod.Patch, $"/api/beauty/appointments/{seed.AppointmentB}", new { status = "completed" }, tok)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{seed.AppointmentB}/cancel", bearer: tok)).StatusCode);

        // запис до чужого календаря створити не можна
        var create = await fx.Send(HttpMethod.Post, "/api/beauty/appointments", new
        {
            locationId = seed.LocationId, specialistId = seed.SpecialistB, serviceId = seed.ServiceId,
            startsAt = DateTimeOffset.UtcNow.AddDays(5), client = new { id = seed.ClientId }, reminder = "none", paymentMethod = "cash",
        }, tok);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        // чужий запис лишився недоторканим
        var untouched = await Read<AppointmentDto>(await fx.Send(HttpMethod.Get, $"/api/beauty/appointments/{seed.AppointmentB}", bearer: owner.AccessToken));
        Assert.Equal("confirmed", untouched.Status);
    }

    // ---------- керування користувачами: admin не керує власником ----------

    [SkippableFact]
    public async Task admin_cannot_manage_owner_or_invite_admins_but_owner_can()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var specialist = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);

        // admin -> owner: заборонено
        var hitOwner = await fx.Send(HttpMethod.Patch, $"/api/users/{t.OwnerId}/status", new { isActive = false }, admin.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, hitOwner.StatusCode);
        Assert.Equal("forbidden_role", await Code(hitOwner));
        // admin -> інший admin / запросити admin: заборонено
        Assert.Equal(HttpStatusCode.Forbidden,
            (await fx.Send(HttpMethod.Post, "/api/invites", new { email = "a2@x.test", role = "admin" }, admin.AccessToken)).StatusCode);
        // ніхто не запрошує другого owner
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await fx.Send(HttpMethod.Post, "/api/invites", new { email = "o2@x.test", role = "owner" }, owner.AccessToken)).StatusCode);
        // власник не вимикає себе
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await fx.Send(HttpMethod.Patch, $"/api/users/{t.OwnerId}/status", new { isActive = false }, owner.AccessToken)).StatusCode);

        // власник усе ще активний і працює
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, "/api/auth/me", bearer: owner.AccessToken)).StatusCode);

        // admin вимикає спеціаліста: refresh-токен спеціаліста більше не діє, вхід неможливий
        var off = await fx.Send(HttpMethod.Patch, $"/api/users/{specialist.User.Id}/status", new { isActive = false }, admin.AccessToken);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = specialist.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Post, "/api/auth/login",
            new { tenant = t.Slug, email = specialist.User.Email, password = Password })).StatusCode);

        // owner запрошує admin — ок
        Assert.Equal(HttpStatusCode.Created,
            (await fx.Send(HttpMethod.Post, "/api/invites", new { email = "a3@x.test", role = "admin" }, owner.AccessToken)).StatusCode);
    }

    [SkippableFact]
    public async Task invite_is_single_use_requires_valid_specialist_and_token_is_not_stored_in_clear()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);

        // specialist без specialistId / з чужим -> 422
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await fx.Send(HttpMethod.Post, "/api/invites", new { email = "s@x.test", role = "specialist" }, owner.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await fx.Send(HttpMethod.Post, "/api/invites", new { email = "s@x.test", role = "specialist", specialistId = Guid.NewGuid() }, owner.AccessToken)).StatusCode);

        var created = await Read<InviteCreatedDto>(await fx.Send(HttpMethod.Post, "/api/invites",
            new { email = "s@x.test", role = "specialist", specialistId = seed.SpecialistA }, owner.AccessToken));
        var accept = new { token = created.Token, fullName = "S", password = Password };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await fx.Send(HttpMethod.Post, "/api/auth/invites/accept",
            new { token = created.Token, fullName = "S", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await fx.Send(HttpMethod.Post, "/api/auth/invites/accept", accept)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Send(HttpMethod.Post, "/api/auth/invites/accept", accept)).StatusCode);

        // профіль майстра вже має користувача
        Assert.Equal(HttpStatusCode.Conflict,
            (await fx.Send(HttpMethod.Post, "/api/invites", new { email = "s2@x.test", role = "specialist", specialistId = seed.SpecialistA }, owner.AccessToken)).StatusCode);

        // у БД лише хеш токена
        await using var ctx = fx.Db.CreateContext(t.TenantId);
        var invite = await ctx.Invites.AsNoTracking().SingleAsync();
        Assert.NotEqual(created.Token, invite.TokenHash);
        Assert.Equal(TokenCodec.Hash(created.Token), invite.TokenHash);
        var user = await ctx.Users.AsNoTracking().SingleAsync(u => u.Email == "s@x.test");
        Assert.StartsWith("$2", user.PasswordHash); // bcrypt, не відкритий текст

        // токен чужого tenant не приймається під іншим tenant (tenant зашитий у токені, RLS не знаходить рядок)
        var other = await fx.CreateTenantAsync();
        var otherOwner = await fx.LoginAsync(other.Slug, other.OwnerEmail);
        var fresh = await Read<InviteCreatedDto>(await fx.Send(HttpMethod.Post, "/api/invites", new { email = "z@x.test", role = "admin" }, otherOwner.AccessToken));
        var swapped = TokenCodec.Parse(fresh.Token)!;
        var forged = fresh.Token.Replace(swapped.TenantId.ToString("N"), t.TenantId.ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Send(HttpMethod.Post, "/api/auth/invites/accept",
            new { token = forged, fullName = "Z", password = Password })).StatusCode);
    }

    // ---------- ізоляція tenant у users (RLS) ----------

    [SkippableFact]
    public async Task users_are_isolated_between_tenants_through_api_and_database()
    {
        NeedDb();
        var a = await fx.CreateTenantAsync();
        var b = await fx.CreateTenantAsync();
        var seedB = await fx.SeedBeautyAsync(b.TenantId);
        var ownerA = await fx.LoginAsync(a.Slug, a.OwnerEmail);
        await fx.InviteAndLoginAsync(a, ownerA.AccessToken, Roles.Admin);

        // API: власник A бачить лише користувачів A і не бачить записів B
        var users = await Read<List<UserDto>>(await fx.Send(HttpMethod.Get, "/api/users", bearer: ownerA.AccessToken));
        Assert.Equal(2, users.Count);
        Assert.DoesNotContain(users, u => u.Email.EndsWith($"@{b.Slug}.test"));
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Send(HttpMethod.Get, $"/api/beauty/appointments/{seedB.AppointmentA}", bearer: ownerA.AccessToken)).StatusCode);
        // чужого користувача вимкнути не можна (RLS -> не знайдено)
        Assert.Equal(HttpStatusCode.NotFound,
            (await fx.Send(HttpMethod.Patch, $"/api/users/{b.OwnerId}/status", new { isActive = false }, ownerA.AccessToken)).StatusCode);

        // БД: ctx(A) бачить лише своїх; без tenant — нікого; вставка під чужим tenant_id відхиляється RLS
        await using (var ctxA = fx.Db.CreateContext(a.TenantId))
        {
            Assert.All(await ctxA.Users.AsNoTracking().ToListAsync(), u => Assert.Equal(a.TenantId, u.TenantId));
            Assert.Null(await ctxA.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == b.OwnerId));
            Assert.Equal(a.TenantId, (await ctxA.Tenants.AsNoTracking().SingleAsync()).Id); // видно лише власний tenant
            Assert.Equal(0, await ctxA.Invites.CountAsync(i => i.TenantId == b.TenantId));
        }
        await using (var none = fx.Db.CreateContext(null))
        {
            Assert.Equal(0, await none.Users.CountAsync());
            Assert.Equal(0, await none.Tenants.CountAsync());
            Assert.Equal(0, await none.RefreshTokens.CountAsync());
        }
        await using (var evil = fx.Db.CreateContext(a.TenantId))
        {
            evil.Users.Add(new User { TenantId = b.TenantId, Email = "evil@x.test", FullName = "E", PasswordHash = "x", Role = Roles.Admin });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => evil.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (ex.InnerException as PostgresException)?.SqlState);
        }
    }

    [SkippableFact]
    public async Task same_email_can_exist_in_two_tenants_but_not_twice_in_one()
    {
        NeedDb();
        var a = await fx.CreateTenantAsync();
        var b = await fx.CreateTenantAsync();
        var email = "shared@x.test";
        foreach (var t in new[] { a, b })
        {
            var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
            var inv = await fx.Send(HttpMethod.Post, "/api/invites", new { email, role = "admin" }, owner.AccessToken);
            var token = (await Read<InviteCreatedDto>(inv)).Token;
            Assert.Equal(HttpStatusCode.Created, (await fx.Send(HttpMethod.Post, "/api/auth/invites/accept", new { token, fullName = "S", password = Password })).StatusCode);
        }
        var ownerA = await fx.LoginAsync(a.Slug, a.OwnerEmail);
        Assert.Equal(HttpStatusCode.Conflict, (await fx.Send(HttpMethod.Post, "/api/invites", new { email, role = "admin" }, ownerA.AccessToken)).StatusCode);
        await fx.LoginAsync(a.Slug, email);
        await fx.LoginAsync(b.Slug, email);
    }
}

internal static class HttpJsonExtensions
{
    public static async Task<JsonElement> ReadFromJsonElementAsync(this HttpContent content) =>
        JsonDocument.Parse(await content.ReadAsStringAsync()).RootElement.Clone();
}
