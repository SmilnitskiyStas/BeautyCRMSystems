import type { Absence, Appointment, BeautyLocation, CalendarKind, ContactSource, ContactVerdict, PhonePendingSource, PhoneSource, LocationClosure, MessageSenderType, MessageStatus, ReminderMethod, StaffMember, WorkingHours } from "@/features/beauty-admin/types";
import { weekdayOf } from "@/features/beauty-admin/closed-days";
import { bookableAt, dayAvailability, hhmm } from "./availability";
import { payoutOf } from "@/features/beauty-admin/analytics/finance";
import { addDaysYmd, localToInstant, mondayOfYmd, todayInZone } from "./zone-time";

/**
 * Єдине джерело демо-даних для mock адмінки й публічного запису (аудит TASK-705, F3/F4).
 * Усе, що залежить від дати, відносне до «сьогодні» закладу; записи будуються з графіків майстрів, годин закладів,
 * вихідних/закриттів і відсутностей тими самими правилами, що й слоти (`availability.ts`).
 */

export const DEMO_TZ = "Europe/Kyiv";
export const demoToday = (now: Date = new Date()) => todayInZone(DEMO_TZ, now);

type Day = "mon" | "tue" | "wed" | "thu" | "fri" | "sat" | "sun";
const hrs = (from: string, to: string, days: Day[]): WorkingHours => Object.fromEntries(days.map((d) => [d, [{ from, to }]]));

// ---------- каталог послуг ----------

export interface CatalogItem {
  id: string;
  name: string;
  category: string;
  durationMinutes: number;
  price: number;
  /** Назва акції (−PROMO_PERCENT%) або відсутня. */
  promo?: string;
}
export const PROMO_PERCENT = 20;
export const promoPrice = (price: number) => Math.round((price * (100 - PROMO_PERCENT)) / 100);

export const CATALOG: CatalogItem[] = [
  { id: "mn", name: "Манікюр + гель-лак", category: "Нігті", durationMinutes: 90, price: 900, promo: "Осінній манікюр" },
  { id: "mn2", name: "Манікюр", category: "Нігті", durationMinutes: 60, price: 700, promo: "Осінній манікюр" },
  { id: "pd", name: "Педикюр", category: "Нігті", durationMinutes: 90, price: 1000 },
  { id: "hc", name: "Жіноча стрижка", category: "Волосся", durationMinutes: 60, price: 650 },
  { id: "br", name: "Чоловіча стрижка", category: "Волосся", durationMinutes: 45, price: 500 },
  { id: "col", name: "Фарбування", category: "Волосся", durationMinutes: 150, price: 2400 },
  { id: "ton", name: "Тонування", category: "Волосся", durationMinutes: 120, price: 1800 },
  { id: "cl", name: "Чистка обличчя", category: "Обличчя", durationMinutes: 75, price: 1400, promo: "Чистка обличчя −20%" },
];
const serviceOf = (id: string) => CATALOG.find((s) => s.id === id)!;

// ---------- заклади ----------

/** Години закладів (§18.1): Поділ без годин - демо «Години не задано». */
const HOURS_CENTER = hrs("09:00", "20:00", ["mon", "tue", "wed", "thu", "fri", "sat"]);
const HOURS_PECHERSK: WorkingHours = { ...hrs("10:00", "19:00", ["mon", "tue", "wed", "thu", "fri"]), sat: [{ from: "10:00", to: "16:00" }] };

export const DEMO_LOCATIONS: BeautyLocation[] = [
  // Центр: неділя вихідна (§17) - демо різниці між графіком майстра (Нд працює) і вихідним закладу.
  { id: "c", name: "Центр", address: "вул. Хрещатик, 22, Київ", phone: "+380 44 000 11 22", timezone: DEMO_TZ, isActive: true, closedWeekdays: ["sun"], workingHours: HOURS_CENTER },
  { id: "p", name: "Поділ", address: "вул. Сагайдачного, 10, Київ", phone: "+380 44 000 33 44", timezone: DEMO_TZ, isActive: true, closedWeekdays: [], workingHours: null },
  { id: "k", name: "Печерськ", address: "вул. Лаврська, 5, Київ", phone: null, timezone: DEMO_TZ, isActive: true, closedWeekdays: [], workingHours: HOURS_PECHERSK },
  { id: "l", name: "Лівий берег (закрито)", address: "просп. Мира, 3, Київ", phone: null, timezone: DEMO_TZ, isActive: false, closedWeekdays: [] },
];

