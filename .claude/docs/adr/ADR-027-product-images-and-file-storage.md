# ADR-027: Зображення товарів і зберігання файлів (диск сервера, публічна роздача за випадковим ключем)

**Date:** 2026-11-21
**Status:** proposed (продуктові рішення §1.2 ухвалено людиною 2026-11-21; схему §3, ліцензію бібліотеки (§2.5) і деплой (§8) ще має затвердити людина; `accepted` ставить людина)
**Author:** project-architect
**Deciders:** продуктові рішення (1)–(5) §1.2 ухвалив користувач 2026-11-21. Рішення архітектора позначено «[А]», вони чекають затвердження.
**Уточнює (після `accepted`):** ADR-026 / контракт §33.5 (у `PublicProductDto` адитивно додаються nullable-поля, контракт стає `API-CONTRACT-PRODUCT-ORDERS-V1.1`), ADR-025 / §32.6 (адитивні поля `ProductDto`/`ProductDetailDto`), ADR-016 (новий docker volume, точка монтування в `api`, правило nginx для тіла upload, бекап тому).
**Не змінює:** ADR-002 (RLS: нових політик і SECURITY DEFINER-функцій немає), ADR-023 (нових ключів прав немає), ADR-022 (каталог закладів), ADR-003 (outbox не потрібен).

Контракт: `.claude/docs/beauty-contracts.md` **§33.22** (дельта §33.5 і §32.6): маршрути, DTO, коди, ліміти, тести. Якщо контракт розходиться з цим ADR, діє ADR. Зміна семантики оформлюється новим ADR.

---

## 1. Context

### 1.1 Стан коду (2026-11-21, перевірено)

- `Product` (`backend/BeautyCrm.Infrastructure/Data/Entities/BeautyInventoryEntities.cs`): `TenantEntity`, полів зображень немає. Каталог мережевий (один на tenant). Створення й редагування (`POST/PUT /api/beauty/inventory/products`) вимагають `inventory.manage = all` (`InventoryController`), у UI це `InventoryScopeDto.canEditCatalog`. Маршруту видалення товару немає (товари архівують через `isActive`).
- Публічний каталог товарів P1 (`GET /api/public/{slug}/locations/{id}/products`, `PublicProductOrdersController`, `ProductOrderService.CatalogAsync`) повертає `PublicProductCatalogDto` / `PublicProductDto(Id, Name, Unit, Price, CategoryPath, InStock)` (`ProductOrderModels.cs`). Контракт §33.5 зобов'язує клієнта ігнорувати невідомі поля.
- Фото майстра (`beauty_specialists.photo_url`, `StaffService.ValidatePublicProfile`) зберігається як **зовнішній** https-URL до 500 символів. Сервер файлів не приймає й не зберігає. Отже, сховища файлів у системі ще немає.
- RLS-патерн міграцій (`20261120122321_beauty_product_orders.cs`): `ENABLE` + `FORCE` + політика `tenant_isolation` (`tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid`) + `DO`-блок перевірки FORCE. GRANT/REVOKE задаються не в міграції, а синхронно в `scripts/dev/setup-db.ps1` і `deploy/staging/db/grants.sql` (тест `staging-grants-sync`).
- Staging (`deploy/staging/docker-compose.staging.yml`): `api` працює з `read_only: true`, `cap_drop: ALL`, tmpfs `/tmp`, `mem_limit: 512m`, UID 1654 (`backend/Dockerfile`), рівно один інстанс. Єдиний том: `beauty_staging_pgdata`. Системний nginx (`deploy/staging/README.md` §6.3) має `client_max_body_size 2m`, basic auth на весь server-блок, `/api/` проксіює в API. Бекап описано лише для БД (`pg_dump` від `postgres`, README §5).
- Воркер отримує tenant-и зі **статичного** списку `WORKER_TENANT_IDS` (`worker/src/db/tenants.ts`, `beauty_list_tenants()` свідомо не створено). `forEachTenant` логує помилку tenant-а й переходить до наступного.

### 1.2 Рішення користувача (2026-11-21)

1. Сховище: **диск сервера** (docker volume), без S3/об'єктного сховища.
2. **Галерея**: кілька фото на товар.
3. Завантажує **лише CRM**: власник або адмін з правом складу (inventory). Мобільний клієнт і публічний API тільки читають.
4. URL **публічні, без авторизації**. Ключ випадковий і непередбачуваний, **tenant-id у ключі й URL немає**.
5. Публічний DTO каталогу §33.5 отримує **лише адитивні nullable-поля** `imageUrl` (перше фото) і `images[]`. Клієнт V1 не ламається.

---

## 2. Decision

### 2.1 Обсяг

Робить: таблицю `beauty_product_images`, upload / delete / reorder у CRM, обробку зображення (перевірка, ресайз, перекодування, зняття метаданих), зберігання на томі, публічну роздачу файлів за ключем, поля в CRM-DTO і публічному DTO каталогу, фонове очищення файлів-сиріт, том і бекап на staging.

**Не робить:** фото в рядках замовлення (`PublicProductOrderLineDto`), перенесення фото майстрів (`photo_url`) у це сховище, CDN, об'єктне сховище, підписані або тимчасові URL, upload з мобільного застосунку, фото послуг і закладів, обрізання (crop) на сервері, модерацію вмісту. Для кожного з цих пунктів потрібне окреме рішення. Механізм §2.3–§2.9 придатний для повторного використання.

### 2.2 Модель зберігання

