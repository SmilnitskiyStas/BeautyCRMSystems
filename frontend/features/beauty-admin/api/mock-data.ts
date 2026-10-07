import type {
  Appointment,
  CalendarKind,
  CalendarMaster,
  ChannelConfig,
  ClientProfile,
  ClientSummary,
  ClientTag,
  PriceListRow,
  StaffProfile,
  StaffSummary,
  BeautyLocation,
  AiRequest,
  AiSegment,
} from "../types";

/** Демо-дані для mock-клієнта. Не імпортувати поза `api/`. */

export const LOCATIONS: BeautyLocation[] = [
  { id: "c", name: "Центр" },
  { id: "p", name: "Поділ" },
  { id: "k", name: "Печерськ" },
];

export const PRICE_LIST: PriceListRow[] = [
  { serviceId: "mn", name: "Манікюр + гель-лак", durationMinutes: 90, price: 900 },
  { serviceId: "pd", name: "Педикюр", durationMinutes: 90, price: 1000 },
  { serviceId: "cl", name: "Чистка обличчя", durationMinutes: 75, price: 1400 },
  { serviceId: "hc", name: "Жіноча стрижка", durationMinutes: 60, price: 650 },
  { serviceId: "br", name: "Чоловіча стрижка", durationMinutes: 45, price: 500 },
];

/** Тривалість послуги за назвою (хвилини) — блок у календарі = ця тривалість. */
const DURATION_BY_SERVICE: Record<string, number> = {
  "Манікюр + гель-лак": 90,
  Манікюр: 60,
  Фарбування: 150,
  Тонування: 120,
  Стрижка: 60,
  "Чистка обличчя": 75,
};

const SERVICE_ID: Record<string, string> = {
  "Манікюр + гель-лак": "mn",
  Манікюр: "mn2",
  Фарбування: "col",
  Тонування: "ton",
  Стрижка: "hc",
  "Чистка обличчя": "cl",
};

export const MASTERS: CalendarMaster[] = [
  { id: "m", name: "Марина Бойко", locationName: "Центр" },
  { id: "a", name: "Анна Шевчук", locationName: "Центр" },
  { id: "o", name: "Оксана Лис", locationName: "Центр" },
];

export const CALENDAR_DAYS = ["Пн 5", "Вт 6", "Ср 7", "Чт 8", "Пт 9", "Сб 10", "Нд 11"];

type Tuple = [day: number, startHour: number, durHours: number, client: string, service: string, kind: CalendarKind, price: number];

