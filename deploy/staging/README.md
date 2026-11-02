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
| Порти на хості | `127.0.0.1:${BEAUTY_API_PORT}` -> api, `127.0.0.1:${BEAUTY_WEB_PORT}` -> web | лише loopback; порт БД не публікується; redis немає |

Ліміти (mem/cpu): db 1g/1.0, api 768m/1.0, worker 384m/0.5, web 512m/0.5, разом близько 2.7 ГБ і 3 vCPU у піку. Перевірте запас сервера (`free -h`, `uptime`) і за потреби зменшіть.
`restart: unless-stopped` для всіх. Журнали Docker обмежено (10 МБ x 5).

## 1. Підготовка (нічого не змінює)

1. Docker Engine і Compose v2 уже є: `docker compose version`.
2. **Вільні порти.** Звірте `ss -ltn` (і `docker ps --format '{{.Names}} {{.Ports}}'`). У шаблоні стоять **5200** (api) і **3400** (web). Якщо одне з них зайняте, змініть значення в `.env.compose`.
   Зайняті за ADR-016 §1.4 (станом на 2026-11-02; стан змінюється, завжди звіряйтесь із `ss -ltn`): 80, 1883, 1884, 3000, 3001, 3010, 3100, 3200, 3300, 5100, 5110, 5432–5435, 6379–6381, 8001, 8443, 10048. Порт SSH і зовнішні порти провайдера не торкаються; не використовуйте 5000 (dev-API).
3. **Вільна підмережа Docker.** Перегляньте наявні: `docker network ls -q | xargs docker network inspect --format '{{.Name}} {{range .IPAM.Config}}{{.Subnet}} {{end}}'`. Виберіть `/24`, що не перетинається (шаблон: `172.29.200.0/24`, шлюз `172.29.200.1`), і пропишіть у `.env.compose` (`BEAUTY_SUBNET`, `BEAUTY_GATEWAY`) та в `Beauty__TrustedProxies` в `api.env` (= шлюз).
4. **Стан мережі (лише читання).** Провайдер: зовнішній 443 направляє на внутрішній 8443 системного nginx; зовнішній 80 відкритий; зовнішній 8443 закритий (очікувано). Публічна адреса: `https://beauty.<DOMAIN>` **без порту**. Перевірка nginx: `sudo nginx -T 2>/dev/null | grep -nE 'listen|server_name'`.
5. Каталог секретів **поза клоном репозиторію**: `sudo install -d -m 700 /opt/beauty_staging/secrets` (шлях можна змінити через `SECRETS_DIR`).
6. Клонувати репозиторій (лише для `deploy/staging/`) або скопіювати цей каталог. Поставити зі шаблонів `.env.compose` і `*.env`:
   ```bash
   cd deploy/staging
   cp .env.compose.example .env.compose
   S=/opt/beauty_staging/secrets
   cp .env.db.example $S/db.env; cp .env.api.example $S/api.env; cp .env.worker.example $S/worker.env
   cp .env.migrator.example $S/migrator.env
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

1. Людина створює тестового бота в BotFather. Токен бота зберігається в застосунку (зашифровано в БД, ADR-010 і канали), процедуру реєстрації каналу — за runbook (ADR-013). Токен у файли репозиторію й у чат не потрапляє.
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
   5. **GRANT-и** від суперкористувача: `$DC exec -T db psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 < db/grants.sql`. Файл відтворює блок `$grants` зі `scripts/dev/setup-db.ps1` (синхронність перевіряє тест `worker/src/staging-grants-sync.test.ts`): `REVOKE UPDATE, DELETE` на `beauty_ai_settings_audit` і `beauty_marketing_consent_events`, точкові права воркера. Повторюйте після **кожної** міграції.
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
proxy_set_header Host $host;
proxy_set_header X-Forwarded-For $remote_addr;
proxy_set_header X-Forwarded-Proto https;
proxy_set_header Connection "";
proxy_read_timeout 60s;
```
Після `sudo nginx -t` — reload. Origin у `Beauty__AllowedOrigins` і `public_origin` образу web: `https://beauty.<DOMAIN>` без порту. Після `up -d api` перевірте, що IP клієнта в логах API бачиться правильно: `Beauty__TrustedProxies` має дорівнювати адресі шлюзу мережі `beauty_staging`, з якої контейнер бачить nginx. Не розширюйте до всієї підмережі без потреби.

### 6.4 Крок 4: перевірка

```bash
curl -sI http://beauty.<DOMAIN>/ | head -1                 # очікування: 301 на https
curl -sI https://beauty.<DOMAIN>/ | head -1                # очікування: 401 (basic auth)
curl -sI https://beauty.<DOMAIN>/api/beauty/webhooks/telegram/<CHANNEL_ID> | head -1   # не 401 від basic auth
openssl s_client -connect beauty.<DOMAIN>:443 -servername beauty.<DOMAIN> </dev/null 2>/dev/null \
  | openssl x509 -noout -subject -issuer -enddate -ext subjectAltName
```
Очікування для `openssl`: видавець Let's Encrypt, SAN лише `beauty.<DOMAIN>`, дата дійсна. Webhook-шлях відповідає від API (не 401/403 від nginx).

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
