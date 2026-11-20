-- GRANT/REVOKE для staging. Ідемпотентний; застосовувати від postgres у БД beautycrm ПІСЛЯ кожної міграції (ADR-016 §2.8 крок 5).
-- Вміст після цього заголовка має збігатися зі змінною $grants у scripts/dev/setup-db.ps1: це перевіряє
-- worker/src/staging-grants-sync.test.ts. Змінюючи права, правте обидва місця в одному коміті.
GRANT USAGE ON SCHEMA public TO beautycrm_app;
GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO beautycrm_app;
GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_app;
REVOKE UPDATE, DELETE ON beauty_ai_settings_audit FROM beautycrm_app;
-- Журнал згод (§26.3) append-only для app-ролі (TASK-791, M3): лише SELECT/INSERT
REVOKE UPDATE, DELETE ON beauty_marketing_consent_events FROM beautycrm_app;
-- Журнал platform-дій control plane (ADR-018, TASK-816) append-only для app-ролі: лише SELECT/INSERT
REVOKE UPDATE, DELETE ON platform_audit_log FROM beautycrm_app;
-- Права доступу користувачів (ADR-023, TASK-849): user_permissions - SELECT/INSERT/UPDATE/DELETE (ALL TABLES); журнал user_access_audit append-only: лише SELECT/INSERT
-- Воркер і control plane не мають доступу до user_permissions та user_access_audit
REVOKE UPDATE, DELETE ON user_access_audit FROM beautycrm_app;
-- Склад (ADR-025, TASK-876): журнал рухів і заголовки операцій append-only для app-ролі: лише SELECT/INSERT
REVOKE UPDATE, DELETE ON beauty_stock_movements FROM beautycrm_app;
REVOKE UPDATE, DELETE ON beauty_stock_operations FROM beautycrm_app;
-- Склад, етап 2b (ADR-025 §12, контракт §32.24.2, TASK-895): журнал подій прив'язки Telegram append-only для app-ролі: лише SELECT/INSERT (+ тригер у БД)
REVOKE UPDATE, DELETE, TRUNCATE ON beauty_staff_telegram_link_events FROM beautycrm_app;

-- Роль воркера: мінімальні права (runbook §6/§11; контракти §22.3, §22.5, §23)
GRANT USAGE ON SCHEMA public TO beautycrm_worker;
GRANT SELECT ON beauty_clients, beauty_client_contact_preferences, beauty_conversations, beauty_channels,
  beauty_appointments, beauty_locations, beauty_services, beauty_reminder_settings TO beautycrm_worker;
GRANT SELECT, UPDATE ON beauty_reminders TO beautycrm_worker;
GRANT SELECT, INSERT, UPDATE ON beauty_messages TO beautycrm_worker;
GRANT SELECT, INSERT ON beauty_call_tasks, beauty_chat_keyboards TO beautycrm_worker;
-- ADR-020 (TASK-836): beauty_chat_booking_flows, beauty_chat_location_hints - лише app-роль (через ALL TABLES); воркер їх не читає
-- Маркетинг (§26.3, «Уточнення TASK-780», TASK-791): воркер НЕ пише згоди, акції, послуги акцій і журнал згод
GRANT SELECT ON tenants, beauty_marketing_consents, beauty_promotions, beauty_promotion_services TO beautycrm_worker;
GRANT SELECT, UPDATE ON beauty_campaigns, beauty_campaign_recipients TO beautycrm_worker;
GRANT SELECT, INSERT, UPDATE ON beauty_coupons TO beautycrm_worker;
-- Control plane (ADR-018, TASK-816): доставка заявок, очищення nonce; users - лише колонки для тіла заявки (без password_hash)
GRANT SELECT, UPDATE ON beauty_platform_feedback TO beautycrm_worker;
GRANT SELECT, DELETE ON control_plane_nonces TO beautycrm_worker;
GRANT SELECT (id, tenant_id, email, full_name, role) ON users TO beautycrm_worker;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_worker;