/** Закриття закладів (§17), відносні до сьогодні: Поділ - санітарний день через 2 дні, Центр - свята через ~6 тижнів. */
export const closuresSeed = (today: string): LocationClosure[] => [
  { id: "cl1", locationId: "p", dateFrom: addDaysYmd(today, 2), dateTo: addDaysYmd(today, 2), reason: "Санітарний день" },
  { id: "cl2", locationId: "c", dateFrom: addDaysYmd(today, 40), dateTo: addDaysYmd(today, 42), reason: "Свята" },
];

// ---------- працівники ----------

const svc = (...ids: string[]) => ids.map((id) => ({ id, name: serviceOf(id).name }));

export const STAFF: StaffMember[] = [
  {
    id: "m",
    name: "Марина Бойко",
    phone: "+380 67 000 00 00",
    position: "Майстер манікюру",
    isActive: true,
    services: svc("mn", "mn2", "pd"),
    locations: [
      { locationId: "c", locationName: "Центр", isActive: true, workingHours: hrs("10:00", "19:00", ["mon", "tue", "wed", "fri", "sun"]) },
      // Сб 10-18 при годинах Печерська 10-16: 16-18 - поза годинами закладу (демо перетину).
      { locationId: "k", locationName: "Печерськ", isActive: true, workingHours: hrs("10:00", "18:00", ["thu", "sat"]) },
    ],
  },
  {
    id: "a",
    name: "Анна Шевчук",
    phone: "+380 50 111 22 33",
    position: "Колорист",
    isActive: true,
    services: svc("col", "ton", "hc"),
    locations: [
      { locationId: "c", locationName: "Центр", isActive: true, workingHours: hrs("09:00", "18:00", ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]) },
      // Знятий зв'язок (§14.5): графік збережено, але це вже не заклад Анни.
      { locationId: "k", locationName: "Печерськ", isActive: false, workingHours: hrs("10:00", "18:00", ["sat"]) },
    ],
  },
  {
    id: "o",
    name: "Оксана Лис",
    phone: "+380 63 222 33 44",
    position: "Косметолог",
    isActive: true,
    services: svc("cl"),
    locations: [{ locationId: "c", locationName: "Центр", isActive: true, workingHours: hrs("10:00", "19:00", ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]) }],
  },
  {
    id: "im",
    name: "Ірина Мельник",
    phone: "+380 96 333 44 55",
    position: "Стиліст",
    isActive: true,
    services: svc("hc", "br"),
    locations: [{ locationId: "p", locationName: "Поділ", isActive: true, workingHours: hrs("09:00", "18:00", ["mon", "tue", "wed", "thu", "fri", "sun"]) }],
  },
  {
    id: "ng",
    name: "Наталя Гук",
    phone: "+380 99 444 55 66",
    position: "Майстер манікюру",
    isActive: false,
    services: svc("mn2"),
    locations: [{ locationId: "k", locationName: "Печерськ", isActive: true, workingHours: hrs("10:00", "18:00", ["tue", "thu", "sun"]) }],
  },
];

// ---------- відсутності ----------

/** `by` = хто створив (`me` - поточний демо-користувач); потрібен mock-клієнту для правил видимості note. */
export type SeedAbsence = Absence & { note: string; by: "me" | "other" };

