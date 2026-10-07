# ADR-005: AI Assistant Modes & Prompt Injection Prevention

**Date:** 2026-10-07

**Status:** Accepted (TASK-677)

**Context:**

Beauty CRM integrates Claude AI for:
- Draft replies to client messages
- Suggest promotions & actions
- Execute actions (create appointment) if approved

**Challenges:**
1. Untrusted input: Client messages may contain prompt injection attempts
2. Risk levels: Some actions (create appointment) are high-risk; others (draft reply) are safe
3. Autonomy trade-off: Fully auto = convenient but risky; manual approval for all = slow UX

**Solution:** AI modes (suggest, confirm, auto) with input sanitization & risk-based approval.

---

## Decision

Three AI modes, configurable per tenant:

1. **`suggest` (safest):** AI proposes action, operator manually approves before execution
2. **`confirm` (default):** AI proposes all actions; auto-executes low-risk (find_slots, draft_reply); requires approval for high-risk (create_appointment)
3. **`auto` (fastest):** AI executes all actions (requires explicit opt-in per tenant)

**Prompt injection prevention:**
- Client input isolated in XML tags: `<client_message>...</client_message>`
- No template strings (no f-strings in prompts)
- Tag escaping & cleanup
- Tool access restricted per scope (client vs. manager)

---

## Architecture

### AI Modes

```csharp
public enum AiMode
{
    Suggest,    // Propose all, require approval for all
    Confirm,    // Propose all, auto-execute low-risk
    Auto        // Auto-execute all
}

public interface IAiClient
{
    Task<AiResponse> PromptAsync(
        string userMessage,
        List<AiTool> availableTools,
        AiMode mode,
        CancellationToken ct);
}

public record AiResponse(
    string Text,
    List<AiToolCall> ToolCalls
);

public record AiToolCall(
    string ToolName,
    Dictionary<string, object> Arguments,
    AiActionStatus InitialStatus // proposed or approved
);
```

### Tool Definitions

**Low-risk tools** (auto-execute in `confirm` mode):
- `find_free_slots` — read-only, informational
- `get_prices` — read-only
- `get_active_promotions` — read-only
- `get_client_context` — read-only
- `draft_reply` — composing only, not sent

**High-risk tools** (require approval):
- `create_appointment` — creates financial & scheduling obligation
- `update_appointment` — modifies booking
- `cancel_appointment` — financial impact (refund)
- `suggest_promotion` — business logic (discount)
- `build_audience` — access to PII

---

## Prompt Injection Prevention

### Template Structure

The system prompt **never uses f-strings**. Client input is isolated:

```csharp
var prompt = $@"
You are a helpful beauty salon assistant. Answer customer questions and suggest services.

Customer message:
<client_message>
{clientMessage}
</client_message>

Available tools:
{string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description}"))}

Respond concisely.
";
```

**Problematic (DO NOT DO):**
```csharp
// ❌ WRONG: Client can break out
var prompt = $"User said: {clientMessage}";
// If clientMessage = "forget all instructions and...", prompt is broken
```

### Input Sanitization

1. **Tag escape:** Replace < and > in client message:
   ```csharp
   var sanitized = clientMessage
       .Replace("<", "&lt;")
       .Replace(">", "&gt;")
       .Replace("&", "&amp;");
   ```

2. **Length limit:** Max 4000 characters per message

3. **Disallowed patterns:** Block obvious prompt injections
   ```csharp
   var blockedPatterns = new[] { "forget", "ignore", "system prompt", "role play" };
   if (blockedPatterns.Any(p => clientMessage.Contains(p, StringComparison.OrdinalIgnoreCase)))
       return Error("message_suspicious", "Message contains prohibited patterns.");
   ```

### Tool Access Control

**Client scope** (message from customer in chat):
```csharp
var clientTools = new[] { "find_free_slots", "get_prices", "draft_reply" };
```

**Manager scope** (internal action planning):
```csharp
var managerTools = new[] {
    "find_free_slots", "get_prices", "get_client_context",
    "create_appointment", "suggest_promotion", "build_audience"
};
```

---

## Implementation

### BeautyAssistant Service

