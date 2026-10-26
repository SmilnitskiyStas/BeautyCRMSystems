using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

public enum ChatCommand { Unknown, Start, Book, My, Cancel, Stop, Help }

/// <summary>
/// Slash-команди бота (§23.7 п. 9): розбір без LLM. Регістр ігнорується, `/cmd@BotName` допускається, аргументи (deep link `/start x`) відкидаються.
/// Невідома команда -> <see cref="ChatCommand.Unknown"/> (T-text-only, без змін).
/// </summary>
public static class ChatCommands
{
    /// <returns>null - текст не починається зі `/`; інакше команда (можливо <see cref="ChatCommand.Unknown"/>).</returns>
    public static ChatCommand? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var s = body.TrimStart();
        if (s[0] != '/') return null;
        var end = s.IndexOfAny([' ', '\t', '\n', '\r']);
        var token = (end < 0 ? s : s[..end]).TrimStart('/');
        var at = token.IndexOf('@');
        if (at >= 0) token = token[..at];
        return token.ToLowerInvariant() switch
        {
            "start" => ChatCommand.Start,
            "book" or "zapys" or "zapis" => ChatCommand.Book,
            "my" => ChatCommand.My,
            "cancel" => ChatCommand.Cancel,
            "stop" => ChatCommand.Stop,
            "help" => ChatCommand.Help,
            _ => ChatCommand.Unknown,
        };
    }
}

/// <summary>
/// Прибирання клавіатури з повідомлення (`editMessageReplyMarkup` через <see cref="IChannelActionsPort"/>, §23.4): після коміту, коли клавіатура
/// стала `used|expired|revoked`, і sweeper-повтор (до 3 спроб, `ListPendingClearAsync`). Best-effort: збій транспорту не ламає хід.
/// Успіх (також "message is not modified" / "not found") і канал без підтримки правки -> `markup_cleared_at`; збій -> лічильник спроб.
/// Порт резолвиться ліниво (DI без каналів лишається валідним).
/// </summary>
public sealed class KeyboardMarkupCleaner(
    BeautyDbContext db, TenantContext tenant, ChatKeyboardService keyboards, IServiceProvider services, ILogger<KeyboardMarkupCleaner> log)
{
    /// <summary>Прибирає розмітку вказаних клавіатур (id із поточного tenant; чужі RLS не віддасть). Повертає кількість прибраних.</summary>
    public async Task<int> ClearAsync(IReadOnlyCollection<Guid> keyboardIds, CancellationToken ct)
    {
        if (keyboardIds.Count == 0) return 0;
        var rows = await (from k in db.ChatKeyboards.AsNoTracking()
                          where keyboardIds.Contains(k.Id) && k.MarkupClearedAt == null
                          join m in db.Messages.AsNoTracking() on k.MessageId equals m.Id
                          join c in db.Conversations.AsNoTracking() on k.ConversationId equals c.Id
                          select new PendingClear(k.Id, c.ChannelId, c.ExternalChatId, m.ExternalMessageId ?? "")).ToListAsync(ct);
        return await ClearRowsAsync(rows, ct);
    }

    /// <summary>Sweeper: протермінувати активні, прибрати розмітку не активних (повтор ≤ 3).</summary>
    public async Task<int> SweepAsync(int take, CancellationToken ct)
    {
        await keyboards.ExpireDueAsync(Math.Max(take, 100), ct);
        return await ClearRowsAsync(await keyboards.ListPendingClearAsync(take, ct), ct);
    }

    private async Task<int> ClearRowsAsync(IReadOnlyList<PendingClear> rows, CancellationToken ct)
    {
        var cleared = new List<Guid>();
        var withoutRef = rows.Where(r => string.IsNullOrEmpty(r.ExternalMessageId)).Select(r => r.KeyboardId).ToList();
        if (withoutRef.Count > 0) cleared.AddRange(withoutRef); // немає id повідомлення в каналі: прибирати нічого
        var port = services.GetService<IChannelActionsPort>();
        var tenantId = tenant.TenantId ?? throw new TenantScopeMissingException();
        foreach (var r in rows.Where(r => !string.IsNullOrEmpty(r.ExternalMessageId)))
        {
            if (port is null) { cleared.Add(r.KeyboardId); continue; }
            ChannelActionResult res;
            try
            {
                res = await port.ClearKeyboardAsync(tenantId, r.ChannelId, r.ExternalChatId, r.ExternalMessageId, ct);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or TenantMismatchException or TenantScopeMissingException))
            {
                log.LogWarning("telegram_clear_failed keyboard={Keyboard} error={ErrorType}", r.KeyboardId, ex.GetType().Name);
                res = ChannelActionResult.Failed;
            }
            switch (res)
            {
                case ChannelActionResult.Ok or ChannelActionResult.Unsupported or ChannelActionResult.ChannelNotFound:
                    cleared.Add(r.KeyboardId); // Unsupported/NotFound: правка неможлива, повтор нічого не змінить
                    break;
                default:
                    await keyboards.RecordClearFailureAsync(r.KeyboardId, ct);
                    break;
            }
        }
        if (cleared.Count > 0) await keyboards.MarkClearedAsync(cleared, ct);
        return cleared.Count;
    }
}