const RAW: Record<string, Tuple[]> = {
  m: [
    [0, 10, 1.5, "Олена К.", "Манікюр + гель-лак", "visit", 900],
    [0, 13, 1, "Перерва", "", "break", 0],
    [0, 15.5, 1.5, "Тетяна Г.", "Манікюр + гель-лак", "promo", 720],
    [0, 17, 1.5, "Онлайн-запис", "Манікюр + гель-лак", "online", 900],
    [1, 9, 1.5, "Софія М.", "Манікюр + гель-лак", "new", 720],
    [1, 11.5, 1.5, "Ірина Л.", "Манікюр + гель-лак", "visit", 900],
    [1, 16, 1.5, "Марія П.", "Манікюр", "promo", 560],
    [2, 10, 1, "Катерина Л.", "Манікюр", "visit", 700],
    [2, 14, 1.5, "Анастасія Р.", "Манікюр + гель-лак", "promo", 720],
    [3, 9.5, 1.5, "Юлія Т.", "Манікюр + гель-лак", "visit", 900],
    [3, 12, 1.5, "Діана В.", "Манікюр + гель-лак", "new", 720],
    [4, 11, 1.5, "Олена К.", "Манікюр + гель-лак", "visit", 900],
    [4, 15, 1.5, "Вероніка С.", "Манікюр + гель-лак", "promo", 720],
    [5, 10, 1.5, "Лілія Д.", "Манікюр + гель-лак", "visit", 900],
    [5, 12, 1.5, "Алла Ф.", "Манікюр + гель-лак", "promo", 720],
    [6, 9, 12, "Вихідний", "", "break", 0],
  ],
  a: [
    [0, 11, 2.5, "Катерина Л.", "Фарбування", "visit", 2400],
    [0, 15, 1, "Ольга Н.", "Стрижка", "visit", 650],
    [1, 10, 2, "Мирослава К.", "Тонування", "new", 1800],
    [2, 12, 2.5, "Христина О.", "Фарбування", "visit", 2400],
    [3, 9, 1, "Перерва", "", "break", 0],
    [3, 14, 1, "Яна Б.", "Стрижка", "visit", 650],
    [4, 10, 2.5, "Людмила Г.", "Фарбування", "visit", 2400],
    [5, 11, 2, "Поліна Е.", "Тонування", "new", 1800],
    [6, 9, 12, "Вихідний", "", "break", 0],
  ],
  o: [
    [0, 16, 1.25, "Софія М.", "Чистка обличчя", "promo", 1120],
    [1, 12, 1.25, "Інна Д.", "Чистка обличчя", "new", 1120],
    [2, 15, 1.25, "Віра Л.", "Чистка обличчя", "visit", 1400],
    [3, 11, 1.25, "Зоя К.", "Чистка обличчя", "promo", 1120],
    [4, 13, 1, "Перерва", "", "break", 0],
    [4, 16, 1.25, "Раїса Т.", "Чистка обличчя", "visit", 1400],
    [5, 10, 1.25, "Олена К.", "Чистка обличчя", "visit", 1400],
    [6, 9, 12, "Вихідний", "", "break", 0],
  ],
};

const pad = (n: number) => String(n).padStart(2, "0");

function toStartsAt(day: number, hour: number): string {
  const h = Math.floor(hour);
  const m = Math.round((hour - h) * 60);
  return `2026-10-${pad(5 + day)}T${pad(h)}:${pad(m)}`;
}

export function appointmentsFor(specialistId: string): Appointment[] {
  return (RAW[specialistId] ?? []).map((t, i) => {
    const [day, start, dur, client, service, kind, price] = t;
    return {
      id: `${specialistId}${i}`,
      locationId: "c",
      specialistId,
      serviceId: SERVICE_ID[service] ?? "none",
      clientId: kind === "break" || kind === "online" ? null : "oc",
      clientName: client,
      serviceName: service,
      startsAt: toStartsAt(day, start),
      durationMinutes: DURATION_BY_SERVICE[service] ?? Math.round(dur * 60),
      status: kind === "online" ? "pending" : "confirmed",
      source: kind === "online" ? "online" : "admin",
      kind,
      priceFinal: price,
      promotionId: kind === "promo" ? "autumn" : null,
      promotionName: kind === "promo" ? "Осінній манікюр" : undefined,
    };
  });
}

export const CLIENTS: (ClientSummary & { profile: Omit<ClientProfile, "id" | "name" | "tag"> })[] = [];

