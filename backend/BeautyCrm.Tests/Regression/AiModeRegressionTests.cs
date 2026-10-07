using System.Net;
using System.Text.Json;
using BeautyCrm.Tests.Auth;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Regression;

/// <summary>
/// TASK-681: режим автономності AI на повному стеку (HTTP + PostgreSQL). Юніт-рівень suggest/confirm/auto покритий
/// BeautyAssistantTests; тут - що за замовчуванням діє confirm, дія не створює запис до підтвердження, а підтвердження
/// й скасування (reject) поводяться за контрактом.
/// </summary>
public sealed class AiModeRegressionTests : IClassFixture<AuthApiFixture>
{
    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;

    public AiModeRegressionTests(AuthApiFixture fx)
    {
        _fx = fx;
        _h = new RegressionHarness(fx);
    }

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    private async Task<int> AppointmentCountAsync(Guid tenantId)
    {
        await using var ctx = _fx.Db.CreateContext(tenantId);
        return await ctx.Appointments.CountAsync();
    }

    [SkippableFact]
    public async Task regression_ai_default_mode_confirm_creates_nothing_until_human_approves_and_reject_keeps_it_empty()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var slots = await _h.SlotsAsync(s, RegressionHarness.SlotDate());
        // базовий запис дає клієнта, від імені якого діє AI
        var seeded = await Read<JsonElement>(await _h.BookAsync(s, slots[0].GetProperty("startsAt").GetDateTimeOffset()));
        var clientId = seeded.GetProperty("clientId").GetGuid();
        Assert.Equal(1, await AppointmentCountAsync(s.Tenant.TenantId));

        object Run(int slotIndex) => new
        {
            tool = "create_appointment", clientId,
            input = new { serviceId = s.ServiceId, staffId = s.SpecialistId, start = slots[slotIndex].GetProperty("startsAt").GetDateTimeOffset() },
        };

        // confirm (за замовчуванням): дія в журналі зі статусом pending, запис НЕ створено
        var run = await _fx.Send(HttpMethod.Post, "/api/beauty/ai/actions", Run(8), s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var body = await Read<JsonElement>(run);
        Assert.False(body.GetProperty("isError").GetBoolean());
        Assert.Equal("pending_confirmation", body.GetProperty("status").GetString());
        Assert.Equal(1, await AppointmentCountAsync(s.Tenant.TenantId));

        var journal = (await Read<JsonElement>(await _fx.Send(HttpMethod.Get, "/api/beauty/ai/actions?status=pending_confirmation", bearer: s.OwnerToken))).EnumerateArray().ToList();
        var action = Assert.Single(journal);
        Assert.Equal("confirm", action.GetProperty("mode").GetString());
        var actionId = action.GetProperty("id").GetGuid();

        // підтвердження людиною -> запис створено (рівно один), повторне підтвердження заборонене
        var approve = await _fx.Send(HttpMethod.Post, $"/api/beauty/ai/actions/{actionId}/approve", bearer: s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal(2, await AppointmentCountAsync(s.Tenant.TenantId));
        Assert.Equal(HttpStatusCode.Conflict, (await _fx.Send(HttpMethod.Post, $"/api/beauty/ai/actions/{actionId}/approve", bearer: s.OwnerToken)).StatusCode);
        Assert.Equal(2, await AppointmentCountAsync(s.Tenant.TenantId));

        // відхилена дія нічого не створює
        var run2 = await Read<JsonElement>(await _fx.Send(HttpMethod.Post, "/api/beauty/ai/actions", Run(12), s.OwnerToken));
        Assert.Equal("pending_confirmation", run2.GetProperty("status").GetString());
        var pending = (await Read<JsonElement>(await _fx.Send(HttpMethod.Get, "/api/beauty/ai/actions?status=pending_confirmation", bearer: s.OwnerToken))).EnumerateArray().Single();
        Assert.Equal(HttpStatusCode.OK, (await _fx.Send(HttpMethod.Post, $"/api/beauty/ai/actions/{pending.GetProperty("id").GetGuid()}/reject", bearer: s.OwnerToken)).StatusCode);
        Assert.Equal(2, await AppointmentCountAsync(s.Tenant.TenantId));
    }

    [SkippableFact]
    public async Task regression_ai_actions_are_tenant_scoped()
    {
        NeedDb();
        var a = await _h.CreateSalonAsync();
        var b = await _h.CreateSalonAsync();
        var slots = await _h.SlotsAsync(a, RegressionHarness.SlotDate());
        var clientId = (await Read<JsonElement>(await _h.BookAsync(a, slots[0].GetProperty("startsAt").GetDateTimeOffset()))).GetProperty("clientId").GetGuid();
        var run = await _fx.Send(HttpMethod.Post, "/api/beauty/ai/actions",
            new { tool = "create_appointment", clientId, input = new { serviceId = a.ServiceId, staffId = a.SpecialistId, start = slots[8].GetProperty("startsAt").GetDateTimeOffset() } },
            a.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var id = (await Read<JsonElement>(await _fx.Send(HttpMethod.Get, "/api/beauty/ai/actions?status=pending_confirmation", bearer: a.OwnerToken)))
            .EnumerateArray().Single().GetProperty("id").GetGuid();

        // tenant B не бачить і не може підтвердити дію tenant A
        Assert.Empty((await Read<JsonElement>(await _fx.Send(HttpMethod.Get, "/api/beauty/ai/actions", bearer: b.OwnerToken))).EnumerateArray());
        var steal = await _fx.Send(HttpMethod.Post, $"/api/beauty/ai/actions/{id}/approve", bearer: b.OwnerToken);
        Assert.NotEqual(HttpStatusCode.OK, steal.StatusCode);
        await using var ctx = _fx.Db.CreateContext(a.Tenant.TenantId);
        Assert.Equal(1, await ctx.Appointments.CountAsync()); // жодного нового запису
    }
}
