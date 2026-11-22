# Staging Beauty CRM: інструкція для людини

Рішення і причини: `.claude/docs/adr/ADR-016-staging-hosting-and-ci-cd.md` (§1.4 факти про сервер, §2.1–§2.3, §2.11, §7). Це лише **staging**; прод заблоковано юридичними gate.
У репозиторії лежать шаблони без секретів. Тут не зберігаються і не мають зберігатися IP, порти SSH, паролі, ключі чи імена користувачів сервера.

> **УВАГА: на цьому сервері працює продакшн інших проєктів.**
> - Кожен крок на сервері виконує або явно схвалює людина. Агенти й CI на сервер не заходять; автоматичного деплою через SSH немає.
> - Перед кожним `up`, `reload` чи зміною мереж/портів виконайте `ss -ltn` і звірте з `.env.compose`.
> - Не запускайте глобальні `docker system prune`, `docker network prune`, `docker volume prune`, масовий `docker rm`.
> - Не чіпайте чужі контейнери, мережі, томи, БД, Redis і системний nginx (чужі server-блоки, сертифікати, `certbot` для чужих доменів).
> - Кожна дія створює або змінює лише ресурси з префіксом `beauty_staging` або новий окремий файл nginx.

## 0. Що розгортається

| Ресурс | Ім'я | Примітка |
|---|---|---|
| Compose-проєкт | `beauty_staging` | `docker-compose.staging.yml` |
| Контейнери | `beauty_staging_db`, `_api`, `_worker`, `_web` | по одному (не масштабувати) |
| Мережі | `beauty_staging` (вихід в інтернет), `beauty_staging_db` (internal, без виходу) | БД лише в internal-мережі |
| Том | `beauty_staging_pgdata` | дані PostgreSQL 16 |
| Адреса web | статична `BEAUTY_WEB_IP` у `beauty_staging` (F-1) | входить у `Beauty__TrustedProxies` разом зі шлюзом |
| Порти на хості | `127.0.0.1:${BEAUTY_API_PORT}` -> api, `127.0.0.1:${BEAUTY_WEB_PORT}` -> web | лише loopback; порт БД не публікується; redis немає |

