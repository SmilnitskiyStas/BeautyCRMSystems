# ADR-004: Multi-Channel Integration & Mock Adapters

**Date:** 2026-10-07

**Status:** Accepted (TASK-676)

**Context:**

Beauty CRM must integrate with multiple external channels (Telegram, Instagram, WhatsApp, etc.) for:
- Inbound: Receive customer messages, booking requests
- Outbound: Send confirmations, reminders, promotions

**Challenges:**
1. Each channel has different API (authentication, message format, webhooks)
2. Development requires live credentials (expensive, risky)
3. Testing must not spam real customers
4. Channel failures should not crash the platform

**Solution:** Adapter pattern with switchable mocks for development/testing.

---

## Decision

We implement a **channel adapter pattern** with:

1. **IChannelAdapter interface:** Unified API for all channels
2. **Concrete adapters:** TelegramAdapter, InstagramAdapter, + mocks (ViberMockAdapter, etc.)
3. **Configuration:** Switch between real & mock adapters via `Channels:UseMocks` flag
4. **Encryption:** Channel secrets (tokens, webhooks) stored encrypted in database
5. **Webhook signature verification:** Each channel's auth method (X-Telegram-Bot-Api-Secret-Token, X-Hub-Signature-256, etc.)

---

## Architecture

### IChannelAdapter (Port)

```csharp
public interface IChannelAdapter
{
    string Channel { get; } // "telegram", "instagram", etc.
    
    // Inbound webhook verification
    bool VerifySignature(WebhookRequest request, string secret);
    
    // Parse incoming message
    Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body, CancellationToken ct);
    
    // Send outbound message
    Task SendAsync(OutboundMessage message, CancellationToken ct);
}

public record InboundMessage(
    string ExternalMessageId,
    string ExternalChatId,
    string Body,
    DateTimeOffset SentAt
);

public record OutboundMessage(
    string ExternalChatId,
    string Body
);

public record WebhookRequest(
    Dictionary<string, StringValues> Headers,
    Dictionary<string, StringValues> Query,
    string Body
);
```

### Concrete Adapters

#### TelegramAdapter

```csharp
public class TelegramAdapter : IChannelAdapter
{
    public string Channel => "telegram";
    
    public bool VerifySignature(WebhookRequest request, string secret)
    {
        // Telegram: X-Telegram-Bot-Api-Secret-Token header
        if (!request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var token))
            return false;
        
        // Constant-time comparison to prevent timing attack
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token.ToString()),
            Encoding.UTF8.GetBytes(secret));
    }
    
    public async Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body, CancellationToken ct)
    {
        var update = JsonSerializer.Deserialize<TelegramUpdate>(body);
        if (update?.Message is null) return [];
        
        return [new InboundMessage(
            ExternalMessageId: update.Message.MessageId.ToString(),
            ExternalChatId: update.Message.Chat.Id.ToString(),
            Body: update.Message.Text,
            SentAt: DateTimeOffset.FromUnixTimeSeconds(update.Message.Date)
        )];
    }
    
    public async Task SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var payload = new { chat_id = message.ExternalChatId, text = message.Body };
        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync(
            $"https://api.telegram.org/bot{_token}/sendMessage",
            payload,
            ct);
        
        if (!response.IsSuccessStatusCode)
            throw new ChannelException($"Telegram API error: {response.StatusCode}");
    }
}
```

#### InstagramAdapter (Meta)

```csharp
public class InstagramAdapter : IChannelAdapter
{
    public string Channel => "instagram";
    
    public bool VerifySignature(WebhookRequest request, string secret)
    {
        // Instagram (Meta): X-Hub-Signature-256 HMAC-SHA256
        if (!request.Headers.TryGetValue("X-Hub-Signature-256", out var sig))
            return false;
        
        var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(request.Body));
        var expected = "sha256=" + BitConverter.ToString(hash).Replace("-", "").ToLower();
        
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(sig.ToString()),
            Encoding.UTF8.GetBytes(expected));
    }
    
    public async Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<InstagramWebhook>(body);
        if (payload?.Entry is null) return [];
        
        var messages = new List<InboundMessage>();
        foreach (var entry in payload.Entry)
        {
            foreach (var msg in entry.Messaging ?? [])
            {
                if (msg.Message?.Text is not null)
                {
                    messages.Add(new InboundMessage(
                        ExternalMessageId: msg.Message.Mid,
                        ExternalChatId: msg.Sender.Id,
                        Body: msg.Message.Text,
                        SentAt: DateTimeOffset.FromUnixTimeSeconds(msg.Timestamp)
                    ));
                }
            }
        }
        return messages;
    }
    
    public async Task SendAsync(OutboundMessage message, CancellationToken ct)
    {
        // Outside 24-hour window? Reject.
        // (Operator must use handoff + templates for promotional messages)
        
        var payload = new
        {
            recipient = new { id = message.ExternalChatId },
            message = new { text = message.Body }
        };
        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync(
            $"https://graph.instagram.com/v18.0/me/messages?access_token={_accessToken}",
            payload,
            ct);
        
        if (!response.IsSuccessStatusCode)
            throw new ChannelException($"Instagram API error: {response.StatusCode}");
    }
}
```

