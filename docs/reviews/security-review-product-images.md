# Security review: зображення товарів (ADR-027, контракт §33.22)

Гілка `claude/youthful-herschel-fd19fc`, коміти 485932e..1c14a5d. Рецензент: security-reviewer (незалежний), 2026-11-21.
Обмеження: рев'ю статичне (без запуску тестів і навантаження); shell був недоступний.

**Вердикт:** blockers немає. 2 major, 11 minor. M1 виправити до мерджу (G7), M2 — до G6 (`SweepMode=delete`).

| ID | Severity | Тема |
|---|---|---|
| M1 | major | Пам'ять API при upload: тіло буферизується до семафора без глобального ліміту; найгірше декодування ≈400 МБ при `mem_limit` 768m → OOM-kill спільного API |
| M2 | major | Очищувач: гілка «варіанти без маркера» не fail-safe; без теки `t/` видаляє живі файли |
| m1 | minor | Немає таймауту обробки; `cpus: 1.0` на весь API |
| m2 | minor | `CommitAsync(ct)`: скасування після commit видаляє файли живого рядка |
| m3 | minor | Шлях зі storage_key у логах через необроблений IOException |
| m4 | minor | Тест symlink не перевіряє symlink; тест «бомби» не бачить unmanaged-пам'ять |
| m5 | minor | М'яка квота tenant-а (гонка між товарами) — прийнято |
| m6 | minor | `public-media` 600/хв на IP (CGNAT, каталог до 500 товарів); `PublicPermitPerMinute` без валідації |
| m7 | info | `$` у regex пропускає кінцевий `\n` → `\z` |
| m8 | minor | Mobile приймає будь-який рядок як URI зображення |
| m9 | minor | Frontend: немає зменшення в браузері (за ADR); CSP `img-src 'self'` |
| m10 | minor | Deploy: `alpine:3` без digest; tar медіа без шифрування |
| m11 | doc | `AllocationLimitMegabytes` в ADR описано як ліміт пам'яті, це ліміт однієї алокації |

## M1. DoS через пам'ять у конвеєрі upload
Де: `InventoryController.cs` `ReadFilePartAsync` (копія в `MemoryStream` до семафора, до ~16 МБ на 10 МБ файл); `ImageSharpProcessor.cs` (`Image.LoadAsync` без generic-формату, `AutoOrient`); `MediaSetup.cs`; `docker-compose.staging.yml` (768m).
Шлях: акаунт з `inventory.manage=all`; до 20 паралельних запитів на користувача, глобального ліміту немає; 16-бітний PNG 25 Мп = `Rgba64` ≈ 200 МБ, поворот EXIF 90° тримає дві копії ≈ 400 МБ; великі буфери ImageSharp з unmanaged-пулу, тож cgroup-ліміт дає OOM-kill, не виняток. `AllocationLimitMegabytes=256` обмежує одну алокацію, не суму.
Рекомендації: глобальний ліміт одночасних upload на процес (2–3, QueueLimit=0 → 503 `media_busy`) ПЕРЕД читанням тіла; фіксований піксельний формат `Rgba32`; `TargetSize` для JPEG; обмеження пулу; перевірка бюджету пам'яті при старті; зменшення в браузері.

## M2. Очищувач видаляє живі файли, якщо `t/` відсутній або неповний
Де: `MediaOrphanSweeper.cs` (побудова `live` лише з tenant-ів у `t/*`; stray = валідне ім'я без ключа в маркерах/live і старше 24 год; лише загальний ліміт 500), `FileProductImageStorage.cs` (порожній набір без помилки при відсутній `t/`).
Наслідок: часткове відновлення tar або ручне прибирання → усі варіанти tenant-а «stray» → незворотне видалення (для ≤500 варіантів — повне).
Рекомендації: пропускати фазу strays, якщо `t/` відсутній або маркерів 0 при наявних варіантах або strays > X% валідних; Error-лог `media_sweep_aborted`; довший grace (7 днів); тест на відсутній `t/`.

## Minor (скорочено)
- m1: таймаут обробки ~20 с (`CancellationTokenSource`, пов'язаний із `ct`).
- m2: `CommitAsync(CancellationToken.None)` в Create/Delete/Reorder.
- m3: обгортати IOException у доменний виняток без шляху (§33.22.8).
- m4: symlink-тест створювати в `p/{key[..2]}/` реального префікса + кейс symlink-теки; тест бомби — перевіряти час, не `GC.GetTotalAllocatedBytes`.
- m6: `Range(...)` на `Media:PublicPermitPerMinute`; обдумати вищий ліміт.
- m7: `\z` замість `$` у regex імені файлу.
- m8: mobile `z.string().url()` + https (+ очікуваний origin).
- m9: зменшення в браузері до ≤2560 (ADR §2.5); CSP `img-src` додати `apiOrigin()` (не wildcard).
- m10: `alpine@sha256:…`; шифрувати tar медіа (`age`) як дампи БД.
- m11: уточнити ADR/контракт щодо `AllocationLimitMegabytes`.

## Перевірено без зауважень
Тип файлу за сигнатурою (GIF/SVG/HEIC/BMP/TIFF → 415); зберігається лише вихід WebP-енкодера; фіксований `image/webp`, `nosniff`, CSP `default-src 'none'; sandbox`, CORP, immutable; `Identify` до декодування + ліміти; зняття метаданих; path traversal (шлях з regex-груп, префікс `GetFullPath`, відмова symlink); ключі 128 біт CSPRNG, глобально UNIQUE; IDOR (RLS FORCE, `id AND product_id`, точна перестановка); права `inventory.manage=all` + матриця; multipart без model binding і з лімітами; публічна роздача без БД; очищувач по tenant-ах під RLS, `off` за замовчуванням; nginx/compose; ImageSharp 3.1.12 (без відомих вразливостей; додати `dotnet list package --vulnerable` у CI).
Зміна `apiFetch` для FormData у frontend коректна.
