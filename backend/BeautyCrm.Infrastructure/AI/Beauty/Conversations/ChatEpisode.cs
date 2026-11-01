using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

/// <summary>Повідомлення розмови, що може бути межею епізоду (лише поля, потрібні для класифікації).</summary>
public sealed record EpisodeCandidate(DateTimeOffset At, bool Inbound, bool System, string Kind, string Body, string? CallbackData);

/// <summary>
/// Межа епізоду діалогу для LLM (TASK-799). Модель бачить лише повідомлення ПІСЛЯ останньої межі; старі лишаються в БД та Inbox.
/// Межі (без нової колонки, з існуючих даних): /book і кнопка «Записатися»; T-book-start; T-confirmed (запис підтверджено);
/// T-cancelled (запис скасовано через чат); журнал resolve_conversation / resume_ai; тиша довша за поріг перед поточним пакетом.
/// Звичайне продовження діалогу межі не створює.
/// </summary>
public static class ChatEpisode
{
    /// <summary>Журнальні дії менеджера, що закривають епізод.</summary>
    public static readonly string[] ManagerBoundaryTools = ["resolve_conversation", "resume_ai"];

    public static bool IsBoundary(EpisodeCandidate c)
    {
        if (c.Inbound)
            return (c.Kind == InboundKinds.Command && ChatCommands.Parse(c.Body) == ChatCommand.Book)
                || (c.Kind == InboundKinds.Callback && c.CallbackData == ButtonVerbs.Book);
        return c.System && (c.Body == ChatTemplates.BookStart || c.Body.StartsWith(ChatTemplates.ConfirmedHeader, StringComparison.Ordinal)
            || ChatTemplates.IsCancelledBody(c.Body));
    }

    /// <summary>
    /// Момент, строго після якого повідомлення йдуть у контекст моделі (null - без межі).
    /// <paramref name="visibleTimes"/> - часи повідомлень історії за зростанням; <paramref name="batchStart"/> - час першого повідомлення поточного пакета.
    /// Кандидати пізніші за <paramref name="batchEnd"/> ігноруються.
    /// </summary>
    public static DateTimeOffset? FindCutoff(IEnumerable<EpisodeCandidate> candidates, DateTimeOffset? managerActionAt,
        IReadOnlyList<DateTimeOffset> visibleTimes, DateTimeOffset batchStart, DateTimeOffset batchEnd, TimeSpan idleTimeout)
    {
        DateTimeOffset? cutoff = managerActionAt is { } m && m <= batchEnd ? m : null;
        foreach (var c in candidates)
            if (c.At <= batchEnd && IsBoundary(c) && (cutoff is null || c.At > cutoff)) cutoff = c.At;

        var prev = (DateTimeOffset?)null;
        foreach (var t in visibleTimes.Append(batchStart).OrderBy(x => x))
        {
            if (prev is { } p && t - p > idleTimeout && (cutoff is null || p > cutoff)) cutoff = p;
            prev = t;
        }
        return cutoff;
    }
}
