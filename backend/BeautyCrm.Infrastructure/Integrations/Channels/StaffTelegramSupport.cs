using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BeautyCrm.Infrastructure.Integrations.Channels;

/// <summary>
/// Прапори етапу 2b (§32.24.6). <c>Inventory:TelegramOwnerAlertsEnabled</c>: лише значення <c>true</c> (без регістру) вмикає, усе інше
/// (відсутнє, порожнє, 1/on/yes) вимикає. <c>Inventory:TelegramL3Approved</c>: технічний запобіжник поза Development (2b-Q11).
/// </summary>
public sealed class StaffTelegramOptions
{
    public const string EnabledKey = "Inventory:TelegramOwnerAlertsEnabled";
    public const string L3Key = "Inventory:TelegramL3Approved";
    public const string ConsentVersion = "inv-tg-v1";

    public bool Enabled { get; init; }
    public bool L3Approved { get; init; }
    public bool IsDevelopment { get; init; }

    /// <summary>Функція реально доступна: прапор увімкнено й (Development або L3 закрито).</summary>
    public bool Active => Enabled && (IsDevelopment || L3Approved);

    public static bool IsOn(string? value) => value is not null && value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    public static StaffTelegramOptions Read(IConfiguration config, IHostEnvironment? env = null) => new()
    {
        Enabled = IsOn(config[EnabledKey]),
        L3Approved = IsOn(config[L3Key]),
        IsDevelopment = env?.IsDevelopment() ?? false,
    };

    /// <summary>Поза Development з увімкненим прапором без запобіжника L3 - помилка старту.</summary>
    public void ValidateForHost()
    {
        if (Enabled && !IsDevelopment && !L3Approved)
            throw new InvalidOperationException(
                $"{EnabledKey}=true outside Development requires {L3Key}=true (L3 legal gate, ADR-025 §7.2).");
    }
}

/// <summary>Падає на старті, якщо запобіжник L3 порушено.</summary>
public sealed class StaffTelegramStartupGuard(StaffTelegramOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        options.ValidateForHost();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Шифрування chat_id тим самим протектором/форматом, що секрети каналів, і HMAC для пошуку (HKDF від Channels:EncryptionKey).</summary>
public sealed class StaffChatCodec(IConfiguration config, ISecretProtector protector)
{
    private const string LookupInfo = "beauty-staff-telegram-lookup-v1";
    private byte[]? _key;

    private byte[] LookupKey => _key ??= LoadKey();

    private byte[] LoadKey()
    {
        var b64 = config["Channels:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(b64))
            throw new InvalidOperationException("Channels:EncryptionKey is not configured (env Channels__EncryptionKey, base64 of 32 bytes).");
        var ikm = Convert.FromBase64String(b64);
        if (ikm.Length != 32) throw new InvalidOperationException("Channels:EncryptionKey must be 32 bytes (base64).");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, info: Encoding.UTF8.GetBytes(LookupInfo));
    }

    public byte[] Hash(Guid channelId, string chatId) =>
        HMACSHA256.HashData(LookupKey, Encoding.UTF8.GetBytes($"staff-tg-chat:v1:{channelId:N}:{chatId}"));

    public string Encrypt(string chatId) => protector.Protect(chatId);
    public string Decrypt(string encrypted) => protector.Unprotect(encrypted);
}

/// <summary>Одноразовий токен прив'язки (32 байти CSPRNG, base64url 43 символи; у БД лише SHA-256) і короткий id прив'язки (22 символи).</summary>
public static class StaffLinkToken
{
    public const int TokenLength = 43;

    public static (string Token, byte[] Hash) Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return (B64Url(bytes), SHA256.HashData(bytes));
    }

    /// <summary>Лише канонічний base64url довжиною 43 (рівно 32 байти); решта - false.</summary>
    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = [];
        if (token is not { Length: TokenLength } || !TryDecode(token, 32, out var bytes)) return false;
        hash = SHA256.HashData(bytes);
        return true;
    }

    public static string LinkId22(Guid id) => B64Url(id.ToByteArray());

    public static bool TryParseLinkId22(string? s, out Guid id)
    {
        id = default;
        if (s is not { Length: 22 } || !TryDecode(s, 16, out var bytes)) return false;
        id = new Guid(bytes);
        return true;
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string s, int expectedBytes, out byte[] bytes)
    {
        bytes = [];
        foreach (var ch in s)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return false;
        try
        {
            var padded = s.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException) { return false; }
        return bytes.Length == expectedBytes && B64Url(bytes) == s; // канонічність: без «зайвих» бітів в останньому символі
    }
}

