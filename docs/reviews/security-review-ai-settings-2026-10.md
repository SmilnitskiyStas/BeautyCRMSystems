# TASK-719: рев'ю безпеки «Налаштування ШІ per tenant» (2026-10-24)

Рев'юер: security-reviewer (лише читання, тести не запускав). Скорочений запис знахідок; повний текст був у відповіді агента.

**Вердикт:** critical 0, high 0, medium 4, low 9, info 6. Staging (feature-гілка, синтетичні дані, тестовий ключ) можна за умов S1–S5. У `main` і прод не можна: відкритий юридичний gate ADR-010 §7.3 (R1), M1–M3 закрити до підключення точки входу асистента.

Контекст: `BeautyAssistant` на момент рев'ю не мав точки входу, тож знахідки про prompt injection і передачу ПД були латентними.

## Умови для staging
- S1. Майстер-ключ (`Ai__Secrets__Keys__{kid}`, `Ai__Secrets__ActiveKeyId`) лише в секретах середовища, не в репозиторії, образі, `appsettings*.json`; бекап окремо від БД.
- S2. Після міграції: `REVOKE UPDATE, DELETE ON beauty_ai_settings_audit FROM <runtime-role>`.
- S3. `ASPNETCORE_ENVIRONMENT` не Development (інакше Swagger, `X-Tenant-Id`, mock-провайдер); `Ai:UseMockProvider` вимкнений.
- S4. Один інстанс API (ліміти частоти in-memory, див. M2).
- S5. Платформний ключ не вмикати до рішення щодо R1; `ConnectionStrings:Migrator` лише для разового CLI `ai-secrets reencrypt`.

## Знахідки
| ID | Severity | Суть | Виконавець |
|---|---|---|---|
| M1 | medium (латентна) | Валідатор промпт-додатку обходиться юнікодом: fullwidth `＜＞`, кирилиця в назві тегу, HTML-сутності, ` ===`, невидимі символи (U+00AD, bidi isolates, блок тегів U+E0000–E007F). Потрібні NFKC-скелет, видалення всіх `Cf` і E0000–E007F до NFC, відхилення будь-яких `<…>` з іменами тегів | ai-agent |
| M2 | medium | Ліміти частоти (AiRateLimiter, AiKeyThrottle 5/600 с) в пам'яті одного інстансу; перевірка після `PlanAsync` (БД + розшифрування) | backend |
| M3 | medium (латентна) | `<client_message>` обгортка обходиться (`< /client_message>`, невидимі символи); `tool_result` з клієнтськими даними не маркується як дані | ai-agent |
| M4 | medium (операційна) | Runbook/права БД не готові: ключі й бекап, REVOKE, ротація (TASK-720) | docs/devops |
| L1 | low | `beauty_ai_settings_audit` не append-only (UPDATE/DELETE через GRANT ALL TABLES), контрактний тест 14 відсутній | docs/db |
| L2 | low | Запити AiSettingsService без явного `TenantId ==` (лише RLS) | backend |
| L3 | low (латентна) | `TenantGuard.Ensure` сам виставляє tenant при порожньому scope: для webhook/HTTP шляхів кидати виняток | ai-agent |
| L4 | low | admin при `key_source=own` може зняти ліміти, обрати opus, увімкнути auto | backend/продукт |
| L5 | low | Скасований виклик провайдера не обліковується | ai-agent |
| L6 | low | Оракул перевірки ключів: ліміт лише per (tenant,user) in-memory | backend |
| L7 | low | CLI перешифрування потребує Migrator-рядка: давати лише джобі; `NpgsqlAiSecretRowStore` не перевірений на реальній БД | docs/devops |
| L8 | low | `autoComplete="off"` на полі ключа; потрібно `new-password` | frontend |
| L9 | low (латентна) | Перевірка тегу до обрізання тексту до 2000 символів | ai-agent |

Info: I1 розшифрований ключ як незмінний string, не кешується; I2 немає FK на tenants (цілісність, не ізоляція); I3 `no-store` для `/ai/actions`; I4 platform PATCH з чужим JWT дає 500; I5 запустити `dotnet list package --vulnerable` і `npm audit`; I6 HttpClient без PooledConnectionLifetime.

## Залишкові ризики
- R1 (high для проду): `get_client_context` передає провайдеру нотатки й алергії (дані про здоров'я). Потрібні DPA, повідомлення про приватність, мінімізація полів. Юридичний gate ADR-010 §7.3 відкритий.
- R2: `beauty_ai_actions` зберігає текст чернеток: потрібна політика зберігання.
- R3: реальна поведінка Anthropic (id моделей, `usage`, коди помилок, `GET /v1/models`) не перевірена.

Task logs відповідають коду, неточних тверджень не знайдено.
