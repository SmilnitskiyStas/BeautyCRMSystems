# API_CONTRACT: логотип закладу (location logo) v1

Адитивна зміна, наявні поля/маршрути не змінюються.

## Дані
- `beauty_locations.logo_storage_key character(32) NULL`, CHECK `~ '^[0-9a-f]{32}$'`, UNIQUE (partial, WHERE NOT NULL).
- Файли: те саме медіасховище, що й фото товарів (ADR-027): WebP-варіанти 400 і 1600, URL `{PublicBaseUrl}/api/public/media/p/{key}-{size}.webp`.
- Orphan sweeper має вважати ключі логотипів посиланими (не видаляти).

## CRM (owner/admin: permission LocationsAccess Edit)
- `LocationDto` + `logoUrl: string | null` — абсолютний URL варіанта 400; null якщо немає або медіа вимкнено (`Media:RootPath` порожній).
- `PUT /api/beauty/locations/{id}/logo` — multipart/form-data, поле `file` (JPEG/PNG/WebP; ліміти як у фото товарів) -> 200 `LocationDto`.
  Попередній файл логотипу видаляється (best effort) після успішного коміту.
- `DELETE /api/beauty/locations/{id}/logo` -> 200 `LocationDto` (logoUrl = null). Ідемпотентно.
- Помилки (фактичні коди реалізації; узгоджено з наявними кодами закладів і фото товарів): 404 `location_not_found` (заклад), 404 `media_disabled`,
  413 `file_too_large`, 415 `unsupported_image_type`, 422 `file_required` | `image_dimensions_invalid` | `image_decode_failed`,
  429 `rate_limited`, 503 `media_busy`. (Початкова чернетка мала `not_found` та `image_too_large`; змінено, бо решта endpoint-ів закладів повертає `location_not_found`, а спільний конвеєр завантаження — `file_too_large`.)

## Публічний каталог (mobile)
- `GET /api/public/resolve/{code}`, `POST /api/public/locations/nearby`, `POST /api/public/locations/search`:
  кожен item + `logoUrl: string | null` (URL варіанта 400). Решта полів без змін.
- Mobile приймає лише `https://`; інакше трактує як null (запасний варіант — ініціали).
