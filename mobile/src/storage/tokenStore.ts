import { z } from "zod";
import { optionalHttpsUrl } from "@/lib/urls";

/**
 * Локальне сховище гостя (ADR-021 §4, skill offline-storage).
 *  - `publicToken` кожного запису: окремий ключ SecureStore `appt-token.{localId}` (bearer-credential);
 *  - індекс записів (без токенів і без телефону): AsyncStorage - для відображення/офлайн-кешу;
 *  - ім'я/телефон: SecureStore;  - остання локація: AsyncStorage (лише код і назва, ніколи координати).
 * Залежності (KV) передаються ззовні -> модуль чистий і тестується без нативних модулів.
 * Конфлікт-правило: сервер - джерело істини для статусу; локальний знімок лише показується офлайн і позначається застарілим.
 */

export interface SecureKV {
  getItemAsync(key: string): Promise<string | null>;
  setItemAsync(key: string, value: string): Promise<void>;
  deleteItemAsync(key: string): Promise<void>;
}
export interface PlainKV {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
}

const INDEX_KEY = "beauty.appointments.index.v1";
const LAST_LOCATION_KEY = "beauty.lastLocation.v1";
const PROFILE_KEY = "beauty.profile.v1";
/** Історія закладів (лише публічні дані закладу, без координат) і незавершений запис - AsyncStorage. */
const RECENT_LOCATIONS_KEY = "beauty.recentLocations.v1";
const BOOKING_DRAFT_KEY = "beauty.bookingDraft.v1";
export const RECENT_LOCATIONS_MAX = 5;
/** Незавершений запис старший за тиждень вважаємо неактуальним. */
export const BOOKING_DRAFT_TTL_MS = 7 * 24 * 60 * 60 * 1000;
/** Резервний реєстр localId токенів у SecureStore: щоб «видалити все» знайшло токени, навіть якщо індекс пошкоджено. */
const TOKEN_IDS_KEY = "appt-token-ids";
/** Ідемпотентний ключ створення запису, що ще не отримав відповіді (SecureStore). */
const PENDING_KEY = "pending-idempotency";
/** Прапорець у AsyncStorage (видаляється разом із застосунком, на відміну від Keychain iOS). */
const FIRST_RUN_KEY = "beauty.firstRunDone.v1";
/** Скільки pending-ключ вважається актуальним (сервер тримає ідемпотентність обмежений час). */
export const PENDING_TTL_MS = 24 * 60 * 60 * 1000;
/** SecureStore допускає лише [A-Za-z0-9._-] у ключі. */
const TOKEN_PREFIX = "appt-token.";
const LOCAL_ID_RE = /^[A-Za-z0-9._-]{8,128}$/;

export const appointmentRecordSchema = z.object({
  localId: z.string().regex(LOCAL_ID_RE),
  tenantSlug: z.string().min(1),
  locationName: z.string(),
  serviceName: z.string(),
  specialistName: z.string(),
  /** Знімок ISO зі зсувом закладу. */
  startsAt: z.string(),
  /** Останній відомий статус із сервера (знімок). */
  status: z.string().optional(),
  createdAt: z.string(),
});
export type AppointmentRecord = z.infer<typeof appointmentRecordSchema>;

export const lastLocationSchema = z.object({
  publicCode: z.string().min(1),
  name: z.string(),
});
export type LastLocation = z.infer<typeof lastLocationSchema>;

export const recentLocationSchema = z.object({
  publicCode: z.string().min(1),
  name: z.string(),
  address: z.string().nullable().optional(),
  city: z.string().nullable().optional(),
  logoUrl: optionalHttpsUrl,
  /** Мілісекунди epoch останнього відкриття закладу. */
  visitedAt: z.number(),
});
export type RecentLocation = z.infer<typeof recentLocationSchema>;