export const absencesSeed = (today: string): SeedAbsence[] => [
  { id: "ab1", specialistId: "a", type: "vacation", dateFrom: addDaysYmd(today, 3), dateTo: addDaysYmd(today, 4), status: "approved", note: "Сімейні обставини", by: "other" },
  { id: "ab2", specialistId: "o", type: "day_off", dateFrom: today, dateTo: today, status: "approved", note: "Особисті справи", by: "other" },
  { id: "ab3", specialistId: "m", type: "sick", dateFrom: addDaysYmd(today, 5), dateTo: addDaysYmd(today, 6), status: "requested", note: "Візит до лікаря", by: "me" },
  { id: "ab4", specialistId: "im", type: "vacation", dateFrom: addDaysYmd(today, 14), dateTo: addDaysYmd(today, 21), status: "approved", note: "Відпустка на морі", by: "other" },
];

/** Чи блокує майстра затверджена відсутність у цю дату (requested не блокує, §13). */
export const blocksOn = (absences: Pick<Absence, "specialistId" | "dateFrom" | "dateTo" | "status">[], specialistId: string, date: string) =>
  absences.some((a) => a.specialistId === specialistId && a.status === "approved" && a.dateFrom <= date && date <= a.dateTo);

// ---------- клієнти ----------

export interface ClientSeed {
  id: string;
  name: string;
  tag: "vip" | "new" | "sleep";
  phone: string;
  /** Скільки візитів було всього (до і поза демо-записами). */
  visits: number;
  /** Середній чек, грн. */
  avg: number;
  since: string;
}
export const CLIENT_SEED: ClientSeed[] = [
  { id: "oc", name: "Олена Кравченко", tag: "vip", phone: "+380 50 000 00 01", visits: 24, avg: 895, since: "березня 2025" },
  { id: "sm", name: "Софія Мартин", tag: "new", phone: "+380 50 000 00 02", visits: 1, avg: 900, since: "вересня 2026" },
  { id: "kl", name: "Катерина Ляшко", tag: "sleep", phone: "+380 50 000 00 03", visits: 8, avg: 1100, since: "січня 2025" },
  { id: "tg", name: "Тетяна Гончар", tag: "vip", phone: "+380 50 000 00 04", visits: 12, avg: 980, since: "червня 2025" },
  { id: "mp", name: "Марія Пономаренко", tag: "new", phone: "+380 50 000 00 05", visits: 3, avg: 760, since: "серпня 2026" },
];

// ---------- записи ----------

const t = (h: number, m = 0) => h * 60 + m;
/** [день тижня (0 = пн), початок (хв), послуга, клієнт, тип]. Заклад береться з графіка майстра на цей день. */
type Tpl = [day: number, start: number, service: string, client: string, kind: CalendarKind];

const TEMPLATES: Record<string, Tpl[]> = {
  m: [
    [0, t(10), "mn", "oc", "visit"],
    [0, t(15, 30), "mn", "tg", "promo"],
    [0, t(17), "mn", "sm", "online"],
    [1, t(10), "mn", "sm", "new"],
    [1, t(12), "mn", "kl", "visit"],
    [1, t(16), "mn2", "mp", "promo"],
    [2, t(10), "mn2", "kl", "visit"],
    [2, t(14), "mn", "oc", "promo"],
    [3, t(10), "mn", "tg", "visit"],
    [3, t(12, 30), "mn", "mp", "new"],
    [4, t(11), "mn", "oc", "visit"],
    [4, t(15), "mn", "tg", "promo"],
    [5, t(10), "mn", "sm", "visit"],
    [5, t(12), "mn", "kl", "promo"],
  ],
  a: [
    [0, t(11), "col", "kl", "visit"],
    [0, t(15), "hc", "tg", "visit"],
    [1, t(10), "ton", "mp", "new"],
    [2, t(12), "col", "oc", "visit"],
    [3, t(14), "hc", "kl", "visit"],
    [4, t(10), "col", "sm", "visit"],
    [5, t(11), "ton", "mp", "new"],
  ],
  o: [
    [0, t(16), "cl", "sm", "promo"],
    [1, t(12), "cl", "mp", "new"],
    [2, t(15), "cl", "kl", "visit"],
    [3, t(11), "cl", "tg", "promo"],
    [4, t(16), "cl", "oc", "visit"],
    [5, t(10), "cl", "oc", "visit"],
  ],
  im: [
    [0, t(11, 30), "hc", "kl", "visit"],
    [1, t(10), "br", "tg", "visit"],
    [2, t(14), "hc", "oc", "visit"],
    [3, t(12), "hc", "sm", "online"],
    [4, t(15), "br", "mp", "visit"],
    [6, t(11), "hc", "kl", "visit"],
  ],
  // Неактивна майстриня з нескасованими записами (§16): має зʼявитися в селекторі з попередженням.
  ng: [
    [1, t(14), "mn2", "tg", "visit"],
    [3, t(11), "mn2", "mp", "visit"],
  ],
};

