using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using BeautyCrm.Tests.Auth;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-685 на реальному PostgreSQL (міграція, RLS під роллю без BYPASSRLS) через повний HTTP-конвеєр.
/// Без БД пропускаються; BEAUTY_TEST_REQUIRE_DB=1 перетворює пропуск на помилку.
/// </summary>
public sealed class CancellationSettingsApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private const string Url = "/api/beauty/settings/cancellation";

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private static readonly object Valid = new { windowHours = 24, refundPercentInWindow = 30, refundPercentOutside = 90, deductFee = true, feePercent = 10 };

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await Read<JsonElement>(r);

    [SkippableFact]
    public async Task get_returns_defaults_when_tenant_never_configured()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);

        var r = await fx.Send(HttpMethod.Get, Url, bearer: owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal(12, j.GetProperty("windowHours").GetInt32());
        Assert.Equal(50, j.GetProperty("refundPercentInWindow").GetInt32());
        Assert.Equal(100, j.GetProperty("refundPercentOutside").GetInt32());
        Assert.False(j.GetProperty("deductFee").GetBoolean());
        Assert.Equal(0, j.GetProperty("feePercent").GetInt32());
    }

    [SkippableFact]
    public async Task roles_everyone_reads_only_owner_writes()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var specialist = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);

        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Get, Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Put, Url, Valid)).StatusCode);

        foreach (var token in new[] { owner.AccessToken, admin.AccessToken, specialist.AccessToken })
            Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Get, Url, bearer: token)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Send(HttpMethod.Put, Url, Valid, admin.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Send(HttpMethod.Put, Url, Valid, specialist.AccessToken)).StatusCode);
        // відмова не змінила налаштувань
        Assert.Equal(12, (await Json(await fx.Send(HttpMethod.Get, Url, bearer: admin.AccessToken))).GetProperty("windowHours").GetInt32());

        var ok = await fx.Send(HttpMethod.Put, Url, Valid, owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        foreach (var token in new[] { owner.AccessToken, admin.AccessToken, specialist.AccessToken })
        {
            var j = await Json(await fx.Send(HttpMethod.Get, Url, bearer: token));
            Assert.Equal(24, j.GetProperty("windowHours").GetInt32());
            Assert.Equal(30, j.GetProperty("refundPercentInWindow").GetInt32());
            Assert.Equal(90, j.GetProperty("refundPercentOutside").GetInt32());
            Assert.True(j.GetProperty("deductFee").GetBoolean());
            Assert.Equal(10, j.GetProperty("feePercent").GetInt32());
        }

        // повторний PUT оновлює той самий рядок (upsert), а не дублює
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Put, Url,
            new { windowHours = 6, refundPercentInWindow = 0, refundPercentOutside = 100, deductFee = false, feePercent = 0 },
            owner.AccessToken)).StatusCode);
        await using var ctx = fx.Db.CreateContext(t.TenantId);
        Assert.Equal(1, await ctx.CancellationSettings.CountAsync());
        Assert.Equal(6, (await ctx.CancellationSettings.SingleAsync()).WindowHours);
    }

    [SkippableTheory]
    [InlineData(-1, 50, 100, true, 10, "invalid_window_hours")]
    [InlineData(12, 101, 100, true, 10, "invalid_refund_percent")]
    [InlineData(12, 50, -5, true, 10, "invalid_refund_percent")]
    [InlineData(12, 50, 100, true, 101, "invalid_fee_percent")]
    [InlineData(12, 50, 100, false, -1, "invalid_fee_percent")]
    public async Task put_returns_422_for_invalid_values(int w, int i, int o, bool d, int f, string code)
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var r = await fx.Send(HttpMethod.Put, Url,
            new { windowHours = w, refundPercentInWindow = i, refundPercentOutside = o, deductFee = d, feePercent = f }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(code, (await Json(r)).GetProperty("code").GetString());
        Assert.Equal(12, (await Json(await fx.Send(HttpMethod.Get, Url, bearer: owner.AccessToken))).GetProperty("windowHours").GetInt32());
    }

    [SkippableFact]
    public async Task put_returns_422_when_field_missing()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var r = await fx.Send(HttpMethod.Put, Url, new { windowHours = 12, refundPercentInWindow = 50 }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("settings_incomplete", (await Json(r)).GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task settings_are_isolated_per_tenant_and_appointment_dto_carries_terms()
    {
        NeedDb();
        var a = await fx.CreateTenantAsync();
        var b = await fx.CreateTenantAsync();
        var ownerA = await fx.LoginAsync(a.Slug, a.OwnerEmail);
        var ownerB = await fx.LoginAsync(b.Slug, b.OwnerEmail);
        var seedA = await fx.SeedBeautyAsync(a.TenantId);

        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Put, Url, Valid, ownerA.AccessToken)).StatusCode);

        var forB = await Json(await fx.Send(HttpMethod.Get, Url, bearer: ownerB.AccessToken));
        Assert.Equal(12, forB.GetProperty("windowHours").GetInt32()); // B бачить значення за замовчуванням, не налаштування A

        var appt = await Json(await fx.Send(HttpMethod.Get, $"/api/beauty/appointments/{seedA.AppointmentA}", bearer: ownerA.AccessToken));
        var terms = appt.GetProperty("cancellation");
        Assert.Equal(24, terms.GetProperty("windowHours").GetInt32());
        Assert.Equal(30, terms.GetProperty("refundPercentInWindow").GetInt32());
        Assert.Equal(90, terms.GetProperty("refundPercentOutside").GetInt32());
        Assert.True(terms.GetProperty("deductFee").GetBoolean());
        Assert.Equal(10, terms.GetProperty("feePercent").GetInt32());

        var date = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2));
        var slots = await Json(await fx.Send(HttpMethod.Get,
            $"/api/beauty/slots?locationId={seedA.LocationId}&serviceId={seedA.ServiceId}&date={date:yyyy-MM-dd}", bearer: ownerA.AccessToken));
        foreach (var s in slots.EnumerateArray())
            Assert.Equal(24, s.GetProperty("cancellation").GetProperty("windowHours").GetInt32());
    }

    // ---------- міграція / RLS на рівні БД ----------

    [SkippableFact]
    public async Task migration_creates_table_with_rls_unique_and_check_constraints()
    {
        NeedDb();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using (var ctx = fx.Db.CreateContext(a))
        {
            ctx.CancellationSettings.Add(new CancellationSettingsRow { RefundPercentInWindow = 40 });
            await ctx.SaveChangesAsync();
        }

        // RLS: інший tenant і запит без tenant не бачать рядок (fail-closed)
        await using (var other = fx.Db.CreateContext(b))
            Assert.Empty(await other.CancellationSettings.ToListAsync());
        await using (var none = fx.Db.CreateContext(null))
            Assert.Empty(await none.CancellationSettings.ToListAsync());
        await using (var own = fx.Db.CreateContext(a))
        {
            var row = await own.CancellationSettings.SingleAsync();
            Assert.Equal(12, row.WindowHours); // DEFAULT з міграції
            Assert.Equal(40, row.RefundPercentInWindow);
            Assert.Equal(100, row.RefundPercentOutside);
            Assert.False(row.DeductFee);
            Assert.Equal(0, row.FeePercent);
        }

        // WITH CHECK: запис рядка з чужим tenant_id відхиляється
        await using (var forged = fx.Db.CreateContext(b))
        {
            forged.CancellationSettings.Add(new CancellationSettingsRow { TenantId = a });
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, SqlState(await Assert.ThrowsAsync<DbUpdateException>(() => forged.SaveChangesAsync())));
        }

        // unique(tenant_id): другий рядок того ж tenant
        await using (var dup = fx.Db.CreateContext(a))
        {
            dup.CancellationSettings.Add(new CancellationSettingsRow());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, SqlState(await Assert.ThrowsAsync<DbUpdateException>(() => dup.SaveChangesAsync())));
        }

        // CHECK: відсотки 0..100, вікно 0..720
        foreach (var bad in new[]
                 {
                     new CancellationSettingsRow { RefundPercentInWindow = 101 },
                     new CancellationSettingsRow { RefundPercentOutside = -1 },
                     new CancellationSettingsRow { FeePercent = 101 },
                     new CancellationSettingsRow { WindowHours = 721 },
                     new CancellationSettingsRow { WindowHours = -1 },
                 })
        {
            await using var c = fx.Db.CreateContext(Guid.NewGuid());
            c.CancellationSettings.Add(bad);
            Assert.Equal(PostgresErrorCodes.CheckViolation, SqlState(await Assert.ThrowsAsync<DbUpdateException>(() => c.SaveChangesAsync())));
        }
    }

    private static string? SqlState(Exception ex) => (ex.InnerException as PostgresException)?.SqlState;
}