const baseProfile: Omit<ClientProfile, "id" | "name" | "tag"> = {
  contactLine: "+380 50 000 00 00 · клієнтка з березня 2025 · основний заклад: Центр",
  kpis: [
    { label: "Візитів", value: "24", note: "з них 2 скасовано" },
    { label: "Витрачено", value: "21 480 ₴", note: "за весь час" },
    { label: "Середній чек", value: "895 ₴", note: "по завершених візитах" },
    { label: "Останній візит", value: "5 жовт.", note: "Центр · Марина Бойко" },
  ],
  visits: [
    { id: "v1", dateLabel: "19 жовт.", serviceName: "Манікюр + гель-лак", specialistId: "m", specialistName: "Марина Бойко", locationName: "Центр", sum: "900 ₴", status: "planned", viaPromo: false },
    { id: "v2", dateLabel: "5 жовт.", serviceName: "Манікюр + гель-лак", specialistId: "m", specialistName: "Марина Бойко", locationName: "Центр", sum: "900 ₴", status: "completed", viaPromo: false },
    { id: "v3", dateLabel: "21 вер.", serviceName: "Чистка обличчя", specialistId: "o", specialistName: "Оксана Лис", locationName: "Центр", sum: "1 120 ₴", status: "completed", viaPromo: true },
    { id: "v4", dateLabel: "7 вер.", serviceName: "Манікюр + гель-лак", specialistId: "m", specialistName: "Марина Бойко", locationName: "Центр", sum: "900 ₴", status: "completed", viaPromo: false },
    { id: "v5", dateLabel: "24 серп.", serviceName: "Манікюр", specialistId: "m", specialistName: "Наталя Гук", locationName: "Печерськ", sum: "650 ₴", status: "cancelled", viaPromo: false },
    { id: "v6", dateLabel: "10 серп.", serviceName: "Манікюр + гель-лак", specialistId: "m", specialistName: "Марина Бойко", locationName: "Центр", sum: "720 ₴", status: "completed", viaPromo: true },
    { id: "v7", dateLabel: "27 лип.", serviceName: "Стрижка", specialistId: "m", specialistName: "Ірина Мельник", locationName: "Поділ", sum: "650 ₴", status: "completed", viaPromo: false },
  ],
  promos: [
    { id: "p1", name: "Осінній манікюр −20%", when: "використано 10 серпня", saved: "−180 ₴" },
    { id: "p2", name: "Чистка обличчя −20%", when: "використано 21 вересня", saved: "−280 ₴" },
    { id: "p3", name: "Знижка за день народження", when: "доступна 14–21 листопада", saved: "ще не використано" },
  ],
  notes: [
    { id: "n1", text: "Алергія на латекс: працювати лише у нітрилових рукавичках.", meta: "Марина Бойко · 12 березня 2025" },
    { id: "n2", text: "Любить нюдові відтінки, довжина коротка.", meta: "Марина Бойко · 7 вересня" },
    { id: "n3", text: "Просила нагадувати про візит за 2 години.", meta: "Адміністратор Центру · 21 вересня" },
  ],
  preferences: [
    { label: "Улюблений майстер", value: "Марина Бойко" },
    { label: "Часті послуги", value: "Манікюр + гель-лак, чистка обличчя" },
    { label: "Зручний час", value: "Пн, Пт · після 15:00" },
    { label: "Канал звʼязку", value: "Telegram" },
  ],
  warning: "Алергія на латекс. Показується майстру перед кожним візитом.",
  loyalty: { balance: "420 балів", nextLevel: "До наступного рівня: ще 3 візити", progressPct: 80 },
};

const clientSeed: [string, string, string, ClientTag][] = [
  ["oc", "Олена Кравченко", "24 візити · остання 5 жовтня", "vip"],
  ["sm", "Софія Мартин", "1 візит · перший запис онлайн", "new"],
  ["kl", "Катерина Ляшко", "8 візитів · остання 2 серпня", "sleep"],
  ["tg", "Тетяна Гончар", "12 візитів · остання 28 вересня", "vip"],
  ["mp", "Марія Пономаренко", "3 візити · остання 20 вересня", "new"],
];
for (const [id, name, meta, tag] of clientSeed) {
  CLIENTS.push({ id, name, meta, tag, profile: baseProfile });
}

export const STAFF: StaffSummary[] = [
  { id: "m", name: "Марина Бойко", role: "Майстер манікюру", locations: "Центр, Печерськ" },
  { id: "a", name: "Анна Шевчук", role: "Колорист", locations: "Центр" },
  { id: "o", name: "Оксана Лис", role: "Косметолог", locations: "Центр" },
  { id: "im", name: "Ірина Мельник", role: "Стиліст", locations: "Поділ" },
];

const days14 = [7, 6, 0, 8, 7, 5, 0, 6, 8, 7, 7, 4, 3, 0];
const labels14 = ["22", "23", "24", "25", "26", "27", "28", "29", "30", "1", "2", "3", "4", "5"];