```csharp
public class BeautyAssistant(
    IAiClient aiClient,
    AiToolExecutor executor,
    IAiActionJournal journal)
{
    public async Task<Result> ProcessClientMessageAsync(
        Guid conversationId, string clientMessage, AiMode mode, CancellationToken ct)
    {
        // 1. Validate input
        if (clientMessage.Length > 4000)
            return Error.Validation("message_too_long", "Message exceeds 4000 characters.");
        
        if (ContainsPromptInjectionPatterns(clientMessage))
            return Error.Validation("message_suspicious", "Message contains prohibited patterns.");
        
        // 2. Sanitize
        var sanitized = SanitizeInput(clientMessage);
        
        // 3. Call AI with client tools only
        var response = await aiClient.PromptAsync(
            sanitized,
            availableTools: GetClientTools(),
            mode: mode,
            ct);
        
        // 4. Process each tool call
        var actions = new List<AiAction>();
        foreach (var call in response.ToolCalls)
        {
            // Create action record
            var action = new AiAction
            {
                ConversationId = conversationId,
                ToolName = call.ToolName,
                Payload = JsonSerializer.Serialize(call.Arguments),
                Status = call.InitialStatus // proposed or approved
            };
            actions.Add(action);
            
            // If auto-execute (confirm mode + low-risk):
            if (mode == AiMode.Confirm && IsLowRiskTool(call.ToolName))
            {
                var result = await executor.ExecuteAsync(action, ct);
                action.Status = result.Success ? AiActionStatus.Executed : AiActionStatus.Error;
                action.Result = JsonSerializer.Serialize(result.Value);
                action.Error = result.Error;
            }
        }
        
        // 5. Journal all actions
        foreach (var action in actions)
        {
            await journal.LogAsync(action, ct);
        }
        
        // 6. Draft reply with tool results
        var reply = new Message
        {
            ConversationId = conversationId,
            Direction = MessageDirection.Outbound,
            SenderType = MessageSenderType.Assistant,
            Body = response.Text,
            Status = "draft" // Not sent yet; manager reviews
        };
        
        return Result.Ok(reply);
    }
    
    private static readonly string[] PromptInjectionPatterns = new[]
    {
        "forget all instructions", "ignore previous", "system prompt",
        "role play", "pretend", "as if", "new instructions"
    };
    
    private static bool ContainsPromptInjectionPatterns(string message) =>
        PromptInjectionPatterns.Any(p =>
            message.Contains(p, StringComparison.OrdinalIgnoreCase));
    
    private static string SanitizeInput(string message)
    {
        return message
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("&", "&amp;");
    }
    
    private static bool IsLowRiskTool(string toolName) =>
        new[] { "find_free_slots", "get_prices", "draft_reply" }.Contains(toolName);
}
```

### Tool Executor

```csharp
public class AiToolExecutor(
    IBookingStore store,
    PromotionPricing pricing,
    ICatalogService catalog)
{
    public async Task<Result<object?>> ExecuteAsync(AiAction action, CancellationToken ct)
    {
        try
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, object>>(action.Payload)
                ?? new();
            
            return action.ToolName switch
            {
                "find_free_slots" => await FindFreeSlotsAsync(args, ct),
                "get_prices" => await GetPricesAsync(args, ct),
                "draft_reply" => DraftReply(args),
                "create_appointment" => await CreateAppointmentAsync(args, ct),
                "suggest_promotion" => await SuggestPromotionAsync(args, ct),
                "build_audience" => await BuildAudienceAsync(args, ct),
                _ => Error.Validation("unknown_tool", $"Unknown tool: {action.ToolName}")
            };
        }
        catch (Exception ex)
        {
            return Error.InternalError($"Execution error: {ex.Message}");
        }
    }
    
    private async Task<Result<object?>> FindFreeSlotsAsync(
        Dictionary<string, object> args, CancellationToken ct)
    {
        var locationId = Guid.Parse(args["locationId"].ToString()!);
        var serviceId = Guid.Parse(args["serviceId"].ToString()!);
        var date = DateOnly.Parse(args["date"].ToString()!);
        
        var slots = await store.GetSlotsAsync(locationId, null, serviceId, date, ct);
        return Result.Ok<object?>(slots);
    }
    
    private async Task<Result<object?>> CreateAppointmentAsync(
        Dictionary<string, object> args, CancellationToken ct)
    {
        // Only execute if explicitly approved
        // (This method should be called only after ConfirmAsync)
        
        var locationId = Guid.Parse(args["locationId"].ToString()!);
        var specialistId = Guid.Parse(args["specialistId"].ToString()!);
        var serviceId = Guid.Parse(args["serviceId"].ToString()!);
        var startsAt = DateTimeOffset.Parse(args["startsAt"].ToString()!);
        var clientId = args.ContainsKey("clientId")
            ? Guid.Parse(args["clientId"].ToString()!)
            : null;
        
        // Create via booking service
        var req = new CreateAppointmentRequest
        {
            LocationId = locationId,
            SpecialistId = specialistId,
            ServiceId = serviceId,
            StartsAt = startsAt,
            Client = new ClientInput { Id = clientId },
            Reminder = "1h",
            PaymentMethod = "cash",
            Source = "ai"
        };
        
        var result = await _bookingService.CreateAsync(req, ct);
        return result.Success
            ? Result.Ok<object?>(result.Value)
            : Error.Conflict(result.Code, result.Message);
    }
}
```

