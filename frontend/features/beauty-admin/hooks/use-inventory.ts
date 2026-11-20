"use client";

import { useInfiniteQuery, useMutation, useQuery, useQueryClient, type InfiniteData } from "@tanstack/react-query";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import type {
  BatchQuery,
  CategoryRequest,
  MovementQuery,
  OperationQuery,
  Page,
  ProductQuery,
  ProductRequest,
  ReceiptRequest,
  StockQuery,
  StockSummaryQuery,
  StocktakeLineInput,
  StocktakeQuery,
  SupplierQuery,
  SupplierRequest,
  TransferRequest,
  WriteOffRequest,
} from "../inventory/types";
import { beautyKeys } from "./use-beauty-admin";

const K = beautyKeys.inventory;

/** 4xx не повторюємо (права, модуль, валідація); мережеві збої - до двох повторів. */
const retry = (count: number, e: unknown) => !(e instanceof BeautyApiError && e.status >= 400 && e.status < 500) && count < 2;

export const useInventoryScope = (enabled = true) =>
  useQuery({ queryKey: K.scope, queryFn: () => beautyApi.getInventoryScope(), enabled, retry, staleTime: 60_000 });

/** Сторінки за курсором: «Показати ще» додає наступну сторінку. */
function usePaged<T, Q extends { cursor?: string }>(key: readonly unknown[], fetchPage: (q: Q) => Promise<Page<T>>, q: Q, enabled: boolean) {
  const query = useInfiniteQuery<Page<T>, Error, InfiniteData<Page<T>, string | undefined>, readonly unknown[], string | undefined>({
    queryKey: key,
    queryFn: ({ pageParam }) => fetchPage({ ...q, ...(pageParam ? { cursor: pageParam } : {}) }),
    initialPageParam: undefined,
    getNextPageParam: (last) => (last.hasMore ? last.nextCursor : undefined),
    enabled,
    retry,
  });
  return { ...query, items: query.data?.pages.flatMap((p) => p.items) ?? [] };
}

export const useInventoryProducts = (q: ProductQuery = {}, enabled = true) => usePaged(K.products(q), (p: ProductQuery) => beautyApi.getInventoryProducts(p), q, enabled);
export const useInventoryProduct = (id: string | null) =>
  useQuery({ queryKey: K.product(id ?? ""), queryFn: () => beautyApi.getInventoryProduct(id!), enabled: id !== null, retry });
/** Плоский список вузлів дерева (разом з архівними: товар може лежати в архівній, а фільтр рахує піддерево). */
export const useInventoryCategories = (enabled = true) =>
  useQuery({ queryKey: K.categories, queryFn: () => beautyApi.getInventoryCategories(true), enabled, retry });
export const useSuppliers = (q: SupplierQuery = {}, enabled = true) => usePaged(K.suppliers(q), (p: SupplierQuery) => beautyApi.getSuppliers(p), q, enabled);

export function useStock(q: StockQuery | null) {
  return useInfiniteQuery({
    queryKey: K.stock(q ?? {}),
    queryFn: ({ pageParam }) => beautyApi.getStock({ ...q!, ...(pageParam ? { cursor: pageParam } : {}) }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => (last.hasMore ? last.nextCursor : undefined),
    enabled: q !== null,
    retry,
    select: (d) => ({ location: d.pages[0]!.location, items: d.pages.flatMap((p) => p.items) }),
  });
}
export function useStockSummary(q: StockSummaryQuery = {}, enabled = true) {
  return useInfiniteQuery({
    queryKey: K.summary(q),
    queryFn: ({ pageParam }) => beautyApi.getStockSummary({ ...q, ...(pageParam ? { cursor: pageParam } : {}) }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => (last.hasMore ? last.nextCursor : undefined),
    enabled,
    retry,
    select: (d) => ({ locations: d.pages[0]!.locations, items: d.pages.flatMap((p) => p.items) }),
  });
}

export const useBatches = (q: BatchQuery = {}, enabled = true) => usePaged(K.batches(q), (p: BatchQuery) => beautyApi.getBatches(p), q, enabled);
export const useMovements = (q: MovementQuery = {}, enabled = true) => usePaged(K.movements(q), (p: MovementQuery) => beautyApi.getMovements(p), q, enabled);
export const useOperations = (q: OperationQuery = {}, enabled = true) => usePaged(K.operations(q), (p: OperationQuery) => beautyApi.getOperations(p), q, enabled);
export const useStocktakes = (q: StocktakeQuery = {}, enabled = true) => usePaged(K.stocktakes(q), (p: StocktakeQuery) => beautyApi.getStocktakes(p), q, enabled);
export const useStocktake = (id: string) => useQuery({ queryKey: K.stocktake(id), queryFn: () => beautyApi.getStocktake(id), retry });

/** Усе, що залежить від залишків і журналу, оновлюється після будь-якої операції. */
function useInvalidateInventory() {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: K.all });
}

