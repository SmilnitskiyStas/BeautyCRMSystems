using System.Text;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

// Транспорт контакту/номера Telegram (TASK-768, контракт §25.4/§25.9/§25.13, ADR-014 частина B).
// Номер живе лише в beauty_messages.contact_phone (тимчасово); у тілі, логах, винятках, ToString і журналі його немає.

/// <summary>
/// Налаштування <c>ChatPhone:*</c> (§25.2): головний вимикач, строк тимчасового зберігання номера (TASK-768) і ключі конвеєра (TASK-770).
/// </summary>
public sealed class ChatPhoneOptions
{
    /// <summary>За замовч. false: номер власного контакту не записується в БД взагалі (verdict лишається).</summary>
    public bool Enabled { get; init; }

    /// <summary>Sweeper обнуляє <c>contact_phone</c>, старші за це (1..72, за замовч. 24).</summary>
    public int ContactRetentionHours { get; init; } = 24;

    /// <summary>TTL питання про номер: <c>min(now + TTL, starts_at запису)</c> (1..72, за замовч. 24).</summary>
    public int PromptTtlHours { get; init; } = 24;

    /// <summary>Автоматичне повторне питання не частіше (1..365 діб, за замовч. 30). Те саме значення читає <c>ClientPhoneService</c>.</summary>
    public int CooldownDays { get; init; } = 30;

    /// <summary>Країна для національного формату (V1 лише UA: <c>0XXXXXXXXX</c> -> <c>+380…</c>).</summary>
    public string DefaultCountry { get; init; } = "UA";

    public static ChatPhoneOptions Read(IConfiguration c)
    {
        static int I(IConfiguration c, string key, int def, int min, int max) =>
            int.TryParse(c[$"ChatPhone:{key}"], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : def;
        return new ChatPhoneOptions
        {
            Enabled = bool.TryParse(c["ChatPhone:Enabled"], out var e) && e,
            ContactRetentionHours = I(c, "ContactRetentionHours", 24, 1, 72),
            PromptTtlHours = I(c, "PromptTtlHours", 24, 1, 72),
            CooldownDays = I(c, "CooldownDays", 30, 1, 365),
            DefaultCountry = string.IsNullOrWhiteSpace(c["ChatPhone:DefaultCountry"]) ? "UA" : c["ChatPhone:DefaultCountry"]!.Trim().ToUpperInvariant(),
        };
    }
}

/// <summary>Нормалізація <c>contact.phone_number</c> від Telegram до E.164 (§25.4); ручний текст нормалізує PhoneNormalizer у конвеєрі.</summary>
public static class ContactPhoneNormalizer
{
    /// <summary>Довжина колонки <c>contact_phone</c> (E.164: '+' і до 15 цифр).</summary>
    public const int MaxLength = 16;

    /// <summary>
    /// ASCII-цифри з необов'язковим '+', без роздільників. З '+': 8..15 цифр; без: 11..15 -> '+' + цифри. Перша цифра не 0;
    /// <c>+380</c> - рівно 12 цифр. Інакше null.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 20) return null;
        var plus = raw[0] == '+';
        var digits = plus ? raw.AsSpan(1) : raw.AsSpan();
        foreach (var ch in digits) if (!char.IsAsciiDigit(ch)) return null;
        if (digits.Length is < 8 or > 15 || (!plus && digits.Length < 11) || digits[0] == '0') return null;
        if (digits.StartsWith("380") && digits.Length != 12) return null;
        return "+" + digits.ToString();
    }

    /// <summary>Формат, який приймає CHECK <c>ck_beauty_messages_contact_phone</c>: <c>^\+[1-9][0-9]{7,14}$</c>.</summary>
    public static bool IsE164(string? s)
    {
        if (s is not { Length: >= 9 and <= MaxLength } || s[0] != '+' || s[1] is < '1' or > '9') return false;
        for (var i = 2; i < s.Length; i++) if (!char.IsAsciiDigit(s[i])) return false;
        return true;
    }
}

/// <summary>Прочитаний контакт. <see cref="Phone"/> є лише для verdict=own і поки не обнулений; у ToString/логи не потрапляє.</summary>
public sealed record ContactPhoneRead(string Verdict, string? Phone)
{
    private bool PrintMembers(StringBuilder sb)
    {
        sb.Append("Verdict = ").Append(Verdict).Append(", HasPhone = ").Append(Phone is not null);
        return true;
    }
}

/// <summary>
/// Порт для конвеєра (TASK-770, §25.5.2): прочитати тимчасовий номер і обнулити його в тій самій транзакції. Працює через
/// <see cref="BeautyDbContext"/> поточного scope під RLS tenant; якщо в контексті вже відкрита транзакція конвеєра - бере участь у ній.
/// </summary>
public interface IContactPhoneStore
{
    /// <summary>Вид і verdict контакту без номера й без побічних ефектів; null - повідомлення немає (чужий tenant) або воно не kind=contact.</summary>
    Task<ContactPhoneRead?> ReadAsync(Guid messageId, CancellationToken ct);

