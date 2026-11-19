"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { beautyApi } from "../api";
import type { MaterialSetRequest } from "../inventory/types";
import { beautyKeys } from "./use-beauty-admin";

const K = beautyKeys.inventory;
const retry = (count: number, e: unknown) => !(e instanceof BeautyApiError && e.status >= 400 && e.status < 500) && count < 2;

/** Усі послуги з нормами (для попередження «товар входить у норми» у формі товару). */
export const useServiceMaterialsList = (enabled = true) =>
  useQuery({ queryKey: K.materialsList, queryFn: () => beautyApi.getServiceMaterialsList(true), enabled, retry: false, staleTime: 60_000 });

/** Норми матеріалів однієї послуги (§32.16): мережа + override закладів. `enabled = false` - без запитів (немає права/модуля). */
export const useServiceMaterials = (serviceId: string, enabled = true) =>
  useQuery({ queryKey: K.materials(serviceId), queryFn: () => beautyApi.getServiceMaterials(serviceId), enabled, retry });

export function useSetServiceMaterials(serviceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (r: MaterialSetRequest) => beautyApi.setServiceMaterials(serviceId, r),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: K.materialsList });
      return qc.invalidateQueries({ queryKey: K.materials(serviceId) });
    },
  });
}

/** `locationId = null` - мережевий набір. */
export function useDeleteServiceMaterials(serviceId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (locationId: string | null) => beautyApi.deleteServiceMaterials(serviceId, locationId),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: K.materialsList });
      return qc.invalidateQueries({ queryKey: K.materials(serviceId) });
    },
  });
}
