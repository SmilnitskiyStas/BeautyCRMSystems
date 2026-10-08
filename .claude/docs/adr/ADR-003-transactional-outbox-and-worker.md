# ADR-003: Transactional Outbox Pattern & Worker Processing

**Date:** 2026-10-07

**Status:** Accepted (TASK-675, TASK-678)

**Context:**

Beauty CRM needs to send messages (reminders, notifications, campaign messages) and process long-running jobs (e.g., send reminder 1 hour before appointment). Requirements:

1. **Reliability:** A job queued must eventually run, even if service restarts
2. **Idempotency:** If a job runs twice, it should be safe (e.g., don't send same reminder twice)
3. **No split-brain:** Booking + "send reminder" must be atomic from user's perspective
4. **Scalability:** Worker can process jobs in parallel, possibly on separate machines

Two approaches:
1. **Dual-write:** Save to DB, then enqueue in Redis/BullMQ (risky: one succeeds, other fails → data loss or duplicates)
2. **Transactional outbox:** Save data + job record in same database transaction, worker polls & processes

---

## Decision

We implement the **Transactional Outbox pattern**:

1. **Outbox table:** `beauty_messages` with status (`pending`, `sent`, `failed`) and idempotency key
2. **Atomic writes:** Booking + message record saved in single database transaction
3. **Worker polls:** BullMQ job reads messages with status `pending`, processes, updates status
4. **Idempotency:** Each message has unique `idempotency_key` per tenant; if duplicate, skip
5. **Retries:** Failed messages retry up to 3 times with exponential backoff

### Tables Involved

**beauty_messages:**
```sql
id              UUID PRIMARY KEY
tenant_id       UUID (RLS)
conversation_id UUID FK
direction       VARCHAR ('inbound', 'outbound')
sender_type     VARCHAR ('client', 'assistant', 'specialist', 'system')
body            TEXT
status          VARCHAR ('received', 'draft', 'pending', 'sent', 'failed')
attempts        INT (0–3)
last_error      VARCHAR (nullable)
idempotency_key VARCHAR (unique per tenant, dedup for webhooks)
sent_at         TIMESTAMPTZ
created_at      TIMESTAMPTZ
```

**beauty_reminders:**
```sql
id              UUID PRIMARY KEY
tenant_id       UUID (RLS)
appointment_id  UUID FK
scheduled_at    TIMESTAMPTZ (time to send)
status          VARCHAR ('scheduled', 'sent', 'failed')
sent_at         TIMESTAMPTZ (nullable)
error           VARCHAR (nullable)
created_at      TIMESTAMPTZ
```

---

## How It Works

### Booking with Reminder

**Application flow:**
```csharp
public async Task<Result<AppointmentDto>> CreateAsync(CreateAppointmentRequest req, CancellationToken ct)
{
    // In a single transaction:
    using var txn = await db.BeginTransactionAsync(ct);
    
    // 1. Save appointment
    var appt = new Appointment { ... };
    db.Appointments.Add(appt);
    
    // 2. If reminder needed, schedule it
    if (req.Reminder == "1h")
    {
        var reminder = new Reminder
        {
            AppointmentId = appt.Id,
            ScheduledAt = appt.StartsAt - TimeSpan.FromHours(1),
            Status = ReminderStatus.Scheduled
        };
        db.Reminders.Add(reminder);
    }
    
    // 3. Create outbox message (queued for sending)
    var message = new Message
    {
        ConversationId = resolvedConversationId,
        Direction = MessageDirection.Outbound,
        SenderType = MessageSenderType.System,
        Body = "Your appointment is confirmed...",
        Status = "pending",
        IdempotencyKey = $"reminder:{reminder.Id}" // Dedup key
    };
    db.Messages.Add(message);
    
    // All saved in one transaction
    await db.SaveChangesAsync(ct);
    await txn.CommitAsync(ct);
    
    // Return to user immediately (job will be processed asynchronously)
    return Result<AppointmentDto>.Ok(new AppointmentDto(appt));
}
```

**Database state after commit:**
- `beauty_appointments`: new record, status `pending`
- `beauty_reminders`: new record, status `scheduled`
- `beauty_messages`: new record, status `pending`, `idempotency_key = "reminder:..."`

---

### Worker Processing (BullMQ)

**Job: beauty-reminder**
```typescript
// worker/src/jobs/beauty-reminder.ts

export async function handleBeautyReminder(job: Job<ReminderJob>) {
  const { appointmentId, offset } = job.data;
  const idempotencyKey = `reminder:${appointmentId}:${offset}`;

  // Claim the job (update status to prevent double-processing)
  const claimed = await db.updateReminderStatus(
    appointmentId,
    "scheduled",
    "processing",
    idempotencyKey
  );

  if (!claimed) {
    // Another worker already claimed it
    return;
  }

  try {
    // Fetch appointment details
    const appt = await db.getAppointment(appointmentId);
    if (!appt || appt.status !== "confirmed") {
      // Skip if cancelled or not confirmed
      await db.updateReminderStatus(appointmentId, "processing", "sent");
      return;
    }

    // Send via channel adapter
    const message = `Reminder: Your appointment with ${appt.specialist} is in ${offset}...`;
    await channelAdapter.send(appt.client.phone, message);

    // Mark as sent
    await db.updateReminderStatus(appointmentId, "processing", "sent");
    await db.updateMessageStatus(idempotencyKey, "sent");
  } catch (error) {
    // Increment attempts; BullMQ handles retries with backoff
    await db.updateReminderStatus(
      appointmentId,
      "processing",
      "failed",
      error.message
    );
    throw error; // Let BullMQ retry up to 3 times
  }
}
```

**Job: beauty-outbox**
```typescript
// worker/src/jobs/beauty-outbox.ts

export async function handleOutbox(job: Job) {
  // Poll for messages with status 'pending'
  const messages = await db.getMessagesWithStatus("pending", limit: 100);

  for (const msg of messages) {
    // Claim the message
    const claimed = await db.claimMessage(msg.id);
    if (!claimed) continue;

    try {
      // Get channel adapter
      const channel = await db.getChannel(msg.channelId);
      const adapter = channelRegistry.get(channel.type);

      // Send
      await adapter.send(msg);

      // Mark sent
      await db.updateMessageStatus(msg.id, "sent", 1); // 1 attempt
    } catch (error) {
      // Increment attempts; retry if < 3
      const newAttempts = msg.attempts + 1;
      const newStatus = newAttempts >= 3 ? "failed" : "pending";
      await db.updateMessageStatus(msg.id, newStatus, newAttempts, error.message);

      if (newAttempts < 3) {
        // Re-enqueue with backoff
        await queue.add({ messageId: msg.id }, {
          delay: 30000 * Math.pow(2, newAttempts - 1), // 30s, 60s, 120s
        });
      }
    }
  }
}
```

---

## Idempotency

### Deduplication Key

Each outbox message has a **unique idempotency key per tenant**:

```
Format: "<job_type>:<resource_id>:<timestamp_or_offset>"
Examples:
  - "reminder:appt-123:1h"
  - "campaign:client-456:2026-10-07"
  - "telegram:msg:12345:retry-1"
```

**DB constraint:**
```sql
UNIQUE (tenant_id, idempotency_key) DEFERRABLE INITIALLY DEFERRED
```

**Webhook deduplication:**
When Telegram/Instagram re-delivers a webhook (if our ACK was lost), we check idempotency_key:
```typescript
const key = `${channel}:${channelId:N}:${externalMessageId}`;
const msg = await db.getMessageByIdempotencyKey(tenantId, key);

if (msg && msg.status !== "draft") {
  // Already received/sent, don't process again
  return 200; // ACK
}
```

**Deduplication key:** `{channel}:{channelId:N}:{externalMessageId}` where `channelId` is the `beauty_channels.id` (UUID, no hyphens).
This prevents double-processing if webhook provider re-delivers due to our timeout or connection drop.

---

## Retry Strategy

**Max 3 attempts total** (`OUTBOX_MAX_ATTEMPTS = 3`, `OUTBOX_BACKOFF_MS = 5000`):

| Attempt | Delay before | Status after failure | Action |
|---------|---------|----------|--------|
| 1 | (none) | failed retry | `throw` → BullMQ re-enqueues |
| 2 | 5 seconds (exponential) | failed retry | `throw` → BullMQ re-enqueues |
| 3 | 10 seconds (exponential: 5s × 2^1) | final failure | `recordSendFailure(msg.id, 3, error, final=true)` |

**Backoff formula:** `delayMs = OUTBOX_BACKOFF_MS × 2^(attemptNumber - 1)`
- Attempt 1 fails → delay = 0ms (immediate retry via BullMQ)
- Attempt 2 fails → delay = 5_000ms × 2^0 = 5s
- Attempt 3 fails → delay = 5_000ms × 2^1 = 10s
- After attempt 3: mark `failed`, do not retry further

**Permanent failures (no retry, mark failed immediately):**
- 4xx HTTP (invalid request, bad token)
- Instagram 24-hour window closed (permanent error flag set)
- Channel disabled or deleted
- Recipient unsubscribed from marketing

**Transient failures (retry with backoff):**
- Network timeout
- 5xx server error
- Rate limit (429)
- Any unhandled exception thrown by adapter

---

## Scheduling (Future Reminders)

**Reminders scheduled in advance** are stored in `beauty_reminders`, not immediately enqueued.

**Separate job: beauty-reminder-enqueue** (runs every 5 minutes):
```typescript
export async function handleReminderEnqueue() {
  // Find reminders that should run in the next 10 minutes
  const upcoming = await db.getRemindersScheduledBetween(
    now,
    now + 10.minutes
  );

  for (const reminder of upcoming) {
    // Enqueue job to run at exact time
    await queue.add(
      { appointmentId: reminder.appointmentId, offset: reminder.offset },
      { delay: reminder.scheduledAt - now }
    );
  }
}
```

---

## Consequences

### Positive

1. **No data loss:** Job record lives in database; survives worker crashes
2. **Idempotent:** Duplicate runs are safe (idempotency key prevents double-send)
3. **Auditable:** Full message history in `beauty_messages`
4. **Scalable:** Multiple workers can process in parallel (different job types or sharded by tenant)
5. **Transactional:** Booking + notification are atomic

### Negative

1. **Eventual consistency:** Notification sent after booking is confirmed (small delay)
   - **Mitigation:** Delay is milliseconds; acceptable for reminders/confirmations
2. **Polling overhead:** Worker polls messages table every second
   - **Mitigation:** INDEX on (status, created_at) for fast filtering; at scale, use PostgreSQL LISTEN/NOTIFY instead
3. **Database write amplification:** Each message written twice (initial + status update)
   - **Mitigation:** Acceptable; updates are indexed & fast

---

## Implementation Status

- [x] `beauty_messages` table (TASK-675)
- [x] `beauty_reminders` table (TASK-674)
- [x] Worker jobs (TASK-678)
- [ ] Webhook integration with idempotency (TASK-676)
- [ ] Outbox processor in worker (TASK-678, partial)

---

## Alternatives Considered

### 1. Dual-write (app → DB, then app → Redis)
**Pros:** Simpler setup (no polling)
**Cons:** If Redis succeeds but DB fails, job runs without record; no audit trail

### 2. Event sourcing
**Pros:** Full event history, replay
**Cons:** Complexity (aggregate roots, projections), harder to reason about current state

### 3. PostgreSQL LISTEN/NOTIFY instead of polling
**Pros:** Real-time, no polling overhead
**Cons:** Requires long-lived connection; harder to scale across multiple workers

---

## References

- [Transactional Outbox Pattern](https://microservices.io/patterns/data/transactional-outbox.html)
- `.claude/docs/database.md` — `beauty_messages` & `beauty_reminders` schema
- `backend/BeautyCrm.Application/Features/BeautyBooking/BookingService.cs` — Usage
- `worker/src/jobs/beauty-*.ts` — Job implementations
