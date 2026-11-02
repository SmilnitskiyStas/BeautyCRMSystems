using System.Text.Json;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.AI.Beauty.Providers;
using BeautyCrm.Infrastructure.AI.Secrets;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Infrastructure.AI.Beauty.Settings;

/// <summary>
/// Налаштування ШІ tenant: ролі моделей, режим, промпт-додаток, ліміти, ключі провайдерів, використання, платформні квоти
/// (контракт §20.5, ADR-010). tenant береться з автентифікованого <see cref="Actor"/> (JWT) і звіряється зі scope; id tenant
/// у запитах немає. Ключ: шифрується <see cref="IAiSecretProtector"/> (AAD tenant+provider), ніколи не повертається, не логується
/// й не потрапляє в аудит. Очікувані бізнес-помилки - <see cref="Result{T}"/>; коди 502/503/429 мапить API-шар за кодом.
/// </summary>
public sealed partial class AiSettingsService(
    BeautyDbContext db, TenantContext tenant, AiModelCatalog catalog, AiProviderRegistry providers, IAiSecretProtector protector,
    AiPlatformOptions platform, AiTenantRuntime runtime, TimeProvider clock, ILogger<AiSettingsService> log)
{
    public const string ErrSecretsUnavailable = "ai_secrets_unavailable";
    public const string ErrProviderUnavailable = "provider_unavailable";
    public const string ErrKeyAffectsOtherRoles = "key_affects_other_roles";
    public const string ErrForbiddenRole = "forbidden_role";

    [GeneratedRegex(@"^[a-z][a-z0-9_]{1,31}$")]
    private static partial Regex ProviderIdRx();

    [GeneratedRegex(@"^[\x21-\x7E]{20,512}$")]
    private static partial Regex KeyShapeRx();

    private static readonly string[] AllRoles = [AiRoles.Chat, AiRoles.Analytics];

    private static Error V(string code, string message) => Error.Validation(code, message);

    private AiSettingsAuditRow Audit(Guid tenantId, Guid? userId, string action, string? provider, IEnumerable<string> fields)
    {
        var row = new AiSettingsAuditRow
        {
            Id = Guid.NewGuid(), TenantId = tenantId, UserId = userId, Action = action, Provider = provider, ChangedFields = fields.ToArray(),
        };
        db.AiSettingsAudit.Add(row);
        return row;
    }

    /// <summary>Структурований лог без значень ключа, last4 і тексту промпту: лише імена полів і довжина промпту.</summary>
    private void LogChanged(Guid tenantId, Guid? userId, string action, string? provider, IEnumerable<string> fields, int promptLength) =>
        log.LogInformation("ai_settings_changed {TenantId} {UserId} {Action} {Provider} {Fields} {PromptLength}",
            tenantId, userId, action, provider, string.Join(",", fields), promptLength);

    private void EnsureScope(Actor actor) => TenantGuard.Ensure(tenant, actor.TenantId);

    // ------------------------------------------------------------------ читання

    public async Task<Result<AiSettingsDto>> GetAsync(Actor actor, CancellationToken ct)
    {
        EnsureScope(actor);
        return await BuildDtoAsync(actor, null, ct);
    }

    private async Task<AiSettingsDto> BuildDtoAsync(Actor actor, IReadOnlyList<AiWarningDto>? warnings, CancellationToken ct)
    {
        var row = await db.AiSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var eff = await runtime.GetEffectiveAsync(actor.TenantId, ct);
        var status = await runtime.GetStatusAsync(actor.TenantId, ct);

        AiRoleDto Role(string role)
        {
            var sel = role == AiRoles.Chat ? eff.Chat : eff.Analytics;
            var r = runtime.ResolveRole(eff, role);
            return r is null ? new AiRoleDto(sel.Provider, sel.Model, "", true) : new AiRoleDto(r.Provider, r.Model, r.Tier, r.Fallback);
        }

        var enabledFlag = eff.PlatformEnabled ?? platform.EnabledByDefault;
        var tokenQuota = eff.PlatformTokenQuota ?? platform.DefaultMonthlyTokenQuota;
        var requestQuota = eff.PlatformRequestQuota ?? platform.DefaultMonthlyRequestQuota;
        var quota = new AiPlatformQuotaDto(enabledFlag && tokenQuota > 0 && requestQuota > 0,
            tokenQuota > 0 ? tokenQuota : null, requestQuota > 0 ? requestQuota : null);

        IReadOnlyList<AiKeyInfoDto>? keys = null;
        if (actor.Role == Roles.Owner) keys = await BuildKeysAsync(eff, ct);

        var isDefault = string.IsNullOrWhiteSpace(eff.PromptAddendum);
        return new AiSettingsDto(
            eff.KeySource, Role(AiRoles.Chat), Role(AiRoles.Analytics), AiModes.ToDb(eff.Mode),
            isDefault ? AiPromptDefaults.Addendum : eff.PromptAddendum!, isDefault, AiPromptDefaults.Addendum, AiPromptAddendum.MaxLength,
            new AiLimitsDto(eff.MonthlyTokenLimit, eff.MonthlyRequestLimit, eff.MaxOutputTokens), quota,
            new AiStatusDto(ToDto(status.Chat), ToDto(status.Analytics)), keys,
            row?.UpdatedAt, await UserNameAsync(row?.UpdatedByUserId, ct), warnings);
    }

    private static AiRoleStatusDto ToDto(AiRoleStatus s) => new(s.State, s.Reason);

    private async Task<string?> UserNameAsync(Guid? userId, CancellationToken ct) =>
        userId is { } id ? await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync(ct) : null;

    private static List<string> UsedBy(AiEffectiveSettings eff, string provider) =>
        AllRoles.Where(r => (r == AiRoles.Chat ? eff.Chat : eff.Analytics).Provider == provider).ToList();

    private async Task<IReadOnlyList<AiKeyInfoDto>> BuildKeysAsync(AiEffectiveSettings eff, CancellationToken ct)
    {
        var rows = await db.AiProviderKeys.AsNoTracking().ToListAsync(ct);
        var ids = rows.Select(r => r.UpdatedByUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var list = new List<AiKeyInfoDto>();
        foreach (var p in catalog.Providers.Where(p => providers.IsAvailable(p.Id)))
        {
            var k = rows.FirstOrDefault(r => r.Provider == p.Id);
            list.Add(new AiKeyInfoDto(p.Id, k is not null, k?.KeyLast4, k?.UpdatedAt,
                k is not null && names.TryGetValue(k.UpdatedByUserId, out var n) ? n : null, UsedBy(eff, p.Id)));
        }
        return list;
    }

    // ------------------------------------------------------------------ запис: налаштування

    public async Task<Result<AiSettingsDto>> UpdateAsync(Actor actor, UpdateAiSettingsRequest req, CancellationToken ct)
    {
        EnsureScope(actor);

        // порядок контракту §20.5: unknown_provider -> provider_not_available -> unknown_model -> model_provider_mismatch
        var chat = req.Chat ?? new AiRoleInput(null, null);
        var analytics = req.Analytics ?? new AiRoleInput(null, null);
        if (catalog.Validate(chat.Provider, chat.Model, providers) is { } e1) return V(e1, $"chat: {e1}.");
        if (catalog.Validate(analytics.Provider, analytics.Model, providers) is { } e2) return V(e2, $"analytics: {e2}.");

        var mode = req.Mode?.Trim().ToLowerInvariant();
        if (mode is not ("suggest" or "confirm" or "auto")) return V("invalid_mode", "mode must be suggest, confirm or auto.");

        var l = req.Limits;
        if (l is null || l.MaxOutputTokensPerRequest is not (>= 64 and <= 8192)
            || l.MonthlyTokenLimit is < 1 or > 100_000_000_000L || l.MonthlyRequestLimit is < 1 or > 10_000_000)
            return V("invalid_limits", "limits are out of range.");

        var prompt = AiPromptAddendum.Validate(req.PromptAddendum);
        if (!prompt.Ok) return V(prompt.ErrorCode!, "promptAddendum is not acceptable.");

        string? keySource = null;
        if (req.KeySource is not null)
        {
            keySource = req.KeySource.Trim().ToLowerInvariant();
            if (keySource is not (AiTenantRuntime.KeySourceOwn or AiTenantRuntime.KeySourcePlatform))
                return V("invalid_key_source", "keySource must be own or platform.");
        }

        var warnings = prompt.Warnings.Select(w => new AiWarningDto(w.Code, w.Message)).ToList();
        return await WriteWithRetryAsync<AiSettingsDto>(async () =>
        {
            var row = await db.AiSettings.FirstOrDefaultAsync(ct);
            var isNew = row is null;
            var requested = AiModes.Parse(mode);
            if (AiModes.AutoRequiresAcknowledgement(isNew ? AiMode.Confirm : AiModes.Parse(row!.Mode), requested, req.AcknowledgeAutoMode == true))
                return V("auto_mode_ack_required", "Switching to auto mode requires acknowledgeAutoMode=true.");
            if (keySource is not null && keySource != (row?.KeySource ?? AiTenantRuntime.KeySourcePlatform) && actor.Role != Roles.Owner)
                return Error.Forbidden(ErrForbiddenRole, "Only the owner can change the key source.");

            row ??= NewRow(actor.TenantId);
            var fields = new List<string>();
            void Chg<T>(string name, T cur, T val, Action set)
            {
                if (EqualityComparer<T>.Default.Equals(cur, val)) return;
                set();
                fields.Add(name);
            }

            Chg("chat_provider", row.ChatProvider, chat.Provider!, () => row.ChatProvider = chat.Provider!);
            Chg("chat_model", row.ChatModel, chat.Model!, () => row.ChatModel = chat.Model!);
            Chg("analytics_provider", row.AnalyticsProvider, analytics.Provider!, () => row.AnalyticsProvider = analytics.Provider!);
            Chg("analytics_model", row.AnalyticsModel, analytics.Model!, () => row.AnalyticsModel = analytics.Model!);
            Chg("mode", row.Mode, AiModes.ToDb(requested), () => row.Mode = AiModes.ToDb(requested));
            Chg("monthly_token_limit", row.MonthlyTokenLimit, l.MonthlyTokenLimit, () => row.MonthlyTokenLimit = l.MonthlyTokenLimit);
            Chg("monthly_request_limit", row.MonthlyRequestLimit, (int?)l.MonthlyRequestLimit, () => row.MonthlyRequestLimit = (int?)l.MonthlyRequestLimit);
            Chg("max_output_tokens", row.MaxOutputTokens, l.MaxOutputTokensPerRequest!.Value, () => row.MaxOutputTokens = l.MaxOutputTokensPerRequest!.Value);

            var promptChanged = row.PromptAddendum != prompt.StoredValue;
            var promptReset = promptChanged && prompt.StoredValue is null;
            if (promptChanged) row.PromptAddendum = prompt.StoredValue;
            if (promptChanged && !promptReset) fields.Add("prompt_addendum");

            var sourceChanged = keySource is not null && row.KeySource != keySource;
            if (sourceChanged) row.KeySource = keySource!;

            if (!isNew && fields.Count == 0 && !promptChanged && !sourceChanged) return await BuildDtoAsync(actor, warnings, ct);
            row.UpdatedByUserId = actor.UserId;
            var promptLength = prompt.StoredValue is null ? 0 : AiPromptAddendum.CodePointLength(prompt.StoredValue);
            if (fields.Count > 0) Audit(actor.TenantId, actor.UserId, "settings_updated", null, fields);
            if (promptReset) Audit(actor.TenantId, actor.UserId, "prompt_reset", null, ["prompt_addendum"]);
            if (sourceChanged) Audit(actor.TenantId, actor.UserId, "key_source_changed", null, ["key_source"]);
            await db.SaveChangesAsync(ct);

            if (fields.Count > 0) LogChanged(actor.TenantId, actor.UserId, "settings_updated", null, fields, promptLength);
            if (promptReset) LogChanged(actor.TenantId, actor.UserId, "prompt_reset", null, ["prompt_addendum"], 0);
            if (sourceChanged) LogChanged(actor.TenantId, actor.UserId, "key_source_changed", null, ["key_source"], 0);
            return await BuildDtoAsync(actor, warnings, ct);
        });
    }

    public async Task<Result<AiSettingsDto>> SetKeySourceAsync(Actor actor, AiKeySourceRequest req, CancellationToken ct)
    {
        EnsureScope(actor);
        var value = req.KeySource?.Trim().ToLowerInvariant();
        if (value is not (AiTenantRuntime.KeySourceOwn or AiTenantRuntime.KeySourcePlatform))
            return V("invalid_key_source", "keySource must be own or platform.");
        return await WriteWithRetryAsync<AiSettingsDto>(async () =>
        {
            var row = await db.AiSettings.FirstOrDefaultAsync(ct);
            if (row is not null && row.KeySource == value) return await BuildDtoAsync(actor, null, ct);
            if (row is null && value == AiTenantRuntime.KeySourcePlatform) return await BuildDtoAsync(actor, null, ct); // дефолт
            row ??= NewRow(actor.TenantId);
            row.KeySource = value;
            row.UpdatedByUserId = actor.UserId;
            Audit(actor.TenantId, actor.UserId, "key_source_changed", null, ["key_source"]);
            await db.SaveChangesAsync(ct);
            LogChanged(actor.TenantId, actor.UserId, "key_source_changed", null, ["key_source"], 0);
            return await BuildDtoAsync(actor, null, ct);
        });
    }

    // ------------------------------------------------------------------ ключі

    /// <summary>unknown_provider / provider_not_available (порядок §20.5). Null = провайдер у каталозі (й доступний, якщо потрібно).</summary>
    private Error? ProviderError(string provider, bool requireAvailable)
    {
        if (!ProviderIdRx().IsMatch(provider ?? "") || catalog.FindProvider(provider) is null)
            return V(AiModelCatalog.ErrUnknownProvider, "Unknown provider.");
        if (requireAvailable && !providers.IsAvailable(provider))
            return V(AiModelCatalog.ErrProviderNotAvailable, "Provider is not available yet.");
        return null;
    }

    private static bool KeyFormatOk(string provider, string key) =>
        KeyShapeRx().IsMatch(key) && (provider != AnthropicAiProvider.ProviderId || key.StartsWith("sk-ant-", StringComparison.Ordinal));

    private static Error BadKeyFormat() => V("invalid_key_format", "API key format is not valid for this provider.");

    private static Error SecretsUnavailable() => Error.Conflict(ErrSecretsUnavailable, "Key storage is not configured.");

    /// <summary>Сценарії поза forRole, яким заміна/видалення ключа змінить роботу (діє лише при keySource=own).</summary>
    private static List<string> AffectedRoles(AiEffectiveSettings eff, string provider, string? forRole) =>
        eff.KeySource != AiTenantRuntime.KeySourceOwn
            ? []
            : UsedBy(eff, provider).Where(r => forRole is null || r != forRole).ToList();

    private Error AffectsOthers(List<string> roles) =>
        Error.ConflictWith(ErrKeyAffectsOtherRoles, "Other scenarios depend on this key; set acknowledgeAffectedRoles=true to proceed.", roles);

    public async Task<Result<AiKeySavedDto>> SaveKeyAsync(Actor actor, string provider, AiKeySaveRequest req, CancellationToken ct)
    {
        EnsureScope(actor);
        if (ProviderError(provider, requireAvailable: true) is { } pe) return pe;
        if (req.ForRole is not null && !AiRoles.IsValid(req.ForRole)) return V("invalid_role", "forRole must be chat or analytics.");
        var key = (req.ApiKey ?? "").Trim();
        if (!KeyFormatOk(provider, key)) return BadKeyFormat();
        if (protector.State != AiSecretsState.Ready) return SecretsUnavailable();

        var eff = await runtime.GetEffectiveAsync(actor.TenantId, ct);
        var ack = req.AcknowledgeAffectedRoles == true;
        var peek = await db.AiProviderKeys.AsNoTracking().AnyAsync(k => k.Provider == provider, ct);
        if (peek && !ack && AffectedRoles(eff, provider, req.ForRole) is { Count: > 0 } early) return AffectsOthers(early);

        if (req.Verify ?? true)
        {
            var check = await CheckAsync(provider, new AiSecret(key), VerifyModel(provider, eff), ct);
            if (!check.Ok)
                return check.Reason is "key_rejected" or "insufficient_permissions"
                    ? V("key_rejected", "The provider rejected the key.")
                    : Error.Conflict(ErrProviderUnavailable, "The provider could not verify the key. The key was not saved.");
        }

        string encrypted;
        try
        {
            encrypted = protector.Protect(key, actor.TenantId, provider);
        }
        catch (AiSecretException ex) when (ex.Error == AiSecretError.InvalidArgument)
        {
            return BadKeyFormat();
        }
        catch (AiSecretException)
        {
            return SecretsUnavailable();
        }
        var kid = protector.TryGetKeyId(encrypted)!;
        var last4 = key[^4..];

        return await WriteWithRetryAsync<AiKeySavedDto>(async () =>
        {
            var existing = await db.AiProviderKeys.FirstOrDefaultAsync(k => k.Provider == provider, ct);
            if (existing is not null && !ack && AffectedRoles(eff, provider, req.ForRole) is { Count: > 0 } affected) return AffectsOthers(affected);

            var action = existing is null ? "key_set" : "key_replaced";
            if (existing is null)
            {
                existing = new AiProviderKey { Id = Guid.NewGuid(), TenantId = actor.TenantId, Provider = provider, CreatedByUserId = actor.UserId };
                db.AiProviderKeys.Add(existing);
            }
            existing.KeyEncrypted = encrypted;
            existing.KeyKid = kid;
            existing.KeyLast4 = last4;
            existing.UpdatedByUserId = actor.UserId;
            Audit(actor.TenantId, actor.UserId, action, provider, []);

            var sourceChanged = false;
            if (req.Activate ?? true)
            {
                var row = await db.AiSettings.FirstOrDefaultAsync(ct) ?? NewRow(actor.TenantId);
                if (row.KeySource != AiTenantRuntime.KeySourceOwn)
                {
                    row.KeySource = AiTenantRuntime.KeySourceOwn;
                    row.UpdatedByUserId = actor.UserId;
                    sourceChanged = true;
                    Audit(actor.TenantId, actor.UserId, "key_source_changed", null, ["key_source"]);
                }
            }
            await db.SaveChangesAsync(ct);
            LogChanged(actor.TenantId, actor.UserId, action, provider, [], 0);
            if (sourceChanged) LogChanged(actor.TenantId, actor.UserId, "key_source_changed", null, ["key_source"], 0);

            var source = sourceChanged ? AiTenantRuntime.KeySourceOwn : eff.KeySource;
            return new AiKeySavedDto(provider, true, last4, existing.UpdatedAt, source, UsedBy(eff, provider));
        });
    }

    public async Task<Result<bool>> DeleteKeyAsync(Actor actor, string provider, string? forRole, bool acknowledge, CancellationToken ct)
    {
        EnsureScope(actor);
        if (ProviderError(provider, requireAvailable: false) is { } pe) return pe;
        if (forRole is not null && !AiRoles.IsValid(forRole)) return V("invalid_role", "forRole must be chat or analytics.");

        var row = await db.AiProviderKeys.FirstOrDefaultAsync(k => k.Provider == provider, ct);
        if (row is null) return true; // ідемпотентно
        var eff = await runtime.GetEffectiveAsync(actor.TenantId, ct);
        if (!acknowledge && AffectedRoles(eff, provider, forRole) is { Count: > 0 } affected) return AffectsOthers(affected);

        // Мовчазного перемикання на platform немає: ролі провайдера отримують own_key_missing.
        db.AiProviderKeys.Remove(row);
        Audit(actor.TenantId, actor.UserId, "key_deleted", provider, []);
        await db.SaveChangesAsync(ct);
        LogChanged(actor.TenantId, actor.UserId, "key_deleted", provider, [], 0);
        return true;
    }

    public async Task<Result<AiKeyTestDto>> TestKeyAsync(Actor actor, string provider, AiKeyTestRequest? req, CancellationToken ct)
    {
        EnsureScope(actor);
        if (ProviderError(provider, requireAvailable: true) is { } pe) return pe;
        var eff = await runtime.GetEffectiveAsync(actor.TenantId, ct);
        var model = VerifyModel(provider, eff);

        AiSecret? secret = null;
        string? preReason = null;
        if (!string.IsNullOrWhiteSpace(req?.ApiKey))
        {
            var candidate = req!.ApiKey!.Trim();
            if (!KeyFormatOk(provider, candidate)) return BadKeyFormat();
            secret = new AiSecret(candidate); // кандидат не зберігається
        }
        else
        {
            if (protector.State != AiSecretsState.Ready) return SecretsUnavailable();
            var stored = await db.AiProviderKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Provider == provider, ct);
            if (stored is null) preReason = "no_key";
            else
            {
                try
                {
                    secret = new AiSecret(protector.Unprotect(stored.KeyEncrypted, actor.TenantId, provider));
                }
                catch (AiSecretException ex) when (ex.Error is AiSecretError.UnknownKeyId or AiSecretError.SecretsUnavailable)
                {
                    return SecretsUnavailable();
                }
                catch (AiSecretException)
                {
                    preReason = "key_rejected"; // нечитаний шифротекст (AAD/пошкодження): виклику провайдера немає
                }
            }
        }

        var check = secret is null ? new AiKeyCheck(false, preReason) : await CheckAsync(provider, secret, model, ct);
        Audit(actor.TenantId, actor.UserId, "key_tested", provider, []);
        await db.SaveChangesAsync(ct);
        LogChanged(actor.TenantId, actor.UserId, "key_tested", provider, [], 0);
        return new AiKeyTestDto(check.Ok, check.Ok ? null : check.Reason ?? "provider_unavailable", provider, model, clock.GetUtcNow());
    }

    /// <summary>Модель перевірки: chat_model, якщо chat на цьому провайдері, інакше analytics_model, інакше економна модель провайдера.</summary>
    private string VerifyModel(string provider, AiEffectiveSettings eff)
    {
        foreach (var sel in new[] { eff.Chat, eff.Analytics })
            if (sel.Provider == provider && catalog.FindModel(sel.Model) is { Enabled: true } m && m.Provider == provider) return m.Id;
        var models = catalog.Models.Where(m => m.Provider == provider && m.Enabled).ToList();
        return (models.FirstOrDefault(m => m.Tier == "economy") ?? models.FirstOrDefault())?.Id ?? "";
    }

    private async Task<AiKeyCheck> CheckAsync(string provider, AiSecret key, string model, CancellationToken ct)
    {
        try
        {
            return await providers.Get(provider)!.CheckKeyAsync(key, model, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new AiKeyCheck(false, "provider_unavailable"); // текст винятку не використовується: міг би містити фрагменти запиту
        }
    }

    // ------------------------------------------------------------------ довідник і використання

    public Result<AiCatalogDto> GetModels()
    {
        var roles = new List<AiCatalogRoleDto>
        {
            new(AiRoles.Chat, "Спілкування з клієнтами", null),
            new(AiRoles.Analytics, "Аналітика й рекомендації", false), // у V1 лише налаштовується
        };
        var provs = catalog.Providers.Select(p =>
        {
            var available = providers.IsAvailable(p.Id);
            return new AiCatalogProviderDto(p.Id, p.Label, available, available ? null : "Скоро");
        }).ToList();
        var models = catalog.Models.Where(m => m.Enabled).Select(m =>
        {
            var rank = m.Tier switch { "economy" => 1, "balanced" => 2, _ => 3 };
            return new AiCatalogModelDto(m.Id, m.Provider, providers.IsAvailable(m.Provider), m.Tier,
                m.Tier switch { "economy" => "Економна", "balanced" => "Збалансована", _ => "Найрозумніша" }, rank,
                rank switch { 1 => "дешевша", 2 => "середня", _ => "дорожча" },
                m.InputPricePerMTok, m.OutputPricePerMTok, m.Currency, m.PriceUpdatedAt, m.DefaultForRoles.ToList());
        }).ToList();
        return new AiCatalogDto(roles, provs, models);
    }

    public async Task<Result<AiUsageDto>> GetUsageAsync(Actor actor, int months, CancellationToken ct)
    {
        EnsureScope(actor);
        if (months is < 1 or > 12) return V("invalid_months", "months must be 1..12.");
        var eff = await runtime.GetEffectiveAsync(actor.TenantId, ct);
        var now = clock.GetUtcNow();
        var cur = AiTenantRuntime.PeriodOf(now);
        var first = cur.AddMonths(-(months - 1));
        var rows = await db.AiUsage.AsNoTracking().Where(u => u.PeriodStart >= first && u.PeriodStart <= cur).ToListAsync(ct);
        var current = rows.Where(r => r.PeriodStart == cur).ToList();

        var byModel = current.OrderBy(r => r.Role).ThenBy(r => r.Provider).ThenBy(r => r.Model).ThenBy(r => r.KeySource)
            .Select(r => new AiUsageByModelDto(r.Role, r.Provider, r.Model, r.KeySource, r.RequestCount, r.InputTokens, r.OutputTokens,
                Cost(r.Model, r.InputTokens, r.OutputTokens)))
            .ToList();
        var costs = byModel.Select(m => m.EstimatedCost).ToList();
        var total = costs.Any(c => c is null) ? (decimal?)null : costs.Sum(c => c!.Value);
        var currency = catalog.Models.FirstOrDefault()?.Currency ?? "USD";

        var history = Enumerable.Range(0, months).Select(i =>
        {
            var p = cur.AddMonths(-i);
            var rs = rows.Where(r => r.PeriodStart == p).ToList();
            return new AiUsageHistoryDto($"{p:yyyy-MM}", rs.Sum(r => (long)r.RequestCount), rs.Sum(r => r.InputTokens), rs.Sum(r => r.OutputTokens));
        }).ToList();

        // Ефективні ліміти: platform -> менше з лімітів tenant і квоти платформи (квота 0 = платформа вимкнена, не ліміт).
        long? tokens = eff.MonthlyTokenLimit, requests = eff.MonthlyRequestLimit;
        var source = "tenant";
        if (eff.KeySource == AiTenantRuntime.KeySourcePlatform)
        {
            var enabled = eff.PlatformEnabled ?? platform.EnabledByDefault;
            var tq = eff.PlatformTokenQuota ?? platform.DefaultMonthlyTokenQuota;
            var rq = eff.PlatformRequestQuota ?? platform.DefaultMonthlyRequestQuota;
            if (enabled && tq > 0 && rq > 0)
            {
                var both = (eff.MonthlyTokenLimit is not null) || (eff.MonthlyRequestLimit is not null);
                source = both ? "min" : "platform";
                tokens = eff.MonthlyTokenLimit is { } t ? Math.Min(t, tq) : tq;
                requests = eff.MonthlyRequestLimit is { } r ? Math.Min(r, rq) : rq;
            }
        }

        return new AiUsageDto(eff.KeySource,
            new AiUsagePeriodDto(cur.ToString("yyyy-MM-dd"), DateOnly.FromDateTime(now.UtcDateTime).ToString("yyyy-MM-dd")),
            new AiUsageLimitsDto(tokens, requests, source),
            new AiUsageCurrentDto(current.Sum(r => (long)r.RequestCount), current.Sum(r => (long)r.ErrorCount),
                current.Sum(r => r.InputTokens), current.Sum(r => r.OutputTokens), total, currency),
            byModel, history);
    }

    private decimal? Cost(string modelId, long input, long output)
    {
        if (input == 0 && output == 0) return 0m;
        var m = catalog.FindModel(modelId);
        if (m?.InputPricePerMTok is not { } ip || m.OutputPricePerMTok is not { } op) return null;
        return Math.Round(input / 1_000_000m * ip + output / 1_000_000m * op, 4);
    }

    // ------------------------------------------------------------------ платформа

    /// <summary>
    /// PATCH квот платформи (оператор платформи, X-Platform-Key). Відсутнє поле = без змін, <c>null</c> = дефолт конфігу.
    /// tenant береться з маршруту й ставиться в scope до першого звернення до БД (RLS), чужий/невідомий = tenant_not_found.
    /// </summary>
    public async Task<Result<bool>> UpdatePlatformAsync(Guid tenantId, JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object) return V("invalid_body", "Body must be a JSON object.");
        bool hasEnabled = body.TryGetProperty("enabled", out var en), hasTokens = body.TryGetProperty("monthlyTokenQuota", out var tq),
            hasRequests = body.TryGetProperty("monthlyRequestQuota", out var rq);
        if (!hasEnabled && !hasTokens && !hasRequests) return V("nothing_to_change", "Provide enabled, monthlyTokenQuota and/or monthlyRequestQuota.");

        bool? enabled = null;
        long? tokens = null;
        int? requests = null;
        if (hasEnabled && en.ValueKind != JsonValueKind.Null)
        {
            if (en.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return V("invalid_enabled", "enabled must be true, false or null.");
            enabled = en.GetBoolean();
        }
        if (hasTokens && tq.ValueKind != JsonValueKind.Null)
        {
            if (tq.ValueKind != JsonValueKind.Number || !tq.TryGetInt64(out var t) || t < 0) return V("invalid_quota", "Quota must be a non-negative integer.");
            tokens = t;
        }
        if (hasRequests && rq.ValueKind != JsonValueKind.Null)
        {
            if (rq.ValueKind != JsonValueKind.Number || !rq.TryGetInt32(out var r) || r < 0) return V("invalid_quota", "Quota must be a non-negative integer.");
            requests = r;
        }

        TenantGuard.EnsureOrSetForTrustedJob(tenant, tenantId); // платформний оператор: tenant з маршруту, явно (L3); до першого звернення до БД
        if (!await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, ct)) return Error.NotFound("tenant_not_found", "Tenant not found.");

        return await WriteWithRetryAsync<bool>(async () =>
        {
            var row = await db.AiSettings.FirstOrDefaultAsync(ct) ?? NewRow(tenantId);
            var fields = new List<string>();
            if (hasEnabled && row.PlatformEnabled != enabled) { row.PlatformEnabled = enabled; fields.Add("platform_enabled"); }
            if (hasTokens && row.PlatformMonthlyTokenQuota != tokens) { row.PlatformMonthlyTokenQuota = tokens; fields.Add("platform_monthly_token_quota"); }
            if (hasRequests && row.PlatformMonthlyRequestQuota != requests) { row.PlatformMonthlyRequestQuota = requests; fields.Add("platform_monthly_request_quota"); }
            if (fields.Count == 0) return true;
            Audit(tenantId, null, "platform_quota_changed", null, fields); // user_id NULL = оператор платформи
            await db.SaveChangesAsync(ct);
            LogChanged(tenantId, null, "platform_quota_changed", null, fields, 0);
            return true;
        });
    }

    // ------------------------------------------------------------------ допоміжне

    private AiModelInfo DefaultModel(string role) =>
        catalog.DefaultFor(role, providers)
        ?? catalog.Models.FirstOrDefault(m => m.Enabled && m.DefaultForRoles.Contains(role))
        ?? catalog.Models.FirstOrDefault(m => m.Enabled)
        ?? new AiModelInfo(role == AiRoles.Chat ? "claude-haiku-5-5" : "claude-sonnet-5-5", AnthropicAiProvider.ProviderId, "economy", true, [role], 4096);

    /// <summary>Рядок з дефолтами ADR-010 (перший запис tenant). Додається до контексту, вставка - при SaveChanges.</summary>
    private AiSettingsRow NewRow(Guid tenantId)
    {
        var chat = DefaultModel(AiRoles.Chat);
        var analytics = DefaultModel(AiRoles.Analytics);
        var row = new AiSettingsRow
        {
            Id = Guid.NewGuid(), TenantId = tenantId, KeySource = AiTenantRuntime.KeySourcePlatform,
            ChatProvider = chat.Provider, ChatModel = chat.Id, AnalyticsProvider = analytics.Provider, AnalyticsModel = analytics.Id,
            Mode = "confirm", MaxOutputTokens = 1024,
        };
        db.AiSettings.Add(row);
        return row;
    }

    /// <summary>Гонка двох перших записів (unique tenant_id / tenant+provider): одна повторна спроба вже як оновлення.</summary>
    private async Task<Result<T>> WriteWithRetryAsync<T>(Func<Task<Result<T>>> op)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await op();
            }
            catch (DbUpdateException ex) when (attempt == 0 && ex.InnerException is PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
