import { BookingApiError, type BookingResult, type CreateAppointmentRequest } from "@beauty/booking-core";
import { createBookingSubmitter, isRetryable } from "../submit";
import { createLocalStore } from "@/storage/tokenStore";

const req = (phone = "+380501112233"): CreateAppointmentRequest => ({
  locationId: "loc1",
  specialistId: "sp1",
  serviceId: "svc1",
  startsAt: "2026-11-12T14:30:00+02:00",
  client: { name: "Olena", phone },
  reminder: "none",
  paymentMethod: "cash",
});
const names = { locationName: "Salon", serviceName: "Manicure", specialistName: "Anna" };

const result = (token = "PUBLIC-TOKEN-1"): BookingResult => ({
  token,
  appointment: {
    locationName: "Salon", specialistName: "Anna", serviceName: "Manicure",
    startsAt: "2026-11-12T14:30:00+02:00", endsAt: "2026-11-12T15:30:00+02:00", durationMinutes: 60,
    status: "pending", priceOriginal: 500, priceFinal: 500, reminderOption: "none",
  },
});

function setup() {
  const secureMap = new Map<string, string>();
  const plainMap = new Map<string, string>();
  const store = createLocalStore(
    { getItemAsync: async (k) => secureMap.get(k) ?? null, setItemAsync: async (k, v) => void secureMap.set(k, v), deleteItemAsync: async (k) => void secureMap.delete(k) },
    { getItem: async (k) => plainMap.get(k) ?? null, setItem: async (k, v) => void plainMap.set(k, v), removeItem: async (k) => void plainMap.delete(k) },
  );
  let n = 0;
  const keys: string[] = [];
  const createAppointment = jest.fn<Promise<BookingResult>, [string, CreateAppointmentRequest, string]>();
  const submitter = createBookingSubmitter({
    api: { createAppointment },
    store,
    generateKey: () => {
      const k = `00000000-0000-4000-8000-${String(++n).padStart(12, "0")}`;
      keys.push(k);
      return k;
    },
    now: () => new Date("2026-11-09T10:00:00Z"),
  });
  return { store, createAppointment, submitter, keys };
}

describe("booking submit: idempotency and dedup", () => {
  it("retry after a network failure reuses the same Idempotency-Key and saves exactly one record", async () => {
    const { store, createAppointment, submitter, keys } = setup();
    createAppointment.mockRejectedValueOnce(new BookingApiError(0, "api_unreachable")).mockResolvedValueOnce(result());

    await expect(submitter.submit("salon-a", req(), names)).rejects.toBeInstanceOf(BookingApiError);
    await submitter.submit("salon-a", req(), names);

    expect(createAppointment).toHaveBeenCalledTimes(2);
    expect(createAppointment.mock.calls[0][2]).toBe(createAppointment.mock.calls[1][2]);
    expect(keys).toHaveLength(1);
    const list = await store.listAppointments();
    expect(list).toHaveLength(1);
    expect(list[0].localId).toBe(keys[0]);
    expect(await store.getToken(keys[0])).toBe("PUBLIC-TOKEN-1");
  });

  it("changed data gets a new key (otherwise the server answers 422 idempotency_key_reused)", async () => {
    const { createAppointment, submitter } = setup();
    createAppointment.mockRejectedValueOnce(new BookingApiError(0, "api_unreachable")).mockResolvedValueOnce(result());
    await expect(submitter.submit("salon-a", req("+380501112233"), names)).rejects.toBeDefined();
    await submitter.submit("salon-a", req("+380509998877"), names);
    expect(createAppointment.mock.calls[0][2]).not.toBe(createAppointment.mock.calls[1][2]);
  });

  it("a new booking after success is a new intent with a new key", async () => {
    const { store, createAppointment, submitter } = setup();
    createAppointment.mockResolvedValueOnce(result("TOKEN-A")).mockResolvedValueOnce(result("TOKEN-B"));
    await submitter.submit("salon-a", req(), names);
    await submitter.submit("salon-a", req(), names);
    expect(createAppointment.mock.calls[0][2]).not.toBe(createAppointment.mock.calls[1][2]);
    expect(await store.listAppointments()).toHaveLength(2);
  });

  it("double tap while in flight shares one request", async () => {
    const { store, createAppointment, submitter } = setup();
    let release!: (r: BookingResult) => void;
    createAppointment.mockReturnValueOnce(new Promise((r) => (release = r)));
    const p1 = submitter.submit("salon-a", req(), names);
    const p2 = submitter.submit("salon-a", req(), names);
    release(result());
    await Promise.all([p1, p2]);
    expect(createAppointment).toHaveBeenCalledTimes(1);
    expect(await store.listAppointments()).toHaveLength(1);
  });

  it("a server-side replay (same token) after a lost response does not duplicate the local record", async () => {
    const { store, createAppointment, submitter } = setup();
    // перша спроба: сервер створив, але відповідь загубилась; ключ у holder лишився
    createAppointment.mockRejectedValueOnce(new BookingApiError(0, "api_unreachable")).mockResolvedValueOnce(result("SAME")).mockResolvedValueOnce(result("SAME"));
    await expect(submitter.submit("salon-a", req(), names)).rejects.toBeDefined();
    await submitter.submit("salon-a", req(), names);
    expect(await store.listAppointments()).toHaveLength(1);
  });
});