/** Тижні відносно поточного: 0 - поточний, 1 - наступний, від'ємні - історія для аналітики. */
function buildAppointments(today: string, now: Date, weeks: number[]): Appointment[] {
  const absences = absencesSeed(today);
  const closures = closuresSeed(today);
  const monday = mondayOfYmd(today);
  const out: Appointment[] = [];
  for (const master of STAFF) {
    (TEMPLATES[master.id] ?? []).forEach(([dayNo, start, serviceId, clientId, kind], i) => {
      for (const week of weeks) {
        const date = addDaysYmd(monday, dayNo + week * 7);
        const link = master.locations.find((l) => l.isActive !== false && (l.workingHours[weekdayOf(date)]?.length ?? 0) > 0);
        const loc = DEMO_LOCATIONS.find((l) => l.id === link?.locationId);
        if (!link || !loc || loc.isActive === false) continue;
        const s = serviceOf(serviceId);
        const day = dayAvailability(
          date,
          {
            masterHours: link.workingHours,
            locationHours: loc.workingHours,
            closedWeekdays: loc.closedWeekdays,
            closures: closures.filter((c) => c.locationId === loc.id),
          },
          blocksOn(absences, master.id, date),
        );
        if (!bookableAt(day, start, s.durationMinutes)) continue;
        const startsAt = `${date}T${hhmm(start)}`;
        const past = localToInstant(startsAt, loc.timezone).getTime() < now.getTime();
        const client = CLIENT_SEED.find((c) => c.id === clientId)!;
        const promo = kind === "promo" && !!s.promo;
        // Історія (week < 0): детермінована різноманітність статусів для аналітики (скасування, no-show).
        const mix = week < 0 ? (i * 7 + -week * 3 + master.id.length) % 13 : -1;
        const status = mix === 0 ? "cancelled" : mix === 7 ? "no_show" : past ? "completed" : kind === "online" ? "pending" : "confirmed";
        out.push({
          id: week === 0 ? `${master.id}${i}` : week === 1 ? `${master.id}${i}w1` : `${master.id}${i}h${-week}`,
          locationId: loc.id,
          specialistId: master.id,
          serviceId,
          clientId,
          clientName: client.name,
          serviceName: s.name,
          startsAt,
          durationMinutes: s.durationMinutes,
          status,
          source: kind === "online" ? "online" : "admin",
          kind: kind === "promo" && !promo ? "visit" : kind,
          priceFinal: promo ? promoPrice(s.price) : s.price,
          promotionId: promo ? "autumn" : null,
          promotionName: promo ? s.promo : undefined,
        });
      }
    });
  }
  return out;
}

/**
 * Записи поточного й наступного тижня. Шаблон потрапляє в набір, лише якщо його час доступний за правилами
 * (графік ∩ години закладу ∩ не вихідний ∩ без відсутності); інакше пропускається, id решти стабільні.
 */
export const appointmentsSeed = (today: string = demoToday(), now: Date = new Date()): Appointment[] => buildAppointments(today, now, [0, 1]);

/** Кількість тижнів історії для аналітики (§19): достатньо для пресету «Рік» і порівняння з попереднім періодом. */
export const HISTORY_WEEKS = 80;

/**
 * Історія записів для аналітики: ті самі шаблони за попередні тижні (з різноманітними статусами: завершено,
 * скасовано, не прийшов). Той самий графік/години/вихідні, що й у календарі, тож аналітика узгоджена з рештою mock.
 */
