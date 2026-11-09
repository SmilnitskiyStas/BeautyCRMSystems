import { create } from "zustand";
import type { Service, Slot } from "@beauty/booking-core";
import type { ResolvedLocation } from "@/lib/locationSource";

/** Чернетка запису в пам'яті (zustand). Нічого з цього не персиститься, окрім останньої локації (storage/tokenStore). */
interface Draft {
  location: ResolvedLocation | null;
  service: Service | null;
  specialistId: string;
  specialistName: string;
  date: string | null;
  slot: Slot | null;
  setLocation(l: ResolvedLocation): void;
  setService(s: Service): void;
  setSpecialist(id: string, name: string): void;
  setDate(d: string): void;
  setSlot(s: Slot | null): void;
  reset(): void;
}

const empty = { service: null, specialistId: "any", specialistName: "", date: null, slot: null };

export const useDraft = create<Draft>((set) => ({
  location: null,
  ...empty,
  // зміна закладу скидає все нижче за ним
  setLocation: (location) => set({ location, ...empty }),
  setService: (service) => set({ service, slot: null }),
  setSpecialist: (specialistId, specialistName) => set({ specialistId, specialistName, slot: null }),
  setDate: (date) => set({ date, slot: null }),
  setSlot: (slot) => set({ slot }),
  reset: () => set({ ...empty }),
}));
