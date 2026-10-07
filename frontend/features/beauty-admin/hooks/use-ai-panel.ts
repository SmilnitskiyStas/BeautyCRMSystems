"use client";

import { create } from "zustand";

/** UI-стан плаваючого AI-віджета (Zustand лише для UI). */
interface AiPanelState {
  open: boolean;
  toggle: () => void;
  close: () => void;
}

export const useAiPanel = create<AiPanelState>((set) => ({
  open: false,
  toggle: () => set((s) => ({ open: !s.open })),
  close: () => set({ open: false }),
}));
