# Деплой Beauty CRM: https://beauty.agrusystems.pp.ua

Docker-стек: `postgres` (16) -> `migrate` (одноразовий job) -> `api` (ASP.NET Core 8) + `worker` (Node 22) + `web` (Next.js), опційно `proxy` (nginx у контейнері).
TLS і reverse proxy на сервері — **хостовий nginx** (`nginx/beauty.conf`). `mobile/` у деплої не бере участі.

Стан перевірки: стек зібрано й запущено **локально** в Docker (HTTP, тестові секрети) — див. звіт розробника. Реальний сервер, TLS, certbot, зовнішні порти й SMTP **не перевірялися**.

## Файли

| Файл | Призначення |
|---|---|
| `docker/backend.Dockerfile` | цілі `api` (рантайм, користувач `app`) і `migrate` (psql + idempotent SQL зі всіх EF-міграцій) |
| `docker/worker.Dockerfile`, `docker/web.Dockerfile` | worker (користувач `node`), Next.js `next start` (збірка з кореня монорепо) |
| `docker-compose.prod.yml` | стек; `--profile proxy` додає контейнерний nginx (HTTP) |
| `db/00-init-roles.sh`, `db/init-roles.sql` | ролі `beautycrm_owner`, `beautycrm_app`, `beautycrm_catalog` + `GRANT beautycrm_catalog TO beautycrm_owner WITH INHERIT FALSE, SET TRUE` (від суперкористувача, лише при першій ініціалізації тому) |
| `db/run-migrate.sh`, `db/post-migrate.sql` | міграції під власником, потім ідемпотентні GRANT для runtime-ролі й перевірка `PUBLIC` EXECUTE |
| `nginx/beauty.conf`, `nginx/beauty-locations.inc` | хостовий nginx (HTTP->HTTPS, TLS-шаблон, проксі) |
| `nginx/compose-proxy.conf` | контейнерний nginx для `--profile proxy` |
| `nginx/well-known/*` | **заглушки** `apple-app-site-association`, `assetlinks.json` |
| `.env.example` | усі змінні; реальний `deploy/.env` не комітити |
| `../.dockerignore` | контекст збірки = корінь репозиторію |

## 1. Передумови на сервері
Docker Engine + Compose v2, nginx (хостовий, `http2 on;` потребує nginx >= 1.25.1, інакше замініть на `listen 443 ssl http2;`), certbot, DNS A-запис `beauty.agrusystems.pp.ua` -> 93.127.143.98, `git`.

## 2. Секрети (генеруються на сервері, ніде не зберігаються в репозиторії)
```bash
cd deploy && cp .env.example .env && chmod 600 .env
# паролі БД: hex (безпечні для рядка підключення)
for v in POSTGRES_PASSWORD BEAUTY_DB_OWNER_PASSWORD BEAUTY_DB_APP_PASSWORD; do sed -i "s|^$v=.*|$v=$(openssl rand -hex 24)|" .env; done
sed -i "s|^Auth__JwtSigningKey=.*|Auth__JwtSigningKey=$(openssl rand -base64 48 | tr -d '\n')|" .env
sed -i "s|^Auth__PlatformKey=.*|Auth__PlatformKey=$(openssl rand -base64 36 | tr -d '\n')|" .env
sed -i "s|^Channels__EncryptionKey=.*|Channels__EncryptionKey=$(openssl rand -base64 32 | tr -d '\n')|" .env
```
Перегляньте решту `.env`: `Beauty__AllowedOrigins`, `NEXT_PUBLIC_API_URL`, `BEAUTY_NET_SUBNET` (не має перетинатися з наявними docker-мережами: `docker network inspect $(docker network ls -q) | grep Subnet`), порти `WEB_HOST_PORT`/`API_HOST_PORT` (мають бути вільні на 127.0.0.1 хоста й збігатися з upstream у `nginx/beauty.conf`).
**Збережіть `Channels__EncryptionKey` і `Auth__*` у менеджері паролів:** при втраті ключа каналів збережені токени каналів не розшифруються.

## 3. Порядок запуску
```bash
cd deploy
C="docker compose -f docker-compose.prod.yml --env-file .env"
$C build                       # образи api, migrate, worker, web (web вбудовує NEXT_PUBLIC_*)
$C up -d postgres              # на порожньому томі виконується init-roles (ролі + передача БД власнику)
$C run --rm migrate            # EF idempotent SQL під beautycrm_owner, потім post-migrate.sql (GRANT, перевірка PUBLIC EXECUTE)
$C up -d api                   # DbRoleGuard перевіряє ролі; healthcheck = resolve неіснуючого коду -> 404
$C up -d worker web
$C ps
```
`$C up -d` без кроків робить те саме в правильному порядку (залежності `service_completed_successfully` / `service_healthy`).
Після **кожного** оновлення коду з новими міграціями: `$C build && $C run --rm migrate && $C up -d`.