- Тека `Media:RootPath` (у контейнері `/data/media`, том `beauty_staging_media`).
- **Файли варіантів:** `{root}/p/{key[0..1]}/{key}-{size}.webp`. Тут `key` — 32 символи `[0-9a-f]` (128 біт з `RandomNumberGenerator`), `size ∈ {400, 1600}`, а `key[0..1]` — перші 2 символи ключа (256 підтек, щоб у теці не було сотень тисяч файлів).
- **Маркер власника [А]:** `{root}/t/{tenantId}/{key}`, порожній файл. Він існує тільки на диску й ніколи не потрапляє в URL. Маркер дозволяє очищувачу (§2.9) працювати по одному tenant-у під звичайним RLS, без крос-tenant читання.
- Права в контейнері: власник UID 1654, теки `0750`, файли `0640`. Більше жоден контейнер том не монтує.
- Оригінал файлу **не зберігається**. На диску лишаються тільки перекодовані варіанти.

### 2.3 Публічна роздача — **вибір: файл за ключем напряму з диска, без БД і без tenant-контексту**

Маршрут: `GET|HEAD /api/public/media/p/{file}` у API, мінімальний endpoint без контролера-сервісу й без доступу до БД.

1. `file` має повністю відповідати `^(?<key>[0-9a-f]{32})-(?<size>400|1600)\.webp$` (`RegexOptions.CultureInvariant`, з таймаутом). Якщо не відповідає, віддається `404`.
2. Шлях збирається **лише з груп regex**: `Path.Combine(root, "p", key[..2], $"{key}-{size}.webp")`. Далі `Path.GetFullPath(...)` має починатися з `GetFullPath(root) + separator`, інакше `404`. Додатково: файл не повинен бути symlink (перевірка `FileSystemInfo.LinkTarget == null`), інакше `404`.
3. Файлу немає: `404` з порожнім тілом і `Cache-Control: no-store`. Немає різниці між «ніколи не існував», «видалено» і «невалідний».
4. Файл є: `200`, тіло потоком (`PhysicalFile`), `Content-Type: image/webp` (фіксований, а не з розширення чи вмісту), `Cache-Control: public, max-age=31536000, immutable`, `ETag` = `"{key}-{size}"`, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox`, `Cross-Origin-Resource-Policy: cross-origin`, `Content-Disposition: inline`. Range-запити дозволені.
5. Без `[PublicClient]` / `426`: завантажувачі зображень не надсилають `X-App-Version`. Без `[PublicTenant]`. Rate limit — нова політика `public-media`, 600/хв на IP (/64 для IPv6).

**Обґрунтування.** Публічний запит зображення не знає tenant-а, а ADR-002 робить RLS fail-closed: без `app.tenant_id` таблиця порожня. Читання з БД означало б або SECURITY DEFINER-функцію / вузьку політику з роллю на кшталт `beautycrm_catalog` (§29), або обхід RLS. Ключ на 128 біт неможливо вгадати, і він сам є «дозволом на читання». БД не додала б захисту: вона лише підтвердила б те, що вже доводить наявність файлу. Роздача без БД також не створює навантаження на пул з'єднань (500 товарів × 2 варіанти на один екран) і не відкриває нової поверхні RLS.

Ціна рішення: призупинення tenant-а, архівування товару чи вимкнення модуля **не блокують** уже виданий URL. Фото товару — публічна вітринна інформація, тому це прийнятно. Блокує тільки видалення фото (файл зникає). Обов'язкове видалення фото через деактивацію — поза цим ADR.

### 2.4 Формат URL

DTO віддають **абсолютний** URL: `{Media:PublicBaseUrl}/api/public/media/p/{key}-{size}.webp`, наприклад `https://beauty.<DOMAIN>/api/public/media/p/3f9a0c1e7b2d4a6f8e1c5b7d9a0f2e4c-400.webp`.

- `Media:PublicBaseUrl` — origin без шляху й без завершального `/`. Поза `Development` дозволено лише `https`. Невалідне значення спричиняє помилку старту.
- Шлях лежить під `/api/`, тому nginx staging уже проксіює його в API, а dev працює без проксі. `/api/public/resolve/{code}` — прецедент публічного маршруту без slug.
- URL **незмінний**: новий вміст завжди отримує новий ключ, файл за наявним ключем не перезаписується. Саме тому можна ставити `immutable`.
- Версіонування варіантів [А]: якщо колись знадобляться інші розміри чи формат, з'являться нові суфікси (`-800.avif`). Старі лишаються, поки існує рядок.

### 2.5 Обробка зображення — **бібліотека: SixLabors.ImageSharp 3.1.x** (керований код, без нативних залежностей)

**Ліцензійне зауваження (human gate G2).** ImageSharp 3.x поширюється під **Six Labors Split License**: безплатно (Apache 2.0) для open source, для використання як транзитивної залежності і для комерційних організацій з річним валовим доходом **менше 1 млн доларів США**. В інших випадках потрібна платна комерційна ліцензія. ImageSharp 2.x (чиста Apache 2.0) не підтримується й не отримує виправлень безпеки, тому відхилено. Перед TASK-912 людина підтверджує, що ліцензійні умови виконано. Якщо ні, резервний вибір — **SkiaSharp** (MIT, нативний libSkia через `SkiaSharp.NativeAssets.Linux.NoDependencies`). Контракт §33.22 від бібліотеки не залежить: усе приховано за портом `IImageProcessor` в Application.

Конвеєр (`IImageProcessor.ProcessAsync(Stream, ct) → ProcessedImage{ variants[], width, height }`):