/**
 * Незавершений запис: заклад + обрані кроки. Слот не зберігається (швидко застаріває), дата - лише як підказка.
 * Без контактів клієнта: ім'я/телефон живуть окремо в SecureStore (профіль).
 */
export const bookingDraftSchema = z.object({
  publicCode: z.string().min(1),
  locationName: z.string(),
  locationLogoUrl: optionalHttpsUrl,
  service: z.object({
    id: z.string().min(1),
    name: z.string(),
    durationMinutes: z.number(),
    priceOriginal: z.number(),
    priceFinal: z.number(),
    description: z.string().optional(),
    category: z.string().optional(),
    promoLabel: z.string().optional(),
    promotionId: z.string().optional(),
  }),
  /** null = «будь-який майстер». */
  specialist: z
    .object({
      id: z.string().min(1),
      name: z.string(),
      role: z.string(),
      photoUrl: z.string().nullable().optional(),
      experienceYears: z.number().nullable().optional(),
      bio: z.string().nullable().optional(),
      rating: z.number().nullable().optional(),
      reviewsCount: z.number().optional(),
    })
    .nullable(),
  date: z.string().regex(/^\d{4}-\d{2}-\d{2}$/).nullable(),
  updatedAt: z.number(),
});
export type BookingDraft = z.infer<typeof bookingDraftSchema>;

export const pendingKeySchema = z.object({
  fingerprint: z.string(),
  key: z.string().regex(/^[A-Za-z0-9._:-]{16,128}$/),
  createdAt: z.number(),
});
export type PendingKey = z.infer<typeof pendingKeySchema>;

export const profileSchema = z.object({ name: z.string(), phone: z.string(), email: z.string().optional() });
export type Profile = z.infer<typeof profileSchema>;

const tokenKey = (localId: string) => `${TOKEN_PREFIX}${localId}`;

/** Замовлення товарів (ADR-026): та сама схема, що й записи - токен у SecureStore, індекс без токенів у AsyncStorage. */
const ORDER_INDEX_KEY = "beauty.orders.index.v1";
const ORDER_TOKEN_PREFIX = "order-token.";
const ORDER_TOKEN_IDS_KEY = "order-token-ids";
const ORDER_PENDING_KEY = "pending-order-idempotency";
const orderTokenKey = (localId: string) => `${ORDER_TOKEN_PREFIX}${localId}`;

/** `localId` = Idempotency-Key створення; публічний токен замовлення в індекс не потрапляє. */
export const orderRecordSchema = z.object({
  localId: z.string().regex(LOCAL_ID_RE),
  tenantSlug: z.string().min(1),
  locationName: z.string(),
  number: z.number(),
  total: z.number(),
  status: z.string().optional(),
  createdAt: z.string(),
});
export type OrderRecord = z.infer<typeof orderRecordSchema>;

function parseJson<T>(raw: string | null, schema: z.ZodType<T>): T | null {
  if (!raw) return null;
  try {
    const r = schema.safeParse(JSON.parse(raw));
    return r.success ? r.data : null;
  } catch {
    return null;
  }
}