## 4. Nginx (хостовий)
```bash
sudo install -m 0644 nginx/beauty.conf /etc/nginx/conf.d/beauty.conf
sudo install -d /etc/nginx/snippets && sudo install -m 0644 nginx/beauty-locations.inc /etc/nginx/snippets/
# well-known: спершу ЗАМІНІТЬ плейсхолдери (team id, bundle id, package, SHA-256) у nginx/well-known/*
sudo install -d /var/www/beauty-wellknown && sudo install -m 0644 nginx/well-known/* /var/www/beauty-wellknown/
sudo install -d /var/www/certbot
```
Спершу отримайте сертифікат (поки `ssl_certificate` не існує, `nginx -t` падає — закоментуйте TLS-сервер або використайте `certbot certonly --standalone`/`--webroot` до встановлення конфігу): `sudo certbot certonly --webroot -w /var/www/certbot -d beauty.agrusystems.pp.ua`. Далі `sudo nginx -t && sudo systemctl reload nginx`. Конфіг не чіпає чужі `server_name`.

Маршрутизація: `/api/session/*` -> web (BFF входу), `/api/*` -> api, `/api/platform/*` -> **404 назовні** (операторське API лише з сервера), решта -> web. nginx **перезаписує** `X-Forwarded-For` значенням `$remote_addr`, `Host`/`X-Forwarded-Host` = `$http_host` (з портом: BFF перевіряє Origin).

### Довірені проксі (rate limit по IP)
API враховує `X-Forwarded-For` лише від адрес із `Beauty__TrustedProxies`. Host nginx підключається до опублікованого порту `127.0.0.1:15080`, і всередині контейнера це виглядає як gateway docker-мережі, тож `Beauty__TrustedProxies` = `BEAUTY_NET_SUBNET` (за замовчуванням `172.29.88.0/24`). Без цього всі клієнти матимуть IP проксі й спільний ліміт. Перевірено локально: з довіреного джерела `X-Forwarded-For` визначає ключ ліміту, а підроблений клієнтом заголовок через nginx не діє. Якщо перед nginx стоїть ще один проксі/CDN — потрібне окреме рішення (`real_ip`), інакше всі клієнти матимуть IP цього проксі.

