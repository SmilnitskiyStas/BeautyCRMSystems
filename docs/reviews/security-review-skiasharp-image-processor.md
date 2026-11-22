# Security review: заміна ImageSharp на SkiaSharp (ADR-027 §12.4)

Дата: 2026-11-22. Рецензент: security-reviewer (незалежний субагент, статичне рев'ю). Обсяг: `SkiaSharpProcessor.cs` (новий), видалений `ImageSharpProcessor.cs`, `backend/Dockerfile`, `.github/workflows/staging-images.yml`, `ProductImageProcessorTests.cs`.

**Вердикт:** blockers немає. 2 major, 4 minor, 2 doc/info. Оцінки пам'яті й часу зроблено за реалізацією libjpeg-turbo/Skia, їх треба виміряти.

| ID | Severity | Тема | Стан |
|---|---|---|---|
| M1 | major | Таймаут 20 с не обмежує CPU і утримання слота: нативне декодування не переривається (progressive JPEG з тисячами сканів → глобальний 503 і голодування CPU) | **виправлено частково:** ліміт `MaxJpegScans = 100` керованим проходом маркерами до декодування (`422 image_decode_failed`) + тест. Лог/cooldown перевищень і ізоляція в окремому процесі — відкрито (ADR §12.3) |
| M2 | major | Бюджет 8 Б/піксель недійсний для буферів коефіцієнтів progressive/CMYK JPEG та interlaced PNG (оцінка до ~370 МБ при 25 Мп); `SKCodec` жив до кінця обробки | **виправлено частково:** кодек і буфери декодера звільняються одразу після `GetPixels`, до ресайзу. Вимірювання піку й корекція множника/`MaxPixels` — відкрито (ADR §12.3) |
| m1 | minor | Нативна поверхня атак libSkia (libjpeg-turbo, libpng, libwebp, zlib): падіння/RCE у процесі API; `dotnet list package --vulnerable` бачить лише advisory NuGet-пакета | прийнято як залишковий ризик (ADR §12.4); рекомендовано Dependabot для `SkiaSharp*` і SLA оновлень — відкрито |
| m2 | minor | Робастність семафора/CTS: `CreateLinkedTokenSource` поза `try`, `Release` після `Dispose` singleton-а, гонка `Cancel`/`Dispose` | **виправлено:** CTS у `using` в `ProcessAsync`, continuation лише звільняє слот і ловить `ObjectDisposedException` |
| m3 | minor | Зайві копії тіла (керована + 2 нативні) | **виправлено:** `MemoryStream.TryGetBuffer` без керованої копії, одна `SKData` на обидва кодеки |
| m4 | minor | Тести не перевіряли утримання слота після таймауту, скасування клієнтом, обірвані JPEG/WebP, JPEG-бомбу | **виправлено:** тести додано (хук `BeforeDecode`) |
| m5 | doc | Застарілі коментар у `MediaSetup.cs` і формулювання ADR | **виправлено** |
| m6 | info | Секрети: збірка більше не працює з секретами | дії людини: видалити секрет `SIXLABORS_LICENSE_KEY`; старий Dockerfile гарантував, що файл `sixlabors.lic` не потрапляє в publish (`find`), але чи вбудовував ImageSharp ключ у збірку, не перевірено; якщо так, ключ є в старих образах `beautycrm-api:sha-*` і GHA-кеші — тоді відкликати ключ у Six Labors |

## Перевірено без зауважень
- Тип за сигнатурою + `SKCodec.EncodedFormat` лише JPEG/PNG/WebP; GIF/SVG/HEIC/текст/порожнє → 415.
- Розміри й мегапікселі з заголовка до декодування пікселів; PNG- і JPEG-бомби відхиляються.
- Кадр 0 (`SKCodecOptions(0)`), анімований WebP → один кадр; будь-який результат, відмінний від `Success`, → 422.
- Матриці всіх 8 EXIF-орієнтацій коректні, поворот після зменшення.
- Вихід без EXIF/XMP/ICC (`WithColorSpace(null)`), WebP lossy q80, 1600/400 без збільшення, фіксований `Rgba8888`.
- Логіка слота: немає подвійного звільнення й витоку на шляхах успіху, таймауту, скасування й помилки; винятки фонової задачі спостерігаються.
- `Dockerfile` перевіряє наявність `libSkiaSharp.so`; бібліотека потребує лише glibc (перевірено `ldd` в `aspnet:8.0`), `read_only` сумісний.
