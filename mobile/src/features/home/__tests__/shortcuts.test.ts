import { activeOrdersCount, nextAppointment } from "../shortcuts";
import type { AppointmentRecord, OrderRecord } from "@/storage/tokenStore";

const appt = (localId: string, startsAt: string, status?: string): AppointmentRecord => ({
  localId, tenantSlug: "t", locationName: "L", serviceName: "S", specialistName: "M", startsAt, status, createdAt: "2026-11-01T00:00:00Z",
});
const order = (localId: string, status?: string): OrderRecord => ({
  localId, tenantSlug: "t", locationName: "L", number: 1, total: 100, status, createdAt: "2026-11-01T00:00:00Z",
});
const now = new Date("2026-11-22T10:00:00Z");

describe("nextAppointment", () => {
  it("обирає найближчий майбутній, пропускає минулі й скасовані", () => {
    const items = [
      appt("past0001", "2026-11-21T14:00:00+02:00"),
      appt("later001", "2026-11-25T14:00:00+02:00"),
      appt("cancel01", "2026-11-23T09:00:00+02:00", "cancelled"),
      appt("soon0001", "2026-11-24T09:00:00+02:00", "confirmed"),
    ];
    expect(nextAppointment(items, now)?.localId).toBe("soon0001");
  });
  it("враховує зсув закладу при порівнянні з поточним часом", () => {
    // 12:30+02:00 = 10:30Z - ще попереду
    expect(nextAppointment([appt("tz000001", "2026-11-22T12:30:00+02:00")], now)?.localId).toBe("tz000001");
    // 11:30+02:00 = 09:30Z - вже минув
    expect(nextAppointment([appt("tz000002", "2026-11-22T11:30:00+02:00")], now)).toBeNull();
  });
  it("порожній список або некоректна дата -> null", () => {
    expect(nextAppointment([], now)).toBeNull();
    expect(nextAppointment([appt("bad00001", "not-a-date")], now)).toBeNull();
  });
});

describe("activeOrdersCount", () => {
  it("рахує лише ті, що чекають оплати або видачі", () => {
    expect(activeOrdersCount([order("o0000001", "pending_payment"), order("o0000002", "paid"), order("o0000003", "fulfilled"), order("o0000004", "cancelled"), order("o0000005")])).toBe(3);
  });
});
