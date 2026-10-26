using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Infrastructure.AI.Beauty;

/// <summary>
/// Факти політики закладу для чату клієнта (TASK-741, §22.9, ADR-012): налаштування нагадувань tenant, наявність телефону клієнта,
/// умови скасування (§11) і телефон закладу. Читає КОД з БД свого tenant (RLS), клієнт і модель на них не впливають.
/// Джерело і для блоку «ФАКТИ ЗАКЛАДУ» в промпті, і для вихідного <see cref="PolicyClaimGuard"/>.
/// </summary>
/// <param name="ClientHasPhone">У картці клієнта розмови є телефон (у чаті його не запитуємо).</param>
public sealed record ChatPolicyFacts(
    bool TelegramEnabled, bool CallEnabled, bool ClientHasPhone, TimeOnly QuietStart, TimeOnly QuietEnd,
    CancellationSettings Cancellation, string? LocationPhone)
{
    /// <summary>Дзвінок-завдання адміністратору можливий: дзвінки ввімкнено в налаштуваннях tenant і в клієнта є телефон (§22.7).</summary>
    public bool CallAvailable => CallEnabled && ClientHasPhone;

    /// <summary>Консервативні факти, коли порт недоступний: дзвінка немає, умови скасування типові.</summary>
    public static readonly ChatPolicyFacts Default =
        new(true, true, false, new TimeOnly(22, 0), new TimeOnly(8, 0), CancellationSettings.Defaults, null);
}

/// <summary>Порт фактів політики для чату. Реалізація - Adapters/ChatPolicyFactsAdapter (звіряє tenant зі scope); у тестах - fake.</summary>
public interface IChatPolicyFactsPort
{
    /// <param name="clientId">Клієнт розмови; null - клієнта ще немає (телефону немає).</param>
    /// <param name="locationId">Заклад каналу; null - телефон закладу не віддається.</param>
    Task<ChatPolicyFacts> GetAsync(Guid tenantId, Guid? clientId, Guid? locationId, CancellationToken ct);
}
