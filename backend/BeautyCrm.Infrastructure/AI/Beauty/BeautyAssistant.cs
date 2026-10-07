using System.Text.Json;

namespace BeautyCrm.Infrastructure.AI.Beauty;

public sealed record AssistantRequest(
    Guid TenantId, Guid? ClientId, string ClientMessage, AiMode? Mode = null, AiScope Scope = AiScope.Client);

public sealed record AssistantResult(string? ReplyText, bool HandedOff, string? HandoffReason, bool InjectionSuspected, AiMode Mode);

/// <summary>Оркестрація: правила передачі -> захист від injection -> цикл tool-use з AI-клієнтом.</summary>
public sealed class BeautyAssistant
{
    private readonly IAiClient _ai;
    private readonly AiToolExecutor _tools;
    private readonly IAiActionJournal _journal;
    private readonly AiSettings _settings;
    private readonly TimeProvider _time;

    public BeautyAssistant(IAiClient ai, AiToolExecutor tools, IAiActionJournal journal, AiSettings settings, TimeProvider time)
    {
        _ai = ai; _tools = tools; _journal = journal; _settings = settings; _time = time;
    }

    public async Task<AssistantResult> HandleAsync(AssistantRequest req, CancellationToken ct)
    {
        // Режим лише з конфігурації/запиту системи; ніколи з тексту клієнта.
        var mode = req.Mode ?? _settings.DefaultMode;
        var target = req.ClientId is null ? "-" : $"client:{req.ClientId}";

        var handoff = HandoffRules.Evaluate(req.ClientMessage);
        if (handoff.Handoff)
        {
            await LogAsync(req.TenantId, "handoff", target, AiActionStatus.HandedOff, mode, JsonSerializer.Serialize(new { reason = handoff.Reason }), ct);
            return new AssistantResult(null, true, handoff.Reason, false, mode);
        }

        var suspicious = PromptInjectionGuard.IsSuspicious(req.ClientMessage);
        if (suspicious)
            await LogAsync(req.TenantId, "injection_suspected", target, AiActionStatus.Flagged, mode, "{}", ct);

        var ctx = new AiToolContext(req.TenantId, req.ClientId, mode, req.Scope);
        var messages = new List<AiMessage> { AiMessage.User(PromptInjectionGuard.Wrap(req.ClientMessage)) };
        var defs = AiToolCatalog.For(req.Scope);
        var system = PromptInjectionGuard.SystemPrompt + $"\nПоточний режим: {mode.ToString().ToLowerInvariant()}.";
        string? lastText = null;

        for (var n = 0; n < _settings.MaxToolIterations; n++)
        {
            var resp = await _ai.CompleteAsync(new AiRequest(system, messages, defs), ct);
            if (!string.IsNullOrWhiteSpace(resp.Text)) lastText = resp.Text;
            var calls = resp.ToolCalls;
            if (calls.Count == 0) break;

            messages.Add(new AiMessage("assistant", resp.Blocks));
            var results = new List<AiBlock>();
            foreach (var call in calls)
            {
                var r = await _tools.ExecuteAsync(ctx, call.Name, call.Input, ct);
                results.Add(new AiToolResultBlock(call.Id, r.Content, r.IsError));
            }
            messages.Add(new AiMessage("user", results));
        }

        return new AssistantResult(lastText, false, null, suspicious, mode);
    }

    private Task LogAsync(Guid tenantId, string action, string target, string status, AiMode mode, string payload, CancellationToken ct) =>
        _journal.AppendAsync(new AiActionRecord(Guid.NewGuid(), tenantId, action, target, _time.GetUtcNow(), status, false, mode, payload), ct);
}
