import { BeautyApiError } from "@/features/beauty-auth/errors";
import { humanizeInventoryError } from "./logic";

/** Ліміти ADR-027 / §33.22.2 (дефолти сервера; сервер лишається авторитетним). */
export const IMAGE_MAX_BYTES = 10 * 1024 * 1024;
export const IMAGE_MAX_COUNT = 8;
export const IMAGE_ACCEPT = "image/jpeg,image/png,image/webp";
const IMAGE_TYPES = ["image/jpeg", "image/png", "image/webp"];

/** Клієнтська перевірка перед відправкою; повертає текст помилки або `null`. */
export function validateImageFile(file: { type: string; size: number }, currentCount: number): string | null {
  if (currentCount >= IMAGE_MAX_COUNT) return `Не більше ${IMAGE_MAX_COUNT} фото на товар. Видаліть зайве.`;
  if (!IMAGE_TYPES.includes(file.type)) return "Підтримуються лише зображення JPEG, PNG або WebP.";
  if (file.size > IMAGE_MAX_BYTES) return "Файл завеликий: максимум 10 МБ.";
  if (file.size === 0) return "Файл порожній.";
  return null;
}

export const IMAGE_MAX_SIDE = 2560;

/** Розмір після зменшення так, щоб довша сторона ≤ `max`; `null` = зменшення не потрібне. */
export function fitSize(w: number, h: number, max = IMAGE_MAX_SIDE): { width: number; height: number } | null {
  const long = Math.max(w, h);
  if (long <= max) return null;
  const k = max / long;
  return { width: Math.max(1, Math.round(w * k)), height: Math.max(1, Math.round(h * k)) };
}

/**
 * Зменшує зображення в браузері до довшої сторони ≤2560 (ADR-027 §2.5) зі збереженням типу.
 * Будь-який збій (немає canvas/декодера, результат не менший) - повертає оригінал; сервер лишається авторитетним.
 */
export async function downscaleImage(file: File): Promise<File> {
  try {
    const bitmap = await createImageBitmap(file);
    const size = fitSize(bitmap.width, bitmap.height);
    if (!size) {
      bitmap.close();
      return file;
    }
    const canvas = document.createElement("canvas");
    canvas.width = size.width;
    canvas.height = size.height;
    const ctx = canvas.getContext("2d");
    if (!ctx) {
      bitmap.close();
      return file;
    }
    ctx.drawImage(bitmap, 0, 0, size.width, size.height);
    bitmap.close();
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, file.type, 0.9));
    if (!blob || blob.type !== file.type || blob.size === 0 || blob.size >= file.size) return file;
    return new File([blob], file.name, { type: file.type, lastModified: file.lastModified });
  } catch {
    return file;
  }
}

/** Нова послідовність id після переміщення елемента `index` на `dir` (-1 вгору, +1 вниз); без змін поза межами. */
export function moveId(ids: readonly string[], index: number, dir: -1 | 1): string[] {
  const to = index + dir;
  if (index < 0 || index >= ids.length || to < 0 || to >= ids.length) return [...ids];
  const next = [...ids];
  [next[index], next[to]] = [next[to]!, next[index]!];
  return next;
}

const IMAGE_BY_CODE: Record<string, string> = {
  product_images_disabled: "Фото товарів на сервері не підключено. Зверніться до адміністратора.",
  product_image_not_found: "Фото вже видалено. Список оновлено.",
  product_not_found: "Товар не знайдено.",
  file_required: "Оберіть файл зображення.",
  file_too_large: "Файл завеликий: максимум 10 МБ.",
  unsupported_image_type: "Підтримуються лише зображення JPEG, PNG або WebP.",
  too_many_images: `Досягнуто ліміт фото на товар (до ${IMAGE_MAX_COUNT}). Видаліть зайве.`,
  image_quota_exceeded: "Вичерпано загальну квоту фото вашого бізнесу.",
  image_dimensions_invalid: "Розміри зображення не підходять: мінімум 200 пікселів по меншій стороні, максимум 10 000 по більшій.",
  image_decode_failed: "Не вдалося прочитати зображення. Файл пошкоджений або не є фото.",
  invalid_image_order: "Порядок фото змінився в іншому місці. Список оновлено, повторіть дію.",
  media_busy: "Сервер зараз обробляє інші фото. Спробуйте за кілька секунд.",
  rate_limited: "Забагато запитів. Зачекайте хвилину і спробуйте ще раз.",
  forbidden_role: "Фото змінює лише керівник з правом «Усі заклади й каталог товарів».",
};
/** Відповіді без нашого коду (напр. HTML від nginx на 413) розпізнаємо за статусом. */
const IMAGE_BY_STATUS: Record<number, string> = {
  413: IMAGE_BY_CODE.file_too_large!,
  415: IMAGE_BY_CODE.unsupported_image_type!,
  503: IMAGE_BY_CODE.media_busy!,
};

const LOGO_BY_CODE: Record<string, string> = {
  media_disabled: "Завантаження зображень на сервері не підключено. Зверніться до адміністратора.",
  location_not_found: "Заклад не знайдено.",
  forbidden_role: "Логотип змінює лише керівник з правом редагування закладів.",
};

/** Помилки логотипу закладу: коди контракту location-logo, решта - як у фото товарів. */
export function humanizeLogoError(e: unknown): string {
  if (e instanceof BeautyApiError && LOGO_BY_CODE[e.code]) return LOGO_BY_CODE[e.code]!;
  return humanizeImageError(e);
}

export function humanizeImageError(e: unknown): string {
  if (e instanceof BeautyApiError) {
    const text = IMAGE_BY_CODE[e.code] ?? IMAGE_BY_STATUS[e.status];
    if (text) return text;
  }
  return humanizeInventoryError(e, "Не вдалося виконати дію з фото. Спробуйте ще раз.");
}