Ліміти (mem/cpu): db 512m/1.0, api 512m/1.0, worker 256m/0.5, web 384m/0.5, разом близько 1.7 ГБ (знижено через малий запас пам'яті спільного сервера, ADR-016); у setup-server.sh є крок створення swap. Перевірте запас сервера (`free -h`, `uptime`) і за потреби змініть.
`restart: unless-stopped` для всіх. Журнали Docker обмежено (10 МБ x 5).

## 1. Підготовка (нічого не змінює)

1. Docker Engine і Compose v2 уже є: `docker compose version`.
2. **Вільні порти.** Звірте `ss -ltn` (і `docker ps --format '{{.Names}} {{.Ports}}'`). У шаблоні стоять **5200** (api) і **3400** (web). Якщо одне з них зайняте, змініть значення в `.env.compose`.
   Зайняті за ADR-016 §1.4 (станом на 2026-11-02; стан змінюється, завжди звіряйтесь із `ss -ltn`): 80, 1883, 1884, 3000, 3001, 3010, 3100, 3200, 3300, 5100, 5110, 5432–5435, 6379–6381, 8001, 8443, 10048. Порт SSH і зовнішні порти провайдера не торкаються; не використовуйте 5000 (dev-API).
3. **Вільна підмережа Docker.** Перегляньте наявні: `docker network ls -q | xargs docker network inspect --format '{{.Name}} {{range .IPAM.Config}}{{.Subnet}} {{end}}'`. Виберіть `/24`, що не перетинається (шаблон: `172.29.200.0/24`, шлюз `172.29.200.1`), і пропишіть у `.env.compose` (`BEAUTY_SUBNET`, `BEAUTY_GATEWAY`) та в `Beauty__TrustedProxies` в `api.env` (= шлюз **і** адреса web). Додатково задайте `BEAUTY_WEB_IP` у `.env.compose`: вільна статична адреса `web` у тій самій підмережі (шаблон `172.29.200.10`; не шлюз, не збігається з наявними контейнерами).
4. **Стан мережі (лише читання).** Провайдер: зовнішній 443 направляє на внутрішній 8443 системного nginx; зовнішній 80 відкритий; зовнішній 8443 закритий (очікувано). Публічна адреса: `https://beauty.<DOMAIN>` **без порту**. Перевірка nginx: `sudo nginx -T 2>/dev/null | grep -nE 'listen|server_name'`.
5. Каталог секретів **поза клоном репозиторію**: `sudo install -d -m 700 /opt/beauty_staging/secrets` (шлях можна змінити через `SECRETS_DIR`).
6. Клонувати репозиторій (лише для `deploy/staging/`) або скопіювати цей каталог. Поставити зі шаблонів `.env.compose` і `*.env`:
   ```bash
   cd deploy/staging
   cp .env.compose.example .env.compose
   S=/opt/beauty_staging/secrets
   cp .env.db.example $S/db.env; cp .env.api.example $S/api.env; cp .env.worker.example $S/worker.env
   cp .env.migrator.example $S/migrator.env
   cp .env.web.example $S/web.env   # опційно: universal links (APPLE_TEAM_ID, ANDROID_*), див. runbook
   chmod 600 $S/*.env .env.compose
   ```

## 2. Секрети (значення не друкуйте і не вставляйте в чат)

Генеруйте на сервері й записуйте прямо у файли (редактор, або `printf ... >> файл` з `$(openssl rand ...)`). Значення відрізняються від dev і будь-яких інших середовищ.

| Змінна | Команда генерації | Файл(и) |
|---|---|---|
| `POSTGRES_PASSWORD`, `DB_OWNER_PASSWORD`, `DB_APP_PASSWORD`, `DB_WORKER_PASSWORD` | `openssl rand -hex 24` (кожне окремо) | `db.env`; app у `api.env`, worker у `worker.env`, owner у `migrator.env` |
| `Auth__JwtSigningKey` | `openssl rand -base64 48` | `api.env` |
| `Auth__PlatformKey` | `openssl rand -base64 36` | `api.env` |
| `Channels__EncryptionKey` | `openssl rand -base64 32` | `api.env` і `worker.env` (однакове) |
| `Ai__Secrets__Keys__k1` (id `k1` = `Ai__Secrets__ActiveKeyId`) | `openssl rand -base64 32` | `api.env` |

Приклад без друку значення: `printf 'DB_APP_PASSWORD=%s\n' "$(openssl rand -hex 24)" >> $S/db.env` (не повторюйте для вже наявних ключів). Пароль app має збігатися в `db.env` і `ConnectionStrings__Default` (`api.env`), пароль worker — у `db.env` і `WORKER_DATABASE_URL` (`worker.env`).

- **Майстер-ключ ШІ (S1):** зашифрована копія `api.env` і ключа зберігається **окремо від дампів БД** (менеджер паролів власника). Втрата ключа = втрата збережених ключів провайдера ШІ.
- `migrator.env` не підключений до жодного сервісу. Тримайте його лише на час міграції.
- Прапорці в шаблонах вимкнені (маркетинг, `ChatPhone`, нагадування, відгуки, чат-асистент, mock-режими). Вмикати лише за окремим рішенням і gate (ADR-016 §2.10).
- Telegram: polling у `Staging` заборонений кодом, тож у `api.env` стоїть `Channels__Telegram__Polling__Enabled=false`. Рекомендований транспорт — **webhook** (ADR-016 §2.3), він чекає формального схвалення людини (Q1). Покроково — розділ 2.1.

### 2.1 Telegram webhook: покроково

Передумови: окремий **тестовий бот** із власним токеном (один токен не можна ділити з dev-polling: `getUpdates` дасть 409 Conflict, а webhook перехопить оновлення dev); синтетичний tenant; схвалення Q1.

1. Людина створює тестового бота в BotFather. Токен бота зберігається в застосунку (зашифровано в БД, ADR-021 і канали), процедуру реєстрації каналу — за runbook (ADR-013). Токен у файли репозиторію й у чат не потрапляє.
2. Секрет webhook (`secret_token`) — це `WebhookSecret` каналу. Згенеруйте і збережіть у застосунку: `openssl rand -hex 32`.
3. Токен і секрет передайте в оболонку без історії (приховане читання), `setWebhook` виконуйте через stdin, щоб не світити токен в аргументах процесу:
   ```bash
   read -rs TG_TOKEN && export TG_TOKEN
   read -rs TG_SECRET && export TG_SECRET
   curl -fsS -K - <<EOF
   url = "https://api.telegram.org/bot${TG_TOKEN}/setWebhook"
   data-urlencode = "url=https://beauty.<DOMAIN>/api/beauty/webhooks/telegram/<CHANNEL_ID>"
   data-urlencode = "secret_token=${TG_SECRET}"
   data-urlencode = "allowed_updates=[\"message\",\"callback_query\"]"
   EOF
   ```
   `<CHANNEL_ID>` — ідентифікатор каналу з застосунку. URL без порту.
4. Перевірка стану (без токена у виводі):
   ```bash
   curl -fsS -K - <<EOF
   url = "https://api.telegram.org/bot${TG_TOKEN}/getWebhookInfo"
   EOF
   ```
   Очікування: `url` збігається, `last_error_message` відсутній, `pending_update_count` не росте. Надішліть тестовому боту повідомлення і перевірте лог `api`.
5. Прибрати змінні: `unset TG_TOKEN TG_SECRET`.
6. Polling не вмикати. Якщо webhook зламаний, `deleteWebhook` (та сама схема, `url = ".../deleteWebhook"`) повертає стан без другого процесу.

## 3. Образи

Публікація лише вручну: GitHub Actions, workflow **Staging images** (`workflow_dispatch`), параметри `ref` (гілка/SHA), `public_origin` = `https://beauty.<DOMAIN>` (вшивається у web), за потреби `tenant_slug`. Workflow перевіряє, що для цього SHA CI зелений, збирає `api`, `worker`, `web` з тегом `sha-<повний SHA>` у `ghcr.io/<owner>/beautycrm-<сервіс>` і виводить **digest** у Summary. Також додає артефакт `migrations-<SHA>` (SQL міграцій).

Пакети в ghcr приватні: на сервері потрібен `docker login ghcr.io` токеном лише з правом читання пакетів (`read:packages`), який створює і зберігає людина. Digest-и впишіть у `.env.compose` (`API_IMAGE=ghcr.io/<owner>/beautycrm-api@sha256:...` тощо). Деплой завжди за digest, а не за тегом.

## 4. Перший запуск (порядок важливий)

Усі команди з `deploy/staging/`; скорочення: `DC="docker compose --env-file .env.compose -f docker-compose.staging.yml"`.

1. **Старий інстанс.** Якщо вже працює інший екземпляр api/worker того самого staging (інша машина, локальний polling), **зупиніть його перед стартом нового**: worker і Telegram polling мають бути лише в одному екземплярі. На цьому сервері: `$DC ps` (перегляд), `$DC stop api worker` (лише проєкт `beauty_staging`).
2. Перевірка конфігурації без запуску: `$DC config -q` (помилка = не задана обов'язкова змінна). Далі `ss -ltn` для портів з `.env.compose`.
3. **БД:** `$DC up -d db`. При першому старті порожнього тому `init-roles.sh` створює ролі `beautycrm_owner`, `beautycrm_app`, `beautycrm_worker` (NOSUPERUSER NOBYPASSRLS) і БД `beautycrm`. Перевірка: `$DC exec db psql -U postgres -c "\du"`. Скрипт запускається лише на порожньому томі; зміна пароля пізніше: `ALTER ROLE ... PASSWORD ...` під `postgres`.
4. **Бекап -> міграція (human gate).** Порядок однаковий щоразу:
   1. Бекап (розділ 5) і перевірка, що файл створено.
   2. Завантажте артефакт `migrations-<SHA>` з GitHub Actions і **перегляньте SQL** (блокування `ACCESS EXCLUSIVE`, видалення даних). Схвалення за людиною.
   3. `$DC stop api worker` (при першому старті їх ще немає).
   4. Застосуйте від **owner** (пароль з `DB_OWNER_PASSWORD` у `db.env`; уводьте прихованим читанням, щоб не потрапив в історію):
      ```bash
      read -rs OWNER_PW
      $DC exec -T -e PGPASSWORD="$OWNER_PW" db psql -h 127.0.0.1 -U beautycrm_owner -d beautycrm -v ON_ERROR_STOP=1 -1 < migrations-<SHA>.sql
      unset OWNER_PW
      ```
      Міграції, що перемикають `FORCE ROW LEVEL SECURITY` для backfill, виконуються саме роллю-власником. `migrator.env` потрібен лише для разового `ai-secrets reencrypt` (S5).
      **Міграція ADR-024 `auth_global_identities` (human gate):** до кроку 4 обов'язково пройти preflight дублікатів пошти (`db/preflight-identity-duplicates.sql`) і задати allowlist злиття (`beautycrm.identity_merge_allowlist`, передається сесійно, не файлом). Повна процедура, порядок і відкат — `.claude/docs/runbook.md` §2b. Без рішення щодо дублікатів міграція зупиниться з `RAISE EXCEPTION` (це очікувано, бекап не змінюється).
   5. **GRANT-и** від суперкористувача: `$DC exec -T db psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 < db/grants.sql`. Файл відтворює блок `$grants` зі `scripts/dev/setup-db.ps1` (синхронність перевіряє тест `worker/src/staging-grants-sync.test.ts`): `REVOKE UPDATE, DELETE` на `beauty_ai_settings_audit` і `beauty_marketing_consent_events`, точкові права воркера. Повторюйте після **кожної** міграції. Для `auth_global_identities` (ADR-024) цей крок обов'язковий: він робить `REVOKE` глобальних таблиць ідентичностей для `beautycrm_app` і воркера (після `GRANT ON ALL TABLES`).
5. **API:** `$DC up -d api`. Очікування: контейнер `healthy`; у `$DC logs api` немає помилок `DbRoleGuard`. Перевірка з хоста: `curl -fsS http://127.0.0.1:$BEAUTY_API_PORT/healthz` (liveness) і `/readyz` (БД); обидва без деталей. Попередження `Failed to determine the https port for redirect` очікуване (TLS закінчується на nginx).
6. **Worker:** `$DC up -d worker` (після створення першого синтетичного tenant допишіть `WORKER_TENANT_IDS` у `worker.env` і `$DC up -d worker`). Відсутність `BYPASSRLS` перевіряє сам процес.
7. **Web:** `$DC up -d web`. Перевірка: `curl -fsS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:$BEAUTY_WEB_PORT/login`.
8. Перший tenant (лише синтетичні дані): `POST /api/platform/tenants` із заголовком `X-Platform-Key` напряму на `127.0.0.1:$BEAUTY_API_PORT` з сервера (в nginx-прикладі `/api/platform/` закрито для публічного доступу). Див. `.claude/docs/runbook.md` §5.
9. Smoke та QA: за `docs/qa/` і чеклістами; передайте `qa-engineer`.

## 5. Бекап і відновлення

**Бекап лише від суперкористувача `postgres`**. Від `beautycrm_owner` не можна: через `FORCE ROW LEVEL SECURITY` дамп або падає, або з `--enable-row-security` **мовчки дає порожні таблиці** (опція заборонена).
```bash
mkdir -p /opt/beauty_staging/backups && chmod 700 /opt/beauty_staging/backups
$DC exec -T db pg_dump -U postgres -Fc beautycrm > /opt/beauty_staging/backups/beautycrm-$(date +%Y%m%d-%H%M%S).dump
ls -l /opt/beauty_staging/backups        # файл має бути ненульового розміру
```
Мінімум для staging: щоденний дамп (cron/systemd timer), шифрування публічним ключем (`age`/gpg), копія **поза сервером**, збереження 14 щоденних і 4 тижневих (ADR-016 §2.9, Q3). Раз на місяць і перед першою міграцією перевіряйте відновлення у **тимчасовий** контейнер (не в `beauty_staging_db`): `pg_restore` у чистий `postgres:16.x`-контейнер без публікації портів і перевірка рядків.

### 5.1 Том медіа товарів (ADR-027 §8; human gate G4/G5)

> **Human gate G4 (інфраструктура: том, `chown`, nginx, `mem_limit`) і G5 (застосування на сервері). Це робить лише людина; агенти не виконують.** До цього функція фото вимкнена. У compose `Media__RootPath` задано, тож **перед `up -d api` з новим compose** потрібні том із правами і `MEDIA_PUBLIC_BASE_URL` в `.env.compose`.

Одноразово, **до першого `up` api** (api працює від UID 1654 з `read_only: true`; без `chown` запис фото дасть помилку прав):
```bash
docker volume create beauty_staging_media
docker run --rm -v beauty_staging_media:/data/media alpine:3 sh -c 'chown 1654:1654 /data/media && chmod 0750 /data/media'
```
Том монтується лише в `api` (`/data/media`), інші контейнери його не бачать. Змінні: `Media__RootPath=/data/media`, `Media__PublicBaseUrl` з `MEDIA_PUBLIC_BASE_URL`. `mem_limit` api піднято до 768m. Режим очищувача `Media__SweepMode=dry-run` додайте в `api.env` (після перевірки логів можна `delete`).

Бекап: **спершу `pg_dump` (розділ 5), потім tar тома**. Порядок важливий: файл без рядка БД очищувач прибере, а рядок без файлу дає биту картинку; дамп, знятий раніше за tar, не посилається на файли, яких у архіві немає.
```bash
docker run --rm -v beauty_staging_media:/data/media:ro -v /opt/beauty_staging/backups:/backup alpine:3 \
  tar czf /backup/media-$(date +%Y%m%d-%H%M%S).tar.gz -C /data/media .
ls -l /opt/beauty_staging/backups
```
Відновлення: у порожній том, потім `chown -R 1654:1654` і `chmod` (теки 0750, файли 0640) тим самим одноразовим контейнером. Копію тримайте разом із дампом, поза сервером, з тією ж ротацією.

Nginx: окремий `location` для upload з `client_max_body_size 11m` і закоментований `auth_basic off` для `/api/public/media/` (ADR-027 Q1): див. 6.3.

## 6. Публікація назовні через nginx

Топологія (ADR-016 §2.1.2): зовнішній 443 → провайдер → внутрішній **8443** системного nginx; зовнішній 80 відкритий. Публічна адреса — `https://beauty.<DOMAIN>` без порту. Зовнішній 8443 закритий, так і має бути. Порт назовні додавати не треба.

Загальні правила: **не змінюйте існуючі файли nginx**; додаються лише нові файли; перед кожним reload — `sudo nginx -t`, reload лише коли тест чистий. Сертифікат випускає людина. Чужий сертифікат ShelfGuard не розширювати і не перевидавати. Плагін `certbot --nginx` **не використовувати** (він редагує конфіги). Плейсхолдери: `<DOMAIN>`, `<OPERATOR_IP>`, `<BEAUTY_API_PORT>`, `<BEAUTY_WEB_PORT>`.

### 6.1 Крок 1: ACME-блок на порту 80

Новий файл, наприклад `/etc/nginx/sites-available/beauty-staging-acme` (далі `ln -s` у `sites-enabled`):
```nginx
# Лише ACME HTTP-01 і редирект для beauty.<DOMAIN>.
# Окремий server_name: nginx обирає server за Host, тож default_server на 80 чужого сайту не перехоплює цей запит
# (default_server бере лише запити з невідомим Host). Чужі server-блоки на 80 не змінюються.
server {
    listen 80;
    server_name beauty.<DOMAIN>;

    location ^~ /.well-known/acme-challenge/ {
        root /var/www/beauty-acme;      # webroot для certbot; створити: sudo install -d -m 755 /var/www/beauty-acme
        default_type text/plain;
    }

    location / {
        return 301 https://beauty.<DOMAIN>$request_uri;
    }
}
```
Перед `reload` перевірте, що жоден інший server-блок не має точного `server_name beauty.<DOMAIN>` чи конфліктного wildcard (`sudo nginx -T 2>/dev/null | grep -n 'server_name'`). Потім: `sudo nginx -t && sudo nginx -s reload`.

### 6.2 Крок 2: сертифікат (людина, sudo на сервері)

Тільки новий сертифікат для піддомену, через webroot (не чіпає чужі конфіги):
```bash
sudo certbot certonly --webroot -w /var/www/beauty-acme -d beauty.<DOMAIN> --cert-name beauty.<DOMAIN>
```
Результат: `/etc/letsencrypt/live/beauty.<DOMAIN>/fullchain.pem` і `privkey.pem`. Поновлення — наявний `certbot.timer`. Після випуску: `sudo certbot renew --dry-run` (має пройти без помилок). Перевірте, що поновлення перезавантажує nginx (deploy-hook у `/etc/letsencrypt/renewal-hooks/deploy/` або схема сервера); якщо ні, це окреме рішення людини.

### 6.3 Крок 3: HTTPS-блок на внутрішньому 8443

`/etc/nginx/sites-available/beauty-staging` (далі `ln -s` у `sites-enabled`):
```nginx
# Beauty CRM staging. Один піддомен: /api/ -> API, решта -> web.
server {
    listen 8443 ssl http2;                    # як у сусідніх сайтів; розрізнення за server_name, чужі server-блоки не зачіпаються
    server_name beauty.<DOMAIN>;

    ssl_certificate     /etc/letsencrypt/live/beauty.<DOMAIN>/fullchain.pem;   # сертифікат лише для beauty.<DOMAIN>
    ssl_certificate_key /etc/letsencrypt/live/beauty.<DOMAIN>/privkey.pem;

    add_header Strict-Transport-Security "max-age=31536000" always;
    add_header X-Robots-Tag "noindex, nofollow" always;
    server_tokens off;
    client_max_body_size 2m;

    # Закриття доступу до staging-вебу (ADR-016 Q5): basic auth на весь сайт, див. 6.5
    auth_basic "Beauty CRM staging";
    auth_basic_user_file /etc/nginx/beauty-staging.htpasswd;

    # Ендпоінти оператора: лише з вашої адреси (платформний ключ ШІ вимкнений)
    location /api/platform/ {
        allow <OPERATOR_IP>;
        deny all;
        proxy_pass http://127.0.0.1:<BEAUTY_API_PORT>;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    # Webhook-и каналів: без basic auth (захист — secret token); має бути довший префікс за /api/
    location /api/beauty/webhooks/ {
        auth_basic off;
        proxy_pass http://127.0.0.1:<BEAUTY_API_PORT>;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    # route handlers Next (сесія) -> web; без basic auth, як вимагає ADR (захист — застосунок)
    location /api/session/ {
        auth_basic off;
        proxy_pass http://127.0.0.1:<BEAUTY_WEB_PORT>;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    # Universal links / App Links: публічні, без basic auth (Apple/Google/мобільний клієнт не вміють його проходити)
    location ^~ /l/ {
        auth_basic off;
        proxy_pass http://127.0.0.1:<BEAUTY_WEB_PORT>;   # web, loopback 3400
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    # Без редиректів; Content-Type application/json; віддає web (404, поки не задано APPLE_TEAM_ID / ANDROID_*)
    location = /.well-known/apple-app-site-association {
        auth_basic off;
        default_type application/json;
        proxy_pass http://127.0.0.1:<BEAUTY_WEB_PORT>;
        proxy_hide_header Content-Type;
        add_header Content-Type application/json always;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    location = /.well-known/assetlinks.json {
        auth_basic off;
        default_type application/json;
        proxy_pass http://127.0.0.1:<BEAUTY_WEB_PORT>;
        proxy_hide_header Content-Type;
        add_header Content-Type application/json always;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    # ADR-027 §8 (G4): upload фото товару. Окремий location з 11m (решта сайту лишається 2m); regex має пріоритет над префіксом /api/.
    # Маршрут за ADR-027: POST /api/beauty/inventory/products/{guid}/images. Basic auth успадковується від server-блоку.
    location ~ ^/api/beauty/inventory/products/[0-9a-fA-F-]{36}/images$ {
        client_max_body_size 11m;
        proxy_pass http://127.0.0.1:<BEAUTY_API_PORT>;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    # ADR-027 Q1 (рішення людини, ЗАКОМЕНТОВАНО за замовчуванням). Публічна роздача фото йде під basic auth, як увесь /api/public/.
    # Розкоментуйте, лише якщо мобільні тестери / завантажувач зображень не передають basic auth: тоді лише роздача
    # фото (GET, без секретів, ключі непередбачувані) стає відкритою, решта /api/public/ лишається під basic auth.
    # location ^~ /api/public/media/ {
    #     auth_basic off;
    #     proxy_pass http://127.0.0.1:<BEAUTY_API_PORT>;
    #     include /etc/nginx/beauty-staging-proxy.conf;
    # }
    # решта API -> api
    location /api/ {
        proxy_pass http://127.0.0.1:<BEAUTY_API_PORT>;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    location / {
        proxy_pass http://127.0.0.1:<BEAUTY_WEB_PORT>;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
}
```
`/etc/nginx/beauty-staging-proxy.conf` (заголовки **перезаписуються**, клієнтський `X-Forwarded-*` не довіряється):
```nginx
proxy_http_version 1.1;
proxy_set_header Host $http_host;   # зберігає порт: BFF порівнює його з Origin (isSameOrigin)
proxy_set_header X-Forwarded-For $remote_addr;
proxy_set_header X-Forwarded-Proto https;
proxy_set_header Connection "";
proxy_read_timeout 60s;
```
Після `sudo nginx -t` — reload. Origin у `Beauty__AllowedOrigins` і `public_origin` образу web: `https://beauty.<DOMAIN>` без порту. Після `up -d api` перевірте, що IP клієнта в логах API бачиться правильно: `Beauty__TrustedProxies` = адреса шлюзу мережі `beauty_staging` (звідси API бачить nginx) **плюс** `BEAUTY_WEB_IP` (звідси API бачить BFF `web`), через кому: `172.29.200.1,172.29.200.10`. Не розширюйте до всієї підмережі. Без адреси web API бачить усіх користувачів BFF з одного IP, і ліміт `auth` (10/хв) спільний для всіх (ADR-024 F-1, §31.9).

### 6.4 Крок 4: перевірка

```bash
curl -sI http://beauty.<DOMAIN>/ | head -1                 # очікування: 301 на https
curl -sI https://beauty.<DOMAIN>/ | head -1                # очікування: 401 (basic auth)
curl -sI https://beauty.<DOMAIN>/api/beauty/webhooks/telegram/<CHANNEL_ID> | head -1   # не 401 від basic auth
curl -sI https://beauty.<DOMAIN>/.well-known/assetlinks.json | head -1                  # не 401; 200 лише коли задано ANDROID_* (інакше 404)
curl -sI https://beauty.<DOMAIN>/l/TESTCODE | head -1                                    # не 401
openssl s_client -connect beauty.<DOMAIN>:443 -servername beauty.<DOMAIN> </dev/null 2>/dev/null \
  | openssl x509 -noout -subject -issuer -enddate -ext subjectAltName
```
Очікування для `openssl`: видавець Let's Encrypt, SAN лише `beauty.<DOMAIN>`, дата дійсна. Webhook-шлях відповідає від API (не 401/403 від nginx).

### 6.6 Крок 5: smoke IP клієнта за BFF (ADR-024 errata E-14 / F-1, §31.9 п.5; блокує реліз TASK-866)

Джерело вимог: `.claude/docs/runbook.md` §2b.4 і `beauty-contracts.md` §31.9. Конфіг (compose, `.env.compose`, `.env.api`, bootstrap-env, nginx-приклад §6.3) уже в репозиторії (TASK-871). На сервері ще не застосовано, smoke не виконано.

Умови (перевірити до smoke): (1) `web` має статичну `BEAUTY_WEB_IP` (`docker inspect beauty_staging_web --format '{{(index .NetworkSettings.Networks "beauty_staging").IPAddress}}'`); (2) `Beauty__TrustedProxies` = шлюз + ця адреса; (3) `proxy_set_header X-Forwarded-For $remote_addr` (6.3), не `$proxy_add_x_forwarded_for`; (4) `ss -ltn` показує порт web лише на `127.0.0.1`. Застосувати: `$DC up -d web api` (web перестворюється зі статичною адресою; api перечитує env).

1. **(а) окремі кошики:** `BEAUTY_WEB_PORT=<порт> SMOKE_ORIGIN=https://beauty.<DOMAIN> ./smoke-client-ip.sh`. 11-й запит з IP A дає `429`, перший з IP B не `429`. Скрипт б'є у loopback web і сам ставить XFF (імітує nginx). Між повторами зачекайте 60 с.
2. **(б) підробка через nginx:** з зовнішнього хоста двічі по 11 запитів `POST https://beauty.<DOMAIN>/api/session/login` (з basic auth) з клієнтським заголовком `X-Forwarded-For: 198.51.100.7` (довільний). Очікування: ліміт рахується за реальною адресою вашого хоста, а не за `198.51.100.7`: зміна підробленого значення між запитами не скидає кошик (11-й дає `429` незалежно від підробки).
3. **(в) IP у логах API:** `$DC logs --tail 50 api | grep -i 'RemoteIp\|client'` не показує адресу web (`BEAUTY_WEB_IP`) і шлюз як клієнтські; видно адресу вашого хоста. Якщо в логах немає IP, достатньо результатів 1 і 2.
4. Результат (дата, виконавець, 429/не 429 для кожного кроку) занесіть у `.claude/logs/tasks/TASK-871.md`.

### 6.5 Basic auth (Q5, рекомендовано)

Створити файл із хешем поза репозиторієм, пароль вводить людина, у чат і в історію не потрапляє:
```bash
sudo htpasswd -c /etc/nginx/beauty-staging.htpasswd <LOGIN>     # інтерактивне введення пароля
sudo chmod 640 /etc/nginx/beauty-staging.htpasswd && sudo chown root:www-data /etc/nginx/beauty-staging.htpasswd
```
Директиви `auth_basic` уже в блоці 6.3. Webhook (`/api/beauty/webhooks/`) і `/api/session/` мають `auth_basic off` (ADR-016 §4.2). Альтернатива: IP allowlist (`allow <OPERATOR_IP>; deny all;`) на вебі замість basic auth. Не заходьте з відкритим доступом до вебу, доки Q5 не закрито.

## 7. Оновлення і відкат

- **Оновлення:** новий workflow публікації -> нові digest-и в `.env.compose` -> бекап -> (якщо є міграції: розділ 4 п. 4) -> `$DC pull && $DC up -d api worker web`. Простій короткий, для staging прийнятно. Не масштабувати `api`/`worker`.
- **Відкат образів:** повернути попередні digest-и в `.env.compose`, `$DC up -d api worker web`. Міграції автоматично не відкочуються: міграції вперед сумісні, відкат схеми лише з бекапу (`pg_restore` у зупинену БД за окремим планом людини). Тому бекап перед кожною міграцією обов'язковий.
- **Зупинка проєкту:** `$DC stop` (дані лишаються). **Видалення** (`$DC down -v` знищує том `beauty_staging_pgdata`) лише свідомо; чужих ресурсів це не торкається, бо всі імена з префіксом `beauty_staging`.

### 7.1 Реліз: мобільний запис (каталог, відгуки, email)

Стосується міграцій `20261109075625` (публічний каталог закладів), `20261109135452` (профіль і відгуки майстрів), `20261109150000` (нагадування за 24 год, email-канал) і `20261110090000` (умови скасування). Кожен крок схвалює людина (ADR-016 §2.1.1). Команди з `deploy/staging/`, `DC` як у розділі 4.

1. **Бекап** (розділ 5): `pg_dump -U postgres -Fc beautycrm` і перевірка ненульового розміру файлу. Без бекапу далі не йти.
2. **Роль каталогу** (суперкористувач, **до** міграцій; `beautycrm_owner` не має `CREATEROLE`, тож без цього кроку міграція каталогу зупиниться з помилкою):
   `$DC exec -T db psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 < db/init-catalog-role.sql`
   Скрипт ідемпотентний; у виводі очікуємо `rolcanlogin=f, rolsuper=f, rolbypassrls=f, rolinherit=f` і `inherit_option=f, set_option=t`.
3. **Міграції:** `$DC stop api worker`, далі SQL-скрипт `migrations-<SHA>.sql` від `beautycrm_owner` (розділ 4 п. 4.4). Перегляньте SQL перед застосуванням. Скрипт ідемпотентний (`__EFMigrationsHistory`).
4. **GRANT-и:** `$DC exec -T db psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 < db/grants.sql`. Додано: `beauty_specialist_reviews` для app, `EXECUTE` на три функції `beauty_public_*` лише для app (не `PUBLIC`, не worker), `SELECT beauty_specialists` і `INSERT beauty_conversations` для worker (підтвердження запису й email-нагадування).
5. **Конфіг:** у `api.env` і `worker.env` додайте змінні з `.env.api.example` (`PublicBooking__*`) і `.env.worker.example` (`EMAIL_*`, `SMTP_*`). Без змін лишається безпечний стан: `EMAIL_MODE=mock` (листи не надсилаються), режими захисту `none`.
6. **Образи:** нові digest-и в `.env.compose`, `$DC pull && $DC up -d api worker` (розділ 7).
7. **Перевірки** (з хоста, `P=http://127.0.0.1:$BEAUTY_API_PORT`, `SLUG` = slug синтетичного tenant):
   - `curl -fsS $P/readyz`; у `$DC logs api` немає помилок `DbRoleGuard`.
   - Код закладу: `curl -sS $P/api/public/resolve/<publicCode>` (код видає міграція для наявних закладів; невідомий код дає 404, не 500).
   - Календар: `curl -sS "$P/api/public/$SLUG/availability?locationId=<id>&serviceId=<id>&days=7"`.
   - Відгуки: `curl -sS "$P/api/public/$SLUG/specialists/<id>/reviews"`.
   - Запис: `POST $P/api/public/$SLUG/appointments` синтетичним клієнтом; у відповіді `publicToken`; потім `GET .../appointments/<publicToken>` і скасування `POST .../cancel` для прибирання.
   - Worker: `$DC logs worker` без помилок; при `EMAIL_MODE=mock` у логу рядок `email queued (mock, not sent)` (лише id і kind, без тексту).
8. **Відкат:** образи (розділ 7: повернути попередні digest-и). Схема назад автоматично не відкочується: міграції лише додають, тож попередні образи сумісні зі схемою; повернення схеми лише з бекапу п. 1 (`pg_restore` у зупинену БД за окремим планом людини). Роль `beautycrm_catalog` кластерна, її не видаляють.

Відкриті питання (не вирішені, потрібне рішення людини):
- **SMTP-провайдер** не обрано: до того `EMAIL_MODE=mock`. Для `smtp` потрібні домен відправника з SPF/DKIM і секрети лише на сервері.
- **Домен для universal links / App Links** не визначено; шаблони в `well-known/` (див. `well-known/README.md`) містять плейсхолдери.
- **App Attest / Play Integrity** не підключені: режими `PublicBooking__Protection__Ios|Android` лишаються `none` (або `monitor`); `enforce` без атестації заблокує мобільний запис. Допустимість `AllowWeakerClientPaths=true` у Production на час MVP: ADR-022, відкрите питання 4.
- Увімкнення `WORKER_REMINDERS_ENABLED` для реальних клієнтів: окреме рішення (ADR-016 §2.10).

## 8. Як не зачепити сусідні сервіси (чеклист)

- Усі команди `docker compose` запускайте лише з цього каталогу з `-f docker-compose.staging.yml` (проєкт `beauty_staging`). Не використовуйте `docker system prune`, `docker rm $(docker ps -aq)`, `docker network prune`, `docker volume prune`.
- Не підключайте контейнери до чужих мереж і не звертайтесь до чужих БД/redis/MQTT. Власний postgres тут окремий, redis у проєкті не потрібен.
- Порти: лише loopback, перевірені через `ss -ltn`. Перед `up` і перед reload nginx ще раз звіряйте.
- Ресурси: ліміти задано; після запуску `docker stats --no-stream` і стежте за навантаженням сусідніх продакшн-сервісів.
- Не змінюйте існуючі server-блоки nginx і сертифікати інших доменів; новий файл + `nginx -t` перед reload.
- Не комітьте `.env.compose`, `*.env`, дампи, htpasswd. Реальні IP, порти SSH і імена користувачів сервера в репозиторій не потрапляють.
- Кожен крок на спільному сервері схвалює людина окремо (ADR-016 §2.1.1).

## 9. Що тут не перевірено

Розгортання, nginx, ACME/сертифікати, webhook Telegram, ghcr-публікація і запуск GitHub Actions перевіряються лише на сервері / після пушу. Локально перевірено: `docker build` кожного образу, `docker compose config`, ініціалізація ролей на одноразовому контейнері postgres.
