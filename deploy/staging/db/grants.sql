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

-- Роль воркера: мінімальні права (runbook §6/§11; контракти §22.3, §22.5, §23)
GRANT USAGE ON SCHEMA public TO beautycrm_worker;
GRANT SELECT ON beauty_clients, beauty_client_contact_preferences, beauty_conversations, beauty_channels,
  beauty_appointments, beauty_locations, beauty_services, beauty_reminder_settings TO beautycrm_worker;
GRANT SELECT, UPDATE ON beauty_reminders TO beautycrm_worker;
GRANT SELECT, INSERT, UPDATE ON beauty_messages TO beautycrm_worker;
GRANT SELECT, INSERT ON beauty_call_tasks, beauty_chat_keyboards TO beautycrm_worker;
-- Маркетинг (§26.3, «Уточнення TASK-780», TASK-791): воркер НЕ пише згоди, акції, послуги акцій і журнал згод
GRANT SELECT ON tenants, beauty_marketing_consents, beauty_promotions, beauty_promotion_services TO beautycrm_worker;
GRANT SELECT, UPDATE ON beauty_campaigns, beauty_campaign_recipients TO beautycrm_worker;
GRANT SELECT, INSERT, UPDATE ON beauty_coupons TO beautycrm_worker;
-- Control plane (ADR-018, TASK-816): доставка заявок, очищення nonce; users - лише колонки для тіла заявки (без password_hash)
GRANT SELECT, UPDATE ON beauty_platform_feedback TO beautycrm_worker;
GRANT SELECT, DELETE ON control_plane_nonces TO beautycrm_worker;
GRANT SELECT (id, tenant_id, email, full_name, role) ON users TO beautycrm_worker;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_worker;
