"use client";

import { createContext, useContext } from "react";

/** Slug tenant-а для всіх запитів публічного запису (з env або з маршруту `/book/[tenant]`). */
export const TenantContext = createContext<string | null>(null);

export function useTenant(): string {
  const t = useContext(TenantContext);
  if (!t) throw new Error("useTenant має викликатися всередині BookingProviders");
  return t;
}