export function staffProfile(id: string): StaffProfile | null {
  const s = STAFF.find((x) => x.id === id);
  if (!s) return null;
  return {
    id: s.id,
    name: s.name,
    role: `${s.role} · у команді з січня 2024`,
    locationBadges: ["Центр · пн, вт, ср, пт", "Печерськ · чт, сб"],
    kpis: [
      { label: "Візитів за місяць", value: "142", note: "5 з них скасовано" },
      { label: "Виручка", value: "118 000 ₴", note: "за місяць" },
      { label: "Завантаження", value: "84%", note: "зайнятих годин у графіку" },
      { label: "Повторні клієнти", value: "67%", note: "повернулися за 60 днів" },
    ],
    activity: days14.map((value, i) => ({ value, label: labels14[i] })),
    feed: [
      { id: "f1", kind: "done", kindLabel: "Візит", text: "Завершила: Олена К. · Манікюр + гель-лак · 900 ₴", when: "Сьогодні 11:30" },
      { id: "f2", kind: "review", kindLabel: "Відгук", text: "Новий відгук 5 з 5 від Софії М.: «Дуже акуратно»", when: "Сьогодні 09:12" },
      { id: "f3", kind: "move", kindLabel: "Перенесення", text: "Запис Тетяни Г. перенесено з 16:00 на 15:30", when: "Вчора 18:40" },
      { id: "f4", kind: "note", kindLabel: "Нотатка", text: "Додала нотатку до профілю Олени К.: нюдові відтінки", when: "Вчора 12:05" },
      { id: "f5", kind: "slot", kindLabel: "Слоти", text: "Відкрила вільні слоти на четвер у Печерську", when: "3 жовт. 10:20" },
      { id: "f6", kind: "done", kindLabel: "Візит", text: "Завершила: Діана В. · Манікюр + гель-лак · 720 ₴ (акція)", when: "3 жовт. 14:00" },
      { id: "f7", kind: "move", kindLabel: "Скасування", text: "Клієнтка Алла Ф. скасувала запис на 12:00", when: "2 жовт. 09:30" },
    ],
    services: [
      { name: "Манікюр + гель-лак", duration: "1 г 30 хв", price: "900 ₴", count: "86" },
      { name: "Манікюр", duration: "1 г", price: "700 ₴", count: "38" },
      { name: "Педикюр", duration: "1 г 30 хв", price: "1 000 ₴", count: "14" },
      { name: "Зняття + нарощування", duration: "2 г 30 хв", price: "1 600 ₴", count: "4" },
    ],
    schedule: [
      { day: "Пн", hours: "10:00–19:00", location: "Центр", off: false },
      { day: "Вт", hours: "10:00–19:00", location: "Центр", off: false },
      { day: "Ср", hours: "10:00–19:00", location: "Центр", off: false },
      { day: "Чт", hours: "10:00–18:00", location: "Печерськ", off: false },
      { day: "Пт", hours: "10:00–19:00", location: "Центр", off: false },
      { day: "Сб", hours: "10:00–17:00", location: "Печерськ", off: false },
      { day: "Нд", hours: "Вихідний", location: "Вихідний", off: true },
    ],
    bars: [
      { label: "Записи по акціях", value: "31 з 142", pct: 22 },
      { label: "Онлайн-записи", value: "104 з 142", pct: 73 },
      { label: "Середня оцінка", value: "4,9 з 5", pct: 98 },
    ],
    contacts: [
      { label: "Телефон", value: "+380 67 000 00 00" },
      { label: "Оплата", value: "30% від послуги" },
      { label: "Онлайн-запис", value: "увімкнено" },
    ],
  };
}

