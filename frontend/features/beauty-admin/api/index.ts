import { USE_MOCK } from "@/features/beauty-auth/types";
import type { BeautyAdminApi } from "./client";
import { httpBeautyApi } from "./http-client";
import { mockBeautyApi } from "./mock-client";

export type { BeautyAdminApi } from "./client";

/**
 * Реальний HTTP-клієнт (`NEXT_PUBLIC_API_URL`) за замовчуванням;
 * `NEXT_PUBLIC_USE_MOCK=1` вмикає демо-режим без backend.
 */
export const beautyApi: BeautyAdminApi = USE_MOCK ? mockBeautyApi : httpBeautyApi;
