using System.Net;
using System.Text.Json;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

/// <summary>
/// Long polling Telegram (ADR-011 §2.1, контракт §21.2). Ключі: Channels:Telegram:Polling:* (синонім Channels:Polling:*).
/// Лише Development і лише з Channels:UseMocks=false; список каналів явний.
/// </summary>
public sealed class TelegramPollingOptions
{
    public bool Enabled { get; init; }
    public IReadOnlyList<Guid> ChannelIds { get; init; } = [];
    public int TimeoutSeconds { get; init; } = 25;
    /// <summary>409 (активний webhook) -> deleteWebhook лише за цим явним прапорцем (default false).</summary>
    public bool DeleteWebhookOnConflict { get; init; }
    public TimeSpan ConflictBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Пауза, якщо lease каналу тримає інший споживач.</summary>
    public TimeSpan LeaseRetry { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Мінімальна пауза після порожньої відповіді (захист від гарячого циклу).</summary>
    public TimeSpan EmptyPollDelay { get; init; } = TimeSpan.FromSeconds(1);

    public static TelegramPollingOptions Read(IConfiguration c)
    {
        static string? Raw(IConfiguration c, string name) => c[$"Channels:Telegram:Polling:{name}"] ?? c[$"Channels:Polling:{name}"];
        var timeout = int.TryParse(Raw(c, "TimeoutSeconds"), out var t) ? t : 25;
        if (timeout is < 1 or > 50) throw new InvalidOperationException("Channels:Telegram:Polling:TimeoutSeconds must be 1..50.");
        return new TelegramPollingOptions
        {
            Enabled = bool.TryParse(Raw(c, "Enabled"), out var e) && e,
            ChannelIds = ReadIds(c),
            TimeoutSeconds = timeout,
            DeleteWebhookOnConflict = bool.TryParse(Raw(c, "DeleteWebhookOnConflict"), out var d) && d,
        };
    }

    private static IReadOnlyList<Guid> ReadIds(IConfiguration c)
    {
        var section = c.GetSection("Channels:Telegram:Polling:ChannelIds");
        if (!section.Exists()) section = c.GetSection("Channels:Polling:ChannelIds");
        var raw = section.GetChildren().Select(x => x.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (raw.Count == 0 && !string.IsNullOrWhiteSpace(section.Value))
            raw = section.Value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Cast<string?>().ToList();
        var ids = new List<Guid>();
        foreach (var v in raw)
        {
            if (!Guid.TryParse(v!.Trim(), out var g) || g == Guid.Empty)
                throw new InvalidOperationException("Channels:Telegram:Polling:ChannelIds must contain channel GUIDs.");
            if (!ids.Contains(g)) ids.Add(g);
        }
        return ids;
    }

    /// <summary>Падає на старті, якщо polling увімкнено поза Development або разом з Channels:UseMocks=true.</summary>
    public void ValidateForHost(bool isDevelopment, bool useMocks)
    {
        if (!Enabled) return;
        if (!isDevelopment)
            throw new InvalidOperationException("Channels:Telegram:Polling:Enabled=true is allowed only in the Development environment.");
        if (useMocks)
            throw new InvalidOperationException("Channels:Telegram:Polling:Enabled=true requires Channels:UseMocks=false (the mock parser does not understand Telegram updates).");
    }
}

/// <summary>
/// Окремий HttpClient для Telegram: без IHttpClientFactory і без логування URI, бо шлях bot{token}/getUpdates містить токен.
/// </summary>
public sealed class TelegramPollingHttp(HttpClient client)
{
    public HttpClient Client { get; } = client;
}

public enum PollStatus { Ok, LeaseHeld, Conflict, Unauthorized, ChannelUnavailable, RateLimited, Transient }

public sealed record PollCycleResult(PollStatus Status, int Updates = 0, int Stored = 0, TimeSpan? RetryAfter = null);

/// <summary>
/// Один цикл getUpdates для каналу: резолв каналу (tenant і токен з БД) -> tenant у scope -> lease -> getUpdates ->
/// InboundIngestCore -> offset у БД ПІСЛЯ збереження пакета. Токен ніколи не потрапляє в логи/винятки: логуються лише
/// id каналу, статус і ТИП винятку.
/// </summary>
public sealed class TelegramPoller(
    IServiceScopeFactory scopes, TelegramPollingHttp http, TelegramPollingOptions options, ILogger<TelegramPoller> log)
{
    private const string ApiBase = "https://api.telegram.org";
    private readonly Guid _owner = Guid.NewGuid();

    public async Task<PollCycleResult> RunCycleAsync(Guid channelId, CancellationToken ct)
    {
        try
        {
            return await CycleAsync(channelId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Не логуємо ex/ex.Message: у мережевих винятках може бути URL з токеном.
            log.LogWarning("telegram_poll_failed channel={ChannelId} error={ErrorType}", channelId, ex.GetType().Name);
            return new PollCycleResult(PollStatus.Transient);
        }
    }

    private async Task<PollCycleResult> CycleAsync(Guid channelId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;

        ResolvedChannel? channel;
        try { channel = await sp.GetRequiredService<ChannelDirectory>().ResolveAsync(ChannelIds.Telegram, channelId, ct); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or JsonException)
        {
            log.LogError("telegram_poll_credentials_unreadable channel={ChannelId}", channelId);
            return new PollCycleResult(PollStatus.ChannelUnavailable);
        }
        if (channel is null || string.IsNullOrEmpty(channel.Credentials.Token))
        {
            log.LogWarning("telegram_poll_channel_unavailable channel={ChannelId}", channelId);
            return new PollCycleResult(PollStatus.ChannelUnavailable);
        }
        if (sp.GetRequiredService<ChannelRegistry>().Find(ChannelIds.Telegram) is not TelegramAdapter adapter)
        {
            log.LogError("telegram_poll_requires_real_adapter channel={ChannelId}", channelId);
            return new PollCycleResult(PollStatus.ChannelUnavailable);
        }

        // Tenant береться з каналу в БД (довірене джерело), не з payload. До першого звернення до EF у цьому scope.
        TenantGuard.EnsureOrSetForTrustedJob(sp.GetRequiredService<TenantContext>(), channel.TenantId);
        var db = sp.GetRequiredService<BeautyDbContext>();
        var tenantId = channel.TenantId;
        var leaseSeconds = options.TimeoutSeconds + 30;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO beauty_channel_poll_state (tenant_id, channel_id) VALUES ({tenantId}, {channelId}) ON CONFLICT (tenant_id, channel_id) DO NOTHING", ct);
        var leased = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE beauty_channel_poll_state SET lease_until = now() + {leaseSeconds} * interval '1 second', lease_owner = {_owner}, updated_at = now()
               WHERE tenant_id = {tenantId} AND channel_id = {channelId}
                 AND (lease_until IS NULL OR lease_until < now() OR lease_owner = {_owner})", ct);
        if (leased == 0) return new PollCycleResult(PollStatus.LeaseHeld);
        var last = await db.ChannelPollStates.AsNoTracking()
            .Where(s => s.ChannelId == channelId).Select(s => s.LastUpdateId).FirstAsync(ct);

        var token = channel.Credentials.Token;
        HttpResponseMessage res;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{ApiBase}/bot{token}/getUpdates?offset={last + 1}&timeout={options.TimeoutSeconds}&allowed_updates=%5B%22message%22%2C%22callback_query%22%5D");
            res = await http.Client.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("telegram_poll_network_error channel={ChannelId} error={ErrorType}", channelId, ex.GetType().Name);
            return new PollCycleResult(PollStatus.Transient);
        }

        using (res)
        {
            var code = (int)res.StatusCode;
            switch (res.StatusCode)
            {
                case HttpStatusCode.Conflict:
                    if (options.DeleteWebhookOnConflict)
                    {
                        await DeleteWebhookAsync(token, channelId, ct);
                        return new PollCycleResult(PollStatus.Ok);
                    }
                    log.LogWarning("telegram_webhook_active channel={ChannelId}: polling paused, webhook is not removed automatically", channelId);
                    return new PollCycleResult(PollStatus.Conflict);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound:
                    log.LogError("telegram_poll_stopped channel={ChannelId} http={Http}", channelId, code);
                    return new PollCycleResult(PollStatus.Unauthorized);
                case HttpStatusCode.TooManyRequests:
                    return new PollCycleResult(PollStatus.RateLimited, RetryAfter: await RetryAfterAsync(res, ct));
            }
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("telegram_poll_http_error channel={ChannelId} http={Http}", channelId, code);
                return new PollCycleResult(PollStatus.Transient);
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)); }
            catch (JsonException) { return new PollCycleResult(PollStatus.Transient); }
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                    || !doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                    return new PollCycleResult(PollStatus.Transient);

                var messages = new List<InboundMessage>();
                long maxUpdateId = 0;
                var updates = 0;
                foreach (var update in result.EnumerateArray())
                {
                    updates++;
                    if (update.TryGetProperty("update_id", out var uid) && uid.TryGetInt64(out var id) && id > maxUpdateId) maxUpdateId = id;
                    try { messages.AddRange(await adapter.ParseInboundAsync(update.GetRawText())); }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                    {
                        // Зіпсований update не блокує чергу: пропускаємо, offset просувається.
                        log.LogWarning("telegram_poll_bad_update channel={ChannelId} error={ErrorType}", channelId, ex.GetType().Name);
                    }
                }

                var stored = 0;
                if (messages.Count > 0)
                {
                    using var _ = sp.GetRequiredService<ChannelRequestContext>().Begin(channel);
                    stored = (await sp.GetRequiredService<InboundIngestCore>().IngestAsync(messages, ct)).Count;
                }

                // Offset лише ПІСЛЯ збереження пакета: збій між ними дає повторну видачу, яку з'їдає дедуплікація.
                if (maxUpdateId > 0)
                {
                    var saved = await db.Database.ExecuteSqlInterpolatedAsync(
                        $@"UPDATE beauty_channel_poll_state SET last_update_id = GREATEST(last_update_id, {maxUpdateId}), updated_at = now()
                           WHERE tenant_id = {tenantId} AND channel_id = {channelId}", ct);
                    if (saved == 0) log.LogWarning("telegram_poll_offset_not_saved channel={ChannelId}", channelId);
                }
                return new PollCycleResult(PollStatus.Ok, updates, stored);
            }
        }
    }

    /// <summary>
    /// setMyCommands на старті polling каналу (§23.4): best-effort, тайм-аут 5 с, без повторів. Токен у логах/винятках відсутній:
    /// лише id каналу й тип помилки.
    /// </summary>
    public async Task SyncCommandsAsync(Guid channelId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var channel = await sp.GetRequiredService<ChannelDirectory>().ResolveAsync(ChannelIds.Telegram, channelId, ct);
            if (channel is null || string.IsNullOrEmpty(channel.Credentials.Token)) return;
            if (sp.GetRequiredService<ChannelRegistry>().Find(ChannelIds.Telegram) is not IChannelCommandsSync sync) return;
            using var _ = sp.GetRequiredService<ChannelRequestContext>().Begin(channel);
            await sync.SyncCommandsAsync(ct);
            log.LogInformation("telegram_commands_synced channel={ChannelId}", channelId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning("telegram_commands_failed channel={ChannelId} error={ErrorType}", channelId,
                (ex as PermanentChannelException)?.Code ?? ex.GetType().Name);
        }
    }

    private async Task DeleteWebhookAsync(string token, Guid channelId, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/bot{token}/deleteWebhook");
            using var res = await http.Client.SendAsync(req, ct);
            log.LogWarning("telegram_webhook_deleted channel={ChannelId} http={Http}", channelId, (int)res.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("telegram_delete_webhook_failed channel={ChannelId} error={ErrorType}", channelId, ex.GetType().Name);
        }
    }

    private static async Task<TimeSpan?> RetryAfterAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.Headers.RetryAfter?.Delta is { } delta) return delta;
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var r) && r.TryGetInt32(out var s))
                return TimeSpan.FromSeconds(Math.Clamp(s, 1, 3600));
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Знімає lease (коректне зупинення): наступний запуск не чекає закінчення lease.</summary>
    public async Task ReleaseAsync(Guid channelId)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var channel = await sp.GetRequiredService<ChannelDirectory>().ResolveAsync(ChannelIds.Telegram, channelId, CancellationToken.None);
            if (channel is null) return;
            TenantGuard.EnsureOrSetForTrustedJob(sp.GetRequiredService<TenantContext>(), channel.TenantId);
            var tenantId = channel.TenantId;
            await sp.GetRequiredService<BeautyDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE beauty_channel_poll_state SET lease_until = NULL, lease_owner = NULL, updated_at = now()
                   WHERE tenant_id = {tenantId} AND channel_id = {channelId} AND lease_owner = {_owner}", CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning("telegram_poll_release_failed channel={ChannelId} error={ErrorType}", channelId, ex.GetType().Name);
        }
    }
}

