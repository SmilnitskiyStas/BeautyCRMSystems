---
name: beauty-schema
description: Схема БД Beauty CRM — таблиці, RLS, запобігання перетину записів. Використовуй при міграціях і моделі даних beauty_*.
---

- Таблиці: `beauty_locations`, `beauty_specialists`, `beauty_specialist_locations` (графік по закладах), `beauty_services`, `beauty_service_prices` (ціна мережі + override по закладу), `beauty_appointments`, `beauty_clients`, `beauty_client_notes`, `beauty_promotions` (+ `_locations`, `_services`), `beauty_channels`, `beauty_conversations`, `beauty_messages`, `beauty_ai_actions`, `beauty_reminders`, `beauty_payments`.
- Кожна таблиця має `tenant_id` і RLS-політику з `NULLIF(current_setting('app.tenant_id', true), '')`.
- `beauty_appointments`: `starts_at`, `duration_minutes` (з послуги на момент запису), `status` (`pending`, `confirmed`, `completed`, `cancelled`, `no_show`), `source` (`admin`, `online`, `telegram`, `instagram`), `price_original`, `price_final`, `promotion_id`.
- Перетин записів майстра: exclusion constraint (`tstzrange` + `btree_gist`) або перевірка в транзакції.
- Секрети каналів зберігати зашифрованими; в API маскувати (останні 4 символи).
- Міграції лише адитивні, без деструктивних змін. Обов'язковий RLS-тест ізоляції двох tenant.