/// <summary>Ковзні лічильники в пам'яті процесу (ліміти спроб, §32.24.3). Час - з <see cref="TimeProvider"/>.</summary>
public sealed class StaffTelegramLimits(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _hits = new();

    /// <summary>true і запис, якщо за вікно було менше <paramref name="limit"/> звернень; інакше false (без запису).</summary>
    public bool TryHit(string key, int limit, TimeSpan window)
    {
        var now = clock.GetUtcNow();
        var list = _hits.GetOrAdd(key, _ => []);
        lock (list)
        {
            list.RemoveAll(t => t <= now - window);
            if (list.Count >= limit) return false;
            list.Add(now);
        }
        Sweep(now, window);
        return true;
    }

    public int Count(string key, TimeSpan window)
    {
        var now = clock.GetUtcNow();
        if (!_hits.TryGetValue(key, out var list)) return 0;
        lock (list)
        {
            list.RemoveAll(t => t <= now - window);
            return list.Count;
        }
    }

    public void Add(string key, TimeSpan window)
    {
        var now = clock.GetUtcNow();
        var list = _hits.GetOrAdd(key, _ => []);
        lock (list)
        {
            list.RemoveAll(t => t <= now - window);
            list.Add(now);
        }
    }

    private void Sweep(DateTimeOffset now, TimeSpan window)
    {
        if (_hits.Count < 10_000) return;
        foreach (var (k, list) in _hits)
            lock (list)
            {
                list.RemoveAll(t => t <= now - window);
                if (list.Count == 0) _hits.TryRemove(k, out _);
            }
    }
}

/// <summary>Транспорт Telegram для прив'язки: ім'я бота (getMe) і пряма best-effort відправка. Підміняється в тестах.</summary>
public interface IStaffTelegramTransport
{
    /// <summary>Ім'я бота без «@»; null, якщо недоступно. Кеш 1 год на канал.</summary>
    Task<string?> GetBotUsernameAsync(ResolvedChannel channel, CancellationToken ct);

    /// <summary>Одна спроба без повторів. Виняток - збій (викликач ловить).</summary>
    Task SendTextAsync(ResolvedChannel channel, string chatId, string text, ChatActionLayout? actions, CancellationToken ct);
}

/// <summary>Можливість каналу Telegram: getMe -> username.</summary>
public interface IChannelBotInfo
{
    Task<string?> GetBotUsernameAsync(CancellationToken ct);
}

public sealed class AdapterStaffTelegramTransport(ChannelRegistry registry, ChannelRequestContext context, TimeProvider clock) : IStaffTelegramTransport
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan GetMeTimeout = TimeSpan.FromSeconds(3);
    private static readonly ConcurrentDictionary<Guid, (string Name, DateTimeOffset Until)> Cache = new();

    public async Task<string?> GetBotUsernameAsync(ResolvedChannel channel, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (Cache.TryGetValue(channel.ChannelId, out var hit) && hit.Until > now) return hit.Name;
        if (registry.Find(ChannelIds.Telegram) is not IChannelBotInfo info) return null;
        using var scope = context.Begin(channel);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(GetMeTimeout);
        string? name;
        try { name = await info.GetBotUsernameAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        if (string.IsNullOrWhiteSpace(name)) return null;
        Cache[channel.ChannelId] = (name, now + CacheTtl);
        return name;
    }

    public async Task SendTextAsync(ResolvedChannel channel, string chatId, string text, ChatActionLayout? actions, CancellationToken ct)
    {
        var adapter = registry.Find(ChannelIds.Telegram) ?? throw new InvalidOperationException("telegram_adapter_missing");
        using var scope = context.Begin(channel);
        await adapter.SendAsync(new OutboundMessage(Guid.NewGuid(), ChannelIds.Telegram, chatId, text, null, actions), ct);
    }
}