export function useSaveProduct(id: string | null) {
  const done = useInvalidateInventory();
  return useMutation({
    mutationFn: (r: ProductRequest) => (id ? beautyApi.updateInventoryProduct(id, r) : beautyApi.createInventoryProduct(r)),
    onSuccess: done,
  });
}
/** Мутації дерева категорій; інвалідуємо весь склад (змінюються і `categoryPath` товарів, і лічильники). */
export function useCategoryMutations() {
  const done = useInvalidateInventory();
  const opts = { onSuccess: done };
  return {
    create: useMutation({ mutationFn: (r: CategoryRequest) => beautyApi.createInventoryCategory(r), ...opts }),
    rename: useMutation({ mutationFn: (v: { id: string; name: string }) => beautyApi.updateInventoryCategory(v.id, { name: v.name }), ...opts }),
    move: useMutation({ mutationFn: (v: { id: string; parentId: string | null }) => beautyApi.moveInventoryCategory(v.id, { parentId: v.parentId }), ...opts }),
    archive: useMutation({ mutationFn: (id: string) => beautyApi.archiveInventoryCategory(id), ...opts }),
    unarchive: useMutation({ mutationFn: (id: string) => beautyApi.unarchiveInventoryCategory(id), ...opts }),
    remove: useMutation({ mutationFn: (id: string) => beautyApi.deleteInventoryCategory(id), ...opts }),
  };
}
export function useSaveSupplier(id: string | null) {
  const done = useInvalidateInventory();
  return useMutation({
    mutationFn: (r: SupplierRequest) => (id ? beautyApi.updateSupplier(id, r) : beautyApi.createSupplier(r)),
    onSuccess: done,
  });
}
export function useSetLocationSetting(productId: string) {
  const done = useInvalidateInventory();
  return useMutation({
    mutationFn: (v: { locationId: string; minStock: number | null; isEnabled: boolean }) =>
      beautyApi.setProductLocationSetting(productId, v.locationId, { minStock: v.minStock, isEnabled: v.isEnabled }),
    onSuccess: done,
  });
}
export function useResetLocationSetting(productId: string) {
  const done = useInvalidateInventory();
  return useMutation({ mutationFn: (locationId: string) => beautyApi.deleteProductLocationSetting(productId, locationId), onSuccess: done });
}

/** Операції шлють `Idempotency-Key`, який форма тримає на час відкриття (повтор того самого запиту = той самий ключ). */
export function useCreateReceipt() {
  const done = useInvalidateInventory();
  return useMutation({ mutationFn: (v: { request: ReceiptRequest; key: string }) => beautyApi.createReceipt(v.request, v.key), onSuccess: done });
}
export function useCreateWriteOff() {
  const done = useInvalidateInventory();
  return useMutation({ mutationFn: (v: { request: WriteOffRequest; key: string }) => beautyApi.createWriteOff(v.request, v.key), onSuccess: done });
}
export function useCreateTransfer() {
  const done = useInvalidateInventory();
  return useMutation({ mutationFn: (v: { request: TransferRequest; key: string }) => beautyApi.createTransfer(v.request, v.key), onSuccess: done });
}

export function useCreateStocktake() {
  const done = useInvalidateInventory();
  return useMutation({ mutationFn: (v: { locationId: string; note?: string; productIds?: string[] }) => beautyApi.createStocktake(v), onSuccess: done });
}
export function useSaveStocktakeLines(id: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { expectedVersion: number; lines: StocktakeLineInput[] }) => beautyApi.saveStocktakeLines(id, v.expectedVersion, v.lines),
    onSuccess: (saved) => qc.setQueryData(K.stocktake(id), saved),
  });
}
export function usePostStocktake(id: string) {
  const done = useInvalidateInventory();
  return useMutation({ mutationFn: (expectedVersion: number) => beautyApi.postStocktake(id, expectedVersion), onSuccess: done });
}
export function useCancelStocktake(id: string) {
  const done = useInvalidateInventory();
  return useMutation({ mutationFn: (expectedVersion: number) => beautyApi.cancelStocktake(id, expectedVersion), onSuccess: done });
}