export const historySeed = (today: string = demoToday(), now: Date = new Date(), weeks: number = HISTORY_WEEKS): Appointment[] =>
  buildAppointments(today, now, Array.from({ length: weeks }, (_, i) => -(i + 1)));


// ---------- вподобання нагадувань і розмови (§22) ----------

export interface ContactPrefSeed {
  clientId: string;
  /** Відсутній запис = «Не обрано» (state: unset). */
  pref?: { method: ReminderMethod; offsets: number[]; source: ContactSource; updatedBy?: { id: string; name: string } };
  /** Чи є розмова в підключеному Telegram-каналі (потрібна для способу «Telegram»). */
  telegram: boolean;
}

/**
 * Демо-сценарії: oc - Telegram 2 год + 24 год (вибір у чаті); sm - не обрано (питає про нагадування в розмові);
 * kl - дзвінок за 24 год (адміністратор); tg - Telegram 1 год, але бот заблоковано (задача «не доставлено»);
 * mp - дзвінок за 2 год; немає Telegram-розмови.
 */
export const CONTACT_PREFS_SEED: ContactPrefSeed[] = [
  { clientId: "oc", telegram: true, pref: { method: "telegram", offsets: [120, 1440], source: "chat" } },
  { clientId: "sm", telegram: true },
  { clientId: "kl", telegram: true, pref: { method: "call", offsets: [1440], source: "admin", updatedBy: { id: "mock-user", name: "Світлана Коваленко" } } },
  { clientId: "tg", telegram: true, pref: { method: "telegram", offsets: [60], source: "admin", updatedBy: { id: "mock-user", name: "Світлана Коваленко" } } },
  { clientId: "mp", telegram: false, pref: { method: "call", offsets: [120], source: "admin", updatedBy: { id: "mock-user", name: "Світлана Коваленко" } } },
];

export interface ConversationSeedMessage {
  /** §25.4: клієнт поділився контактом (`kind=contact`, body `[контакт]`, номера в повідомленні немає). */
  contact?: ContactVerdict;
  /** §25.5.2: клієнт написав номер текстом - тіло замінюється на `[номер телефону]`. */
  numberText?: boolean;
  from: MessageSenderType;
  text: string;
  /** Скільки хвилин тому. */
  minutesAgo: number;
  status: MessageStatus;
  staffName?: string;
  failureCode?: string;
  /** Підписи кнопок клавіатури вихідного повідомлення (§23.10). */
  buttons?: string[];
  /** Клієнт натиснув кнопку з таким підписом (`kind=callback`, `body="[кнопка] …"`). */
  pressed?: string;
  /** §26.9.3: клієнт ввів код купона текстом - тіло замінюється на `[код купона]`. */
  couponText?: boolean;
}

export interface ConversationSeed {
  id: string;
  channelId: "tg" | "ig";
  /** Заклад каналу; відсутній = канал без закладу. */
  locationId?: string;
  clientId?: string;
  title: string;
  aiState: "active" | "handoff";
  handoffReason?: string;
  /** Хвилин до кінця паузи ШІ від «зараз»; відсутнє при handoff = безстроково. */
  pausedForMinutes?: number;
  /** Скільки хвилин очікує відповіді людини (`awaiting_staff_since`). */
  awaitingMinutes?: number;
  /** Коли менеджер востаннє читав розмову (хвилин тому). */
  readMinutesAgo: number;
  messages: ConversationSeedMessage[];
}

