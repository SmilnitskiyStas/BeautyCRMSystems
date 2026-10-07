import type {
  AppointmentInfo, BeautyDeps, CampaignInfo, ClientInfo, LapsedClient, NotificationLogEntry, StoredMessage,
} from "../ports";

export function makeDeps(init: {
  now?: Date; appointments?: AppointmentInfo[]; clients?: ClientInfo[]; campaign?: CampaignInfo;
  audience?: ClientInfo[]; lapsed?: LapsedClient[]; sendFailures?: number;
} = {}) {
  const messages = new Map<string, StoredMessage>();
  const claims = new Map<string, "held" | "done">();
  const logs: NotificationLogEntry[] = [];
  const sent: StoredMessage[] = [];
  const outbox: string[] = [];
  const reminders: { appointmentId: string; offset: string; delayMs: number }[] = [];
  const campaigns: { id: string; delayMs: number }[] = [];
  let failures = init.sendFailures ?? 0;
  const state = { now: init.now ?? new Date("2026-01-10T10:00:00Z") };

  const deps: BeautyDeps = {
    now: () => state.now,
    data: {
      getAppointment: async (id) => init.appointments?.find((a) => a.id === id) ?? null,
      getClient: async (id) => init.clients?.find((c) => c.id === id) ?? null,
      getCampaign: async () => init.campaign ?? null,
      listCampaignAudience: async () => init.audience ?? [],
      listLapsedClients: async () => init.lapsed ?? [],
      upsertMessage: async (m) => {
        const ex = [...messages.values()].find((x) => x.idempotencyKey === m.idempotencyKey);
        if (ex) return { message: ex, created: false };
        const message: StoredMessage = { ...m, id: `m${messages.size + 1}`, status: "queued" };
        messages.set(message.id, message);
        return { message, created: true };
      },
      getMessage: async (id) => messages.get(id) ?? null,
      setMessageStatus: async (id, s) => { messages.get(id)!.status = s; },
      recordSendFailure: async (id, _attempt, _error, final) => { if (final) messages.get(id)!.status = "failed"; },
    },
    sender: { send: async (m) => { if (failures > 0) { failures--; throw new Error("channel down"); } sent.push(m); } },
    idempotency: {
      claim: async (k) => { if (claims.has(k)) return false; claims.set(k, "held"); return true; },
      complete: async (k) => { claims.set(k, "done"); },
      release: async (k) => { claims.delete(k); },
    },
    log: { append: async (e) => { logs.push(e); } },
    queue: {
      enqueueOutbox: async (id) => { outbox.push(id); },
      enqueueReminder: async (d, delayMs) => { reminders.push({ ...d, delayMs }); },
      enqueueCampaign: async (id, delayMs) => { campaigns.push({ id, delayMs }); },
    },
  };
  return { deps, messages, logs, sent, outbox, reminders, campaigns, state };
}
