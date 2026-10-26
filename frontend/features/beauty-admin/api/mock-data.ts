import { dateTimeLabel, money, shortDate } from "../format";
import {
  CATALOG,
  CLIENT_SEED,
  DEMO_LOCATIONS,
  STAFF,
  absencesSeed,
  appointmentsSeed,
  closuresSeed,
  demoToday,
  type SeedAbsence,
} from "../../beauty-shared/demo-seed";
import type {
  Appointment,
  ChannelConfig,
  ClientProfile,
  ClientSummary,
  PriceListRow,
  ServiceItem,
  StaffMember,
  BeautyLocation,
  LocationClosure,
  AiRequest,
  AiSegment,
} from "../types";

/**
 * Демо-дані для mock-клієнта. Не імпортувати поза `api/`.
 * Заклади, працівники, відсутності, закриття, записи й клієнти беруться з ЄДИНОГО джерела `beauty-shared/demo-seed.ts`
 * (його ж читає публічний mock запису) і відносні до сьогодні (аудит TASK-705, F3/F4).
 */

export const LOCATIONS: BeautyLocation[] = DEMO_LOCATIONS;

export const CLOSURES_SEED = (): LocationClosure[] => closuresSeed(demoToday());

export const ABSENCES_SEED = (): MockAbsence[] => absencesSeed(demoToday());
export type MockAbsence = SeedAbsence;

export const PRICE_LIST: PriceListRow[] = CATALOG.map((s) => ({ serviceId: s.id, name: s.name, durationMinutes: s.durationMinutes, price: s.price }));

/** Каталог послуг для призначення працівникам (з категоріями). */
export const SERVICES: ServiceItem[] = CATALOG.map((s) => ({ id: s.id, name: s.name, category: s.category, durationMinutes: s.durationMinutes }));

export const STAFF_SEED: StaffMember[] = STAFF;

/** Записи поточного й наступного тижня (сьогодні - за календарем закладу). */
export const demoAppointments = (): Appointment[] => appointmentsSeed(demoToday());

// ---------- клієнти (похідні від записів) ----------

const staffName = (id: string) => STAFF.find((m) => m.id === id)?.name ?? "";
const locName = (id: string) => DEMO_LOCATIONS.find((l) => l.id === id)?.name ?? "";

const plural = (n: number) => (n % 10 === 1 && n % 100 !== 11 ? "візит" : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? "візити" : "візитів");

