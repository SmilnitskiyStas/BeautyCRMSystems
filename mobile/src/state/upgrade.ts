import { create } from "zustand";

/** Прапорець «потрібне оновлення застосунку» (426): виставляється fetch-обгорткою, читається кореневим layout. */
export const useUpgrade = create<{ required: boolean; setRequired(): void }>((set) => ({
  required: false,
  setRequired: () => set({ required: true }),
}));