1. **Розмір тіла:** ≤ `Media:MaxUploadBytes` (10 МБ). Перевіряють Kestrel (`[RequestSizeLimit]`, `[RequestFormLimits(MultipartBodyLengthLimit)]`) і читач, що буферизує в пам'ять з лімітом.
2. **Тип визначається за сигнатурою (magic bytes)**, а не за `Content-Type` чи ім'ям файлу. Дозволено тільки JPEG, PNG, WebP: `Configuration` ImageSharp містить лише ці три формати. GIF, BMP, TIFF, SVG, HEIC/HEIF, AVIF та інші дають `415 unsupported_image_type`. SVG ніколи не приймається (активний вміст).
3. **`Image.IdentifyAsync` до декодування:** ширина × висота ≤ `Media:MaxPixels` (25 Мп), кожна сторона ≤ 10 000 px, коротша сторона ≥ 200 px. Інакше `422 image_dimensions_invalid`. Це захист від декомпресійної бомби.
4. **Декодування:** `DecoderOptions { MaxFrames = 1 }` (з анімованих WebP/APNG береться лише перший кадр). `MemoryAllocator` з `AllocationLimitMegabytes = 256`. Збій декодування дає `422 image_decode_failed`.
5. **Орієнтація й метадані:** `AutoOrient()` (EXIF-орієнтація застосовується до пікселів), після чого **знімаються всі метадані**: EXIF (зокрема GPS), XMP, IPTC, ICC. `SkipMetadata = true` в енкодері.
6. **Ресайз:** два варіанти, які вписуються в квадрат 400 і 1600 px за довшою стороною (`ResizeMode.Max`, `Lanczos3`), **без збільшення** (менше зображення лишається у своєму розмірі).
7. **Перекодування в WebP [А]** (lossy, `Quality = 80`; прозорість PNG зберігається). Єдиний вихідний формат. JPEG не потрібен, бо WebP підтримують усі цільові клієнти (iOS 14+, Android, сучасні браузери CRM), а JPEG не має прозорості й важчий.
8. **Пам'ять і паралелізм:** `SemaphoreSlim(Media:MaxConcurrentProcessing = 1)`. Очікування понад 10 с дає `503 media_busy` + `Retry-After: 5`. Запит обробляється в межах `mem_limit` API. Рекомендація для deploy-гейту: підняти `mem_limit` api до 768m.
9. Результат: `width`/`height` варіанта 1600 і `size_bytes` = сума байтів обох варіантів.

**Frontend CRM [А]** перед відправкою зменшує фото в браузері (canvas, довша сторона ≤ 2560, JPEG/WebP 0.9). Це скорочує трафік і лишає більшість запитів далеко від лімітів. HEIC з iPhone браузер Safari при виборі файлу зазвичай віддає вже як JPEG. Сервер однаково все перевіряє (§2.5 п.1–4) і клієнту не довіряє.

### 2.6 Схема (на затвердження людини; точні колонки — §33.22.6)

Одна адитивна міграція `beauty_product_images`:

| Колонка | Тип | Обмеження |
|---|---|---|
| `id` | `uuid` | PK |
| `tenant_id` | `uuid NOT NULL` | FK `tenants` RESTRICT; RLS |
| `product_id` | `uuid NOT NULL` | композитний FK `(tenant_id, product_id)` → `beauty_products (tenant_id, id)` **ON DELETE CASCADE** |
| `storage_key` | `char(32) NOT NULL` | CHECK `storage_key ~ '^[0-9a-f]{32}$'`; **UNIQUE глобально** `ux_beauty_product_images_storage_key (storage_key)` (простір URL спільний для всіх tenant-ів) |
| `mime` | `varchar(32) NOT NULL` | CHECK `= 'image/webp'` (формат збережених варіантів; новий формат = зміна CHECK) |
| `size_bytes` | `integer NOT NULL` | CHECK `> 0 AND <= 10485760` (сума варіантів) |
| `width`, `height` | `smallint NOT NULL` | CHECK `BETWEEN 1 AND 1600` (варіант 1600) |
| `sort_order` | `smallint NOT NULL` | CHECK `BETWEEN 0 AND 99`; UNIQUE `(tenant_id, product_id, sort_order)` **DEFERRABLE INITIALLY DEFERRED** (переставлення одним `UPDATE`) |
| `created_by_user_id` | `uuid NOT NULL` | FK `users` RESTRICT (аудит) |
| `created_at` | `timestamptz NOT NULL` | `DEFAULT now()` |

- Індекс `(tenant_id, product_id, sort_order)`, щоб читати галерею й перше фото.
- **RLS:** ENABLE + FORCE + `tenant_isolation` (патерн `20261120122321`), `DO`-блок перевірки FORCE. Нових політик і функцій немає.
- **ON DELETE CASCADE**: якщо товар (або tenant каскадом) колись видалять, рядки зникнуть разом з ним. Файли за видаленими рядками прибирає очищувач (§2.9). Маршруту видалення товару зараз немає, тож CASCADE — запобіжник, а не робочий шлях.
- **GRANT** (у двох файлах синхронно): app — `SELECT, INSERT, DELETE` і `UPDATE (sort_order)`, без оновлення інших колонок (рядок незмінний, крім порядку). Воркер — **нічого**.
- **Down:** відмовляє (`RAISE EXCEPTION`), якщо є хоч один рядок. Інакше видаляє таблицю. Файли на томі Down не чіпає.

### 2.7 Порядок «файл, потім транзакція»

