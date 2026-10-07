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
}

/// <summary>Запис журналу beauty_ai_actions {id, action, target, createdAt, status, revertible}.</summary>
public sealed record AiActionRecord(
    Guid Id, Guid TenantId, string Action, string Target, DateTimeOffset CreatedAt,
    string Status, bool Revertible, AiMode Mode, string PayloadJson);

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

public sealed record AiRequest(
    string System, IReadOnlyList<AiMessage> Messages, IReadOnlyList<AiToolDefinition> Tools);

public sealed record AiResponse(IReadOnlyList<AiBlock> Blocks)
{
    public string Text => string.Concat(Blocks.OfType<AiTextBlock>().Select(b => b.Text));
    public IReadOnlyList<AiToolUseBlock> ToolCalls => Blocks.OfType<AiToolUseBlock>().ToList();
}

public interface IAiClient
{
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct);
}

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
