// Ports: the worker owns job logic only. DB access and channel delivery are
// implemented by other agents (database/integrations) and injected here.

export type ReminderOption = "none" | "1h" | "2h";
export type ReminderOffset = "1h" | "2h";
export type AppointmentStatus = "pending" | "confirmed" | "completed" | "cancelled" | "no_show";

export interface AppointmentInfo {
  id: string;
  tenantId: string;
  clientId: string;
  startsAt: Date;
  status: AppointmentStatus;
  reminderOption: ReminderOption;
}

export interface ClientInfo {
  id: string;
  tenantId: string;
  channel: string; // telegram | instagram | ...
  address: string; // channel-specific recipient id
  marketingConsent: boolean;
  name?: string;
}

export interface OutboundMessage {
  tenantId: string;
  clientId: string;
  channel: string;
  address: string;
  text: string;
  /** Idempotency key: same key => the message is created/sent at most once. */
  idempotencyKey: string;
  kind: "reminder" | "campaign" | "winback" | "review" | "message";
}

export interface StoredMessage extends OutboundMessage {
  id: string;
  status: "queued" | "sent" | "failed";
}

export interface CampaignInfo {
  id: string;
  tenantId: string;
  text: string;
  /** Allowed sending window, local hours [from, to). */
  allowedFromHour: number;
  allowedToHour: number;
  utcOffsetMinutes: number;
}

export interface LapsedClient extends ClientInfo {
  lastVisitAt: Date;
}

/** Data access (implemented over PostgreSQL by another agent). */
export interface BeautyDataPort {
  getAppointment(id: string): Promise<AppointmentInfo | null>;
  getClient(id: string): Promise<ClientInfo | null>;
  getCampaign(id: string): Promise<CampaignInfo | null>;
  /** Segment members; implementation may pre-filter, the job re-checks consent. */
  listCampaignAudience(campaignId: string): Promise<ClientInfo[]>;
  listLapsedClients(now: Date, inactiveDays: number): Promise<LapsedClient[]>;
  /** Create message row, or return the existing one for the same key. */
  upsertMessage(msg: OutboundMessage): Promise<{ message: StoredMessage; created: boolean }>;
  getMessage(id: string): Promise<StoredMessage | null>;
  setMessageStatus(id: string, status: StoredMessage["status"]): Promise<void>;
  /** Marketing (campaign/winback) messages of the current tenant sent since `since` (daily limit). */
  countMarketingSentSince(since: Date): Promise<number>;
  /** Persist a failed attempt (attempts/last_error); `final` => status "failed", otherwise stays queued ("pending"). */
  recordSendFailure(id: string, attempt: number, error: string, final: boolean): Promise<void>;
}

/** Channel delivery (Telegram/Instagram adapters, other agent). Throws on failure. */
export interface ChannelSenderPort {
  send(msg: StoredMessage): Promise<void>;
}

/** Idempotency claims, e.g. Redis SET NX or DB unique index. */
export interface IdempotencyPort {
  /** true if the caller now owns the key; false if already done or in progress. */
  claim(key: string): Promise<boolean>;
  complete(key: string): Promise<void>;
  /** Release the claim after a failed attempt so a retry can proceed. */
  release(key: string): Promise<void>;
}

export interface NotificationLogEntry {
  messageId: string;
  idempotencyKey: string;
  tenantId: string;
  kind: OutboundMessage["kind"];
  status: "enqueued" | "sent" | "failed" | "retry" | "skipped";
  attempt?: number;
  reason?: string;
  error?: string;
  at: Date;
}

export interface NotificationLogPort {
  append(entry: NotificationLogEntry): Promise<void>;
}

/** Queue access (BullMQ in prod, fake in tests). */
export interface QueuePort {
  enqueueOutbox(messageId: string): Promise<void>;
  /** Delayed enqueue of a reminder job. */
  enqueueReminder(data: { appointmentId: string; offset: ReminderOffset }, delayMs: number): Promise<void>;
  enqueueCampaign(campaignId: string, delayMs: number): Promise<void>;
}

export interface BeautyDeps {
  data: BeautyDataPort;
  sender: ChannelSenderPort;
  idempotency: IdempotencyPort;
  log: NotificationLogPort;
  queue: QueuePort;
  now: () => Date;
  /** Marketing messages per tenant per rolling 24 h; default DEFAULT_MARKETING_DAILY_LIMIT. */
  marketingDailyLimit?: number;
}