**Upload** (`POST …/products/{id}/images`):
1. Перевірки до важкої роботи: права (§2.10), `Media:RootPath` налаштовано (інакше `404 product_images_disabled`), товар існує під RLS (`404 product_not_found`), швидкий підрахунок `count < MaxImagesPerProduct` і квоти tenant-а (`422`). Це лише оптимізація, остаточну перевірку робить п.5.
2. Обробка (§2.5) поза транзакцією.
3. Генерація `key`: 16 байт `RandomNumberGenerator` → hex.
4. **Запис на диск у такому порядку:** маркер `t/{tenantId}/{key}` → кожен варіант у `p/{kk}/{key}-{size}.webp.tmp` (`FileMode.CreateNew`, `Flush(true)`) → `File.Move(tmp, final, overwrite: false)`. Якщо файл існує (колізія 128 біт, практично неможлива), генерується новий ключ.
5. **Транзакція:** `SELECT … FROM beauty_products WHERE id = $1 FOR NO KEY UPDATE` (серіалізує upload/reorder/delete одного товару; `NO KEY`, щоб не блокувати FK-перевірки `FOR KEY SHARE` з таблиць складу й замовлень) → повторний підрахунок фото товару й tenant-а → `sort_order = COALESCE(max, -1) + 1` → `INSERT` → commit → `201`.
6. **Збій п.5** (ліміт, конфлікт, БД недоступна): варіанти й маркер видаляються в `finally` (best effort), далі код помилки. Якщо процес упав між п.4 і п.5, лишаються файли без рядка. Їх видалить очищувач після grace-періоду.

Інваріант: **рядок ніколи не з'являється раніше за файли**. Тому кожен рядок, який бачить клієнт, має файли. Виняток — відновлення з неузгодженого бекапу, §2.11.

**Delete** (`DELETE …/images/{imageId}`): транзакція `FOR NO KEY UPDATE` товару → `DELETE … WHERE id = $imageId AND product_id = $id` → commit → `204`. **Лише після commit** видаляються варіанти, потім маркер (best effort). Помилку видалення файлу логують, але відповіді вона не змінює: залишок прибере очищувач. Видаляти файли до commit заборонено, бо rollback лишив би рядок без файлу. Пропуски в `sort_order` допустимі. Порядок визначає сортування `sort_order, created_at, id`.

### 2.8 Ліміти

