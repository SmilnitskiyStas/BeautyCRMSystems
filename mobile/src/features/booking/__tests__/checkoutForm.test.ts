import { createAppointmentSchema } from "@beauty/booking-core";
import { validateCheckoutForm } from "../checkoutForm";

const base = { name: "Олена", phone: "+380501112233", email: "", reminder: "none" as const };

describe("validateCheckoutForm", () => {
  it("порожня пошта без нагадування - ок, email відсутній", () => {
    const r = validateCheckoutForm(base);
    expect(r.errors).toEqual({});
    expect(r.client).toEqual({ name: "Олена", phone: "+380501112233", email: undefined });
  });

  it("порожня пошта з пробілів - ок", () => {
    expect(validateCheckoutForm({ ...base, email: "   " }).errors).toEqual({});
  });

  it("невалідний формат пошти - помилка біля поля", () => {
    for (const email of ["abc", "a@b", "a@@b.com", "a b@c.com", "@c.com", "a@.com"]) {
      const r = validateCheckoutForm({ ...base, email });
      expect(r.errors.email).toBe("Вкажіть коректну пошту");
      expect(r.client).toBeNull();
    }
  });

  it("пошта довша за 254 символи - помилка", () => {
    const email = `${"a".repeat(250)}@b.com`;
    expect(validateCheckoutForm({ ...base, email }).errors.email).toBeDefined();
  });

  it("тримається й переводиться в нижній регістр", () => {
    const r = validateCheckoutForm({ ...base, email: "  Olena@Example.COM " });
    expect(r.errors).toEqual({});
    expect(r.client?.email).toBe("olena@example.com");
  });

  it("нагадування без пошти - помилка біля пошти, запис не відправляється", () => {
    for (const reminder of ["1h", "2h", "24h"] as const) {
      const r = validateCheckoutForm({ ...base, reminder });
      expect(r.errors.email).toBe("Вкажіть пошту, щоб ми могли надіслати нагадування");
      expect(r.client).toBeNull();
    }
  });

  it("нагадування з валідною поштою - ок", () => {
    const r = validateCheckoutForm({ ...base, reminder: "24h", email: "a@b.co" });
    expect(r.errors).toEqual({});
    expect(r.client).not.toBeNull();
  });

  it("невалідна пошта з нагадуванням показує помилку формату, не 'вкажіть'", () => {
    expect(validateCheckoutForm({ ...base, reminder: "1h", email: "x" }).errors.email).toBe("Вкажіть коректну пошту");
  });
});

describe("тіло запиту", () => {
  const body = (reminder: "none" | "1h" | "2h" | "24h", email: string) => {
    const { client } = validateCheckoutForm({ ...base, reminder, email });
    return { locationId: "l", specialistId: "s", serviceId: "v", startsAt: "2026-12-01T10:00:00+02:00", client: { ...client!, marketingConsent: false }, reminder, paymentMethod: "cash" };
  };

  it.each(["none", "1h", "2h", "24h"] as const)("reminder %s проходить схему запиту", (reminder) => {
    const r = createAppointmentSchema.safeParse(body(reminder, "A@B.com"));
    expect(r.success).toBe(true);
    if (r.success) {
      expect(r.data.reminder).toBe(reminder);
      expect(r.data.client.email).toBe("a@b.com");
      expect(r.data.client.marketingConsent).toBe(false);
    }
  });

  it("без пошти email відсутній у JSON", () => {
    expect(JSON.stringify(body("none", "").client)).not.toContain("email");
  });
});
