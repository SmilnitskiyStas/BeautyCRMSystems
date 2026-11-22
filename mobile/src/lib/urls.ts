import { z } from "zod";

/** Абсолютний https-URL (зображення з DTO). Не-https/невалідний відхиляється. */
export const httpsUrl = z.string().url().refine((u) => u.startsWith("https://"));

/** Необов'язковий https-URL: відсутній/null/невалідний -> null, парсинг батьківського об'єкта не ламається. */
export const optionalHttpsUrl = httpsUrl.nullable().optional().catch(null);
