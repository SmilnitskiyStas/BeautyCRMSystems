using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Application.Features.BeautyChatFlow;

/// <summary>
/// Автомат запису в чаті (ADR-020, §28.3-§28.4, §28.8): порядок «послуга → спеціаліст → заклад (з «Так, у …») → час → підтвердження».
/// Усі кроки - з БД, без LLM; цей клас НЕ залежить від шару ШІ (гарантія метрики «0 LLM на кнопкових кроках», перевіряє архітектурний тест).
/// Сервіс лише ОБЧИСЛЮЄ перехід (<see cref="FlowResult"/>: новий стан + дані кроку) і зберігає його (<see cref="PersistAsync"/>, CAS за версією);
/// повідомлення, клавіатури, CAS клавіатури й підсумок (<c>PrepareAsync</c>) - у конвеєрі розмов (Infrastructure).
/// Інваріант «не одразу підсумок»: крок <c>confirm</c> ставить лише <see cref="ToConfirm"/> після вибору конкретного слота; жоден префіл його не мине.
/// </summary>
public sealed class ChatBookingFlowService(
    IChatFlowStore store, IChatFlowCatalog catalog, OnlineBookingCore slots, ChatFlowOptions opts, TimeProvider clock)
{
    private const int MaxHops = 8;

    private sealed record Resolved(string Code, FlowState State, FlowView? View);

    // ------------------------------------------------------------------ старт

    /// <summary>
    /// Новий потік (§28.3 «Старт»): попередній <c>active</c> стає <c>superseded</c> при збереженні. Без префілу перший крок - <c>service</c>
    /// (не пропускається ніколи). Префіл (LLM) перевіряється під RLS і на придатність; невалідне тихо відкидається; далі <c>time</c> не заходить.
    /// </summary>
    public async Task<FlowResult> StartAsync(FlowContext ctx, string origin, FlowPrefill? prefill, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        Guid? suggested = null;
        if (opts.OfferLastLocation && ctx.ClientId is { } clientId)
            suggested = await catalog.GetLastAppointmentLocationAsync(clientId, ct);
        var s = new FlowState(Guid.NewGuid(), ctx.ConversationId, FlowStatuses.Active, FlowSteps.Service, 0, null, null, null, suggested,
            null, null, null, null, [], null, origin, now + opts.IdleTimeout);
        var chosen = new List<FlowChosen>();
        if (prefill is not null) s = await ApplyPrefillAsync(s, ctx, prefill, chosen, ct);
        return Seal(await ResolveAsync(s, ctx, chosen, null, false, null, ct), null, isNew: true);
    }

    private async Task<FlowState> ApplyPrefillAsync(FlowState s, FlowContext ctx, FlowPrefill p, List<FlowChosen> chosen, CancellationToken ct)
    {
        if (p.LocationId is { } pl && ctx.ChannelLocationId is null
            && (await catalog.ListBookableServicesAsync(null, pl, ct)).Count > 0)
            s = s with { LocationId = pl, SkippedSteps = Add(Add(s.SkippedSteps, FlowSteps.LastLocation), FlowSteps.Location) };
        var filterLocation = ctx.ChannelLocationId ?? s.LocationId;

        if (p.ServiceId is { } sid && (await catalog.ListBookableServicesAsync(null, filterLocation, ct)).Any(x => x.Id == sid))
            s = s with { ServiceId = sid };
        if (p.SpecialistId is { } pid)
        {
            var ok = s.ServiceId is { } svc
                ? (await catalog.ListSpecialistsForServiceAsync(svc, filterLocation, ct)).Any(x => x.Id == pid)
                : (await catalog.ListBookableServicesAsync(pid, filterLocation, ct)).Count > 0;
            if (ok)
            {
                var names = await catalog.GetNamesAsync(null, pid, null, ct);
                s = s with { SpecialistId = pid, SkippedSteps = Add(s.SkippedSteps, FlowSteps.Specialist) };
                if (names.Specialist is { } sn) chosen.Add(new FlowChosen("specialist", sn, null));
            }
        }
        if (s.LocationId is { } loc && ctx.ChannelLocationId is null)
        {
            var names = await catalog.GetNamesAsync(null, null, loc, ct);
            if (names.Location is { } ln) chosen.Add(new FlowChosen("location", ln, names.Address));
        }
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (p.Day is { } day && day >= today && day <= today.AddDays(slots.Options.MaxDaysAhead))
            s = s with { Day = day, PreferredTime = p.PreferredTime };
        return s;
    }

    // ------------------------------------------------------------------ переходи

    /// <summary>Перехід за натисканням/текстовим збігом. Не обробляє <c>ft</c> (слот -> <c>PrepareAsync</c> у конвеєрі) - див. <see cref="SlotUnavailableAsync"/>, <see cref="ToConfirm"/>.</summary>
    public async Task<FlowResult> ApplyAsync(FlowState s, FlowContext ctx, FlowEvent ev, CancellationToken ct)
    {
        if (!s.IsActive) return Invalid();
        var chosen = new List<FlowChosen>();
        switch (ev.Verb)
        {
            case FlowVerbs.Service when s.Step == FlowSteps.Service && ev.ServiceId is { } serviceId:
            {
                if (!(await catalog.ListBookableServicesAsync(s.SpecialistId, s.LocationId, ct)).Any(x => x.Id == serviceId))
                    return Unavailable(await ResolveAsync(s with { ListCursor = null }, ctx, chosen, FlowNotices.Unavailable, false, null, ct), s);
                var next = s with { ServiceId = serviceId, ListCursor = null };
                if (s.SpecialistId is { } pinned)
                {
                    // майстер визначений префілом: послуги були відфільтровані ним, тож він виконує обрану
                    next = next with { SkippedSteps = Add(next.SkippedSteps, FlowSteps.Specialist) };
                    if ((await catalog.GetNamesAsync(null, pinned, null, ct)).Specialist is { } n) chosen.Add(new FlowChosen("specialist", n, null));
                }
                return Seal(await ResolveAsync(next, ctx, chosen, null, false, null, ct), s);
            }
            case FlowVerbs.Specialist when s.Step == FlowSteps.Specialist && ev.SpecialistId is { } specialistId && s.ServiceId is { } svc:
            {
                if (!(await catalog.ListSpecialistsForServiceAsync(svc, s.LocationId, ct)).Any(x => x.Id == specialistId))
                    return Unavailable(await ResolveAsync(s with { ListCursor = null }, ctx, chosen, FlowNotices.Unavailable, false, null, ct), s);
                return Seal(await ResolveAsync(s with { SpecialistId = specialistId, ListCursor = null }, ctx, chosen, null, false, null, ct), s);
            }
            case FlowVerbs.Location when s.Step is FlowSteps.LastLocation or FlowSteps.Location && ev.LocationId is { } locationId
                                         && s.ServiceId is { } sv && s.SpecialistId is { } sp:
            {
                if (!(await catalog.ListLocationsForAsync(sv, sp, ct)).Any(x => x.Id == locationId))
                    return Unavailable(await ResolveAsync(s with { ListCursor = null }, ctx, chosen, FlowNotices.Unavailable, s.Step == FlowSteps.Location, null, ct), s);
                var next = s with { LocationId = locationId, CursorAt = null, ListCursor = null };
                return Seal(await RenderTimeAsync(next, ctx, chosen, null, ct), s);
            }
            case FlowVerbs.Day when s.Step == FlowSteps.Day:
            {
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                if (!DateOnly.TryParseExact(ev.Date, "yyyy-MM-dd", out var date) || date < today.AddDays(-1) || date > today.AddDays(slots.Options.MaxDaysAhead))
                    return Unavailable(await RenderDayAsync(s with { ListCursor = null }, ctx, null, FlowNotices.Unavailable, ct), s);
                var next = s with { Day = date, CursorAt = null, PreferredTime = null, ListCursor = null };
                return Seal(await RenderTimeAsync(next, ctx, chosen, null, ct), s);
            }
            case FlowVerbs.Nav when ev.Nav is { } nav && FlowNav.AllowedFor(s.Step, nav):
                return await NavAsync(s, ctx, nav, chosen, ct);
            default:
                return Invalid();
        }
    }

    private async Task<FlowResult> NavAsync(FlowState s, FlowContext ctx, string nav, List<FlowChosen> chosen, CancellationToken ct)
    {
        switch (nav)
        {
            case FlowNav.Cancel:
                return new FlowResult(FlowResultCodes.Cancelled, Terminal(s, FlowStatuses.Cancelled, FlowCancelReasons.Client), null);
            case FlowNav.More:
                return s.Step switch
                {
                    FlowSteps.Time => Seal(await RenderTimeAsync(s, ctx, chosen, null, ct), s),
                    FlowSteps.Day => Seal(await RenderDayAsync(s, ctx, s.ListCursor, null, ct), s),
                    _ => Seal(await ResolveAsync(s, ctx, chosen, null, s.Step == FlowSteps.Location, s.ListCursor, ct), s),
                };
            case FlowNav.OtherLocation:
                return Seal(await ResolveAsync(s with { ListCursor = null }, ctx, chosen, null, true, null, ct), s);
            case FlowNav.OtherDay:
                return Seal(await RenderDayAsync(s with { ListCursor = null }, ctx, null, null, ct), s);
            case FlowNav.OtherSpecialist:
                return Seal(await ResolveAsync(ClearFrom(s, FlowSteps.Specialist), ctx, chosen, null, false, null, ct), s);
            case FlowNav.Back:
                return Seal(await BackAsync(s, ctx, chosen, ct), s);
            default:
                return Invalid();
        }
    }

    private async Task<Resolved> BackAsync(FlowState s, FlowContext ctx, List<FlowChosen> chosen, CancellationToken ct)
    {
        switch (s.Step)
        {
            case FlowSteps.Day:
                return await RenderTimeResolvedAsync(s with { Day = null, CursorAt = null, PreferredTime = null, ListCursor = null }, ctx, chosen, null, ct);
            case FlowSteps.Time:
            {
                if (s.Skipped(FlowSteps.Location)) return await ResolveBackToAsync(s, SpecialistOrService(s), ctx, chosen, ct);
                var viaLast = s.SuggestedLastLocationId is { } sug && s.LocationId == sug && !s.Skipped(FlowSteps.LastLocation);
                return await ResolveBackToAsync(s, viaLast ? FlowSteps.LastLocation : FlowSteps.Location, ctx, chosen, ct);
            }
            case FlowSteps.Location:
            {
                var target = await LastLocationOfferedAsync(s, ct) ? FlowSteps.LastLocation : SpecialistOrService(s);
                return await ResolveBackToAsync(s, target, ctx, chosen, ct);
            }
            case FlowSteps.LastLocation:
                return await ResolveBackToAsync(s, SpecialistOrService(s), ctx, chosen, ct);
            default: // specialist
                return await ResolveBackToAsync(s, FlowSteps.Service, ctx, chosen, ct);
        }
    }

    private static string SpecialistOrService(FlowState s) => s.Skipped(FlowSteps.Specialist) ? FlowSteps.Service : FlowSteps.Specialist;

    private async Task<bool> LastLocationOfferedAsync(FlowState s, CancellationToken ct) =>
        s.SuggestedLastLocationId is { } sug && !s.Skipped(FlowSteps.LastLocation) && s.ServiceId is { } sv && s.SpecialistId is { } sp
        && (await catalog.ListLocationsForAsync(sv, sp, ct)).Any(x => x.Id == sug);

    /// <summary>«Назад» на <paramref name="target"/>: вибір цього кроку й наступних очищається (§28.3).</summary>
    private Task<Resolved> ResolveBackToAsync(FlowState s, string target, FlowContext ctx, List<FlowChosen> chosen, CancellationToken ct)
    {
        var cleared = ClearFrom(s, target == FlowSteps.Service ? FlowSteps.Service : target == FlowSteps.Specialist ? FlowSteps.Specialist : FlowSteps.Location);
        return ResolveAsync(cleared, ctx, chosen, null, target == FlowSteps.Location, null, ct);
    }

    /// <summary>
    /// Слот недоступний (<c>PrepareAsync</c>: <c>slot_not_offered|invalid_start</c>): свіжі слоти з місця невдалого (він сам, якщо ще вільний, лишається в списку).
    /// </summary>
    public async Task<FlowResult> SlotUnavailableAsync(FlowState s, FlowContext ctx, DateTimeOffset failedStart, CancellationToken ct) =>
        Unavailable(Seal(await RenderTimeResolvedAsync(s with { CursorAt = failedStart.AddMinutes(-1), ListCursor = null }, ctx, [], FlowNotices.Unavailable, ct), s), s);

    /// <summary>
    /// Повернення з підсумку на <c>time</c> (§28.8): «Інший час» (<paramref name="cursor"/> = початок відхиленої пропозиції), слот зайнято/протерміновано
    /// (<paramref name="cursor"/> = null, день скидається), текст на підсумку (курсор без змін). Пропозиція з рядка потоку знімається.
    /// </summary>
    public async Task<FlowResult> ReturnToTimeAsync(FlowState s, FlowContext ctx, DateTimeOffset? cursor, bool resetDay, string? notice, CancellationToken ct)
    {
        var back = s with
        {
            ProposalId = null, CursorAt = cursor, ListCursor = null, PreferredTime = resetDay ? null : s.PreferredTime, Day = resetDay ? null : s.Day,
        };
        return Seal(await RenderTimeResolvedAsync(back, ctx, [], notice, ct), s);
    }

    /// <summary>Те саме поточне місце зі свіжими даними (нова версія): після <c>option_unavailable</c>, таймаутів тощо.</summary>
    public async Task<FlowResult> ReshowAsync(FlowState s, FlowContext ctx, string? notice, CancellationToken ct) =>
        Seal(s.Step switch
        {
            FlowSteps.Time => await RenderTimeResolvedAsync(s with { ListCursor = null }, ctx, [], notice, ct),
            FlowSteps.Day => await RenderDayResolvedAsync(s with { ListCursor = null }, ctx, null, notice, ct),
            _ => await ResolveAsync(s with { ListCursor = null }, ctx, [], notice, s.Step == FlowSteps.Location, null, ct),
        }, s);

    /// <summary>Слот обрано й пропозиція створена: <c>time → confirm</c> (єдиний шлях до <c>confirm</c>).</summary>
    public FlowResult ToConfirm(FlowState s, Guid proposalId) =>
        new(FlowResultCodes.Ok, s with
        {
            Step = FlowSteps.Confirm, ProposalId = proposalId, ListCursor = null, Version = s.Version + 1, ExpiresAt = clock.GetUtcNow() + opts.IdleTimeout,
        }, null);

    /// <summary>Термінальний стан зі збереженням вибору (completed|cancelled|expired|superseded); CAS за версією при <see cref="PersistAsync"/>.</summary>
    public FlowResult Terminate(FlowState s, string status, string? cancelReason) =>
        new(status == FlowStatuses.Cancelled ? FlowResultCodes.Cancelled : FlowResultCodes.Ok, Terminal(s, status, cancelReason), null);

    private FlowState Terminal(FlowState s, string status, string? reason) =>
        s with { Status = status, CancelReason = reason, Version = s.Version + 1, ExpiresAt = clock.GetUtcNow() };

    // ------------------------------------------------------------------ збереження

    /// <summary>
    /// Старт: витіснення активного + вставка. Перехід: CAS <c>WHERE version = @v AND status = 'active'</c>. false - програно (інший виконавець встиг):
    /// конвеєр відкочує транзакцію й відповідає <c>stale_step</c>.
    /// </summary>
    public async Task<bool> PersistAsync(FlowResult result, FlowState? before, CancellationToken ct)
    {
        if (result.Next is not { } next) return false;
        if (result.IsNew || before is null)
        {
            await store.CreateAsync(next, clock.GetUtcNow(), ct);
            return true;
        }
        return await store.TryAdvanceAsync(before, next, ct);
    }

    public Task<FlowState?> GetActiveAsync(Guid conversationId, CancellationToken ct) => store.GetActiveAsync(conversationId, ct);

    public Task<FlowState?> GetAsync(Guid flowId, Guid conversationId, CancellationToken ct) => store.GetAsync(flowId, conversationId, ct);

    public Task<bool> FinishAsync(Guid flowId, string status, string? cancelReason, CancellationToken ct) =>
        store.FinishAsync(flowId, status, cancelReason, clock.GetUtcNow(), ct);

    // ------------------------------------------------------------------ вибір кроку (автопропуск)

    private static FlowResult Invalid() => new(FlowResultCodes.Invalid, null, null);

    private static FlowResult Unavailable(FlowResult r, FlowState before) =>
        r with { Code = r.IsStep ? FlowResultCodes.OptionUnavailable : r.Code };

    private FlowResult Unavailable(Resolved r, FlowState before) => Unavailable(Seal(r, before), before);

    private FlowResult Seal(Resolved r, FlowState? before, bool isNew = false)
    {
        var next = r.State with { Version = before is null ? 0 : before.Version + 1, ExpiresAt = clock.GetUtcNow() + opts.IdleTimeout };
        if (r.Code == FlowResultCodes.NoServices)
            return new FlowResult(r.Code, next with { Status = FlowStatuses.Cancelled, CancelReason = FlowCancelReasons.NoServices }, null, isNew);
        return new FlowResult(r.Code, next, r.View, isNew);
    }

    private static FlowState Pin(FlowState s, FlowContext ctx) =>
        ctx.ChannelLocationId is { } channel
            ? s with { LocationId = channel, SkippedSteps = Add(Add(s.SkippedSteps, FlowSteps.LastLocation), FlowSteps.Location) }
            : s;

    private static IReadOnlyList<string> Add(IReadOnlyList<string> list, string step) => list.Contains(step) ? list : [.. list, step];

    /// <summary>Вибір цього кроку й усіх наступних очищається (разом із позначками автопропуску); заклад каналу повертає <see cref="Pin"/>.</summary>
    private static FlowState ClearFrom(FlowState s, string from)
    {
        var cleared = s with { Day = null, PreferredTime = null, CursorAt = null, ListCursor = null, ProposalId = null };
        return from switch
        {
            FlowSteps.Service => cleared with { ServiceId = null, SpecialistId = null, LocationId = null, SkippedSteps = [] },
            FlowSteps.Specialist => cleared with { SpecialistId = null, LocationId = null, SkippedSteps = [] },
            _ => cleared with { LocationId = null, SkippedSteps = s.SkippedSteps.Where(x => x == FlowSteps.Specialist).ToList() },
        };
    }

    /// <summary>
    /// Перший невирішений крок із автопропуском (§28.3): єдиний спеціаліст / єдиний заклад -> «Обрано: …». Послуга не пропускається.
    /// Нуль опцій: повернення на попередній крок із приміткою <see cref="FlowNotices.Unavailable"/>; порожні послуги -> <c>no_services</c>.
    /// </summary>
    private async Task<Resolved> ResolveAsync(
        FlowState s, FlowContext ctx, List<FlowChosen> chosen, string? notice, bool forceLocationList, string? after, CancellationToken ct)
    {
        s = Pin(s, ctx);
        for (var hop = 0; hop < MaxHops; hop++)
        {
            if (s.ServiceId is null)
            {
                var services = await catalog.ListBookableServicesAsync(s.SpecialistId, s.LocationId, ct);
                if (services.Count == 0)
                {
                    if (s.SpecialistId is not null || (s.LocationId is not null && ctx.ChannelLocationId is null))
                    {
                        s = Pin(ClearFrom(s, FlowSteps.Service), ctx); // префільний фільтр уже не придатний: без нього
                        notice ??= FlowNotices.Unavailable;
                        continue;
                    }
                    return new Resolved(FlowResultCodes.NoServices, s with { Step = FlowSteps.Service }, null);
                }
                var page = Page(services, x => x.Id.ToString(), after, opts.PageSize);
                var items = page.Items.Select(x => new FlowItem(FlowVerbs.Service, x.Name, ServiceId: x.Id)).ToList();
                return Done(s with { Step = FlowSteps.Service, ListCursor = page.Cursor }, items, ListNav(page, false), chosen, notice,
                    new FlowNames(null, null, null, null, null), page.MoreIsRestart);
            }
            if (s.SpecialistId is null)
            {
                var specialists = await catalog.ListSpecialistsForServiceAsync(s.ServiceId.Value, s.LocationId, ct);
                if (specialists.Count == 0)
                {
                    s = Pin(ClearFrom(s, FlowSteps.Service), ctx);
                    notice ??= FlowNotices.Unavailable;
                    after = null;
                    continue;
                }
                if (specialists.Count == 1)
                {
                    s = s with { SpecialistId = specialists[0].Id, SkippedSteps = Add(s.SkippedSteps, FlowSteps.Specialist) };
                    chosen.Add(new FlowChosen("specialist", specialists[0].Name, null));
                    continue;
                }
                var page = Page(specialists, x => x.Id.ToString(), after, opts.PageSize);
                var items = page.Items.Select(x => new FlowItem(FlowVerbs.Specialist, x.Name, SpecialistId: x.Id)).ToList();
                var names = await catalog.GetNamesAsync(s.ServiceId, null, null, ct);
                return Done(s with { Step = FlowSteps.Specialist, ListCursor = page.Cursor }, items, ListNav(page, true), chosen, notice, names, page.MoreIsRestart);
            }
            if (s.LocationId is null)
            {
                var locations = await catalog.ListLocationsForAsync(s.ServiceId.Value, s.SpecialistId.Value, ct);
                if (locations.Count == 0)
                {
                    s = Pin(ClearFrom(s, s.Skipped(FlowSteps.Specialist) ? FlowSteps.Service : FlowSteps.Specialist), ctx);
                    notice ??= FlowNotices.Unavailable;
                    after = null;
                    continue;
                }
                if (locations.Count == 1)
                {
                    s = s with
                    {
                        LocationId = locations[0].Id,
                        SkippedSteps = Add(Add(s.SkippedSteps, FlowSteps.LastLocation), FlowSteps.Location),
                    };
                    chosen.Add(new FlowChosen("location", locations[0].Name, locations[0].Address));
                    continue;
                }
                var names = await catalog.GetNamesAsync(s.ServiceId, s.SpecialistId, null, ct);
                if (!forceLocationList && s.SuggestedLastLocationId is { } suggested && !s.Skipped(FlowSteps.LastLocation)
                    && locations.FirstOrDefault(x => x.Id == suggested) is { } last)
                    return Done(s with { Step = FlowSteps.LastLocation, ListCursor = null },
                        [new FlowItem(FlowVerbs.Location, last.Name, LocationId: last.Id, Address: last.Address)], [FlowNav.OtherLocation, FlowNav.Back], chosen, notice,
                        names with { Location = last.Name, Address = last.Address, Timezone = last.Timezone });
                var page = Page(locations, x => x.Id.ToString(), after, opts.PageSize);
                var items = page.Items.Select(x => new FlowItem(FlowVerbs.Location, x.Name, LocationId: x.Id, Address: x.Address)).ToList();
                return Done(s with { Step = FlowSteps.Location, ListCursor = page.Cursor }, items, ListNav(page, true), chosen, notice, names, page.MoreIsRestart,
                    allLocations: locations);
            }
            return await RenderTimeResolvedAsync(s, ctx, chosen, notice, ct);
        }
        return new Resolved(FlowResultCodes.NoServices, s with { Step = FlowSteps.Service }, null);
    }

    private static Resolved Done(FlowState s, IReadOnlyList<FlowItem> items, IReadOnlyList<string> nav, List<FlowChosen> chosen, string? notice, FlowNames names,
        bool moreIsRestart = false, IReadOnlyList<FlowLocationItem>? allLocations = null) =>
        new(FlowResultCodes.Ok, s, new FlowView(s.Step, items, nav, chosen.ToList(), notice, names, moreIsRestart, AllLocations: allLocations));

    private static List<string> ListNav<T>(ListPage<T> page, bool canBack)
    {
        var nav = new List<string>();
        if (page.More || page.Multi) nav.Add(FlowNav.More);
        if (canBack) nav.Add(FlowNav.Back);
        nav.Add(FlowNav.Cancel);
        return nav;
    }

    // ------------------------------------------------------------------ пагінація списків (курсор = ключ останнього показаного)

    private sealed record ListPage<T>(IReadOnlyList<T> Items, bool More, bool Multi, string? Cursor)
    {
        /// <summary>Остання сторінка багатосторінкового списку: «Ще» стає «↩ На початок» (<c>fn:more</c> з порожнім курсором).</summary>
        public bool MoreIsRestart => !More && Multi;
    }

    /// <summary>Елементи ПІСЛЯ <paramref name="after"/> у стабільному порядку; зниклий елемент -> сторінка 1 (§28.4).</summary>
    private static ListPage<T> Page<T>(IReadOnlyList<T> all, Func<T, string> key, string? after, int size)
    {
        var start = 0;
        if (after is not null)
        {
            var at = -1;
            for (var i = 0; i < all.Count; i++)
                if (key(all[i]) == after) { at = i; break; }
            if (at >= 0 && at + 1 < all.Count) start = at + 1;
        }
        var items = all.Skip(start).Take(size).ToList();
        var more = start + items.Count < all.Count;
        return new ListPage<T>(items, more, all.Count > size, more ? key(items[^1]) : null);
    }

    // ------------------------------------------------------------------ час

    private Task<Resolved> RenderTimeAsync(FlowState s, FlowContext ctx, List<FlowChosen> chosen, string? notice, CancellationToken ct) =>
        RenderTimeResolvedAsync(s, ctx, chosen, notice, ct);

    private async Task<Resolved> RenderTimeResolvedAsync(FlowState s, FlowContext ctx, List<FlowChosen> chosen, string? notice, CancellationToken ct)
    {
        var (location, specialist, service) = (s.LocationId!.Value, s.SpecialistId!.Value, s.ServiceId!.Value);
        var names = await catalog.GetNamesAsync(service, specialist, location, ct);
        var tzId = names.Timezone ?? "UTC";
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        var now = clock.GetUtcNow();

        // нижня межа L (§28.4): курсор (строго >), інакше день + бажаний час − 60 хв, початок дня або now; не раніше now
        DateTimeOffset low;
        var strict = false;
        if (s.CursorAt is { } cursor) { low = cursor; strict = true; }
        else if (s.Day is { } day)
        {
            var local = day.ToDateTime(s.PreferredTime ?? TimeOnly.MinValue).AddMinutes(s.PreferredTime is null ? 0 : -60);
            low = SlotCalculator.ToOffset(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local), tz);
        }
        else low = now;
        if (low < now) { low = now; strict = false; }

        var today = SlotCalculator.LocalDate(now, tzId);
        var lastDay = Min(SlotCalculator.LocalDate(low, tzId).AddDays(opts.MaxScanDays - 1), today.AddDays(slots.Options.MaxDaysAhead));
        var found = new List<FreeSlot>();
        for (var d = SlotCalculator.LocalDate(low, tzId); d <= lastDay && found.Count <= opts.SlotButtons; d = d.AddDays(1))
        {
            var r = await slots.GetSlotsAsync(location, specialist, service, d, ct);
            if (!r.IsOk)
            {
                if (r.Error!.Code == "invalid_date") break;
                continue;
            }
            foreach (var slot in r.Value!.Where(x => x.SpecialistId == specialist).OrderBy(x => x.StartsAt))
            {
                if ((strict ? slot.StartsAt <= low : slot.StartsAt < low) || slot.StartsAt < now) continue;
                found.Add(slot);
                if (found.Count > opts.SlotButtons) break;
            }
        }
        var page = found.Take(opts.SlotButtons).ToList();
        var more = found.Count > opts.SlotButtons;
        var severalSpecialists = (await catalog.ListSpecialistsForServiceAsync(service, ctx.ChannelLocationId, ct)).Count > 1;

        var nav = new List<string>();
        if (more) nav.Add(FlowNav.More);
        nav.Add(FlowNav.OtherDay);
        if (severalSpecialists) nav.Add(FlowNav.OtherSpecialist);
        nav.Add(FlowNav.Back);
        if (page.Count + nav.Count < 10) nav.Add(FlowNav.Cancel);

        var next = s with { Step = FlowSteps.Time, ListCursor = null, CursorAt = page.Count > 0 ? page[^1].StartsAt : s.CursorAt };
        var items = page.Select(x => new FlowItem(FlowVerbs.Time, "", StartsAt: x.StartsAt)).ToList();
        return new Resolved(FlowResultCodes.Ok, next, new FlowView(FlowSteps.Time, items, nav, chosen.ToList(), notice, names, NoSlots: page.Count == 0));
    }

    private Task<Resolved> RenderDayAsync(FlowState s, FlowContext ctx, string? after, string? notice, CancellationToken ct) =>
        RenderDayResolvedAsync(s, ctx, after, notice, ct);

    private async Task<Resolved> RenderDayResolvedAsync(FlowState s, FlowContext ctx, string? after, string? notice, CancellationToken ct)
    {
        var (location, specialist, service) = (s.LocationId!.Value, s.SpecialistId!.Value, s.ServiceId!.Value);
        var names = await catalog.GetNamesAsync(service, specialist, location, ct);
        var tzId = names.Timezone ?? "UTC";
        var now = clock.GetUtcNow();
        var today = SlotCalculator.LocalDate(now, tzId);
        var maxDate = today.AddDays(slots.Options.MaxDaysAhead);

        var start = after is not null && DateOnly.TryParseExact(after, "yyyy-MM-dd", out var seen) ? seen.AddDays(1)
            : s.CursorAt is { } c ? SlotCalculator.LocalDate(c, tzId).AddDays(1) : today;
        if (start < today) start = today;
        var scanEnd = Min(start.AddDays(opts.MaxScanDays - 1), maxDate);

        var found = new List<DateOnly>();
        var lastScanned = start.AddDays(-1);
        for (var d = start; d <= scanEnd && found.Count <= opts.DayPageSize; d = d.AddDays(1))
        {
            lastScanned = d;
            var r = await slots.GetSlotsAsync(location, specialist, service, d, ct);
            if (!r.IsOk)
            {
                if (r.Error!.Code == "invalid_date") { lastScanned = maxDate; break; }
                continue;
            }
            if (r.Value!.Any(x => x.SpecialistId == specialist && x.StartsAt >= now)) found.Add(d);
        }
        var page = found.Take(opts.DayPageSize).ToList();
        var moreFound = found.Count > opts.DayPageSize;
        var more = moreFound || lastScanned < maxDate;
        var cursor = !more ? null : moreFound ? page[^1].ToString("yyyy-MM-dd") : lastScanned.ToString("yyyy-MM-dd");

        var nav = new List<string>();
        if (more) nav.Add(FlowNav.More);
        nav.Add(FlowNav.Back);
        nav.Add(FlowNav.Cancel);
        var items = page.Select(x => new FlowItem(FlowVerbs.Day, "", Date: x.ToString("yyyy-MM-dd"))).ToList();
        return new Resolved(FlowResultCodes.Ok, s with { Step = FlowSteps.Day, ListCursor = cursor },
            new FlowView(FlowSteps.Day, items, nav, [], notice, names, NoDays: page.Count == 0 && !more));
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a <= b ? a : b;
}

public static class ChatFlowServiceExtensions
{
    /// <summary>Реєструє <see cref="ChatBookingFlowService"/>. Потребує <c>AddBeautyPublicBookingApplication</c> (<see cref="OnlineBookingCore"/>), портів <see cref="IChatFlowStore"/> / <see cref="IChatFlowCatalog"/> (Infrastructure) і <see cref="ChatFlowOptions"/>.</summary>
    public static IServiceCollection AddBeautyChatFlowApplication(this IServiceCollection services)
    {
        services.AddScoped<ChatBookingFlowService>();
        return services;
    }
}