/** Стани розмов (кнопки: підсумок запису, нагадування з підтвердженням і скасуванням клієнтом — §23): ШІ відповідає, передано людині (просив нагадування), пауза з `held`, Instagram без відповіді з вебу, не доставлено. */
export const CONVERSATIONS_SEED: ConversationSeed[] = [
  {
    id: "cv1",
    channelId: "tg",
    locationId: "c",
    clientId: "oc",
    title: "Олена Кравченко",
    aiState: "active",
    readMinutesAgo: 60,
    messages: [
      { from: "client", text: "", numberText: true, minutesAgo: 400, status: "processed" },
      { from: "system", text: "Дякуємо, номер збережено. Адміністратор зателефонує вам напередодні візиту.", minutesAgo: 400, status: "sent" },
      { from: "client", text: "Добрий день! Чи є вільне вікно на манікюр у п'ятницю?", minutesAgo: 95, status: "processed" },
      { from: "ai", text: "Добрий день, Олено! У п'ятницю є 11:00 та 15:00 у закладі Центр. Який час підійде?", minutesAgo: 94, status: "sent" },
      { from: "client", text: "11:00, будь ласка", minutesAgo: 90, status: "processed" },
      { from: "ai", text: "Підготувала запис: манікюр + гель-лак, п'ятниця 11:00, Центр, Марина Бойко, 900 ₴. Підтверджуєте?", minutesAgo: 89, status: "sent", buttons: ["✅ Так, записати", "❌ Ні", "🕐 Інший час"] },
      { from: "client", text: "", pressed: "✅ Так, записати", minutesAgo: 88, status: "processed" },
      { from: "ai", text: "Готово, вас записано. Нагадаємо в Telegram за 2 год і за 24 год.", minutesAgo: 88, status: "sent" },
    ],
  },
  {
    id: "cv2",
    channelId: "tg",
    locationId: "c",
    clientId: "sm",
    title: "Софія Мартин",
    aiState: "handoff",
    handoffReason: "policy_unknown",
    pausedForMinutes: 11 * 60 + 30,
    awaitingMinutes: 25,
    readMinutesAgo: 120,
    messages: [
      { from: "client", text: "", contact: "foreign", minutesAgo: 130, status: "processed" },
      { from: "system", text: "Це контакт іншої людини. Надішліть, будь ласка, свій або напишіть свій номер.", minutesAgo: 129, status: "sent" },
      { from: "client", text: "Добрий вечір! Підкажіть, чи нагадаєте ви мені про запис? І чи зателефонує адміністратор?", minutesAgo: 26, status: "processed" },
      { from: "system", text: "Щодо цього я уточню в адміністратора — він відповість вам тут.", minutesAgo: 25, status: "sent" },
    ],
  },
  {
    id: "cv3",
    channelId: "tg",
    locationId: "p",
    clientId: "kl",
    title: "Катерина Ляшко",
    aiState: "handoff",
    handoffReason: "human_requested",
    pausedForMinutes: 9 * 60,
    awaitingMinutes: 140,
    readMinutesAgo: 145,
    messages: [
      { from: "client", text: "", contact: "own", minutesAgo: 300, status: "processed" },
      { from: "system", text: "Дякуємо! У нас уже є інший ваш номер, тож адміністратор перевірить новий і зателефонує щодо запису.", minutesAgo: 300, status: "sent" },
      { from: "client", text: "Хочу перенести запис на наступний тиждень, можна?", minutesAgo: 150, status: "processed" },
      { from: "system", text: "Передаю розмову адміністратору, він відповість найближчим часом.", minutesAgo: 149, status: "sent" },
      { from: "client", text: "Алло? Є хтось на зв'язку?", minutesAgo: 140, status: "held" },
      { from: "client", text: "Можна на вівторок після 14:00", minutesAgo: 70, status: "held" },
      { from: "client", text: "Будь ласка, дайте відповідь, це важливо", minutesAgo: 20, status: "held" },
    ],
  },
  {
    id: "cv4",
    channelId: "ig",
    title: "Ірина Л.",
    aiState: "active",
    readMinutesAgo: 600,
    messages: [
      { from: "client", text: "Чи є вільний час на манікюр з гель-лаком сьогодні ввечері?", minutesAgo: 45, status: "processed" },
      { from: "ai", text: "Чернетка: Добрий день, Ірино! Сьогодні є вільні вікна о 17:00 та 18:30 у закладі Центр. Записати вас на 17:00?", minutesAgo: 44, status: "draft" },
    ],
  },
  {
    id: "cv5",
    channelId: "tg",
    locationId: "p",
    clientId: "tg",
    title: "Тетяна Гончар",
    aiState: "active",
    readMinutesAgo: 400,
    messages: [
      { from: "client", text: "Дякую, до зустрічі!", minutesAgo: 1500, status: "processed" },
      { from: "system", text: "Нагадуємо про ваш візит. Скасувати чи перенести — напишіть сюди. Щоб не отримувати нагадування, напишіть «Стоп».", minutesAgo: 300, status: "failed", failureCode: "bot_blocked" },
    ],
  },
  {
    id: "cv6",
    channelId: "tg",
    locationId: "c",
    clientId: "mp",
    title: "Марія Пономаренко",
    aiState: "active",
    readMinutesAgo: 100,
    messages: [
      { from: "system", text: "Нагадуємо про ваш візит. Підтвердьте, будь ласка, що прийдете.", minutesAgo: 600, status: "sent", buttons: ["✅ Підтверджую візит", "❌ Скасувати візит", "🔕 Більше не нагадувати"] },
      { from: "client", text: "", pressed: "✅ Підтверджую візит", minutesAgo: 590, status: "processed" },
      { from: "system", text: "Дякуємо, візит підтверджено. Чекаємо на вас!", minutesAgo: 590, status: "sent" },
      { from: "client", text: "", pressed: "❌ Скасувати візит", minutesAgo: 190, status: "processed" },
      { from: "ai", text: "Скасувати наступний запис? Оплата в закладі, тож нічого повертати не потрібно.", minutesAgo: 189, status: "sent", buttons: ["Так, скасувати", "Ні, залишити"] },
      { from: "client", text: "", pressed: "Так, скасувати", minutesAgo: 185, status: "processed" },
      { from: "system", text: "Запис скасовано. Щоб записатися знову, напишіть нам.", minutesAgo: 185, status: "sent" },
      // §26: згода на розсилки, кампанія з купоном, код текстом, відписка (тексти шаблонні; коди в UI маскуються).
      { from: "system", text: "Хочете отримувати від нас акції та знижки в цьому чаті? Оберіть варіант - змінити можна будь-коли командою /marketing.", minutesAgo: 150, status: "sent", buttons: ["📰 Повна інформація", "🏷 Лише акції на мої послуги", "🔕 Не надсилати"] },
      { from: "client", text: "", pressed: "🏷 Лише акції на мої послуги", minutesAgo: 148, status: "processed" },
      { from: "system", text: "Готово: надсилатимемо лише акції та знижки на послуги, якими ви користуєтесь.", minutesAgo: 147, status: "sent" },
      { from: "system", text: "Знижка 15% на манікюр - для своїх!\n\nУмови: знижка 15% на всі послуги, діє по 30.11.2026. Одна знижка на запис, не сумується з іншими акціями.\n\nВаш код: Q7W2K-9XM3D\n\nЩоб не отримувати акції, напишіть «Стоп» або натисніть кнопку «Не надсилати акції».", minutesAgo: 130, status: "sent", buttons: ["🎟 Записатися зі знижкою", "🔕 Не надсилати акції"] },
      { from: "client", text: "", pressed: "🎟 Записатися зі знижкою", minutesAgo: 128, status: "processed" },
      { from: "system", text: "Знижку 15% закріплено за вами. Напишіть, на коли бажаєте записатися.", minutesAgo: 127, status: "sent" },
      { from: "client", text: "", couponText: true, minutesAgo: 120, status: "processed" },
      { from: "system", text: "Код прийнято: 15% на всі послуги. Напишіть, на яку послугу й коли бажаєте записатися.", minutesAgo: 119, status: "sent" },
      { from: "client", text: "", pressed: "🔕 Не надсилати акції", minutesAgo: 110, status: "processed" },
      { from: "system", text: "Добре, акцій і новин не надсилатимемо. Нагадування про візити працюють як раніше.", minutesAgo: 109, status: "sent" },
    ],
  },
];