/// <summary>
/// BackgroundService: по одному циклу на канал зі списку (один poller на канал; між інстансами - lease у БД).
/// Увімкнений лише за Channels:Telegram:Polling:Enabled=true у Development; поза ним старт падає.
/// </summary>
public sealed class TelegramPollingService(
    IConfiguration config, TelegramPollingOptions options, TelegramPoller poller, ILogger<TelegramPollingService> log, IHostEnvironment? env = null)
    : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Без IHostEnvironment (голий контейнер) = не Development.
        options.ValidateForHost(env?.IsDevelopment() ?? false, bool.TryParse(config["Channels:UseMocks"], out var m) && m);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        if (options.ChannelIds.Count == 0)
        {
            log.LogWarning("telegram_polling_enabled_without_channels: set Channels:Telegram:Polling:ChannelIds");
            return;
        }
        await Task.WhenAll(options.ChannelIds.Distinct().Select(id => Task.Run(() => RunChannelAsync(id, stoppingToken), CancellationToken.None)));
    }

    internal async Task RunChannelAsync(Guid channelId, CancellationToken stop)
    {
        var failures = 0;
        try
        {
            await poller.SyncCommandsAsync(channelId, stop); // меню команд бота при старті polling (§23.4)
            while (!stop.IsCancellationRequested)
            {
                var r = await poller.RunCycleAsync(channelId, stop);
                var delay = TimeSpan.Zero;
                switch (r.Status)
                {
                    case PollStatus.Ok: failures = 0; if (r.Updates == 0) delay = options.EmptyPollDelay; break;
                    case PollStatus.LeaseHeld: delay = options.LeaseRetry; break;
                    case PollStatus.Conflict: delay = options.ConflictBackoff; break;
                    case PollStatus.RateLimited: delay = r.RetryAfter ?? Backoff(++failures); break;
                    case PollStatus.Transient: delay = Backoff(++failures); break;
                    default: // Unauthorized / ChannelUnavailable: токен недійсний або канал вимкнено - зупиняємо polling цього каналу
                        log.LogError("telegram_polling_stopped channel={ChannelId} reason={Reason}", channelId, r.Status);
                        return;
                }
                if (delay > TimeSpan.Zero) await Task.Delay(delay, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await poller.ReleaseAsync(channelId);
        }
    }

    private TimeSpan Backoff(int failures) =>
        TimeSpan.FromTicks(Math.Min(options.MaxBackoff.Ticks, options.BaseBackoff.Ticks * (1L << Math.Min(failures - 1, 20))));
}
