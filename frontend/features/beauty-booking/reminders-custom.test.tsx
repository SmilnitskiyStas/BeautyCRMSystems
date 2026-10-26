import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { toAppointment, type PublicAppointmentDto } from "./api/http-client";
import { reminderHint, reminderSummary } from "./content";
import { appointmentReminderOptionSchema, createAppointmentSchema, reminderOptionSchema } from "./types";

const flow = {
  location: { id: "c", name: "Центр", timezone: "Europe/Kyiv" },
  specialist: { id: "m", name: "Марина", role: "" },
  service: { id: "mn", name: "Манікюр", durationMinutes: 60, priceOriginal: 700, priceFinal: 700 },
  slot: { startsAt: "2026-10-16T08:00:00Z", label: "11:00", specialistId: "m" },
  reminder: "1h",
  paymentMethod: "cash",
  client: { name: "", phone: "" },
  website: "",
  setReminder: vi.fn(),
  setPaymentMethod: vi.fn(),
  setClient: vi.fn(),
  setWebsite: vi.fn(),
};
vi.mock("./hooks/useBookingFlow", () => ({
  useBookingFlow: (sel?: (s: typeof flow) => unknown) => (sel ? sel(flow) : flow),
}));

import { CheckoutStep } from "./components/CheckoutStep";

const dto = (reminderOption: string): PublicAppointmentDto => ({
  locationId: "c",
  locationName: "Центр",
  specialistId: "m",
  specialistName: "Марина",
  serviceId: "mn",
  serviceName: "Манікюр",
  startsAt: "2026-10-16T08:00:00Z",
  endsAt: "2026-10-16T09:00:00Z",
  durationMinutes: 60,
  status: "confirmed",
  priceOriginal: 700,
  priceFinal: 700,
  promotionId: null,
  reminderOption,
  paymentMethod: "cash",
  paymentStatus: null,
  cancellation: null,
});

describe("reminder_option=custom (§22.6.1)", () => {
  it("відповідь API приймає custom, запит - лише none/1h/2h", () => {
    expect(appointmentReminderOptionSchema.safeParse("custom").success).toBe(true);
    expect(reminderOptionSchema.safeParse("custom").success).toBe(false);
    const base = { locationId: "c", specialistId: "m", serviceId: "mn", startsAt: "x", client: { name: "Іван", phone: "+380501112233" }, paymentMethod: "cash" };
    expect(createAppointmentSchema.safeParse({ ...base, reminder: "2h" }).success).toBe(true);
    expect(createAppointmentSchema.safeParse({ ...base, reminder: "custom" }).success).toBe(false);
  });

  it("мапер запису зберігає custom, невідоме значення -> none", () => {
    expect(toAppointment(dto("custom")).reminderOption).toBe("custom");
    expect(toAppointment(dto("1h")).reminderOption).toBe("1h");
    expect(toAppointment(dto("sms")).reminderOption).toBe("none");
  });

  it("підсумок для custom не вигадує годин", () => {
    expect(reminderSummary("custom")).toMatch(/вибрані адміністратором моменти/);
    expect(reminderSummary("none")).toBe("Нагадування вимкнено.");
    expect(reminderSummary("2h")).toBe("Нагадаємо про запис за 2 години до візиту.");
  });
});

describe("підказка біля вибору нагадування в публічній формі", () => {
  it("пояснює, що нагадування надсилає Telegram-бот, і прив'язана до поля", () => {
    render(<CheckoutStep errors={{}} />);
    const select = screen.getByLabelText("Нагадати про запис");
    expect(select).toHaveAttribute("aria-describedby", "remind-hint");
    expect(document.getElementById("remind-hint")).toHaveTextContent(reminderHint);
    expect(reminderHint).toMatch(/Telegram/);
    // Логіка запису не змінена: ті самі варіанти вибору.
    expect(Array.from((select as HTMLSelectElement).options).map((o) => o.value)).toEqual(["none", "1h", "2h"]);
  });
});
