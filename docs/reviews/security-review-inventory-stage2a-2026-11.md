# Security-review: склад, етап 2a (TASK-883…888)

Рев'юер: `security-reviewer` (лише читання). Гілка `feature/inventory-stage2a`, коміти db92c54…17db42b. Джерело істини: контракт §32.15–§32.25, ADR-025 §11.

## Вердикт
Блокуючих (critical/high) знахідок немає. Зливати можна після штатного human gate TASK-885. Перед увімкненням модуля на staging/prod закрити M1 і L1.

## Знахідки

| ID | Серйозність | Суть | Де | Виправлення |
|---|---|---|---|---|
| M1 | medium | Складський крок без `lock_timeout`: за блокування рівня іншою транзакцією завершення візиту чекає до `CommandTimeout` (30 с), тримаючи рядок візиту; при обриві HTTP-запиту `OperationCanceledException` виключена з `catch`, і відкочується вся транзакція (статус і знімок ADR-014), що суперечить «візит завершується завжди» (§32.18) | `EfBeautyStore.InventoryConsumption.cs:29-41,73-77`; виклик `EfBeautyStore.Booking.cs:339` | `SET LOCAL lock_timeout` одразу після SAVEPOINT (55P03 поглинає SAVEPOINT); вирішити `CancellationToken.None` для складського кроку й коміту після умовного UPDATE; тест «рівень заблоковано → візит completed ≤ N с, лог `inventory_consumption_failed sqlstate=55P03`» |
| L1 | low | `Inventory:AutoConsumptionEnabled` з нерозпізнаним значенням (`0`, `off`, `no`) лишає автосписання увімкненим | `EfBeautyStore.InventoryConsumption.cs:19` | будь-яке непорожнє значення, крім `true`, — вимкнено; задокументувати в runbook |
| L2 | low | Модульний гейт автосписання без `t.Id == tenant` (лише RLS) і чутливий до регістру, на відміну від API (`OrdinalIgnoreCase`) та воркера (`lower(m)`) | `EfBeautyStore.InventoryConsumption.cs:49` | явний фільтр за tenant, порівняння без урахування регістру; винести в `InventoryModuleGate` (§32.18 п.2) |
| L3 | low | Воркер ковтає помилки закладів без логу (`catch { res.errors++ }`), у лозі запуску немає `tenant=` | `worker/src/inventory/alerts.ts:86-88`; `pollers.ts:187` | `inventory_alerts_location_failed tenant= location= errorCode=` (без ПД), `tenant=` у підсумковий лог |
| L4 | low | GRANT воркера ширші за потребу: `UPDATE` на всю `beauty_staff_alerts`, `SELECT` на `beauty_products` включно з `purchase_price` | `deploy/staging/db/grants.sql:45-46`; `scripts/dev/setup-db.ps1:117` | `GRANT UPDATE (status, resolved_at, updated_at)`; за бажанням колонковий SELECT на products; обидва файли синхронно (`staging-grants-sync`) |
| L5 | low | Роль owner/admin для зміни норм перевіряється в сервісі після прив'язки тіла, а не фільтром (§32.16): deputy на кривий JSON отримає 400, а не 403 | `InventoryNormsController.cs:46-71`; `InventoryNormsService.cs:32-36` | рольовий фільтр на PUT/DELETE; обходу немає |

## Info
- **I1.** §32.18 п.7 описує порядок «товари FOR SHARE → рівні», код — «рівні FOR UPDATE → товари FOR SHARE → налаштування», як в етапі 1; циклу немає. Виправити текст контракту.
- **I2.** SAVEPOINT у коді береться до читань кроку (безпечніше), у контракті — на п.5. Зафіксувати в контракті.
- **I3.** Тихе пропускання списання при технічному збої (2a-Q1) — прийнятий ризик; потрібні моніторинг логу `inventory_consumption_failed` (Error) і процедура виправлення через інвентаризацію в runbook.
- **I4.** `shortage_quantity` не обмежений `MaxLevel`; переповнення практично недосяжне, 22003 поглинає SAVEPOINT. За бажанням: перевірка `shortage + u <= MaxLevel`.
- **I5.** Інваріант: `SaveChangesAsync` складського кроку зберіг би незбережені tracked-зміни, а `ChangeTracker.Clear()` у catch їх відкинув би; зараз шлях завершення використовує лише raw SQL / `ExecuteUpdate`. Зафіксувати коментарем.
- **I6.** `LockLevelsAsync` створює рядки рівня з `quantity = 0` до вставки заголовка; вплив мізерний (2a-Q2).
- **I7.** `canEditInventoryNorms` не перевіряє роль: deputy бачить редактор і отримує 403 (лише UX).
- **I8.** `GET /service-materials` без пагінації; обсяг обмежений.
- **I9.** Для `scope.All` чужий `locationId` у фільтрі сповіщень дає 200 з порожнім списком замість 404; витоку немає.
- **I10.** Тести автосписання — `SkippableFact`; CI має запускати їх з `BEAUTY_TEST_REQUIRE_DB=1`.

## Перевірено, проблем немає
Транзакційність завершення й знімок ADR-014 (SAVEPOINT, інʼєкційний тест); ізоляція tenant (RLS ENABLE+FORCE, композитні FK, `tenant_id` з `app.tenant_id`); IDOR (service/location/product/alert чужого tenant → 404); права (`RequireModule` + `inventory.view`/`inventory.manage = all` + owner/admin); append-only журнали (тригери + REVOKE, нові поля незмінні, підробити чи обнулити дефіцит через API неможливо, від'ємний залишок поза автосписанням неможливий); ідемпотентність і гонки (partial UNIQUE + `ON CONFLICT`, порядок блокувань спільний з етапом 1); воркер (`assertRlsEnforced`, `set_config` на транзакцію, `WITH CHECK`, прапор за замовчуванням `false`, ліміт 500); приватність (алерти без `client_id`/`appointment_id`/імен/цін, логи лише з ідентифікаторами); ліміти (≤50 рядків, `0 < q ≤ 100000`, тіло ≤16 KB, throttle, `take` 1..200); міграція (ADD COLUMN DEFAULT без перезапису, Down відмовляє за наявності історії).

## Маршрутизація виправлень
backend-developer: M1, L1, L2, L5. Воркер: L3. database/GRANT: L4. documentation-writer (TASK-894): I1, I2, I3 і runbook (значення вимикача, моніторинг).