| Ключ | За замовч. | Діапазон | Призначення |
|---|---|---|---|
| `Media:RootPath` | відсутній = **функцію вимкнено** | абсолютний шлях | тека тому. Якщо задано, при старті перевіряються існування й запис (probe-файл); провал = помилка старту |
| `Media:PublicBaseUrl` | — (обов'язковий за наявності `RootPath`) | `https://…` (у Development дозволено `http`) | origin для URL у DTO |
| `Media:MaxUploadBytes` | `10485760` (10 МБ) | 1 МБ..20 МБ | тіло файлу |
| `Media:MaxImagesPerProduct` | `8` | 1..20 | фото на товар |
| `Media:MaxImagesPerTenant` | `5000` | 1..100000 | квота tenant-а (≈ 0,3–0,6 МБ на фото → ≤ ~3 ГБ) |
| `Media:MaxPixels` | `25000000` | 1 Мп..50 Мп | захист від декомпресійної бомби |
| `Media:MaxConcurrentProcessing` | `1` | 1..4 | паралельні обробки на процес |
| `Media:UploadsPerUserPerMinute` | `20` | 1..120 | rate limit upload за користувачем |
| `Media:SweepMode` | `off` | `off` \| `dry-run` \| `delete` | очищувач (§2.9) |

Константи: варіанти 400 і 1600, WebP q80, мінімальна сторона 200 px, максимальна 10 000 px, `public-media` 600/хв на IP.

### 2.9 Очищення сиріт — **hosted service в API, по одному tenant-у, за маркерами на диску**

`MediaOrphanSweeper` (`BackgroundService` в API; том монтується тільки в API). Запуск раз на 6 годин і при старті +10 хв. Працює лише за `Media:SweepMode ≠ off`. `pg_try_advisory_lock` (простір `media_sweep`) захищає від двох паралельних запусків.

1. Список tenant-ів береться **з диска**: імена тек `t/*`, які є валідними uuid.
2. Для кожного tenant-а: з'єднання з `app.tenant_id` = tenant (той самий механізм tenant-контексту, що й у запитах, RLS FORCE діє) → перевірка `SELECT EXISTS (SELECT 1 FROM tenants WHERE id = current tenant)`: **не видно → tenant пропускається** (нічого не видаляється, лог Warning) → `SELECT storage_key FROM beauty_product_images` → множина `live`.
3. Кандидати: маркери `t/{tenant}/{key}` з `mtime` старшим за **24 год** і `key ∉ live`. Для кожного кандидата видаляються `p/{kk}/{key}-*.webp`, потім маркер. Окремо: `*.tmp` старші за 1 год. Файли `p/**` без жодного маркера, старші за 24 год (аварія між маркером і варіантом неможлива, бо маркер пишеться першим), вважаються сиротами без власника й видаляються. Файли, імена яких не відповідають regex, не чіпаються: лише лог Warning.
4. **Запобіжники (fail safe):** помилка БД або файлової системи на tenant-і означає, що tenant пропущено. Кандидатів на tenant більше за 50% його маркерів (за > 20 маркерів) або загалом більше за `500` за запуск → **нічого не видаляється** для цього tenant-а / запуску, лог Error `media_sweep_aborted`. `dry-run` лише логує кандидатів (за кількістю, без ключів).
5. Додатково (лише лог): рядки, для яких немає файлу (`live ∖ маркери`), дають Warning `media_missing_files tenant={id} count={n}`.

Чому не воркер: воркер бере tenant-ів зі статичного `WORKER_TENANT_IDS`. Tenant, якого немає в списку, виглядав би як «нуль живих ключів», і всі його файли видалилися б. Крім того, воркеру довелося б монтувати том із записом і узгоджувати UID між контейнерами. Чому tenant-и з диска, а не з БД: API не має крос-tenant читання tenant-ів без вузької політики (ADR-002 / ADR-026 §5), а маркери дають рівно той список, де можуть бути файли. Tenant, видалений з БД, не буде видно на кроці 2, тому його файли **не видаляються автоматично**. Їх прибирають вручну за runbook (рідкісна дія людини).

### 2.10 CRM endpoint-и й права (без нових ключів)

Клас-рівень як у `InventoryController`: `[Authorize(Staff)]` + `[RequireModule("beauty_inventory")]` + `inventory.view ≥ own`. Дії змінюють **мережевий каталог**, тому вимагають `inventory.manage = all`, як `POST/PUT /products` (`canEditCatalog`). За пресетами ADR-023 це owner і admin (deputy — якщо так налаштовано у «Команді й доступах»).

| Метод | Шлях (`/api/beauty/inventory`) | Право | Тіло / відповідь |
|---|---|---|---|
| POST | `/products/{id}/images` | `inventory.manage = all` | `multipart/form-data`, рівно одне поле-файл `file` → `201 ProductImageDto`; rate limit `Media:UploadsPerUserPerMinute` і `Inventory:WritePerUserPerMinute` |
| DELETE | `/products/{id}/images/{imageId}` | `inventory.manage = all` | → `204`; повтор → `404 product_image_not_found` |
| PATCH | `/products/{id}/images/order` | `inventory.manage = all` | `{ imageIds: uuid[] }` — точна перестановка поточного набору → `200 { images: ProductImageDto[] }` |
| GET | `/products/{id}` (наявний) | `inventory.view ≥ own` | `ProductDetailDto.images: ProductImageDto[]` (адитивно) |
| GET | `/products` (наявний) | `inventory.view ≥ own` | `ProductDto.imageUrl: string \| null` (прев'ю першого фото, адитивно) |

Окремого `GET …/images` немає: галерея приходить у деталі товару. Upload дозволено для будь-якого `purpose`, зокрема `salon_use` (корисно в CRM). Публічно фото видно лише для товарів, видимих у P1 (ADR-026 §2.4).

### 2.11 Volume і backup

- Compose: том `beauty_staging_media` (`name: beauty_staging_media`), змонтований у `api` як `/data/media` (rw). `read_only: true` для кореневої ФС лишається. Права: перед першим `up` людина один раз виконує `chown 1654:1654` і `chmod 0750` для тома (одноразовий контейнер `alpine`; команду описує runbook). `api.env`: `Media__RootPath=/data/media`, `Media__PublicBaseUrl=https://beauty.<DOMAIN>`, `Media__SweepMode=dry-run` (після перевірки буде `delete`).
- nginx: окремий `location ~ ^/api/beauty/inventory/products/[0-9a-fA-F-]{36}/images$ { client_max_body_size 11m; proxy_pass …; include …proxy.conf; }`. Глобальні 2m для решти лишаються. `proxy_request_buffering` за замовчуванням. Basic auth для `/api/public/media/` — відкрите питання Q1.
- **Бекап:** спершу `pg_dump`, як і зараз, а **одразу після нього** `docker run --rm -v beauty_staging_media:/m:ro -v /opt/beauty_staging/backups:/b alpine tar -czf /b/media-<ts>.tgz -C /m .`. Порядок «БД, потім файли» означає, що фото, завантажені між двома кроками, після відновлення стануть сиротами (їх прибере очищувач). Фото, видалені між кроками, після відновлення стануть рядками без файлу (`404` на URL, Warning очищувача; адмін видаляє фото в CRM). Обидва випадки нешкідливі. Відновлення: `pg_restore`, потім розпакування tar у порожній том.
- Dev: `Media:RootPath` = `.data/media` у корені репозиторію (у `.gitignore`), задається через `scripts/dev/start-api.ps1` (env). `PublicBaseUrl = http://localhost:5000`.
- Моніторинг диска: runbook-перевірка `df` тома на staging. Квота tenant-а (§2.8) обмежує ріст.

### 2.12 Публічний DTO каталогу (адитивно, §33.5)

```ts
interface PublicProductImageDto { url: string; thumbUrl: string; width: number; height: number } // width/height — варіант url (1600)
interface PublicProductDto {
  // ...наявні поля V1 без змін
  imageUrl: string | null;          // thumbUrl першого фото (прев'ю картки), null = немає фото
  images: PublicProductImageDto[];  // уся галерея за sort_order; сервер завжди віддає масив
}
```
Клієнт трактує відсутнє поле або `null` як «немає фото» / `[]` (сервер до ADR-027). `MinAppVersion` не змінюється, версія контракту — `API-CONTRACT-PRODUCT-ORDERS-V1.1` (minor, адитивно). P1 читає фото **одним запитом** для всіх ≤ 500 товарів (`WHERE product_id = ANY(@ids) ORDER BY product_id, sort_order, created_at, id`) і обмежує кількість на товар значенням `MaxImagesPerProduct`. Якщо функцію вимкнено (`RootPath` відсутній), віддаються `imageUrl = null` і `images = []`.

### 2.13 Безпека й відповідність IDOR

| Загроза | Захист |
|---|---|
| IDOR у CRM (чужий товар/фото) | товар під RLS FORCE (чужий tenant → `404 product_not_found`); фото шукається за `id = imageId AND product_id = id` (фото іншого товару того ж tenant-а → `404 product_image_not_found`); композитний FK `(tenant_id, product_id)`; reorder приймає лише точну перестановку власного набору (`422 invalid_image_order`), чужі id не можуть потрапити в `UPDATE` |
| Перебір публічних URL | 128 біт CSPRNG, без tenant-id, без послідовних id, без лістингу тек (маршрут віддає тільки точне ім'я); однакова `404` для всього, чого немає; `public-media` rate limit |
| Path traversal / symlink | шлях з груп regex, перевірка префікса `GetFullPath`, відмова від symlink; ім'я від клієнта ніколи не потрапляє в шлях |
| Stored XSS / поліглот (HTML/SVG під виглядом картинки) | приймаються лише JPEG/PNG/WebP за сигнатурою; **повне перекодування** (віддається тільки вихід енкодера); фіксований `Content-Type: image/webp`, `nosniff`, CSP `sandbox` |
| Декомпресійна бомба / OOM | `Identify` до декодування, `MaxPixels`, ліміт сторін, `MaxFrames = 1`, ліміт алокатора, семафор паралелізму, ліміт тіла |
| Витік геолокації та даних пристрою | зняття EXIF/XMP/IPTC/ICC після `AutoOrient` |
| Заповнення диска | право `inventory.manage = all`, ліміти на товар і tenant, rate limit upload, очищувач сиріт |
| Крос-tenant через сховище | URL без tenant-а не дає доступу ні до чого, крім самого файлу; маркери tenant-а лежать поза маршрутом роздачі (`t/` не обслуговується) |
| Видалення чужих файлів очищувачем | per-tenant під RLS, перевірка видимості tenant-а, поріг 50% / 500, `dry-run` за замовчуванням на staging, grace 24 год |
| Вразливості бібліотеки | закріплена версія 3.1.x з останніми виправленнями; Dependabot / перевірка GitHub Advisories у security-review |
| SSRF | сервер не завантажує зображення за URL (лише multipart). Патерн зовнішнього `photo_url` майстра для товарів не використовується |
| Кеш після видалення | прийнято: `immutable` дозволяє клієнтам показувати кешоване фото, поки воно в їхньому кеші; сервер одразу віддає `404` |

Логи (без ключів сховища й імен файлів клієнта): `product_image_uploaded product={id} image={id} bytes={n} w={w} h={h} ms={t}`, `product_image_rejected product={id} code={code}`, `product_image_deleted product={id} image={id}`, `product_image_file_cleanup_failed image={id}` (Warning), `media_sweep tenant={id} mode={m} markers={n} candidates={n} deleted={n}`, `media_sweep_aborted reason={…}` (Error).

---

## 3. Consequences

**Positive**
- Фото товарів у CRM і мобільному каталозі без зовнішнього сервісу й без нових секретів.
- Публічна роздача не торкається БД і RLS: нових політик, ролей і definer-функцій немає, пул з'єднань не навантажується.
- Перекодування робить сервер, тож клієнт не отримує активного вмісту чи метаданих завантажувача.
- `immutable` URL дешево кешуються клієнтами. Контракт V1 не ламається.
- Механізм (ключ, маркер, очищувач, роздача) можна повторно використати для інших медіа окремим ADR.

**Negative / accepted trade-offs**
- Диск одного сервера — єдина точка відмови для файлів. Бекап тома окремий від БД і не атомарний з ним (§2.11).
- API стає stateful за томом: горизонтальне масштабування API потребує спільного сховища (вже обмежено «рівно один інстанс», ADR-016).
- Обробка зображень споживає CPU й пам'ять процесу API (семафор, ліміти, рекомендовано 768m).
- Виданий URL працює й після призупинення tenant-а чи архівування товару, поки фото не видалено.
- Файли tenant-а, видаленого з БД, прибираються вручну.
- Ліцензія ImageSharp обмежена доходом організації (gate G2).
- Без CDN роздача йде через nginx → Kestrel. Для обсягів staging і раннього prod цього достатньо, CDN потребує окремого ADR.

---

## 4. Alternatives considered

- **S3 / MinIO / об'єктне сховище** — відхилено рішенням користувача (диск сервера). MinIO у compose додав би сервіс, секрети й бекап без вигоди для одного сервера.
- **Зберігати байти в PostgreSQL (`bytea`)** — відхилено: роздув дампу й WAL, навантаження на пул при кожному показі каталогу, а публічне читання без tenant-а однаково потребувало б обходу RLS.
- **Публічна роздача через БД з вузькою політикою / SECURITY DEFINER (`beauty_public_product_image(key)`)** — відхилено (§2.3): нова поверхня RLS і запит до БД на кожну картинку без додаткового захисту. Ключ на 128 біт уже є межею доступу.
- **nginx віддає файли з тома напряму (`alias`)** — відхилено [А]: системний nginx спільного сервера мав би читати `/var/lib/docker/volumes/…` (права root, зв'язок з внутрішньою структурою Docker), а regex-валідацію й заголовки довелося б дублювати в конфігурації nginx. Може повернутися разом з CDN / `proxy_cache`.
- **Tenant-id або id товару в URL чи ключі** — відхилено рішенням користувача (розкриває структуру, дозволяє перебір у межах tenant-а).
- **Зовнішній https-URL, як `photo_url` майстра** — відхилено: hotlinking сторонніх хостів (трекінг IP клієнтів застосунку, mixed content, зникнення зображень), немає контролю над розміром і вмістом, суперечить рішенню «завантажує CRM».
- **Підписані URL зі строком дії** — відхилено рішенням користувача (публічні URL). Також ламають `immutable`-кеш.
- **Зберігати оригінал** — відхилено: містить EXIF/GPS, займає місце, а потреби в ньому немає (CRM не редагує фото).
- **JPEG як вихідний формат** або **JPEG + WebP** — відхилено [А]: WebP підтримують усі цільові клієнти. JPEG без прозорості й важчий, а другий формат подвоює диск без вигоди.
- **Magick.NET (ImageMagick)** — відхилено: велика нативна поверхня атак, історія CVE в декодерах, важкий образ. **NetVips** — швидкий, але нативний libvips. **SkiaSharp** — лишається резервним вибором (§2.5). **ImageSharp 2.x** — без підтримки.
- **Очищувач у воркері** — відхилено (§2.9): статичний список tenant-ів веде до масового видалення файлів неврахованих tenant-ів, а ще потрібні спільний том і узгодження UID.
- **Спершу транзакція, потім файл** — відхилено: між commit і записом клієнт бачив би рядок без файлу (`404`). Збій запису після commit лишив би «биті» рядки, які довелося б компенсувати ще однією транзакцією.
- **Видалення файлу в тій самій дії до commit** — відхилено (§2.7): rollback лишив би рядок без файлу.
- **Окремий ключ права `inventory.media`** — відхилено: потрібні backfill і `catalogVersion` (ADR-023 §2.11), а редагування каталогу вже покриває `inventory.manage = all`.
- **Upload фото з мобільного застосунку** — відхилено рішенням користувача.

---

## 5. Ризики

| # | Ризик | Пом'якшення |
|---|---|---|
| R1 | OOM API під час обробки великого фото | `Identify` + `MaxPixels`, ліміт алокатора, семафор 1, `503 media_busy`, рекомендація 768m, зменшення на клієнті |
| R2 | Очищувач видаляє живі файли | per-tenant RLS + перевірка видимості tenant-а, grace 24 год, пороги, `dry-run` спершу, тести на «порожній live-набір не видаляє» |
| R3 | Неузгоджений бекап (БД і том) | порядок «БД → том», обидва випадки нешкідливі, Warning `media_missing_files` |
| R4 | Том не змонтовано або без прав на staging | probe запису при старті → помилка старту (не мовчазний `500` на upload) |
| R5 | nginx 2m ріже upload (`413` від nginx до API) | окремий `location` з `client_max_body_size 11m`; frontend зменшує фото й обробляє `413` за статусом |
| R6 | Вразливість декодера | лише 3 формати, закріплена версія з оновленнями, процес не root, `read_only` + `cap_drop` |
| R7 | Ліцензія ImageSharp не виконується | gate G2 до TASK-912; порт `IImageProcessor` дозволяє замінити бібліотеку на SkiaSharp без зміни контракту |
| R8 | Basic auth staging блокує завантаження фото в мобільному застосунку | Q1 |

---

## 6. Human gates

1. **G1 — Прийняття ADR-027 і схеми §2.6** (нова таблиця з RLS, CASCADE, глобальний UNIQUE ключа, GRANT) — до старту TASK-911.
2. **G2 — Ліцензія ImageSharp** (Six Labors Split License, поріг доходу 1 млн доларів) — підтвердження або вибір SkiaSharp до старту TASK-912.
3. **G3 — Міграція `beauty_product_images` на staging/prod**: бекап (`pg_dump`), перегляд SQL, застосування за runbook. `Down` відмовляє за наявності рядків.
4. **G4 — Деплой інфраструктури staging/prod**: новий том `beauty_*_media` і `chown`, монтування в `api`, змінні `Media__*` в `api.env`, нове правило nginx для `client_max_body_size` (новий `location` у власному файлі `beauty-staging`, `nginx -t`, reload), рекомендоване підвищення `mem_limit` api, рішення Q1 щодо basic auth для `/api/public/media/`.
5. **G5 — Зміна процедури бекапу** (tar тома після `pg_dump`) і перевірка відновлення на копії.
6. **G6 — `Media:SweepMode=delete`** на staging/prod: лише після щонайменше одного запуску в `dry-run` без неочікуваних кандидатів (видалення файлів незворотне).
7. **G7 — Мердж `integrate/*`** після security-review (TASK-916).

---

## 7. Відкриті питання

| # | Питання | Дефолт у контракті |
|---|---|---|
| Q1 | Чи виводити `/api/public/media/` з-під basic auth staging (ADR-016 Q5)? Зараз `/api/public/*` теж під basic auth | Наслідувати поточну політику `/api/public/` (без змін ADR-016). Якщо мобільні тестери не передають basic auth у завантажувач зображень, людина додає `location ^~ /api/public/media/ { auth_basic off; … }` |
| Q2 | Ліміти: 8 фото на товар, 10 МБ, 5000 на tenant | Значення §2.8 (конфігуровані) |
| Q3 | Чи потрібне фото в рядках замовлення (`PublicProductOrderLineDto.imageUrl`) | Ні (поза ADR-027; можна додати адитивно пізніше) |
| Q4 | Чи переносити фото майстрів (`photo_url`) у це сховище | Ні, окремий ADR |
| Q5 | Якість/розміри варіантів (400/1600, q80) | Як у §2.5; змінюються новими суфіксами |

---

## 8. План міграції й розгортання

1. G1 → TASK-911: міграція (dev), GRANT у двох файлах, `worker/src/testing/schema.sql` (без BOM, LF), тести БД.
2. G2 → TASK-912: `IImageProcessor` (ImageSharp), `IMediaStorage` (файлова реалізація, маркери, tmp → rename), `MediaOptions` з валідацією старту, CRM-маршрути, публічний маршрут роздачі, поля DTO (§32.6, §33.5), `MediaOrphanSweeper`. Якщо `Media:RootPath` немає в `appsettings*`, функція вимкнена. Dev вмикає її через `scripts/dev/`.
3. TASK-913 (frontend CRM): галерея в картці товару (upload зі зменшенням у браузері, видалення з підтвердженням, перетягування для порядку), прев'ю в списку. Порядок: mock → http.
4. Мобільна команда: показ `imageUrl`/`images` за §33.22 (адитивно, без підвищення `MinAppVersion`).
5. QA (TASK-914) → security-review (TASK-916) → docs (TASK-917) → G3/G4/G5 на staging (93.127.143.98 працює з іншої гілки, тож спершу інтеграція) → `dry-run` → G6.
6. Prod: міграція адитивна. Поки том і `Media__*` не налаштовано (G4), функція вимкнена.

Відкат коду: прибрати `Media__RootPath` (функція вимикається, DTO віддають `null`/`[]`, файли й рядки лишаються). Відкат схеми — лише без рядків.

---

## 9. Scope of truth

Після `accepted` ADR-027 авторитетний для: сховища медіа товарів (диск, том, розкладка `p/` і `t/`); формату й генерації ключа; публічної роздачі без БД (regex, заголовки, кеш); формату URL; конвеєра обробки (формати, ліміти, ресайз, WebP, зняття метаданих) і вибору бібліотеки за портом `IImageProcessor`; порядку «файл, потім транзакція» і видалення після commit; моделі очищувача (API, per-tenant, маркери, запобіжники); прав CRM на фото; бекапу тома.

Не авторитетний для: RLS (ADR-002), прав (ADR-023), каталогу й видимості товарів (ADR-025/026), хостингу загалом (ADR-016). Поля, коди, ліміти й тести визначає контракт §33.22.

Новий ADR потрібен для: CDN або об'єктного сховища, підписаних URL, upload з мобільного застосунку, інших типів медіа (майстри, послуги, заклади), зміни вихідного формату без нових суфіксів, модерації.

---

## 10. Задачі (орієнтовні ID; PM підтверджує)

| Задача | Агент | Обсяг | Залежить |
|---|---|---|---|
| TASK-910 | project-architect | ADR-027 + §33.22 | → G1, G2 |
| TASK-911 | database-engineer (`beauty-schema`) | міграція, EF-сутність `ProductImage`, RLS FORCE, deferrable UNIQUE, GRANT у двох файлах, `schema.sql`, тести §33.22.9 п.1 | 910 + G1 |
| TASK-912 | backend-developer (`beauty-booking-api`) | порти `IImageProcessor`/`IMediaStorage`, ImageSharp, `MediaOptions`, CRM-маршрути, роздача, DTO, `MediaOrphanSweeper`, rate limit `public-media`, тести п.2–6 | 911 + G2 |
| TASK-913 | frontend-developer (`beauty-admin-ui`) | галерея CRM, зменшення в браузері, тести п.7 | 910 (mock); 912 (http) |
| TASK-914 | qa-engineer (`beauty-qa`) | таблиця відповідності ADR-027 → тести; ручний чекліст (EXIF/GPS знято, HEIC → 415, великий файл, порядок, видалення, кеш-заголовки) | 912, 913 |
| TASK-915 | devops-engineer | compose-том і монтування, `.env.api.example` (`Media__*`), nginx `location` для upload, бекап/відновлення тома в README і runbook, `scripts/dev/` | 912; застосування на сервері — G4/G5 |
| TASK-916 | security-reviewer (`beauty-security`) | роздача (traversal, nosniff, перебір), конвеєр (бомби, поліглоти, EXIF), IDOR, очищувач, ліцензія/версія бібліотеки; звіт у `docs/reviews/` | 914, 915 |
| TASK-917 | documentation-writer (`beauty-docs`) | `database.md`, runbook (том, бекап, sweep-режими, ручне прибирання файлів видаленого tenant-а), known-issues, статус ADR-027 | 916 |

DAG: 910 → [G1, G2] → 911 → 912 → {914, 915} → 916 → 917; 913 — mock після 910, http після 912; мобільний клієнт — від §33.22 (V1.1).

---

## 11. References

- `backend/BeautyCrm.Infrastructure/Data/Entities/BeautyInventoryEntities.cs` (`Product`), `Migrations/20261120122321_beauty_product_orders.cs` (RLS-патерн, Down-запобіжник)
- `backend/BeautyCrm.Application/Features/BeautyProductOrders/ProductOrderModels.cs` (`PublicProductCatalogDto`), `ProductOrderService.CatalogAsync`, `backend/BeautyCrm.Api/Controllers/PublicProductOrdersController.cs`, `InventoryController.cs` (права каталогу)
- `backend/BeautyCrm.Application/Features/BeautyStaff/StaffService.cs` (`photo_url` як зовнішній URL), `EfPublicBookingStore.cs`
- `backend/BeautyCrm.Api/PublicBooking/PublicBookingSetup.cs` (політики rate limit), `backend/Dockerfile` (UID 1654)
- `deploy/staging/docker-compose.staging.yml`, `deploy/staging/README.md` §5 (бекап), §6.3 (nginx), `deploy/staging/db/grants.sql`, `scripts/dev/setup-db.ps1`
- `worker/src/db/tenants.ts` (статичний список tenant-ів, причина відмови від очищувача у воркері)
- ADR-002, ADR-016 (Q5), ADR-022, ADR-023, ADR-025, ADR-026; контракт §29, §32.6, §33.5