public static class StaffTelegramTexts
{
    public const string InvalidLink = "Посилання недійсне або прострочене. Створіть нове в адмінці: Налаштування -> Сповіщення.";
    public const string Disabled = "Функцію зараз вимкнено.";
    public const string Confirm = "Підтверджую";
    public const string Cancel = "Скасувати";
    public const string Declined = "Скасовано. Сповіщення не підключено.";
    public const string Stopped = "Сповіщення складу вимкнено, ідентифікатор чату видалено.";
    public const string Unlinked = "Сповіщення вимкнено.";
    public const string Hint = "Це службовий чат сповіщень складу. Команди: /status, /stop";

    public static string ConsentPrompt(string botName) =>
        $"Ви вмикаєте службові сповіщення складу в Telegram від бота «{botName}». Ми збережемо ідентифікатор вашого чату Telegram у зашифрованому " +
        "вигляді лише для надсилання цих сповіщень: заклад, товар, кількість, термін придатності. Дані клієнтів у сповіщеннях не надсилаються. " +
        "Повідомлення передаються через сервіс Telegram. Вимкнути можна будь-коли в адмінці або командою /stop у боті - ідентифікатор чату буде видалено.";

    public static string Done(int openAlerts) => $"Готово. Сповіщення складу увімкнено. Відкритих сповіщень зараз: {openAlerts}. Вимкнути: /stop.";

    public static string Status(bool featureOn, DateTimeOffset? since, bool awaiting) =>
        awaiting ? "Сповіщення складу: очікує підтвердження"
        : $"Сповіщення складу: {(featureOn ? "увімкнено" : "вимкнено адміністратором платформи")}; прив'язано {(since is { } s ? s.ToString("dd.MM.yyyy") : "-")}";
}

/// <summary>Відкликання прив'язок (стирання даних чату, скасування черги, подія журналу). Спільне для API, бота й хуків деактивації.</summary>
public static class StaffTelegramRevocation
{
    public static readonly string[] OpenStatuses = ["issued", "claimed", "active"];

    /// <summary>
    /// Відкриті прив'язки користувача -> revoked. Виконується у транзакції викликача (або без неї: кожен рядок - окремий атомарний UPDATE).
    /// Повертає id відкликаних. Не вимагає ключа шифрування: chat_id лише стирається.
    /// </summary>
    public static async Task<List<Guid>> RevokeForUserAsync(
        BeautyDbContext db, Guid userId, string reason, string actor, Guid? actorUserId, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.StaffTelegramLinks.AsNoTracking()
            .Where(l => l.UserId == userId && OpenStatuses.Contains(l.Status)).Select(l => l.Id).ToListAsync(ct);
        var revoked = new List<Guid>();
        foreach (var id in rows)
            if (await RevokeOneAsync(db, id, userId, reason, actor, actorUserId, now, ct)) revoked.Add(id);
        return revoked;
    }

    /// <summary>Один рядок; true, якщо саме цей виклик його закрив. Пише подію <c>revoked</c> (без даних чату).</summary>
    public static async Task<bool> RevokeOneAsync(
        BeautyDbContext db, Guid linkId, Guid userId, string reason, string actor, Guid? actorUserId, DateTimeOffset now, CancellationToken ct)
    {
        var n = await db.StaffTelegramLinks.Where(l => l.Id == linkId && OpenStatuses.Contains(l.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, "revoked")
                .SetProperty(l => l.TokenHash, (byte[]?)null)
                .SetProperty(l => l.TokenExpiresAt, (DateTimeOffset?)null)
                .SetProperty(l => l.ChatIdEncrypted, (string?)null)
                .SetProperty(l => l.ChatIdHash, (byte[]?)null)
                .SetProperty(l => l.RevokedAt, now)
                .SetProperty(l => l.RevokeReason, reason)
                .SetProperty(l => l.UpdatedAt, now), ct);
        if (n == 0) return false;
        await db.StaffNotifications.Where(x => x.LinkId == linkId && (x.Status == "pending" || x.Status == "sending"))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "cancelled").SetProperty(x => x.UpdatedAt, now), ct);
        AddEvent(db, linkId, userId, "revoked", reason, actor, actorUserId, now);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static void AddEvent(BeautyDbContext db, Guid linkId, Guid userId, string ev, string? reason, string actor, Guid? actorUserId, DateTimeOffset now) =>
        db.StaffTelegramLinkEvents.Add(new StaffTelegramLinkEvent
        {
            Id = Guid.NewGuid(), LinkId = linkId, UserId = userId, Event = ev, Reason = reason, Actor = actor, ActorUserId = actorUserId, CreatedAt = now,
        });
}