## 5. Перший tenant (публічної реєстрації немає)
Операторське API закрите в nginx, тому викликайте його з сервера на loopback-порт API:
```bash
set -a; . ./.env; set +a
cat > /tmp/tenant.json <<'JSON'
{"name":"Beauty Lab","slug":"beauty-lab","modules":["beauty_booking","beauty_catalog","beauty_clients","beauty_analytics","beauty_channels","beauty_ai"],
 "owner":{"email":"owner@example.com","fullName":"Owner","password":"<мін. 12 символів>"}}
JSON
curl -sS -X POST http://127.0.0.1:${API_HOST_PORT:-15080}/api/platform/tenants -H "X-Platform-Key: $Auth__PlatformKey" -H 'Content-Type: application/json' --data-binary @/tmp/tenant.json; shred -u /tmp/tenant.json
```
Відповідь містить `tenantId`: додайте його в `WORKER_TENANT_IDS` у `.env` (через кому) і `$C up -d worker` — worker читає перелік tenant-ів **лише зі змінної** (автоматичного переліку немає), без цього нагадування/розсилки не йдуть. Slug для публічного запису в `NEXT_PUBLIC_TENANT_SLUG` потребує перезбірки `web` (`$C up -d --build web`).
Далі власник входить на `/login`, створює заклад (`POST /api/beauty/locations`), послуги (`POST /api/beauty/services`, ціни `PUT /api/beauty/services/{id}/prices` з тілом-**об'єктом** `{"locationId":null,"price":500}`), працівників.

## 6. Перевірки
```bash
B=https://beauty.agrusystems.pp.ua      # (+ :порт, якщо зовнішній порт не 443)
curl -si $B/api/public/resolve/XXXXXXXX            # 404 {"code":"not_found"}, НЕ 500
curl -sI $B/login                                  # 200, CSP/HSTS
curl -s -o /dev/null -w '%{http_code}\n' $B/api/platform/tenants   # 404 (закрито nginx)
curl -si $B/.well-known/assetlinks.json            # 200 application/json, без редиректу
$C ps ; $C logs --tail=50 api worker
docker exec $(docker compose -f docker-compose.prod.yml ps -q postgres) psql -U postgres -d beautycrm -Atc "select count(*) from \"__EFMigrationsHistory\""   # 14
```
Swagger і `/health` у Production **не існують** (swagger лише Development; health-ендпоінта в API немає) — тому healthcheck API використовує resolve неіснуючого коду.
Публічний запис: див. `.claude/docs/runbook.md` §5 і `docs/api.md` (`Idempotency-Key` 16–128 символів).

## 7. Бекап і відкат
```bash
mkdir -p ~/backups && docker exec $(docker compose -f docker-compose.prod.yml ps -q postgres) pg_dump -U postgres -Fc beautycrm > ~/backups/beautycrm-$(date +%F-%H%M).dump
# відновлення в ПОРОЖНЮ БД (спершу $C stop api worker web):
docker exec -i <postgres-container> pg_restore -U postgres -d beautycrm --clean --if-exists --no-owner < ~/backups/<file>.dump
```
Робіть дамп **перед кожним** `run --rm migrate`. Відкат застосунку: перезбірка попереднього коміту (`BEAUTY_IMAGE_TAG`); відкат схеми — лише з дампа (міграції в скрипті йдуть вперед; Down не використовується). Після restore виконайте `$C run --rm migrate` (відновить GRANT). Том БД (`<project>_pgdata`) не видаляйте (`down -v` знищує дані).

## 8. Чекліст перед запуском
- [ ] DNS, відкриті/змаплені порти, `listen` у `beauty.conf` і `AllowedOrigins`/`NEXT_PUBLIC_API_URL` узгоджені з реальним зовнішнім портом
- [ ] сертифікат certbot отримано, автооновлення працює, `nginx -t` OK
- [ ] `.env` має права 600, усі `__GENERATE__` замінені, секрети збережено поза сервером
- [ ] `Channels__UseMocks=false`, `Beauty__TrustedProxies` = підмережа compose-мережі
- [ ] `PublicBooking__Protection__Web/Ios/Android` однакові (інакше Production не стартує)
- [ ] `migrate` завершився `[migrate] done`, у `__EFMigrationsHistory` 14 записів, API `healthy`
- [ ] tenant створено, `WORKER_TENANT_IDS` оновлено, worker бачить tenant
- [ ] `/api/platform/*` зовні = 404; порти 13000/15080/postgres назовні недоступні (`ss -tlnp`, перевірка ззовні)
- [ ] перший `pg_dump` зроблено, відновлення перевірено на копії
- [ ] well-known файли заповнено реальними значеннями (або не публікуються, якщо мобільного застосунку ще немає)

## Відкриті питання і ризики (чесно)
1. **Домен/порти/TLS.** Зовнішні порти за провайдером невідомі. Редирект HTTP->HTTPS у `beauty.conf` без порту; `AllowedOrigins`, `NEXT_PUBLIC_API_URL` і CSP залежать від порту. TLS-секція — шаблон (шляхи certbot для цього домену).
2. **SMTP.** Провайдер не обрано: `EMAIL_MODE=mock` — листи (підтвердження, 24h-нагадування) **не надсилаються**. Для `smtp` потрібні `SMTP_HOST/PORT/USER/PASSWORD/SECURE`, `EMAIL_FROM`.
3. **Provisioning ролі каталогу в проді.** Роль `beautycrm_catalog` створює `init-roles.sql` лише на **порожньому** томі. Для наявного кластера виконайте його вручну суперкористувачем: `psql -U postgres -v db=beautycrm -v owner_pw=... -v app_pw=... -f db/init-roles.sql` (паролі існуючих ролей не змінюються). Без ролі/членства міграція ADR-011 падає. Якщо PostgreSQL зовнішній/керований — потрібні CREATEROLE або створення ролі адміністратором.
4. **Знімок EF-моделі не синхронізований із міграціями** (див. коментарі в міграціях 12–14): не виконуйте `migrations add` без виправлення знімка; міграції накочуються SQL-скриптом, згенерованим під час збірки образу `migrate`. Ручні SQL-міграції входять у скрипт (перевірено: 14 з 14).
5. **Worker не бачить нових tenant-ів автоматично** (`WORKER_TENANT_IDS`, потрібен рестарт) — обмеження коду (TASK-686).
6. **Web fallback `/l/{code}` не реалізований** (ADR-010/011): universal link відкриє 404 у браузері без застосунку; AASA/assetlinks — заглушки.
7. **Rate limit у пам'яті процесу** (один екземпляр API; не масштабуйте `api` горизонтально без рішення). Лічильник блокувань входу теж у пам'яті.
8. **Healthcheck API шумить попередженням** `Failed to determine the https port for redirect` у логах (запит healthcheck йде по HTTP без проксі). Безпечно; усунути можна `ASPNETCORE_HTTPS_PORT` або `/health` у коді.
9. Резервне копіювання поки ручне (cron/offsite не налаштовано). Образи збираються на сервері (реєстру немає; `staging-images.yml` у цьому репозиторії публікує образи за іншим контуром — не використовується).
10. `docs/api.md` описує `PUT /beauty/services/{id}/prices` як масив, фактично приймається об'єкт `{locationId, price}` (виправити документацію окремою задачею).