function topBy(items: string[]): string | null {
  const counts = new Map<string, number>();
  for (const i of items) counts.set(i, (counts.get(i) ?? 0) + 1);
  return [...counts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? null;
}

const lastDone = (mine: Appointment[]) =>
  mine.filter((a) => a.status === "completed").sort((a, b) => b.startsAt.localeCompare(a.startsAt))[0] ?? null;

export function clientSummaries(appts: Appointment[]): ClientSummary[] {
  return CLIENT_SEED.map((c) => {
    const last = lastDone(appts.filter((a) => a.clientId === c.id));
    return {
      id: c.id,
      name: c.name,
      meta: last ? `Візитів: ${c.visits} · останній ${shortDate(last.startsAt.slice(0, 10))}` : `Візитів: ${c.visits}`,
      tag: c.tag,
    };
  });
}

/** Профіль клієнта з його записів у демо-наборі (скасовані - з `cancelledAt`/`cancelledBy` на запису). */
export function clientProfileFor(id: string, appts: Appointment[]): Omit<ClientProfile, "id" | "name" | "tag"> | null {
  const c = CLIENT_SEED.find((x) => x.id === id);
  if (!c) return null;
  const mine = appts.filter((a) => a.clientId === id).sort((a, b) => b.startsAt.localeCompare(a.startsAt));
  const last = lastDone(mine);
  const main = topBy(mine.map((a) => locName(a.locationId))) ?? DEMO_LOCATIONS[0].name;
  const favourite = topBy(mine.map((a) => staffName(a.specialistId)));
  const cancelledNow = mine.filter((a) => a.status === "cancelled");
  // Давня історія лише для постійної клієнтки: узгоджена з майстрами й закладами набору.
  const older =
    id === "oc"
      ? [
          { id: "old1", dateLabel: "24 серп.", serviceName: "Манікюр", specialistId: "ng", specialistName: staffName("ng"), locationName: locName("k"), sum: "700 ₴", status: "cancelled" as const, viaPromo: false, cancelledAtLabel: "23 серп., 18:05", cancelledBy: { type: "client" as const }, cancelReason: "Змінились плани" },
          { id: "old2", dateLabel: "2 серп.", serviceName: "Жіноча стрижка", specialistId: "im", specialistName: staffName("im"), locationName: locName("p"), sum: "650 ₴", status: "cancelled" as const, viaPromo: false, cancelledAtLabel: "1 серп., 09:30", cancelledBy: { type: "staff" as const, name: "Світлана Коваленко" } },
        ]
      : [];
  const visits: ClientProfile["visits"] = [
    ...mine.map((a) => ({
      id: a.id,
      dateLabel: `${shortDate(a.startsAt.slice(0, 10))}, ${a.startsAt.slice(11, 16)}`,
      serviceName: a.serviceName,
      specialistId: a.specialistId,
      specialistName: staffName(a.specialistId),
      locationName: locName(a.locationId),
      sum: money(a.priceFinal),
      status: (a.status === "completed" ? "completed" : a.status === "cancelled" ? "cancelled" : a.status === "no_show" ? "no_show" : "planned") as ClientProfile["visits"][number]["status"],
      viaPromo: !!a.promotionId,
      ...(a.status === "cancelled"
        ? { ...(a.cancelledAt ? { cancelledAtLabel: dateTimeLabel(a.cancelledAt) } : {}), ...(a.cancelledBy ? { cancelledBy: a.cancelledBy } : {}), ...(a.cancelReason ? { cancelReason: a.cancelReason } : {}) }
        : {}),
    })),
    ...older,
  ];
  const promoUses = mine.filter((a) => a.promotionId && a.status !== "cancelled");
  return {
    contactLine: `${c.phone} · клієнт з ${c.since} · основний заклад: ${main}`,
    kpis: [
      { label: "Візитів", value: String(c.visits), note: `з них ${cancelledNow.length + older.length} скасовано` },
      { label: "Витрачено", value: money(c.visits * c.avg), note: "за весь час" },
      { label: "Середній чек", value: money(c.avg), note: "по завершених візитах" },
      {
        label: "Останній візит",
        value: last ? shortDate(last.startsAt.slice(0, 10)) : "—",
        note: last ? `${locName(last.locationId)} · ${staffName(last.specialistId)}` : "ще не було",
      },
    ],
    visits,
    promos: promoUses.map((a) => {
      const base = CATALOG.find((s) => s.id === a.serviceId)?.price ?? a.priceFinal;
      return {
        id: `promo-${a.id}`,
        name: a.promotionName ?? "Акція",
        when: a.status === "completed" ? `використано ${shortDate(a.startsAt.slice(0, 10))}` : `заплановано на ${shortDate(a.startsAt.slice(0, 10))}`,
        saved: `−${money(base - a.priceFinal)}`,
      };
    }),
    notes:
      id === "oc"
        ? [
            { id: "n1", text: "Алергія на латекс: працювати лише у нітрилових рукавичках.", meta: `${staffName("m")} · 12 березня 2025` },
            { id: "n2", text: "Любить нюдові відтінки, довжина коротка.", meta: `${staffName("m")} · 7 вересня` },
          ]
        : [],
    preferences: [
      ...(favourite ? [{ label: "Улюблений майстер", value: favourite }] : []),
      { label: "Часті послуги", value: topBy(mine.map((a) => a.serviceName)) ?? "—" },
    ],
    warning: id === "oc" ? "Алергія на латекс. Показується майстру перед кожним візитом." : null,
    loyalty: { balance: `${c.visits * 18} балів`, nextLevel: `До наступного рівня: ще ${10 - (c.visits % 10)} ${plural(10 - (c.visits % 10))}`, progressPct: (c.visits % 10) * 10 },
    cancelledCount: cancelledNow.length + older.length,
    cancelledByClientCount: cancelledNow.filter((a) => a.cancelledBy?.type === "client").length + older.filter((o) => o.cancelledBy.type === "client").length,
  };
}

export const INITIAL_CHANNELS: ChannelConfig[] = [
  {
    id: "tg", name: "Telegram", icon: "TG", description: "Бот для записів і повідомлень клієнтів.",
    connected: true, inbox: true, booking: true, ai: true, mode: "confirm", greeting: "", handoff: { negative: true, payment: true, human: true },
    assistantEnabled: true, buttons: true,
    maskedSecret: "••••••••••••",
    connectHelp: { field: "Токен бота", placeholder: "Вставте токен із BotFather", how: "Створіть бота в @BotFather, скопіюйте токен і вставте його нижче. Клієнти зможуть писати боту й записуватися." },
  },
  {
    id: "ig", name: "Instagram", icon: "IG", description: "Direct-повідомлення бізнес-акаунта.",
    connected: true, inbox: true, booking: false, ai: true, mode: "suggest", greeting: "", handoff: { negative: true, payment: true, human: true },
    maskedSecret: "••••••••••••",
    connectHelp: { field: "Бізнес-акаунт Instagram", placeholder: "Назва акаунта", how: "Підключається через Facebook-сторінку. Увійдіть у Facebook і оберіть бізнес-акаунт Instagram." },
  },
  {
    id: "fb", name: "Facebook Messenger", icon: "FB", description: "Повідомлення на сторінку закладу.",
    connected: false, inbox: true, booking: false, ai: false, mode: "suggest", greeting: "", handoff: { negative: true, payment: true, human: true },
    maskedSecret: null,
    connectHelp: { field: "Facebook-сторінка", placeholder: "Назва сторінки", how: "Увійдіть у Facebook і надайте доступ до повідомлень сторінки вашого закладу." },
  },
  {
    id: "wa", name: "WhatsApp Business", icon: "WA", description: "Повідомлення на бізнес-номер.",
    connected: false, inbox: true, booking: false, ai: false, mode: "suggest", greeting: "", handoff: { negative: true, payment: true, human: true },
    maskedSecret: null,
    connectHelp: { field: "Номер телефону", placeholder: "+380", how: "Вкажіть номер WhatsApp Business і підтвердьте його кодом із SMS." },
  },
  {
    id: "vb", name: "Viber", icon: "VB", description: "Бот у Viber для клієнтів.",
    connected: false, inbox: true, booking: false, ai: false, mode: "suggest", greeting: "", handoff: { negative: true, payment: true, human: true },
    maskedSecret: null,
    connectHelp: { field: "Токен бота", placeholder: "Вставте токен Viber", how: "Створіть бот-акаунт у Viber, скопіюйте токен і вставте його нижче." },
  },
  {
    id: "web", name: "Віджет на сайті", icon: "W", description: "Чат і кнопка запису на вашому сайті.",
    connected: true, inbox: true, booking: true, ai: false, mode: "suggest", greeting: "", handoff: { negative: true, payment: true, human: true },
    maskedSecret: "••••••••••••",
    connectHelp: { field: "Адреса сайту", placeholder: "https://", how: "Скопіюйте код віджета на свій сайт. Чат зʼявиться в нижньому куті сторінки." },
  },
];

export const DEFAULT_GREETING =
  "Вітаємо в Beauty Lab! Я допоможу з записом і відповім на питання про ціни та акції.";

export const AI_REQUESTS: AiRequest[] = [
  { id: "r1", name: "Ірина Л.", channel: "Instagram", time: "10:42", text: "Добрий день! Чи є вільний час на манікюр з гель-лаком сьогодні ввечері?", intent: "Запис на послугу", reply: "Добрий день, Ірино! Сьогодні є вільні вікна о 17:00 та 18:30 у закладі Центр. Манікюр з гель-лаком триває 1 г 30 хв і коштує 900 ₴. Записати вас на 17:00?", replied: false },
  { id: "r2", name: "Дмитро Г.", channel: "Telegram", time: "10:15", text: "Скільки коштує чоловіча стрижка і чи можна до Ірини?", intent: "Ціна та вибір майстра", reply: "Добрий день! Чоловіча стрижка коштує 500 ₴ і триває 45 хв. Ірина Мельник працює у закладі Поділ, найближчий вільний час сьогодні о 15:00. Записати?", replied: false },
  { id: "r3", name: "Софія М.", channel: "Сайт", time: "09:50", text: "Хочу перенести запис на четвер.", intent: "Перенесення запису", reply: "Звісно, Софіє! На четвер є вільні слоти о 12:00 та 16:30. Який зручніший? Перенесу без додаткової оплати.", replied: false },
  { id: "r4", name: "Новий клієнт", channel: "Instagram", time: "09:20", text: "Чи діє знижка для нових клієнтів?", intent: "Акції", reply: "Так! Для нових клієнтів діє знижка 20% на першу послугу. Показати доступні години?", replied: false },
];

export const AI_SEGMENTS: AiSegment[] = [
  { id: "sleep", label: "Не були 60+ днів", note: "давно не записувались", count: 38 },
  { id: "vip", label: "VIP-клієнти", note: "10+ візитів", count: 24 },
  { id: "fresh", label: "Нові за 30 днів", note: "ще без повторного візиту", count: 17 },
  { id: "all", label: "Усі клієнти Центру", note: "широка розсилка", count: 212 },
];

export const AI_GOALS = {
  fill: {
    label: "Заповнити вільні слоти", percent: 15, when: "пн–чт, 12:00–15:00, заклади Центр і Поділ",
    reason: "У будні 12:00–15:00 завантаження найнижче. Помірна знижка швидше перенесе клієнтів у ці години, ніж велика на весь день.",
    rows: [["Манікюр + гель-лак", 900], ["Манікюр", 700], ["Педикюр", 1000]] as [string, number][],
    rec: ["sleep"],
    text: (p: number) => `Будні в Beauty Lab: −${p}% на манікюр і педикюр з понеділка по четвер, 12:00–15:00 у закладах Центр і Поділ. Оберіть зручний час і майстра онлайн.`,
  },
  back: {
    label: "Повернути клієнтів", percent: 20, when: "протягом 14 днів, усі заклади",
    reason: "Клієнтам, які не були 60+ днів, потрібна відчутна причина повернутися. Знижка 20% на улюблену послугу працює краще за загальну.",
    rows: [["Чистка обличчя", 1400], ["Манікюр + гель-лак", 900]] as [string, number][],
    rec: ["sleep"],
    text: (p: number) => `Давно не бачились! Для вас −${p}% на чистку обличчя та манікюр протягом 14 днів. Запишіться до свого майстра онлайн.`,
  },
  fresh: {
    label: "Залучити нових клієнтів", percent: 20, when: "перша послуга, постійна пропозиція",
    reason: "Для першого візиту знижка 20% знімає бар’єр. Показуйте її новим підписникам і тим, хто вже звертався, але ще не записався.",
    rows: [["Манікюр", 700], ["Жіноча стрижка", 650], ["Чистка обличчя", 1400]] as [string, number][],
    rec: ["fresh"],
    text: (p: number) => `Вперше у Beauty Lab? −${p}% на першу послугу. Оберіть заклад і майстра онлайн за кілька хвилин.`,
  },
};
