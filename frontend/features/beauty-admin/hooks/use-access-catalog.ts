"use client";

import { useQuery } from "@tanstack/react-query";
import { beautyApi } from "../api";

/** Каталог прав (§30.9.2): однаковий для всіх ролей, кеш 5 хв (як `Cache-Control: private, max-age=300`). Для екрана «Команда й доступи» (TASK-855). */
export const accessCatalogKey = ["beauty", "access", "catalog"] as const;

export const useAccessCatalog = (enabled = true) =>
  useQuery({ queryKey: accessCatalogKey, queryFn: () => beautyApi.getAccessCatalog(), staleTime: 5 * 60_000, enabled });
