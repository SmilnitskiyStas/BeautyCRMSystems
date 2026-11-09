import type { BeautyDeps } from "../ports";
import { enqueueMessage } from "./beauty-common";

export interface ConfirmationJobData { appointmentId: string }

/** Дата й час візиту у часовій зоні закладу, напр. «12 січня 2026 р. о 14:30». */
export function formatVisitTime(startsAt: Date, timezone: string): string {
  const fmt = (o: Intl.DateTimeFormatOptions, tz: string) => new Intl.DateTimeFormat("uk-UA", { timeZone: tz, ...o }).format(startsAt);
  let tz = timezone;
  try { fmt({}, tz); } catch { tz = "UTC"; } // невалідна зона в даних не має валити лист
  return `${fmt({ day: "numeric", month: "long", year: "numeric" }, tz)} о ${fmt({ hour: "2-digit", minute: "2-digit", hourCycle: "h23" }, tz)}`;
}

/**
 * beauty.confirmation: транзакційний лист-підтвердження запису (marketingConsent не потрібен).
 * Ключ ідемпотентності `confirmation:{appointmentId}` — повторний запуск не створює дубль.
 */
export async function processConfirmation(deps: BeautyDeps, data: ConfirmationJobData): Promise<"queued" | "skipped"> {
  const appt = await deps.data.getAppointment(data.appointmentId);
  if (!appt || !["pending", "confirmed"].includes(appt.status)) return "skipped";
  if (appt.startsAt.getTime() <= deps.now().getTime()) return "skipped"; // візит уже минув
  const client = await deps.data.getClient(appt.clientId);
  if (!client || !client.address) return "skipped";
  const d = await deps.data.getAppointmentDetails(appt.id);
  if (!d) return "skipped";
  const lines = [
    `${client.name ? `Вітаємо, ${client.name}!` : "Вітаємо!"} Ваш запис підтверджено.`,
    "",
    `Заклад: ${d.locationName}`,
    ...(d.locationAddress ? [`Адреса: ${d.locationAddress}`] : []),
    `Послуга: ${d.serviceName}`,
    `Майстер: ${d.specialistName}`,
    `Дата й час: ${formatVisitTime(appt.startsAt, d.timezone)}`,
    "",
    "Чекаємо на вас!",
  ];
  const created = await enqueueMessage(deps, {
    tenantId: appt.tenantId, clientId: client.id, channel: client.channel, address: client.address,
    text: lines.join("\n"), idempotencyKey: `confirmation:${appt.id}`, kind: "confirmation",
  });
  return created ? "queued" : "skipped";
}
