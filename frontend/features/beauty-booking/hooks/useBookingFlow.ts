"use client";

import { create } from "zustand";
import type {
  BookingResult,
  Location,
  PaymentMethod,
  ReminderOption,
  Service,
  Slot,
  Specialist,
} from "../types";

export type Step = 1 | 2 | 3 | 4 | 5;

interface BookingState {
  step: Step;
  location: Location | null;
  specialist: Specialist | null;
  service: Service | null;
  slot: Slot | null;
  reminder: ReminderOption;
  paymentMethod: PaymentMethod;
  client: { name: string; phone: string };
  /** Результат створення: `publicToken` + запис. Токен живе лише в пам'яті та в посиланні на запис. */
  booking: BookingResult | null;
  /** Honeypot-поле: людина його не бачить, тож значення завжди порожнє. */
  website: string;
  setWebsite: (v: string) => void;
  setLocation: (l: Location) => void;
  setSpecialist: (s: Specialist) => void;
  setService: (s: Service) => void;
  setSlot: (s: Slot | null) => void;
  setReminder: (r: ReminderOption) => void;
  setPaymentMethod: (p: PaymentMethod) => void;
  setClient: (c: Partial<{ name: string; phone: string }>) => void;
  next: () => void;
  back: () => void;
  done: (booking: BookingResult) => void;
  reset: () => void;
}

const initial = {
  step: 1 as Step,
  location: null,
  specialist: null,
  service: null,
  slot: null,
  reminder: "1h" as ReminderOption,
  paymentMethod: "card" as PaymentMethod,
  client: { name: "", phone: "" },
  booking: null,
  website: "",
};

export const useBookingFlow = create<BookingState>((set) => ({
  ...initial,
  // Зміна попереднього вибору скидає залежні кроки.
  setLocation: (location) => set({ location, specialist: null, service: null, slot: null }),
  setSpecialist: (specialist) => set({ specialist, service: null, slot: null }),
  setService: (service) => set({ service, slot: null }),
  setSlot: (slot) => set({ slot }),
  setReminder: (reminder) => set({ reminder }),
  setPaymentMethod: (paymentMethod) => set({ paymentMethod }),
  setClient: (c) => set((s) => ({ client: { ...s.client, ...c } })),
  next: () => set((s) => ({ step: Math.min(5, s.step + 1) as Step })),
  back: () => set((s) => ({ step: Math.max(1, s.step - 1) as Step })),
  setWebsite: (website) => set({ website }),
  done: (booking) => set({ booking, step: 5 }),
  reset: () => set(initial),
}));
