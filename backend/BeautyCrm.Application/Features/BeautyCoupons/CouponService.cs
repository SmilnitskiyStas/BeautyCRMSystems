using System.Collections.Concurrent;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyCoupons;

/// <summary>
/// Лічильники спроб у пам'яті (на процес): ковзне вікно. Ключ задає викликач (користувач / IP / розмова).
/// </summary>
public sealed class CouponAttemptLimiter(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    private Queue<DateTimeOffset> Prune(string key, TimeSpan window, DateTimeOffset now)
    {
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (q)
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
        if (_hits.Count > 20_000)
            foreach (var kv in _hits.ToArray().Where(kv => { lock (kv.Value) return kv.Value.Count == 0; }).Take(5_000)) _hits.TryRemove(kv.Key, out _);
        return q;
    }

    /// <summary>Чи досягнуто ліміт у вікні (без запису).</summary>
    public bool IsBlocked(string key, int limit, TimeSpan window)
    {
        var q = Prune(key, window, clock.GetUtcNow());
        lock (q) return q.Count >= limit;
    }

    public void Record(string key, TimeSpan window)
    {
        var now = clock.GetUtcNow();
        var q = Prune(key, window, now);
        lock (q) q.Enqueue(now);
    }

    /// <summary>Рахує звернення; false, якщо понад ліміт (звернення не додається).</summary>
    public bool TryHit(string key, int limit, TimeSpan window)
    {
        var now = clock.GetUtcNow();
        var q = Prune(key, window, now);
        lock (q)
        {
            if (q.Count >= limit) return false;
            q.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// Купони (§26.6): випуск персональних кодів, загальні коди, перевірка з єдиною відповіддю «invalid» назовні (захист від підбору),
/// повернення. Погашення виконується в транзакції створення запису (store.AddAppointmentAsync), а не тут. Коди не логуються.
/// </summary>
public sealed class CouponService(
    ICouponStore store, IBookingStore bookings, CouponOptions options, CouponAttemptLimiter limiter, TimeProvider clock,
    ILogger<CouponService>? log = null)
{
    public const string Invalid = "coupon_invalid";
    public const string Locked = "coupon_locked";

    private enum Reason { Ok, InvalidFormat, NotFound, Revoked, Expired, NotStarted, Exhausted, ForeignClient, AlreadyUsed, PromotionInactive }

    // ---------- випуск ----------

    /// <summary>Персональний код для одержувача кампанії. Повний код - лише в <see cref="IssuedCoupon"/> (для повідомлення).</summary>
    public async Task<Result<IssuedCoupon>> IssueAsync(IssueCouponRequest req, CancellationToken ct)
    {
        if (req.ValidTo <= req.ValidFrom) return Error.Validation("invalid_period", "validTo must be after validFrom.");
        var access = await store.GetPromotionAccessAsync(req.PromotionId, ct);
        if (access is null) return Error.NotFound("promotion_not_found", "Promotion not found.");
        if (access != "code_only") return Error.Validation("promotion_not_code_only", "Coupons can be issued only for code_only promotions.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var code = CouponCodes.GeneratePersonal();
            var c = new NewPersonalCoupon(req.CampaignId, req.ClientId, req.PromotionId, CouponCodes.Hash(code), CouponCodes.Hint(code),
                req.ValidFrom, req.ValidTo, req.CreatedByUserId);
            if (await store.InsertPersonalAsync(c, ct)) return new IssuedCoupon(c.Id, code);
        }
        throw new InvalidOperationException("coupon_code_generation_failed");
    }

    // ---------- загальні коди ----------

    public Task<IReadOnlyList<SharedCouponDto>> ListSharedAsync(Guid? promotionId, CancellationToken ct) => store.ListSharedAsync(promotionId, ct);

    public async Task<Result<SharedCouponDto>> CreateSharedAsync(CreateSharedCouponRequest req, Guid? userId, CancellationToken ct)
    {
        if (WriteLimited(userId) is { } limited) return limited;
        if (!CouponCodes.IsValidSharedInput(req.Code)) return Error.Validation("invalid_code", "Code must be 6-20 characters A-Z, a-z, 0-9.");
        if (IsBlockedCode(req.Code!)) return Error.Validation("invalid_code", "This code is too easy to guess; choose a less obvious one.");
        if (req.ValidFrom is not { } from || req.ValidTo is not { } to || to <= from)
            return Error.Validation("invalid_period", "validFrom and validTo are required and validTo must be after validFrom.");
        if (req.MaxUses is { } m && (m < 1 || m > 100_000)) return Error.Validation("invalid_max_uses", "maxUses must be 1..100000.");
        if (req.PromotionId is not { } pid) return Error.Validation("promotion_required", "promotionId is required.");
        var access = await store.GetPromotionAccessAsync(pid, ct);
        if (access is null) return Error.NotFound("promotion_not_found", "Promotion not found.");
        if (access != "code_only") return Error.Validation("promotion_not_code_only", "Coupons can be created only for code_only promotions.");

        var code = req.Code!.ToUpperInvariant();
        var created = await store.CreateSharedAsync(new NewSharedCoupon(pid, code, CouponCodes.Hash(code), CouponCodes.Hint(code), from, to, req.MaxUses, userId), ct);
        if (created is null) return Error.Conflict("coupon_code_taken", "This code is already in use.");
        log?.LogInformation("coupon_created coupon={Coupon} user={User}", created.Id, userId);
        return created;
    }

    /// <summary>TASK-790 (M5): код з блок-списку (за літерами без цифр: SALE, SALE10, 2024SALE).</summary>
    private bool IsBlockedCode(string code)
    {
        var upper = code.Trim().ToUpperInvariant();
        return options.BlockedCodes.Contains(upper) || options.BlockedCodes.Contains(new string(upper.Where(char.IsAsciiLetter).ToArray()));
    }

    /// <summary>TASK-790 (L7): rate limit запису загальних кодів на користувача.</summary>
    private Error? WriteLimited(Guid? userId) =>
        limiter.TryHit($"coupon-write:{userId}", options.WritePerUserPerMinute, TimeSpan.FromMinutes(1))
            ? null : Error.TooMany("rate_limited", "Too many requests. Try again later.");

    public async Task<Result<bool>> RevokeAsync(Guid id, Guid? userId, CancellationToken ct)
    {
        if (WriteLimited(userId) is { } limited) return limited;
        if (!await store.RevokeSharedAsync(id, ct)) return Error.NotFound("coupon_not_found", "Coupon not found.");
        log?.LogInformation("coupon_revoked coupon={Coupon} user={User}", id, userId);
        return true;
    }

    public async Task<Result<IReadOnlyList<ClientCouponDto>>> ListForClientAsync(Guid clientId, CancellationToken ct) =>
        await store.ListForClientAsync(clientId, ct) is { } list
            ? Result<IReadOnlyList<ClientCouponDto>>.Ok(list)
            : Error.NotFound("client_not_found", "Client not found.");

    // ---------- повернення ----------

    /// <summary>Ідемпотентне повернення купона запису (скасування / збій оплати); no_show і completed не повертають.</summary>
    public async Task<int> ReleaseAsync(Guid appointmentId, string reason, CancellationToken ct)
    {
        var n = await store.ReleaseForAppointmentAsync(appointmentId, reason, clock.GetUtcNow(), ct);
        if (n > 0) log?.LogInformation("coupon_released appointment={Appointment} reason={Reason}", appointmentId, reason);
        return n;
    }

    // ---------- перевірка ----------

    /// <summary>
    /// Validate: знаходить купон і перевіряє чинність для клієнта. Назовні (чат, публічний) - завжди <c>coupon_invalid</c> (409);
    /// staff отримує деталізований код. Невдалі спроби чату рахуються на розмову (понад ліміт - 429 coupon_locked), публічні й staff
    /// звернення - на IP / користувача (429 rate_limited).
    /// </summary>
    public async Task<Result<CouponCandidate>> ResolveAsync(CouponUse use, Guid? clientId, DateTimeOffset now, CancellationToken ct)
    {
        // TASK-790 (M5): IPv6 публічного ліміту - за підмережею /64 (одна «адреса» не множиться на 2^64 варіантів)
        var key = use.RateKey is null ? null : $"{use.Scope}:{(use.Scope == CouponScope.Public ? ClientIpKey.Normalize(use.RateKey) : use.RateKey)}";
        if (key is not null)
        {
            switch (use.Scope)
            {
                case CouponScope.Chat when limiter.IsBlocked(key, options.ChatInvalidPerConversationPerHour, TimeSpan.FromHours(1)):
                    log?.LogInformation("coupon_check scope=chat result={Result}", Locked);
                    return Error.TooMany(Locked, "Too many invalid codes. Try again later.");
                case CouponScope.Public when !limiter.TryHit(key, options.PublicCheckPerIpPer10Min, TimeSpan.FromMinutes(10)):
                    log?.LogInformation("coupon_check scope=public result=rate_limited");
                    return Error.TooMany("rate_limited", "Too many requests. Try again later.");
                case CouponScope.Staff when !limiter.TryHit(key, options.StaffCheckPerUserPerMinute, TimeSpan.FromMinutes(1)):
                    log?.LogInformation("coupon_check scope=staff result=rate_limited");
                    return Error.TooMany("rate_limited", "Too many requests. Try again later.");
            }
        }

        CouponLookup? lookup = null;
        var reason = Reason.NotFound;
        if (use.CouponId is { } id) lookup = await store.FindByIdAsync(id, ct);
        else if (!CouponCodes.IsPlausible(CouponCodes.Normalize(use.Code))) reason = Reason.InvalidFormat;
        else lookup = await store.FindByHashAsync(CouponCodes.Hash(use.Code), ct);

        if (lookup is not null) reason = await EvaluateAsync(lookup, use.Scope, clientId, now, ct);
        if (reason == Reason.Ok)
        {
            log?.LogInformation("coupon_check scope={Scope} result=ok", use.Scope.ToString().ToLowerInvariant());
            var c = lookup!.Coupon;
            var p = lookup.Promotion;
            var rule = p with
            {
                StartsAt = Max(p.StartsAt, c.ValidFrom), EndsAt = Min(p.EndsAt, c.ValidTo),
                IsActive = p.IsActive && c.Status == "active" && (c.MaxUses is null || c.UsesCount < c.MaxUses),
            };
            return new CouponCandidate(c.Id, c.Kind, c.CodeHint, p.Name, use.CampaignId ?? c.CampaignId, rule);
        }

        if (use.Scope == CouponScope.Chat && key is not null) limiter.Record(key, TimeSpan.FromHours(1));
        var error = ToError(reason, use.Scope);
        log?.LogInformation("coupon_check scope={Scope} result={Result}", use.Scope.ToString().ToLowerInvariant(), error.Code);
        return error;
    }

    /// <summary>Перевірка коду проти ціни послуги (staff <c>POST /coupons/check</c>, публічна перевірка).</summary>
    public async Task<Result<CouponCheckResult>> CheckAsync(CouponUse use, Guid locationId, Guid serviceId, Guid? clientId, CancellationToken ct)
    {
        if (await bookings.GetServiceAsync(serviceId, ct) is not { IsActive: true }) return Error.NotFound("service_not_found", "Service not found.");
        if (await bookings.GetLocationAsync(locationId, ct) is not { IsActive: true }) return Error.NotFound("location_not_found", "Location not found.");
        var price = await bookings.GetPriceAsync(serviceId, locationId, ct);
        if (price is null) return Error.Validation("price_not_set", "Service has no price for this location.");

        var now = clock.GetUtcNow();
        var resolved = await ResolveAsync(use, clientId, now, ct);
        if (!resolved.IsOk) return resolved.Error!;
        var q = PromotionPricing.QuoteWithCoupon(price.Value, locationId, serviceId, now, await bookings.GetPromotionRulesAsync(ct), resolved.Value);
        return new CouponCheckResult(q.Outcome, q.Quote.Original, q.Quote.Final, resolved.Value!.CouponId, resolved.Value.PromotionName);
    }

    private async Task<Reason> EvaluateAsync(CouponLookup l, CouponScope scope, Guid? clientId, DateTimeOffset now, CancellationToken ct)
    {
        var c = l.Coupon;
        if (scope == CouponScope.Public && c.Kind == "personal") return Reason.NotFound; // публічний шлях: лише загальні
        if (c.Status != "active") return Reason.Revoked;
        if (c.Kind == "personal" && (clientId is null || c.ClientId != clientId)) return Reason.ForeignClient;
        if (now < c.ValidFrom) return Reason.NotStarted;
        if (now >= c.ValidTo) return Reason.Expired;
        if (c.MaxUses is { } max && c.UsesCount >= max) return Reason.Exhausted;
        if (clientId is { } cid && await store.HasActiveRedemptionAsync(c.Id, cid, ct)) return Reason.AlreadyUsed;
        if (!l.Promotion.IsActive) return Reason.PromotionInactive;
        return Reason.Ok;
    }

    private static Error ToError(Reason r, CouponScope scope)
    {
        if (scope != CouponScope.Staff) return Error.Conflict(Invalid, "The code is not valid.");
        return r switch
        {
            Reason.InvalidFormat => Error.Validation("invalid_coupon_format", "The code has an invalid format."),
            Reason.Revoked => Error.Conflict("coupon_revoked", "The coupon is revoked."),
            Reason.Expired => Error.Conflict("coupon_expired", "The coupon has expired."),
            Reason.NotStarted => Error.Conflict("coupon_not_started", "The coupon is not valid yet."),
            Reason.Exhausted => Error.Conflict("coupon_exhausted", "The coupon has no uses left."),
            Reason.ForeignClient => Error.Conflict("coupon_foreign_client", "The coupon belongs to another client."),
            Reason.AlreadyUsed => Error.Conflict("coupon_already_used", "The client has already used this coupon."),
            Reason.PromotionInactive => Error.Conflict("promotion_inactive", "The promotion is not active."),
            _ => Error.Conflict("coupon_not_found", "Coupon not found."),
        };
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x > b ? x : b;
    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x < b ? x : b;
}