export const OVERVIEW_ROWS = [
  { id: "r1", time: "10:00", clientId: "oc", clientName: "Олена Кравченко", serviceName: "Манікюр + гель-лак", specialistId: "m", specialistName: "Марина Бойко", locationId: "c", locationName: "Центр", status: "completed" },
  { id: "r2", time: "11:30", clientId: "kl", clientName: "Ірина Левченко", serviceName: "Жіноча стрижка", specialistId: "im", specialistName: "Ірина Мельник", locationId: "p", locationName: "Поділ", status: "in_progress" },
  { id: "r3", time: "12:00", clientId: "sm", clientName: "Софія Мартин", serviceName: "Чистка обличчя", specialistId: "o", specialistName: "Вікторія Руда", locationId: "k", locationName: "Печерськ", status: "confirmed" },
  { id: "r4", time: "13:15", clientId: "kl", clientName: "Катерина Ляшко", serviceName: "Фарбування", specialistId: "a", specialistName: "Анна Шевчук", locationId: "c", locationName: "Центр", status: "pending" },
  { id: "r5", time: "14:00", clientId: "mp", clientName: "Марія Пономаренко", serviceName: "Манікюр", specialistId: "m", specialistName: "Дарина Коваль", locationId: "p", locationName: "Поділ", status: "confirmed" },
  { id: "r6", time: "15:30", clientId: "tg", clientName: "Тетяна Гончар", serviceName: "Манікюр + гель-лак", specialistId: "m", specialistName: "Марина Бойко", locationId: "c", locationName: "Центр", status: "confirmed" },
  { id: "r7", time: "17:00", clientId: null, clientName: "Онлайн-запис", serviceName: "Манікюр + гель-лак", specialistId: "m", specialistName: "Марина Бойко", locationId: "c", locationName: "Центр", status: "pending" },
] as const;

export const INITIAL_CHANNELS: ChannelConfig[] = [
  {
    id: "tg", name: "Telegram", icon: "TG", description: "Бот для записів і повідомлень клієнтів.",
    connected: true, inbox: true, booking: true, ai: true, mode: "confirm", greeting: "", handoff: { negative: true, payment: true, human: true },
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

export const ANALYTICS_LOCS = {
  c: { label: "Центр", rev: 412000, n: 596, avg: 691, load: 78,
    specs: [["m", "Марина Бойко", 142, 118000, 84], ["a", "Анна Шевчук", 96, 164000, 72], ["o", "Оксана Лис", 88, 98000, 69]] as [string, string, number, number, number][],
    svcs: [["Манікюр + гель-лак", 214000], ["Фарбування", 164000], ["Чистка обличчя", 98000], ["Стрижка", 54000]] as [string, number][] },
  p: { label: "Поділ", rev: 268000, n: 402, avg: 667, load: 66,
    specs: [["dk", "Дарина Коваль", 128, 96000, 74], ["im", "Ірина Мельник", 118, 172000, 68]] as [string, string, number, number, number][],
    svcs: [["Стрижка", 120000], ["Манікюр", 96000], ["Барбер-догляд", 52000]] as [string, number][] },
  k: { label: "Печерськ", rev: 231000, n: 338, avg: 683, load: 61,
    specs: [["vr", "Вікторія Руда", 104, 128000, 64], ["ng", "Наталя Гук", 98, 103000, 58]] as [string, string, number, number, number][],
    svcs: [["Чистка обличчя", 118000], ["Манікюр", 78000], ["Депіляція", 35000]] as [string, number][] },
};

export const ANALYTICS_WEEKS = [188, 201, 195, 214, 223, 208, 236, 241];

export const ANALYTICS_PROMOS = [
  { id: "a1", name: "Осінній манікюр", period: "7–14 жовт.", where: "Центр, Поділ", n: 64, fresh: 21, rev: 44800, cost: 11200 },
  { id: "a2", name: "Нові клієнти −20%", period: "постійна", where: "Усі заклади", n: 58, fresh: 58, rev: 52200, cost: 13050 },
  { id: "a3", name: "Чистка обличчя −20%", period: "1–31 жовт.", where: "Центр, Печерськ", n: 37, fresh: 9, rev: 40700, cost: 10175 },
  { id: "a4", name: "Happy hours 12–15", period: "пн–пт", where: "Поділ", n: 46, fresh: 12, rev: 29900, cost: 4485 },
];