// ---------- собівартість послуг і знімки записів (§24) ----------

export interface CostSeed {
  serviceId: string;
  /** `null` = мережевий рядок; інакше override закладу (діє на весь рядок: відсоток мережі не успадковується, §24.4). */
  locationId: string | null;
  costAmount: number;
  masterPercent: number | null;
}

/**
 * Початкова собівартість. Без неї навмисно лишені «Чоловіча стрижка» (br) і «Тонування» (ton) - демо попередження «без собівартості».
 * Печерськ (k) має override для «Манікюр + гель-лак»: інша собівартість і БЕЗ виплати майстру (відсоток мережі 40% не успадковується).
 */
export const SERVICE_COSTS_SEED: CostSeed[] = [
  { serviceId: "mn", locationId: null, costAmount: 220, masterPercent: 40 },
  { serviceId: "mn", locationId: "k", costAmount: 260, masterPercent: null },
  { serviceId: "mn2", locationId: null, costAmount: 150, masterPercent: 40 },
  { serviceId: "pd", locationId: null, costAmount: 300, masterPercent: 35 },
  { serviceId: "hc", locationId: null, costAmount: 120, masterPercent: 45 },
  { serviceId: "col", locationId: null, costAmount: 700, masterPercent: 30 },
  { serviceId: "cl", locationId: null, costAmount: 400, masterPercent: 30 },
];