#### Mock Adapters

```csharp
public class ViberMockAdapter : IChannelAdapter
{
    public string Channel => "viber";
    
    public bool VerifySignature(WebhookRequest request, string secret)
    {
        // Mock: always accept
        return true;
    }
    
    public async Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body, CancellationToken ct)
    {
        // Mock: parse as-is or return empty
        return [];
    }
    
    public async Task SendAsync(OutboundMessage message, CancellationToken ct)
    {
        // Mock: log only, don't actually send
        _logger.LogInformation("Viber (mock): {ExternalChatId}: {Body}",
            message.ExternalChatId, message.Body);
        
        await Task.CompletedTask;
    }
}
```

---

## Secret Encryption (beauty_channels.credentials_encrypted)

Channel tokens & webhook secrets are **AES-256-GCM encrypted** in the database:

```csharp
public class AesGcmSecretProtector(IConfiguration config)
{
    public string Encrypt(string plaintext)
    {
        var keyBase64 = config["Channels:EncryptionKey"];
        var key = Convert.FromBase64String(keyBase64); // 32 bytes
        
        using var aes = new AesGcm(key);
        byte[] nonce = new byte[12]; // 96-bit nonce
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(nonce);
        
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] ciphertext = new byte[plainBytes.Length];
        byte[] tag = new byte[16]; // 128-bit auth tag
        
        aes.Encrypt(nonce, plainBytes, null, ciphertext, tag);
        
        // Return: nonce + ciphertext + tag (all base64)
        var combined = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length + ciphertext.Length, tag.Length);
        
        return Convert.ToBase64String(combined);
    }
    
    public string Decrypt(string ciphertext)
    {
        var keyBase64 = config["Channels:EncryptionKey"];
        var key = Convert.FromBase64String(keyBase64);
        
        var combined = Convert.FromBase64String(ciphertext);
        var nonce = combined[..12];
        var ct = combined[12..^16];
        var tag = combined[^16..];
        
        using var aes = new AesGcm(key);
        byte[] plaintext = new byte[ct.Length];
        
        aes.Decrypt(nonce, ct, null, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
```

**Usage:**
```csharp
var channel = new Channel
{
    Type = "telegram",
    CredentialsEncrypted = _protector.Encrypt(
        JsonSerializer.Serialize(new { token = "123:ABC..." })
    ),
    CredentialsLast4 = "...ABC" // For display in API
};
```

---

## Configuration

### Environment Variables

**Development:**
```
Channels__UseMocks=true
```

**Production:**
```
Channels__UseMocks=false
Channels__EncryptionKey=<base64-32-bytes>
TELEGRAM_BOT_TOKEN=<token>
INSTAGRAM_APP_SECRET=<secret-for-HMAC>
INSTAGRAM_VERIFY_TOKEN=<verify-token-for-handshake>
```

### Registration & DI Setup

```csharp
public static IServiceCollection AddBeautyChannels(
    this IServiceCollection services, bool useMocks)
{
    // In Development, useMocks=true → register only mocks
    // Outside Development, useMocks=true is an ERROR: API refuses to start
    if (useMocks && !env.IsDevelopment())
        throw new InvalidOperationException("Channels:UseMocks=true is not allowed outside Development");

    // Register all adapter instances
    services.AddSingleton<TelegramAdapter>();
    services.AddSingleton<InstagramAdapter>();
    services.AddSingleton<ViberMockAdapter>();
    services.AddSingleton<WhatsAppMockAdapter>();
    
    // Registry: select real or mock based on config
    services.AddSingleton<ChannelRegistry>(provider =>
    {
        var registry = new ChannelRegistry();
        registry.Register("telegram", useMocks
            ? provider.GetRequiredService<TelegramMockAdapter>()
            : provider.GetRequiredService<TelegramAdapter>());
        registry.Register("instagram", useMocks
            ? provider.GetRequiredService<InstagramMockAdapter>()
            : provider.GetRequiredService<InstagramAdapter>());
        registry.Register("viber", provider.GetRequiredService<ViberMockAdapter>());
        registry.Register("whatsapp", provider.GetRequiredService<WhatsAppMockAdapter>());
        return registry;
    });
    
    services.AddScoped<AesGcmSecretProtector>();
    services.AddScoped<WebhookIngestService>();
    
    return services;
}
```

---

## Webhook Handling

### Controller