    /// <summary>Атомарно (один UPDATE ... RETURNING) віддає номер і ставить <c>contact_phone=NULL</c>; повторний виклик -> null.</summary>
    Task<string?> TakeAsync(Guid messageId, CancellationToken ct);

    /// <summary>Обнуляє <c>contact_phone</c> повідомлення (будь-який результат обробки, §25.5.2). Ідемпотентно.</summary>
    Task ClearContactPhoneAsync(Guid messageId, CancellationToken ct);
}

public sealed class EfContactPhoneStore(BeautyDbContext db) : IContactPhoneStore
{
    public async Task<ContactPhoneRead?> ReadAsync(Guid messageId, CancellationToken ct)
    {
        var row = await db.Messages.AsNoTracking()
            .Where(m => m.Id == messageId && m.Kind == InboundKinds.Contact)
            .Select(m => new { m.ContactVerdict, m.ContactPhone }).FirstOrDefaultAsync(ct);
        return row is null ? null : new ContactPhoneRead(row.ContactVerdict ?? ContactVerdicts.Invalid, row.ContactPhone);
    }

    public async Task<string?> TakeAsync(Guid messageId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<string>($"""
            UPDATE beauty_messages m SET contact_phone = NULL
            FROM (SELECT id, contact_phone FROM beauty_messages WHERE id = {messageId} AND contact_phone IS NOT NULL FOR UPDATE) old
            WHERE m.id = old.id
            RETURNING old.contact_phone AS "Value"
            """).ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    public Task ClearContactPhoneAsync(Guid messageId, CancellationToken ct) =>
        db.Messages.Where(m => m.Id == messageId && m.ContactPhone != null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ContactPhone, (string?)null), ct);
}

/// <summary>
/// Sweeper тимчасових номерів (§25.5.7): <c>contact_phone=NULL</c> для рядків старших за <see cref="ChatPhoneOptions.ContactRetentionHours"/>,
/// пакетами по 500. Викликається з hosted-сервісу нижче і може викликатися з <c>ConversationWorker.SweepTenantAsync</c> (TASK-770).
/// У лог іде лише кількість.
/// </summary>
public sealed class ContactPhoneSweeper(
    BeautyDbContext db, TenantContext tenant, ChatPhoneOptions options, TimeProvider clock, ILogger<ContactPhoneSweeper> log)
{
    public const int BatchSize = 500;

    /// <returns>Скільки номерів обнулено.</returns>
    public async Task<int> SweepTenantAsync(Guid tenantId, CancellationToken ct)
    {
        TenantGuard.EnsureOrSetForTrustedJob(tenant, tenantId);
        var cutoff = clock.GetUtcNow() - TimeSpan.FromHours(options.ContactRetentionHours);
        var total = 0;
        int n;
        do
        {
            n = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE beauty_messages SET contact_phone = NULL
                WHERE id IN (SELECT id FROM beauty_messages WHERE contact_phone IS NOT NULL AND created_at < {cutoff} ORDER BY created_at LIMIT {BatchSize})
                """, ct);
            total += n;
        } while (n >= BatchSize && !ct.IsCancellationRequested);
        if (total > 0) log.LogInformation("contact_phone_swept tenant={TenantId} count={Count}", tenantId, total);
        return total;
    }
}

/// <summary>
/// Hosted-обхід sweeper. Tenants - ті, що відомі процесу з ingest/polling (як у ConversationWorker: SECURITY DEFINER-функції немає).
/// Працює завжди, навіть при <c>ChatPhone:Enabled=false</c>: номери, записані до вимкнення, теж мають зникнути.
/// </summary>
public sealed class ContactPhoneSweeperService(
    IServiceScopeFactory scopes, ConversationSignals signals, TelegramPollingOptions polling, ILogger<ContactPhoneSweeperService> log)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(Interval, stop);
                await SweepOnceAsync(stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async Task SweepOnceAsync(CancellationToken ct)
    {
        var tenants = new HashSet<Guid>(signals.KnownTenants);
        foreach (var channelId in polling.ChannelIds)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var ch = await scope.ServiceProvider.GetRequiredService<ChannelDirectory>().ResolveAsync(ChannelIds.Telegram, channelId, ct);
                if (ch is not null) tenants.Add(ch.TenantId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("contact_phone_sweep_channel_unresolved channel={ChannelId} error={ErrorType}", channelId, ex.GetType().Name);
            }
        }
        foreach (var tenantId in tenants)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ContactPhoneSweeper>().SweepTenantAsync(tenantId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.LogError("contact_phone_sweep_failed tenant={TenantId} error={ErrorType}", tenantId, ex.GetType().Name);
            }
        }
    }
}
