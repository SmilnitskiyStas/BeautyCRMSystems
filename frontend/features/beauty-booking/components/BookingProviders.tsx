"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { TenantContext } from "./tenant-context";

export function BookingProviders({ tenant, children }: { tenant: string; children: ReactNode }) {
  const [client] = useState(
    () => new QueryClient({ defaultOptions: { queries: { staleTime: 30_000, refetchOnWindowFocus: false } } }),
  );
  return (
    <TenantContext.Provider value={tenant}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </TenantContext.Provider>
  );
}
