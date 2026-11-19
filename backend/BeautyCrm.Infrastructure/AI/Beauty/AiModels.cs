using System.Text.Json;

namespace BeautyCrm.Infrastructure.AI.Beauty;

public enum AiMode { Suggest, Confirm, Auto }

public static class AiActionStatus
{
    public const string Done = "done";
    public const string Drafted = "drafted";
    public const string PendingConfirmation = "pending_confirmation";
    public const string Rejected = "rejected";
    public const string Reverted = "reverted";
    public const string HandedOff = "handed_off";
    public const string Flagged = "flagged";
    /// <summary>Внутрішні claim-статуси compare-and-swap (M2): дія вже виконується / відкочується, повторний approve/revert не можливий.</summary>
    public const string Executing = "executing";
    public const string Reverting = "reverting";
}

/// <summary>Запис журналу beauty_ai_actions {id, action, target, createdAt, status, revertible, conversationId?}.</summary>
public sealed record AiActionRecord(
    Guid Id, Guid TenantId, string Action, string Target, DateTimeOffset CreatedAt,
    string Status, bool Revertible, AiMode Mode, string PayloadJson, Guid? ConversationId = null);

// ---- AI-клієнт (незалежний від провайдера) ----
public abstract record AiBlock;
public sealed record AiTextBlock(string Text) : AiBlock;
public sealed record AiToolUseBlock(string Id, string Name, JsonElement Input) : AiBlock;
public sealed record AiToolResultBlock(string ToolUseId, string Content, bool IsError) : AiBlock;

public sealed record AiMessage(string Role, IReadOnlyList<AiBlock> Blocks)
{
    public static AiMessage User(string text) => new("user", new AiBlock[] { new AiTextBlock(text) });
}

public sealed record AiToolDefinition(string Name, string Description, string InputSchemaJson);

/// <summary>Ролі моделей (ADR-010 §2.2). У V1 викликається лише <c>chat</c>.</summary>
public static class AiRoles
{
    public const string Chat = "chat";
    public const string Analytics = "analytics";

    public static bool IsValid(string? role) => role is Chat or Analytics;
}

/// <summary>Режим автономності: парсинг/запис у beauty_ai_settings.mode. Невідоме значення = безпечний confirm.</summary>
public static class AiModes
{
    public static AiMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "suggest" => AiMode.Suggest,
        "auto" => AiMode.Auto,
        _ => AiMode.Confirm,
    };

    public static string ToDb(AiMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>
    /// Перехід на auto вимагає явної згоди (<c>acknowledgeAutoMode</c>, контракт §20.5). Використовує сервіс налаштувань (TASK-716):
    /// у БД немає окремої колонки згоди, тож «auto у сховищі» = згода була дана при запису.
    /// </summary>
    public static bool AutoRequiresAcknowledgement(AiMode current, AiMode requested, bool acknowledged) =>
        requested == AiMode.Auto && current != AiMode.Auto && !acknowledged;
}

public sealed record AiUsage(long InputTokens, long OutputTokens);

/// <param name="ClientChat">Виклик із чату клієнта (scope ClientChat): діє окремий бюджет чату на tenant (частка хвилинного ліміту, M2).</param>
public sealed record AiRequest(
    string System, IReadOnlyList<AiMessage> Messages, IReadOnlyList<AiToolDefinition> Tools, string Role = AiRoles.Chat,
    bool ClientChat = false);

public sealed record AiResponse(IReadOnlyList<AiBlock> Blocks, AiUsage? Usage = null)
{
    public string Text => string.Concat(Blocks.OfType<AiTextBlock>().Select(b => b.Text));
    public IReadOnlyList<AiToolUseBlock> ToolCalls => Blocks.OfType<AiToolUseBlock>().ToList();
}

public interface IAiClient
{
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct);
}

/// <summary>
/// Глобальні параметри виконання ШІ (ADR-010: роль «AiRuntimeOptions»): ліміт знижки, години розсилок, ітерації tools.
/// Модель, ключ, режим і ліміти токенів — НЕ тут, а per tenant (beauty_ai_settings, <see cref="AiTenantRuntime"/>).
/// <see cref="Model"/>, <see cref="ApiKeyEnvVar"/>, <see cref="MaxTokens"/> і <see cref="DefaultMode"/> лишені лише для
/// сумісності з наявними тестами і в бойовому шляху не використовуються;
/// <see cref="DefaultMode"/> діє тільки коли BeautyAssistant створено без <see cref="AiTenantRuntime"/> (юніт-тести).
/// </summary>
public sealed class AiSettings
{
    public string Model { get; set; } = "claude-sonnet-4-5";
    public string ApiKeyEnvVar { get; set; } = "ANTHROPIC_API_KEY";
    public AiMode DefaultMode { get; set; } = AiMode.Confirm;
    public decimal MaxDiscountPercent { get; set; } = 20m;
    /// <summary>Дозволені години розсилок [From, To) за локальним часом закладу.</summary>
    public int BroadcastFromHour { get; set; } = 9;
    public int BroadcastToHour { get; set; } = 20;
    public int MaxToolIterations { get; set; } = 6;
    public int MaxTokens { get; set; } = 1024;
}