export function createLocalStore(secure: SecureKV, plain: PlainKV) {
  async function readIndex(): Promise<AppointmentRecord[]> {
    const raw = await plain.getItem(INDEX_KEY);
    if (!raw) return [];
    try {
      const arr = z.array(z.unknown()).parse(JSON.parse(raw));
      // пошкоджені елементи відкидаємо поодинці
      return arr.flatMap((x) => {
        const r = appointmentRecordSchema.safeParse(x);
        return r.success ? [r.data] : [];
      });
    } catch {
      return [];
    }
  }
  async function readTokenIds(): Promise<string[]> {
    const raw = await secure.getItemAsync(TOKEN_IDS_KEY);
    try {
      return raw ? z.array(z.string()).parse(JSON.parse(raw)) : [];
    } catch {
      return [];
    }
  }
  const writeIndex = (items: AppointmentRecord[]) => plain.setItem(INDEX_KEY, JSON.stringify(items));

  async function readOrderIndex(): Promise<OrderRecord[]> {
    const raw = await plain.getItem(ORDER_INDEX_KEY);
    if (!raw) return [];
    try {
      return z.array(z.unknown()).parse(JSON.parse(raw)).flatMap((x) => {
        const r = orderRecordSchema.safeParse(x);
        return r.success ? [r.data] : [];
      });
    } catch {
      return [];
    }
  }
  async function readOrderTokenIds(): Promise<string[]> {
    const raw = await secure.getItemAsync(ORDER_TOKEN_IDS_KEY);
    try {
      return raw ? z.array(z.string()).parse(JSON.parse(raw)) : [];
    } catch {
      return [];
    }
  }
  const writeOrderIndex = (items: OrderRecord[]) => plain.setItem(ORDER_INDEX_KEY, JSON.stringify(items));

  async function listRecentLocations(): Promise<RecentLocation[]> {
    const raw = await plain.getItem(RECENT_LOCATIONS_KEY);
    if (!raw) {
      // історії ще немає (стара збірка або перший вибір до цієї версії): показуємо збережену останню локацію
      const last = parseJson(await plain.getItem(LAST_LOCATION_KEY), lastLocationSchema);
      return last ? [{ ...last, visitedAt: 0 }] : [];
    }
    try {
      return z
        .array(z.unknown())
        .parse(JSON.parse(raw))
        .flatMap((x) => {
          const r = recentLocationSchema.safeParse(x);
          return r.success ? [r.data] : [];
        })
        .sort((a, b) => b.visitedAt - a.visitedAt)
        .slice(0, RECENT_LOCATIONS_MAX);
    } catch {
      return [];
    }
  }

  /** «Видалити мої дані на цьому пристрої»: токени (кожен ключ), профіль, pending-ключ, індекс, остання локація. */
  async function wipeAll(): Promise<void> {
    const ids = new Set([...(await readIndex()).map((i) => i.localId), ...(await readTokenIds())]);
    const orderIds = new Set([...(await readOrderIndex()).map((i) => i.localId), ...(await readOrderTokenIds())]);
    // allSettled: збій одного ключа не зупиняє видалення решти; токени йдуть першими, реєстри/індекси - в тому ж пакеті
    const results = await Promise.allSettled([
      ...[...ids].filter((id) => LOCAL_ID_RE.test(id)).map((id) => secure.deleteItemAsync(tokenKey(id))),
      ...[...orderIds].filter((id) => LOCAL_ID_RE.test(id)).map((id) => secure.deleteItemAsync(orderTokenKey(id))),
      secure.deleteItemAsync(ORDER_TOKEN_IDS_KEY),
      secure.deleteItemAsync(ORDER_PENDING_KEY),
      plain.removeItem(ORDER_INDEX_KEY),
      secure.deleteItemAsync(TOKEN_IDS_KEY),
      secure.deleteItemAsync(PROFILE_KEY),
      secure.deleteItemAsync(PENDING_KEY),
      plain.removeItem(INDEX_KEY),
      plain.removeItem(LAST_LOCATION_KEY),
      plain.removeItem(RECENT_LOCATIONS_KEY),
      plain.removeItem(BOOKING_DRAFT_KEY),
    ]);
    if (results.some((r) => r.status === "rejected")) throw new Error("wipe_incomplete");
  }

  return {
    /**
     * Зберегти запис. `localId` = Idempotency-Key створення: повтор того ж запиту (той самий ключ, той самий токен)
     * оновлює наявний запис, а не додає дубль. Спочатку токен, потім індекс: збій між ними не лишає запис без токена.
     */
    async saveAppointment(record: AppointmentRecord, token: string): Promise<void> {
      const rec = appointmentRecordSchema.parse(record);
      await secure.setItemAsync(tokenKey(rec.localId), token);
      const ids = await readTokenIds();
      if (!ids.includes(rec.localId)) await secure.setItemAsync(TOKEN_IDS_KEY, JSON.stringify([...ids, rec.localId]));
      const items = await readIndex();
      const next = items.some((i) => i.localId === rec.localId)
        ? items.map((i) => (i.localId === rec.localId ? rec : i))
        : [rec, ...items];
      await writeIndex(next);
    },

    /** Список для відображення: БЕЗ токенів. Найближчі за часом першими. */
    async listAppointments(): Promise<AppointmentRecord[]> {
      return (await readIndex()).sort((a, b) => b.startsAt.localeCompare(a.startsAt));
    },

    /** Токен читається лише в момент запиту до API (перегляд/скасування). */
    getToken: (localId: string) =>
      LOCAL_ID_RE.test(localId) ? secure.getItemAsync(tokenKey(localId)) : Promise.resolve(null),

    async updateSnapshot(localId: string, patch: Partial<Pick<AppointmentRecord, "status" | "startsAt">>): Promise<void> {
      const items = await readIndex();
      await writeIndex(items.map((i) => (i.localId === localId ? { ...i, ...patch } : i)));
    },

    async removeAppointment(localId: string): Promise<void> {
      await secure.deleteItemAsync(tokenKey(localId));
      await secure.setItemAsync(TOKEN_IDS_KEY, JSON.stringify((await readTokenIds()).filter((i) => i !== localId)));
      await writeIndex((await readIndex()).filter((i) => i.localId !== localId));
    },

    async getLastLocation(): Promise<LastLocation | null> {
      return parseJson(await plain.getItem(LAST_LOCATION_KEY), lastLocationSchema);
    },
    setLastLocation: (l: LastLocation) =>
      plain.setItem(LAST_LOCATION_KEY, JSON.stringify(lastLocationSchema.parse(l))),
    clearLastLocation: () => plain.removeItem(LAST_LOCATION_KEY),

    /** Нещодавні заклади: останній відкритий першим, без дублів за кодом, не більше RECENT_LOCATIONS_MAX. */
    listRecentLocations,
    async addRecentLocation(l: Omit<RecentLocation, "visitedAt">, now: number = Date.now()): Promise<void> {
      const rec = recentLocationSchema.parse({ ...l, visitedAt: now });
      const rest = (await listRecentLocations()).filter((i) => i.publicCode !== rec.publicCode);
      await plain.setItem(RECENT_LOCATIONS_KEY, JSON.stringify([rec, ...rest].slice(0, RECENT_LOCATIONS_MAX)));
    },
    async removeRecentLocation(publicCode: string): Promise<void> {
      const items = await listRecentLocations();
      await plain.setItem(RECENT_LOCATIONS_KEY, JSON.stringify(items.filter((i) => i.publicCode !== publicCode)));
    },

    /** Незавершений запис; протермінований (TTL) або пошкоджений - видаляється й повертає null. */
    async getBookingDraft(now: number = Date.now()): Promise<BookingDraft | null> {
      const raw = await plain.getItem(BOOKING_DRAFT_KEY);
      const d = parseJson(raw, bookingDraftSchema);
      if (d && now - d.updatedAt < BOOKING_DRAFT_TTL_MS) return d;
      if (raw !== null) await plain.removeItem(BOOKING_DRAFT_KEY);
      return null;
    },
    setBookingDraft: (d: BookingDraft) => plain.setItem(BOOKING_DRAFT_KEY, JSON.stringify(bookingDraftSchema.parse(d))),
    clearBookingDraft: () => plain.removeItem(BOOKING_DRAFT_KEY),

    /** Замовлення: спершу токен, потім індекс (збій між ними не лишає запис без токена). Повтор з тим самим localId оновлює. */
    async saveOrder(record: OrderRecord, token: string): Promise<void> {
      const rec = orderRecordSchema.parse(record);
      await secure.setItemAsync(orderTokenKey(rec.localId), token);
      const ids = await readOrderTokenIds();
      if (!ids.includes(rec.localId)) await secure.setItemAsync(ORDER_TOKEN_IDS_KEY, JSON.stringify([...ids, rec.localId]));
      const items = await readOrderIndex();
      await writeOrderIndex(
        items.some((i) => i.localId === rec.localId) ? items.map((i) => (i.localId === rec.localId ? rec : i)) : [rec, ...items],
      );
    },
    /** Без токенів; нові першими. */
    async listOrders(): Promise<OrderRecord[]> {
      return (await readOrderIndex()).sort((a, b) => b.createdAt.localeCompare(a.createdAt));
    },
    getOrderToken: (localId: string) =>
      LOCAL_ID_RE.test(localId) ? secure.getItemAsync(orderTokenKey(localId)) : Promise.resolve(null),
    async updateOrderSnapshot(localId: string, patch: Partial<Pick<OrderRecord, "status">>): Promise<void> {
      await writeOrderIndex((await readOrderIndex()).map((i) => (i.localId === localId ? { ...i, ...patch } : i)));
    },
    async removeOrder(localId: string): Promise<void> {
      if (!LOCAL_ID_RE.test(localId)) return;
      await secure.deleteItemAsync(orderTokenKey(localId));
      await secure.setItemAsync(ORDER_TOKEN_IDS_KEY, JSON.stringify((await readOrderTokenIds()).filter((i) => i !== localId)));
      await writeOrderIndex((await readOrderIndex()).filter((i) => i.localId !== localId));
    },
    async getOrderPendingKey(now: number = Date.now()): Promise<PendingKey | null> {
      const p = parseJson(await secure.getItemAsync(ORDER_PENDING_KEY), pendingKeySchema);
      if (p && now - p.createdAt < PENDING_TTL_MS) return p;
      if (p) await secure.deleteItemAsync(ORDER_PENDING_KEY); // протермінований: не тримати
      return null;
    },
    setOrderPendingKey: (p: PendingKey) => secure.setItemAsync(ORDER_PENDING_KEY, JSON.stringify(pendingKeySchema.parse(p))),
    clearOrderPendingKey: () => secure.deleteItemAsync(ORDER_PENDING_KEY),

    async getProfile(): Promise<Profile | null> {
      return parseJson(await secure.getItemAsync(PROFILE_KEY), profileSchema);
    },
    setProfile: (p: Profile) => secure.setItemAsync(PROFILE_KEY, JSON.stringify(profileSchema.parse(p))),

    async getPendingKey(now: number = Date.now()): Promise<PendingKey | null> {
      const p = parseJson(await secure.getItemAsync(PENDING_KEY), pendingKeySchema);
      return p && now - p.createdAt < PENDING_TTL_MS ? p : null;
    },
    setPendingKey: (p: PendingKey) => secure.setItemAsync(PENDING_KEY, JSON.stringify(pendingKeySchema.parse(p))),
    clearPendingKey: () => secure.deleteItemAsync(PENDING_KEY),

    /**
     * L8: Keychain iOS переживає видалення застосунку, AsyncStorage - ні. Якщо прапорця першого запуску немає, а індексу
     * записів теж немає (перевстановлення), стираємо залишки SecureStore. Якщо індекс є (оновлення старої збірки) -
     * дані легітимні, лише ставимо прапорець.
     */
    async firstRunCleanup(): Promise<"skipped" | "kept" | "purged"> {
      if (await plain.getItem(FIRST_RUN_KEY)) return "skipped";
      const legit = (await plain.getItem(INDEX_KEY)) !== null || (await plain.getItem(ORDER_INDEX_KEY)) !== null;
      if (!legit) await wipeAll();
      await plain.setItem(FIRST_RUN_KEY, "1");
      return legit ? "kept" : "purged";
    },

    wipeAll,
  };
}

export type LocalStore = ReturnType<typeof createLocalStore>;