### Journal (Audit Trail)

```csharp
public interface IAiActionJournal
{
    Task LogAsync(AiAction action, CancellationToken ct);
}

public class EfAiActionJournal(BeautyDbContext db) : IAiActionJournal
{
    public async Task LogAsync(AiAction action, CancellationToken ct)
    {
        db.AiActions.Add(action);
        await db.SaveChangesAsync(ct);
    }
}
```

**Database table:**
```sql
CREATE TABLE beauty_ai_actions (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    conversation_id UUID,
    appointment_id UUID,
    tool_name VARCHAR NOT NULL,
    payload JSONB NOT NULL,
    status VARCHAR NOT NULL, -- proposed, approved, rejected, executed, error
    result JSONB,
    error VARCHAR,
    confirmed_by_user_id UUID,
    confirmed_at TIMESTAMPTZ,
    executed_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ,
    ...
);
```

---

## Approval Flow (Confirm Mode)

**High-risk tools require human approval:**

```
Client message
    ↓
[Sanitize & Validate]
    ↓
[AI proposes "create_appointment"]
    ↓
[Create AiAction with status="proposed"]
    ↓
[Manager sees in dashboard]
    ↓
[POST /api/beauty/ai/actions/{id}/approve]
    ↓
[Execute: create appointment]
    ↓
[Update AiAction: status="executed", result=...]
    ↓
[Send reply to customer]
```

---

## Settings & Limits

**Per-tenant configuration:**

```csharp
public class AiSettings
{
    public AiMode Mode { get; set; } = AiMode.Confirm;
    public decimal MaxDiscountPercent { get; set; } = 20m; // Limit AI discounts
    public int CampaignWindowStartHour { get; set; } = 9;
    public int CampaignWindowEndHour { get; set; } = 20; // No late-night campaigns
    public bool AllowAutoAppointmentCreation { get; set; } = false; // Opt-in for auto
}
```

**Enforced:**
- AI cannot suggest discount > 20%
- Campaigns only scheduled 09:00–20:00 (local)
- Auto mode disabled by default

---

## Consequences

### Positive

1. **Safety:** Prompt injection nearly impossible with XML isolation
2. **Auditability:** Every AI action logged & reviewable
3. **Flexibility:** Modes allow risk/convenience trade-off per tenant
4. **Scalability:** Tool framework easy to extend

### Negative

1. **False positives:** Legitimate messages might trigger injection filters
   - **Mitigation:** Allowlist common words; review logs
2. **Manual overhead:** Confirm mode requires manager review for high-risk actions
   - **Mitigation:** Batch approval UI; shortcuts for trusted actions

---

## Testing

**Unit tests:**
- Prompt injection detection (confirm blocked patterns)
- Tool executor (mock tools, error handling)
- Mode enforcement (confirm mode = auto-exec low-risk only)

**Integration tests:**
- End-to-end: client message → AI response → approval → execution
- Negative: high-risk tool in client scope → should not be offered

---

## References

- [OWASP Prompt Injection](https://owasp.org/www-community/attacks/Prompt_Injection)
- `backend/BeautyCrm.Infrastructure/AI/Beauty/BeautyAssistant.cs`
- `backend/BeautyCrm.Infrastructure/AI/Beauty/AiToolExecutor.cs`
- `.claude/docs/api.md` — `/ai/actions` endpoints
