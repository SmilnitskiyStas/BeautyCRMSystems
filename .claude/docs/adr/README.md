# Індекс ADR

**Owner:** documentation-writer (оновлює за тригерами `workflow`: нове або змінене рішення)
**Updated:** 2026-11-21

Статус кожного рішення — у заголовку його файлу (`**Status:**`). Цей індекс статуси не дублює, щоб не розходитися з файлами. Новий ADR — за шаблоном `.claude/templates/adr-template.md` і навичкою `create-adr`.

| ADR | Файл | Тема (за назвою файлу) |
|---|---|---|
| ADR-001 | `ADR-001-modular-monolith-and-stack.md` | модульний моноліт і стек |
| ADR-002 | `ADR-002-row-level-security-and-tenant-context.md` | RLS і контекст tenant |
| ADR-003 | `ADR-003-transactional-outbox-and-worker.md` | transactional outbox і worker |
| ADR-004 | `ADR-004-channels-and-mock-adapters.md` | канали і mock-адаптери |
| ADR-005 | `ADR-005-ai-modes-and-prompt-injection.md` | режими AI і prompt injection |
| ADR-006 | `ADR-006-authentication-without-self-registration.md` | автентифікація без самореєстрації |
| ADR-007 | `ADR-007-configurable-cancellation-and-refund-policy.md` | скасування і політика повернень |
| ADR-008 | `ADR-008-staff-management-absences-privacy.md` | персонал, відсутності, приватність |
| ADR-009 | `ADR-009-cancellation-history-locations.md` | історія скасувань, заклади |
| ADR-010 | `ADR-010-ai-settings-keys-models-prompt.md` | налаштування AI, ключі, моделі, промпт |
| ADR-011 | `ADR-011-client-chat-assistant-flow.md` | чат-асистент для клієнта |
| ADR-012 | `ADR-012-client-reminders-contact-preferences-and-inbox.md` | нагадування клієнту, контакти, inbox |
| ADR-013 | `ADR-013-telegram-inline-buttons-and-commands.md` | Telegram: inline-кнопки і команди |
| ADR-014 | `ADR-014-service-cost-net-income-and-phone-capture.md` | собівартість послуг, чистий дохід, телефон |
| ADR-015 | `ADR-015-marketing-consent-campaigns-coupons.md` | маркетингова згода, кампанії, купони |
| ADR-016 | `ADR-016-staging-hosting-and-ci-cd.md` | staging-хостинг і CI/CD |
| ADR-017 | `ADR-017-specialist-service-price-and-duration.md` | ціна і тривалість послуги майстра |
| ADR-018 | `ADR-018-shelfguard-control-plane-integration.md` | інтеграція з control plane |
| ADR-020 | `ADR-020-deterministic-telegram-booking-flow.md` | детермінований потік запису в Telegram |
| ADR-021 | `ADR-021-mobile-booking-client.md` | мобільний клієнт запису |
| ADR-022 | `ADR-022-public-location-code-catalog-and-mobile-booking-protection.md` | публічний каталог закладів, захист мобільного запису |
| ADR-023 | `ADR-023-per-user-access-permissions.md` | права доступу за користувачем |
| ADR-025 | `ADR-025-inventory-module.md` | модуль складу |
| ADR-026 | `ADR-026-product-orders-mobile.md` | замовлення товарів з мобільного застосунку |
| ADR-027 | `ADR-027-product-images-and-file-storage.md` | зображення товарів і зберігання файлів (accepted, реалізовано в гілці; §12 — відхилення) |

Номерів **ADR-019** і **ADR-024** у каталозі немає. Нових файлів під ці номери не створено; якщо колись з'являться, додати рядки сюди.

## Правила для індексу

- Одна таблиця, один рядок на ADR. Статус не дублювати.
- Коли ADR уточнено реалізацією, фактичні відхилення записуються в самому ADR (окремий розділ, як ADR-027 §12), а не в цьому індексі.
- Зміна семантики (наприклад, інший grace очищувача) — новий ADR або явне уточнення в ADR з посиланням з контракту (`.claude/docs/beauty-contracts.md`).
