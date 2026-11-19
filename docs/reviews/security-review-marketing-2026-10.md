# TASK-787: рев'ю безпеки маркетингу (згода, розсилки, аудиторії, купони), 2026-10-31

Рев'юер: security-reviewer (лише читання). Скорочений запис знахідок.

**Вердикт:** critical 0, high 1, medium 6, low 12, info 5.
- Ручний smoke із тестовим ботом: можна за умов SM1-SM12.
- Staging: поки ні (закрити H1, M2, M3 і QA TASK-786).
- main/прод: ні (юридичний gate §26.16 п.1, H1, M1-M6, погодження міграції).

## Знахідки
| ID | Severity | Суть | Виконавець |
|---|---|---|---|
| H1 | high | Відписка («Стоп», «не надсилати акції», i:mkoff) не працює, коли вимкнено ШІ-конвеєр (`ChatAssistant:Enabled=false` або `assistant.enabled≠true`): вхідні лишаються received/skipped, а воркер кампаній не перевіряє це й далі шле розсилки | backend (ingest) |
| M1 | medium | Повний код купона лежить у `beauty_messages.body` кампанії, потрапляє в історію LLM (до провайдера ШІ) і в API inbox | worker + backend |
| M2 | medium | У воркері немає legal guard (WORKER_MARKETING_LEGAL_APPROVED) для кампаній і winback поза Development | worker |
| M3 | medium | Журнал згод не append-only для app-ролі (немає REVOKE UPDATE/DELETE на consent_events); роль воркера не має GRANT на таблиці маркетингу | scripts/runbook/DbRoleGuard |
| M4 | medium | Kill switch неузгоджений: cancel кампанії недоступний при Marketing:Enabled=false; воркер не знає про прапорець/модуль; outbox шле вже поставлені campaign/winback | backend + worker |
| M5 | medium | Підбір загальних кодів у чаті без лічильника промахів (хеш-пошук без префікса); слабкі загальні коди (від 4 символів); лімітери in-memory, IPv6 повна адреса | backend |
| M6 | medium (winback) | Winback іде не лише в Telegram (будь-який активний канал), сире ім'я, без ліміту постановки/вікна | worker |
| L1 | low | Гонка частоти 1/7 днів між паралельними воркерами | worker |
| L2 | low | Перегляд/підтвердження не враховують unsubscribed і pref none (розходження з воркером) | backend |
| L3 | low | Лінивий клієнт після «Стоп» без рядка none/stop: повторно пропонується згода | backend |
| L4 | low | Модуль beauty_marketing не перевіряється в чаті й воркері | backend + worker |
| L5 | low | Ліміт 200/добу при відправці не атомарний | worker |
| L6 | low | revision не охоплює зміни акції/купона після перегляду | backend |
| L7 | low | Купонні ендпоінти без rate limit; /coupons/check доступний specialist з деталізованими причинами | backend |
| L8 | low | codeHint віддається specialist через API (ховає лише UI) | backend |
| L9 | low | Unicode Cf (bidi, zero-width) пропускаються у шаблоні/імені | backend + worker |
| L10 | low | Код у handoff і при Marketing:Enabled=false не маскується | backend |
| L11 | low | Невалідна зона закладу: одержувач лишається pending, помилка щохвилини | worker |
| L12 | low | Старий AI-шлях (build_audience + draft_reply Auto) оминає запобіжники кампаній (відомий борг V2) | відкладено |

Info: I1 порядок фільтрів (400 замість 403), I2 DEFAULT_FAVORITE_SERVICES_DAYS не з env, I3 формат коду, I4 немає лога TASK-786 (QA), I5 `revision_changed` vs `campaign_changed` (frontend приймає обидва).

## Перевірено без проблем
Згода пишеться лише через SetConsentAsync; advisory lock; публічна форма/staff пишуть false; AdminGrant типово вимкнено; підтвердження кампанії атомарне, гонка двох confirm має переможця; перевірки згоди/частоти/вікна/ліміту перед відправкою; cancel відсікає queued; plain text без parse_mode; RLS FORCE; CSV defuse; код купона 50 біт CSPRNG, SHA-256 з префіксом coupon:, CAS/UNIQUE одноразовості, повернення без подвійності, code_only не витікає, публічний шлях лише загальні коди.

## Умови ручного smoke (SM)
SM1 Development, один API і воркер, polling. SM2 новий тестовий бот, username нікому. SM3 ChatAssistant:Enabled=true і assistant.enabled=true весь час тесту (інакше відписка не діє, H1). SM4 синтетичні дані, модуль beauty_marketing лише на тестовому tenant. SM5 AdminGrantEnabled=false, WORKER_WINBACK_ENABLED=false. SM6 малі ліміти (WORKER_MARKETING_DAILY_LIMIT, Marketing:DailyLimit, WORKER_CAMPAIGN_BATCH). SM7 GRANT воркеру й REVOKE UPDATE/DELETE на consent_events для app. SM8 тестовий ключ ШІ з лімітами; код купона до виправлення M1 іде провайдеру. SM9 платежі mock. SM10 cancel кампанії лише поки Marketing:Enabled=true; після тесту зупинити воркер, потім API. SM11 перевірити логи на коди, тексти, імена, chat id. SM12 після тесту: /revoke токена, видалити ключ ШІ, деактивувати канал, відкликати загальні коди, скасувати кампанії.

## До main/прод
Юридичний gate; H1; M1; M2; M3; M4; M5; M6 до ввімкнення winback; QA TASK-786; погодження міграції §26.16 п.2; продуктові рішення (L2/L3); бажано L1, L4, L5, L7, L12.
