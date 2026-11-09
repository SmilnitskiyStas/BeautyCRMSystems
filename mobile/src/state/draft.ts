import { create } from "zustand";
import type { Service, Slot, Specialist } from "@beauty/booking-core";
import type { ResolvedLocation } from "@/lib/locationSource";

/** Чернетка запису в пам'яті (zustand). Нічого з цього не персиститься, окрім останньої локації (storage/tokenStore). */
interface Draft {
  location: ResolvedLocation | null;
  service: Service | null;
  specialistId: string;
  specialistName: string;
  /** Повні дані обраного майстра (null = «будь-який»); для шапки кроку дати/часу. */
  specialist: Specialist | null;
  date: string | null;
  slot: Slot | null;
  setLocation(l: ResolvedLocation): void;
  setService(s: Service): void;
  /** null = «будь-який майстер». Зміна майстра скидає дату й слот. */
  setSpecialist(sp: Specialist | null): void;
  setDate(d: string): void;
  setSlot(s: Slot | null): void;
  reset(): void;
}

const empty = { service: null, specialistId: "any", specialistName: "", specialist: null, date: null, slot: null };

export const useDraft = create<Draft>((set) => ({
  location: null,
  ...empty,
  // зміна закладу скидає все нижче за ним
  setLocation: (location) => set({ location, ...empty }),
  // інша послуга скидає залежні кроки: майстер (список залежить від послуги), дату, слот
  setService: (service) =>
    set((st) =>
      st.service?.id === service.id
        ? { service }
        : { service, specialistId: "any", specialistName: "", specialist: null, date: null, slot: null },
    ),
  setSpecialist: (sp) =>
    set({
      specialistId: sp?.id ?? "any",
      specialistName: sp?.name ?? "",
      specialist: sp,
      date: null,
      slot: null,
    }),
  setDate: (date) => set({ date, slot: null }),
  setSlot: (slot) => set({ slot }),
  reset: () => set({ ...empty }),
}));
