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
-- Глобальна ідентичність (ADR-024, TASK-862; контракт §31.16, чекліст F-8). ПІСЛЯ масових GRANT вище: таблиці deny-all для app, воркера й PUBLIC
-- (захист двошаровий: права + FORCE RLS без політик для цих ролей). Повторний прогін скрипта прав НЕ повертає доступ.
REVOKE ALL ON identities, auth_login_tickets, auth_password_resets, auth_login_attempts FROM PUBLIC, beautycrm_app, beautycrm_worker;
-- Ніхто з app/воркера/PUBLIC не може створювати об'єкти в public (перехоплення функції попри search_path)
REVOKE CREATE ON SCHEMA public FROM PUBLIC, beautycrm_app, beautycrm_worker;
-- Матриця EXECUTE для auth_* (явно для кожної функції, без масових GRANT ON ALL FUNCTIONS): спершу REVOKE, потім лише дозволені ролі
-- app (beautycrm_app): вхід, квитки, видача/споживання скидання, створення ідентичностей
REVOKE ALL ON FUNCTION public.auth_identity_find(text) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_identity_find(text) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_login_attempt_is_locked(bytea, timestamptz) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_login_attempt_is_locked(bytea, timestamptz) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_login_attempt_failed(bytea, uuid, integer, integer, integer, timestamptz) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_login_attempt_failed(bytea, uuid, integer, integer, integer, timestamptz) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_login_attempt_reset(bytea) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_login_attempt_reset(bytea) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_identity_login_succeeded(uuid, timestamptz) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_identity_login_succeeded(uuid, timestamptz) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_identity_memberships(uuid) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_identity_memberships(uuid) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_identity_is_active(uuid) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_identity_is_active(uuid) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_identity_email_in_use(text) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_identity_email_in_use(text) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_identity_create(text, text, uuid, text, integer) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_identity_create(text, text, uuid, text, integer) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_identity_create_pending(text, uuid, text) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_identity_create_pending(text, uuid, text) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_identity_activate_pending(uuid, text) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_identity_activate_pending(uuid, text) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_login_ticket_issue(uuid, bytea, timestamptz) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_login_ticket_issue(uuid, bytea, timestamptz) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_login_ticket_consume(bytea, uuid, timestamptz) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_login_ticket_consume(bytea, uuid, timestamptz) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_password_reset_issue(text, bytea, timestamptz) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_password_reset_issue(text, bytea, timestamptz) TO beautycrm_app;
REVOKE ALL ON FUNCTION public.auth_password_reset_consume(bytea, text, timestamptz) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_password_reset_consume(bytea, text, timestamptz) TO beautycrm_app;
-- воркер: лише доставка листів скидання й очищення
REVOKE ALL ON FUNCTION public.auth_password_reset_claim_delivery(timestamptz, integer, integer) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_password_reset_claim_delivery(timestamptz, integer, integer) TO beautycrm_worker;
REVOKE ALL ON FUNCTION public.auth_password_reset_attach_token(uuid, bytea, timestamptz) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_password_reset_attach_token(uuid, bytea, timestamptz) TO beautycrm_worker;
REVOKE ALL ON FUNCTION public.auth_password_reset_mark_delivery(uuid, text, text, text, timestamptz) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_password_reset_mark_delivery(uuid, text, text, text, timestamptz) TO beautycrm_worker;
REVOKE ALL ON FUNCTION public.auth_identity_purge_expired(timestamptz) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_identity_purge_expired(timestamptz) TO beautycrm_worker;
-- звільнення пошти (F-4, human gate за runbook): лише ops/мігратор (beautycrm_owner); у app і воркера EXECUTE немає
REVOKE ALL ON FUNCTION public.auth_identity_release(uuid, text) FROM PUBLIC, beautycrm_app, beautycrm_worker;
GRANT EXECUTE ON FUNCTION public.auth_identity_release(uuid, text) TO beautycrm_owner;
-- тригерна функція узгодженості пошти: EXECUTE не потрібен нікому
REVOKE ALL ON FUNCTION public.users_identity_email_guard() FROM PUBLIC, beautycrm_app, beautycrm_worker;
