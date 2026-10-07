using System.Net;
using System.Text.Json;
using BeautyCrm.Tests.Auth;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Regression;

/// <summary>
/// TASK-681: відтворення знайдених дефектів (QA production не змінює). Тест падає, поки дефект не виправлено;
/// після виправлення лишається регресійним. BUG-681-1 на момент здачі вже виправлено (EfBeautyStore.GetBusyAsync
/// нормалізує до UTC) - тест зелений. Фільтр знайомих дефектів: dotnet test --filter "Category=KnownBug".
/// </summary>
public sealed class BugReproTests : IClassFixture<AuthApiFixture>
{
    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;

    public BugReproTests(AuthApiFixture fx)
    {
        _fx = fx;
        _h = new RegressionHarness(fx);
    }

    /// <summary>
    /// BUG-681-1: заклад з не-UTC таймзоною (напр. Europe/Kyiv) -> GET /slots = 500.
    /// BookingService.GetSlotsAsync рахує dayStart/dayEnd з offset +02/+03 і передає в EfBeautyStore.GetBusyAsync,
    /// Npgsql: "Cannot write DateTimeOffset with Offset=03:00:00 to PostgreSQL type 'timestamp with time zone'".
    /// Той самий збій для POST /appointments зі startsAt з ненульовим offset (слот із /slots повертається з offset).
    /// </summary>
    [SkippableFact]
    [Trait("Category", "KnownBug")]
    public async Task bug_681_1_slots_and_create_return_500_for_non_utc_location_timezone()
    {
        Skip.If(_fx.SkipReason is not null, _fx.SkipReason);
        var s = await _h.CreateSalonAsync(timezone: "Europe/Kyiv");
        var r = await _fx.Send(HttpMethod.Get,
            $"/api/beauty/slots?locationId={s.LocationId}&serviceId={s.ServiceId}&date={RegressionHarness.SlotDate()}&specialistId={s.SpecialistId}",
            bearer: s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var slots = (await Read<JsonElement>(r)).EnumerateArray().ToList();
        Assert.NotEmpty(slots);

        var start = slots[0].GetProperty("startsAt").GetDateTimeOffset(); // з offset таймзони закладу
        var created = await _h.BookAsync(s, start);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }
}