-- Мобільний запис (міграції 20261109*): відгуки про майстрів - лише app-роль (воркер не читає); явний GRANT на випадок, коли ALL TABLES виконано до міграції
GRANT SELECT, INSERT, UPDATE, DELETE ON beauty_specialist_reviews TO beautycrm_app;
-- Нові колонки (public_code, city, координати, reminder 24h, email-канал, умови скасування, профіль майстра) покриті табличними GRANT-ами вище
-- Воркер: підтвердження запису читає ім'я майстра; email-нагадування створює розмову на каналі email (INSERT beauty_conversations)
GRANT SELECT ON beauty_specialists TO beautycrm_worker;
GRANT INSERT ON beauty_conversations TO beautycrm_worker;
-- Склад, етап 2a (ADR-025 §11, контракт §32.20, TASK-883): воркер pollInventory читає залишки, партії, товари й налаштування закладів
-- і веде сповіщення персоналу (INSERT/UPDATE; без DELETE). Норми матеріалів і журнал рухів воркеру не потрібні. app-роль: нові таблиці - через ALL TABLES
GRANT SELECT ON beauty_stock_levels, beauty_stock_batches, beauty_product_location_settings TO beautycrm_worker;
GRANT SELECT (id, tenant_id, is_active, min_stock) ON beauty_products TO beautycrm_worker;
GRANT SELECT, INSERT ON beauty_staff_alerts TO beautycrm_worker;
GRANT UPDATE (status, resolved_at, updated_at) ON beauty_staff_alerts TO beautycrm_worker;
-- Склад, етап 2b (TASK-895): воркер pollInventoryTelegram. Прив'язки: колонковий SELECT (шифрований chat_id потрібен лише для відправки; без token_hash, chat_id_hash, згоди) і
-- колонковий UPDATE (відв'язка стирає дані чату, прибирання прострочених); без INSERT/DELETE. Журнал подій - лише INSERT (append-only). Черга - SELECT/INSERT/UPDATE/DELETE
-- (30-денне прибирання; рядки beauty_staff_notification_alerts видаляються каскадом). Зв'язок алертів - SELECT/INSERT. Нові колонкові SELECT: users (is_active, state - перевірка адресата),
-- beauty_products (name, unit - тексти; без purchase_price). beauty_locations, beauty_stock_batches (lot_number), beauty_staff_alerts, beauty_channels - вже є
GRANT SELECT (id, tenant_id, user_id, channel_id, status, chat_id_encrypted, token_expires_at, activated_at) ON beauty_staff_telegram_links TO beautycrm_worker;
GRANT UPDATE (status, token_hash, chat_id_encrypted, chat_id_hash, revoked_at, revoke_reason, last_delivery_at, updated_at) ON beauty_staff_telegram_links TO beautycrm_worker;
GRANT INSERT ON beauty_staff_telegram_link_events TO beautycrm_worker;
GRANT SELECT, INSERT, UPDATE, DELETE ON beauty_staff_notifications TO beautycrm_worker;
GRANT SELECT, INSERT ON beauty_staff_notification_alerts TO beautycrm_worker;
GRANT SELECT (is_active, state) ON users TO beautycrm_worker;
GRANT SELECT (name, unit) ON beauty_products TO beautycrm_worker;
-- Замовлення товарів (ADR-026, TASK-901): замовлення не видаляються; рядки замовлень append-only для app-ролі
REVOKE DELETE ON beauty_product_orders FROM beautycrm_app;
REVOKE UPDATE, DELETE ON beauty_product_order_lines FROM beautycrm_app;
-- Воркер: лише косметичне закриття прострочених неоплачених (TTL) і підрахунок зависих оплат; без рядків, платежів, клієнтів і складу
GRANT SELECT (id, tenant_id, status, reservation_expires_at, payment_started_at) ON beauty_product_orders TO beautycrm_worker;
GRANT UPDATE (status, closed_at, close_reason, updated_at) ON beauty_product_orders TO beautycrm_worker;
-- Публічний каталог закладів (ADR-022): функції належать beautycrm_catalog, EXECUTE лише для app-ролі, НЕ для PUBLIC і НЕ для воркера
REVOKE ALL ON FUNCTION public.beauty_public_resolve_location(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.beauty_public_search_locations_by_city(text, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.beauty_public_resolve_location(text) TO beautycrm_app;
GRANT EXECUTE ON FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) TO beautycrm_app;
GRANT EXECUTE ON FUNCTION public.beauty_public_search_locations_by_city(text, integer) TO beautycrm_app;
-- Retention журналу доступу (ADR-023, Q7): SECURITY DEFINER-функція (власник beautycrm_audit_purge), EXECUTE лише воркеру, не PUBLIC і не app
REVOKE ALL ON FUNCTION public.purge_user_access_audit(timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.purge_user_access_audit(timestamptz) TO beautycrm_worker;