describe("isRetryable", () => {
  it("network and 5xx are retryable, 4xx are not", () => {
    expect(isRetryable(new BookingApiError(0, "api_unreachable"))).toBe(true);
    expect(isRetryable(new BookingApiError(503, ""))).toBe(true);
    expect(isRetryable(new BookingApiError(409, "slot_unavailable"))).toBe(false);
    expect(isRetryable(new BookingApiError(422, "invalid_phone"))).toBe(false);
  });
});

describe("pending idempotency key survives restarts", () => {
  function sharedStore() {
    const secureMap = new Map<string, string>();
    const plainMap = new Map<string, string>();
    return createLocalStore(
      { getItemAsync: async (k) => secureMap.get(k) ?? null, setItemAsync: async (k, v) => void secureMap.set(k, v), deleteItemAsync: async (k) => void secureMap.delete(k) },
      { getItem: async (k) => plainMap.get(k) ?? null, setItem: async (k, v) => void plainMap.set(k, v), removeItem: async (k) => void plainMap.delete(k) },
    );
  }
  const make = (store: ReturnType<typeof sharedStore>, createAppointment: jest.Mock, prefix: string, now = new Date("2026-11-09T10:00:00Z")) => {
    let n = 0;
    return createBookingSubmitter({
      api: { createAppointment },
      store,
      generateKey: () => `${prefix}-0000-4000-8000-${String(++n).padStart(12, "0")}`,
      now: () => now,
    });
  };

  it("a new submitter (app restart) reuses the key of an unanswered request with the same body", async () => {
    const store = sharedStore();
    const create = jest.fn().mockRejectedValueOnce(new BookingApiError(0, "api_unreachable")).mockResolvedValueOnce(result());
    await expect(make(store, create, "aaaaaaaa").submit("salon-a", req(), names)).rejects.toBeDefined();
    await make(store, create, "bbbbbbbb").submit("salon-a", req(), names); // "перезапуск": інший генератор/holder
    expect(create.mock.calls[1][2]).toBe(create.mock.calls[0][2]);
    expect(create.mock.calls[0][2]).toMatch(/^aaaaaaaa/);
    expect(await store.getPendingKey()).toBeNull(); // очищено після успіху
  });

  it("a different body after restart gets a new key", async () => {
    const store = sharedStore();
    const create = jest.fn().mockRejectedValueOnce(new BookingApiError(0, "api_unreachable")).mockResolvedValueOnce(result());
    await expect(make(store, create, "aaaaaaaa").submit("salon-a", req("+380501112233"), names)).rejects.toBeDefined();
    await make(store, create, "bbbbbbbb").submit("salon-a", req("+380509998877"), names);
    expect(create.mock.calls[1][2]).not.toBe(create.mock.calls[0][2]);
  });

  it("the key is persisted before the request is sent", async () => {
    const store = sharedStore();
    let seen: unknown = null;
    const create = jest.fn(async () => {
      seen = await store.getPendingKey(new Date("2026-11-09T10:00:00Z").getTime());
      return result();
    });
    await make(store, create, "aaaaaaaa").submit("salon-a", req(), names);
    expect(seen).not.toBeNull();
  });

  it("a final 4xx failure clears the pending key; a stale one (over 24h) is ignored", async () => {
    const store = sharedStore();
    const create = jest.fn().mockRejectedValueOnce(new BookingApiError(409, "slot_unavailable"));
    await expect(make(store, create, "aaaaaaaa").submit("salon-a", req(), names)).rejects.toBeDefined();
    expect(await store.getPendingKey()).toBeNull();

    const fail = jest.fn().mockRejectedValueOnce(new BookingApiError(0, "api_unreachable"));
    await expect(make(store, fail, "cccccccc").submit("salon-a", req(), names)).rejects.toBeDefined();
    const ok = jest.fn().mockResolvedValueOnce(result());
    await make(store, ok, "dddddddd", new Date("2026-11-11T10:00:00Z")).submit("salon-a", req(), names);
    expect(ok.mock.calls[0][2]).toMatch(/^dddddddd/);
  });
});
