"use client";

import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { isTenantSuspended } from "../errors";
import { TenantContext } from "./tenant-context";
import { TenantSuspended } from "./TenantSuspended";

export function BookingProviders({ tenant, children }: { tenant: string; children: ReactNode }) {
  const [suspended, setSuspended] = useState(false);
  const [client] = useState(() => {
    const onError = (e: unknown) => {
      if (isTenantSuspended(e)) setSuspended(true);
    };
    return new QueryClient({
      queryCache: new QueryCache({ onError }),
      mutationCache: new MutationCache({ onError }),
      defaultOptions: {
        queries: {
          staleTime: 30_000,
          refetchOnWindowFocus: false,
          retry: (count, e) => !isTenantSuspended(e) && count < 3,
        },
      },
    });
  });
  if (suspended) return <TenantSuspended />;
  return (
    <TenantContext.Provider value={tenant}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </TenantContext.Provider>
  );
}