```csharp
[ApiController]
[Route("api/beauty/webhooks/{channel}/{channelId:guid}")]
[RequireModule("beauty_channels")]
public class WebhooksController(WebhookIngestService ingest)
{
    [HttpPost]
    [AllowAnonymous] // Public endpoint
    public async Task<IActionResult> Ingest(
        string channel, Guid channelId, [FromBody] string body, CancellationToken ct)
    {
        // Extract signature from headers
        var sig = Request.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToString());
        
        var result = await ingest.ProcessAsync(
            channel, channelId, body, sig, ct);
        
        return result.Success ? Ok() : Unauthorized();
    }
    
    [HttpGet]
    [AllowAnonymous] // Meta hub.challenge
    public IActionResult Challenge([FromQuery] string hub_challenge)
    {
        // Instagram webhook verification
        return Ok(hub_challenge);
    }
}
```

### Service

```csharp
public class WebhookIngestService(ChannelRegistry registry, ChannelRequestContext ctx)
{
    public async Task<Result> ProcessAsync(
        string channel, Guid channelId, string body,
        Dictionary<string, string> headers, CancellationToken ct)
    {
        // 1. Resolve tenant & channel from channelId
        var dbChannel = await db.GetChannelAsync(channelId, ct);
        if (dbChannel is null || dbChannel.Type != channel)
            return Result.Unauthorized();
        
        ctx.SetTenant(dbChannel.TenantId);
        ctx.SetChannel(channelId);
        
        // 2. Verify signature
        var adapter = registry.Get(channel);
        if (!adapter.VerifySignature(
            new WebhookRequest(headers, body), dbChannel.GetSecret()))
            return Result.Unauthorized();
        
        // 3. Parse & store messages
        var messages = await adapter.ParseInboundAsync(body, ct);
        foreach (var inMsg in messages)
        {
            await db.StoreInboundMessageAsync(
                channelId, inMsg, idempotencyKey: $"{channel}:{inMsg.ExternalMessageId}", ct);
        }
        
        // 4. Trigger AI processing (async)
        await _queue.EnqueueProcessConversation(dbChannel.TenantId, channelId, ct);
        
        return Result.Ok();
    }
}
```

---

## Instagram Webhook Secrets (Meta Handshake)

Instagram uses two separate secrets:

1. **`appSecret`** — used for POST (webhook delivery) signature verification
   - Format: HMAC-SHA256 key
   - Header: `X-Hub-Signature-256: sha256=<hex>`
   - Verification: HMAC-SHA256(body, secret).toHex()

2. **`verifyToken`** — used for GET (webhook subscription handshake)
   - Format: arbitrary string (≤512 chars)
   - Query param: `hub.verify_token`
   - Handshake: returns `hub.challenge` if token matches

**Storage:** Both secrets encrypted in `beauty_channels.credentials_encrypted` as JSON:
```json
{ "appSecret": "...", "verifyToken": "..." }
```

**Webhook flow:**
```
1. GET /webhooks/instagram/{channelId}?hub.verify_token=...&hub.challenge=...
   → Decrypt credentials, check verifyToken, return hub.challenge
   
2. POST /webhooks/instagram/{channelId}
   → Verify X-Hub-Signature-256 using appSecret
   → Parse & store messages
```

**API update (§14.4):**
- `PUT /api/beauty/channels/{id}` accepts optional `appSecret` & `verifyToken` (null/omitted = no change)
- Response includes `hasAppSecret`, `hasVerifyToken` (booleans, secrets not returned)
- 409 `channel_id_conflict` if id already exists in another tenant

---

## Consequences

### Positive

1. **Extensible:** Add new channels by implementing IChannelAdapter
2. **Testable:** Mock adapters for unit/integration tests (no real API calls)
3. **Safe development:** Use mocks during development, swap to real in production
4. **Secure:** Secrets encrypted in database
5. **Unified API:** All channels behind one interface

### Negative

1. **Per-channel quirks:** Meta 24-hour window, Telegram rate limits, Instagram throttling
   - **Mitigation:** Document in adapter comments; handle in application logic (HANDOFF for out-of-window)
2. **Encryption overhead:** Decrypt every channel access
   - **Mitigation:** Cache decrypted credentials in-memory per request (AsyncLocal<>)

---

## Implementation Status

- [x] TelegramAdapter (TASK-676)
- [x] InstagramAdapter (TASK-676)
- [x] Mock adapters (TASK-676)
- [x] AES-GCM encryption (TASK-676)
- [ ] WhatsApp, Facebook, Widget (future)

---

## References

- [Telegram Bot API](https://core.telegram.org/bots/api)
- [Meta Messenger Platform](https://developers.facebook.com/docs/messenger-platform/)
- [Instagram Webhooks](https://developers.facebook.com/docs/instagram-api/webhooks)
- `backend/BeautyCrm.Infrastructure/Integrations/Channels/` — Implementation
- `.claude/docs/api.md` — `/webhooks` endpoints
