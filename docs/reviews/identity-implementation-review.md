# Review: реалізація ADR-024 «Глобальна ідентичність і вибір бізнесу» (TASK-868)

**Date:** 2026-11-18 · **Reviewer:** security-reviewer · **Verdict:** approved-with-notes (critical/high немає; 1 medium, решта low/info)

Перевірено код (не логи) проти ADR-024 (§2, errata §9), контракту §31 (§31.15, §31.16) і threat-model (identity-threat-model.md: F-1…F-13, Q-C1…Q-C5). Модель «ідентичність ≠ членство» реалізована за контрактом: deny-all (FORCE RLS + REVOKE після масових GRANT), SECURITY DEFINER-функції з фіксованим search_path і явним RETURNS TABLE, consume одним UPDATE…RETURNING, матриця EXECUTE за §31.16, DbRoleGuard на старті, блокування в auth_login_attempts до пошуку пошти, рівно одна bcrypt-перевірка, Q-C1, switch з refresh і перечитуванням членства, sessions_valid_after у middleware і refresh, forgot з 202 до БД, токен скидання лише в воркері (sha256), квиток входу не в JSON. IDOR між бізнесами не знайдено.

## Знахідки

- **R-1 [medium]** `frontend/features/beauty-auth/components/business-switcher.tsx:37-39`: кеш попереднього tenant очищається (`qc.clear()`) ПІСЛЯ `switchBusiness` (який вже зберіг токен B і зробив `reloadMe()`), а не до першого запиту з новим токеном (F-11, §31.11). Тесту §31.15 п.29 немає. Виправлення: колбек `beforeActivate` (cancelQueries, clear, скидання tenant-сховищ) до `store(s)`/`reloadMe()`, або повне перезавантаження; unit-тест.
- **R-2 [low]** `DbRoleGuard.CheckIdentityIsolationAsync` не бачить колонкових грантів, членства ролей ідентичності в інших ролях і політик, адресованих не-ідентичним ролям. Додати `has_any_column_privilege`, перевірку `pg_auth_members`, перевірку `pg_policy.polroles`.
- **R-3 [low]** Роль app може змінювати `users.identity_id` (GRANT ON ALL TABLES); тригер `users_identity_email_guard` перевіряє лише пошту. Додати в тригер заборону зміни `identity_id` при UPDATE (нова міграція + тест).
- **R-4 [low]** `Auth:LoginAttemptWindowSeconds` не обмежено зверху, а purge розрахований на ≤ 86400. Вимагати `60 ≤ window ≤ 86400` і `MaxFailedAttempts ≥ 1` у `ValidateIdentityOptions`.
- **R-5 [low]** `/reset-password` без HTTP-заголовків `Referrer-Policy: no-referrer` і `Cache-Control: no-store` у `frontend/next.config.ts` (для `/invite` є).
- **R-6 [low]** `AuthService.SelectAsync`: `tenantId == Guid.Empty` повертає 401 без погашення квитка (Q-C5 вимагає погашення будь-яким запитом із чинним квитком).
- **R-7 [low]** Довіра до XFF від шлюзу на спільному сервері: процес на хості може звернутися до loopback-порту API/web від адреси шлюзу й підробити XFF. Обмежити доступ (iptables `--uid-owner` / unix-сокет) або задокументувати прийнятий ризик у runbook.
- **R-8 [info]** `SwitchAsync` не порівнює `record.CreatedAt` з `users.sessions_valid_after` (defense in depth — додати).
- **R-9 [info]** `auth_identity_create` приймає `p_via` від застосунку (в межах прийнятої межі довіри §2.3).
- **R-10 [info]** `password/reset` хешує пароль до перевірки токена; обмежено IP-лімітом, оракула немає.
- **R-11 [info]** BFF `switch` видаляє `beauty_rt` на будь-який 401 (за §31.11).
- **R-12 [info]** `isSameOrigin` довіряє `X-Forwarded-Host`; nginx має явно задавати `proxy_set_header X-Forwarded-Host $host`.

## Не перевірено
Покриття тестами §31.15 п.1–31 загалом; результат staging smoke F-1; порядок middleware в `Program.cs`; `AuthResultMapping.cs`, `EfPasswordResetStore.cs`, `ControlPlaneService.cs`; RLS на `platform_audit_log` для вставок від `beautycrm_identity`; повна ідентичність `$grants` у `setup-db.ps1` і `grants.sql` (є тест staging-grants-sync).

## Порядок
До злиття `integrate/*`: виправити R-1 із тестом п.29 і закрити «Не перевірено» щодо тестів §31.15 і smoke F-1. Окремо: R-2, R-3 (нова міграція), R-4, R-6, R-8 (backend); R-5 (frontend); R-7 (devops/людина). Повторне рев'ю не потрібне, якщо виправлення покриті тестами.