/** Записи, завершені раніше, ніж за стільки днів до «сьогодні», - до розгортання функції: знімка немає («без собівартості», §24.4). */
export const COST_DEPLOY_DAYS_AGO = 45;

export interface CostSnapshot {
  cost: number;
  masterPercent: number | null;
  masterPayout: number;
}

/**
 * Знімок собівартості завершеного запису (§24.4): початкові значення сіду, недоторкані подальшими змінами в редакторі
 * (знімок незмінний). Заклад запису має пріоритет над мережею; немає ні того, ні іншого - `null` (costMissing).
 */
export function costSnapshotFor(a: Pick<Appointment, "status" | "serviceId" | "locationId" | "startsAt" | "priceFinal">, today: string): CostSnapshot | null {
  if (a.status !== "completed" || a.startsAt.slice(0, 10) < addDaysYmd(today, -COST_DEPLOY_DAYS_AGO)) return null;
  const eff =
    SERVICE_COSTS_SEED.find((c) => c.serviceId === a.serviceId && c.locationId === a.locationId) ??
    SERVICE_COSTS_SEED.find((c) => c.serviceId === a.serviceId && c.locationId === null);
  if (!eff) return null;
  return { cost: eff.costAmount, masterPercent: eff.masterPercent, masterPayout: payoutOf(a.priceFinal, eff.masterPercent) };
}


// ---------- телефони клієнтів (§25) ----------

export interface PhoneSeed {
  clientId: string;
  source?: PhoneSource;
  /** Згода на дзвінок: скільки діб тому. */
  consentDaysAgo?: number;
  /** Інший номер від клієнта в чаті - чекає рішення адміністратора. */
  pending?: { phone: string; source: PhonePendingSource; minutesAgo: number };
  /** М'який зв'язок «можливий дубль». */
  duplicateOf?: string;
}

/**
 * oc - номер із Telegram-контакту, згода 3 доби тому; kl - номер від адміністратора і НОВИЙ номер із чату, що чекає рішення
 * (див. розмову cv3 і «Дзвінки»); mp - з Telegram-контакту, можливий дубль клієнта sm (онлайн-запис).
 */
export const PHONE_SEED: PhoneSeed[] = [
  { clientId: "oc", source: "telegram_contact", consentDaysAgo: 3 },
  { clientId: "sm", source: "web_form" },
  { clientId: "kl", source: "staff", pending: { phone: "+380 67 111 22 33", source: "telegram_contact", minutesAgo: 300 } },
  { clientId: "tg", source: "staff" },
  { clientId: "mp", source: "telegram_contact", consentDaysAgo: 1, duplicateOf: "sm" },
];
