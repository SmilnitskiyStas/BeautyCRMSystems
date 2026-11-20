"use client";

import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import type { ProductOrder, ProductOrderQuery } from "../inventory/orders";
import { beautyKeys } from "./use-beauty-admin";

const K = beautyKeys.inventory;
/** Опитування бейджа - як сповіщення складу: 30 с, не у фоновій вкладці. */
export const ORDERS_BADGE_POLL_MS = 30_000;
const retry = (count: number, e: unknown) => !(e instanceof BeautyApiError && e.status >= 400 && e.status < 500) && count < 2;

/** Кількість оплачених замовлень до видачі (`counts.paid`). `enabled` = модуль `beauty_inventory` + `inventory.view`. */
export const useProductOrderCounts = (enabled: boolean) =>
  useQuery({
    queryKey: K.orderCounts,
    queryFn: () => beautyApi.getProductOrderCounts(),
    enabled,
    retry,
    refetchInterval: ORDERS_BADGE_POLL_MS,
    refetchIntervalInBackground: false,
  });

export function useProductOrders(q: ProductOrderQuery, enabled = true) {
  const query = useInfiniteQuery({
    queryKey: K.orders(q),
    queryFn: ({ pageParam }) => beautyApi.getProductOrders({ ...q, ...(pageParam ? { cursor: pageParam } : {}) }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => (last.hasMore ? last.nextCursor : undefined),
    enabled,
    retry,
  });
  return { ...query, items: query.data?.pages.flatMap((p) => p.items) ?? [] };
}

export const useProductOrder = (id: string | null) =>
  useQuery({ queryKey: K.order(id ?? ""), queryFn: () => beautyApi.getProductOrder(id!), enabled: id !== null, retry });

/** Після дії оновлюємо список і лічильник, а також залишки/журнал (видача й повернення рухають склад). */
function useAfterOrderAction() {
  const qc = useQueryClient();
  return (order: ProductOrder) => {
    qc.setQueryData(K.order(order.id), order);
    void qc.invalidateQueries({ queryKey: K.ordersAll });
    void qc.invalidateQueries({ queryKey: K.all });
  };
}

export function useFulfillProductOrder() {
  const done = useAfterOrderAction();
  return useMutation({ mutationFn: (id: string) => beautyApi.fulfillProductOrder(id), onSuccess: done });
}
export function useCancelProductOrder() {
  const done = useAfterOrderAction();
  return useMutation({ mutationFn: (v: { id: string; reason?: string }) => beautyApi.cancelProductOrder(v.id, v.reason), onSuccess: (r) => done(r.order) });
}
export function useReconcileProductOrderPayment() {
  const done = useAfterOrderAction();
  return useMutation({ mutationFn: (id: string) => beautyApi.reconcileProductOrderPayment(id), onSuccess: done });
}
