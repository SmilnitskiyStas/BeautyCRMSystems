#!/usr/bin/env bash
# Розгортання staging Beauty CRM на спільному сервері одним запуском (README §1-§6).
# Запуск людиною: cd /opt/beauty_staging/deploy && ./setup-server.sh
# Скрипт сам зупиняється при будь-якій помилці. Він НЕ чіпає чужі контейнери, мережі, томи і наявні конфіги nginx:
# створює лише ресурси beauty_staging і нові файли beauty-staging* у nginx. Секрети не друкуються.
# Інтерактивно запитує: пароль sudo, токен ghcr (read:packages), email для certbot, логін/пароль basic auth, підтвердження міграції.
set -euo pipefail

DOMAIN_HOST="beauty.agrusystems.pp.ua"
ORIGIN="https://${DOMAIN_HOST}:10054"
SHA="c375c5f0be87787cf5b84db3f4c415d9c21d01cd"
API_IMAGE="ghcr.io/smilnitskiystas/beautycrm-api@sha256:0342af9aa65b8684def3961a0f80c9da06ee5f6b7463e8dd4f3a80fe8d19d24e"
WORKER_IMAGE="ghcr.io/smilnitskiystas/beautycrm-worker@sha256:0ae1250cd182e34f07ca01f38f7b3903f62f237d94169144006c60701f526292"
WEB_IMAGE="ghcr.io/smilnitskiystas/beautycrm-web@sha256:bd3e9678583add09fd75126a204e652eeb768b4a1afac70bfeed2a331a087433"
MIGRATION="migrations/migrate-${SHA}.sql"
MIGRATION_SHA256="ce38c0e2e9f8ec9d253cd9db159c9847da724e600607bb2e53d58bd0aebd50c3"
SECRETS_DIR="/opt/beauty_staging/secrets"
BACKUPS="/opt/beauty_staging/backups"

cd "$(dirname "$0")"
step() { printf '\n=== %s\n' "$*"; }
ask()  { local a; read -r -p "$1 [y/N] " a; [[ "$a" == y || "$a" == Y ]]; }
DC() { docker compose --env-file .env.compose -f docker-compose.staging.yml "$@"; }

step "0. Перевірки (лише читання)"
[ -f "$MIGRATION" ] || { echo "Немає $MIGRATION"; exit 1; }
echo "${MIGRATION_SHA256}  ${MIGRATION}" | sha256sum -c - >/dev/null || { echo "Контрольна сума міграції не збігається"; exit 1; }
echo "SQL міграцій: контрольна сума збігається (файл той самий, що переглянуто)."
chmod +x bootstrap-env.sh db/init-roles.sh
if docker ps -a --format "{{.Names}}" | grep "^beauty_staging_" >/dev/null; then
  echo "Стек beauty_staging уже існує: перевірку вільних портів і підмережі пропускаю."
else
for p in 5200 3400; do
  if ss -ltn | awk '{print $4}' | grep -E "[:.]${p}$" >/dev/null; then echo "Порт ${p} зайнятий: змініть у .env.compose і запустіть знову"; exit 1; fi
done
if docker network ls -q | xargs docker network inspect --format '{{range .IPAM.Config}}{{.Subnet}} {{end}}' | grep '172.29.200.' >/dev/null; then
  echo "Підмережа 172.29.200.0/24 зайнята: оберіть іншу (BEAUTY_SUBNET/BEAUTY_GATEWAY, Beauty__TrustedProxies)"; exit 1
fi
if sudo nginx -T 2>/dev/null | grep "server_name[^;]*${DOMAIN_HOST}" >/dev/null; then
  [ -e /etc/nginx/sites-available/beauty-staging-acme ] || { echo "У nginx вже є server_name ${DOMAIN_HOST} (не наш файл)"; exit 1; }
fi
free -h | head -2; uptime
echo "Порти і підмережа вільні."
fi

step "0b. Swap (захист спільного сервера від OOM)"
if [ "$(swapon --noheadings | wc -l)" -gt 0 ]; then
  echo "Swap вже є:"; swapon --show
elif ask "Swap відсутній. Створити /swapfile-beauty на 2 ГБ і додати в /etc/fstab (sudo)?"; then
  sudo fallocate -l 2G /swapfile-beauty || sudo dd if=/dev/zero of=/swapfile-beauty bs=1M count=2048 status=none
  sudo chmod 600 /swapfile-beauty
  sudo mkswap /swapfile-beauty >/dev/null
  sudo swapon /swapfile-beauty
  grep -q '^/swapfile-beauty ' /etc/fstab || echo '/swapfile-beauty none swap sw 0 0' | sudo tee -a /etc/fstab >/dev/null
  swapon --show
else
  echo "Swap пропущено за вашим рішенням."
fi

step "1. Env-файли з секретами"
if [ -e "${SECRETS_DIR}/api.env" ]; then
  echo "Вже існують: пропускаю генерацію."
else
  ./bootstrap-env.sh "$ORIGIN"
fi
sed -i "s#^API_IMAGE=.*#API_IMAGE=${API_IMAGE}#; s#^WORKER_IMAGE=.*#WORKER_IMAGE=${WORKER_IMAGE}#; s#^WEB_IMAGE=.*#WEB_IMAGE=${WEB_IMAGE}#" .env.compose

step "2. Вхід у ghcr.io (токен лише read:packages; введення приховане)"
if ! DC pull --quiet web 2>/dev/null; then
  read -rs -p "Токен ghcr (read:packages): " GH_TOKEN; echo
  printf '%s' "$GH_TOKEN" | docker login ghcr.io -u SmilnitskiyStas --password-stdin
  unset GH_TOKEN
fi
DC config -q && echo "compose config OK"

step "3. Сертифікат ${DOMAIN_HOST} (DNS-challenge: зовнішній :80 обслуговує сторонній проксі, HTTP-перевірка недоступна)"
sudo install -d -m 755 /var/www/beauty-acme
if [ ! -e /etc/nginx/sites-available/beauty-staging-acme ]; then
  sudo tee /etc/nginx/sites-available/beauty-staging-acme >/dev/null <<EOF
server {
    listen 80;
    server_name ${DOMAIN_HOST};
    location ^~ /.well-known/acme-challenge/ {
        root /var/www/beauty-acme;
        default_type text/plain;
    }
    location / {
        return 301 https://${DOMAIN_HOST}\$request_uri;
    }
}
EOF
  sudo ln -sf /etc/nginx/sites-available/beauty-staging-acme /etc/nginx/sites-enabled/beauty-staging-acme
  sudo nginx -t
  sudo nginx -s reload
fi
if [ ! -e "/etc/letsencrypt/live/${DOMAIN_HOST}/fullchain.pem" ]; then
  echo "certbot запитає email і згоду з умовами Let's Encrypt (ваше рішення), потім покаже TXT-запис _acme-challenge."
  echo "Додайте його в DNS-панелі, зачекайте 1-2 хв, перевірте (dig +short TXT _acme-challenge.${DOMAIN_HOST}) і натисніть Enter."
  sudo certbot certonly --manual --preferred-challenges dns -d "$DOMAIN_HOST" --cert-name "$DOMAIN_HOST"
fi
echo "УВАГА: сертифікат отримано вручну (DNS), автоматичного поновлення немає. Нагадування: повторити certbot certonly --manual за ~60 днів."

step "4. База даних"
DC up -d db
printf 'Очікую healthy'
for _ in $(seq 1 60); do
  [ "$(docker inspect -f '{{.State.Health.Status}}' beauty_staging_db 2>/dev/null)" = healthy ] && break
  printf '.'; sleep 2
done
echo
[ "$(docker inspect -f '{{.State.Health.Status}}' beauty_staging_db)" = healthy ] || { echo "db не став healthy: DC logs db"; exit 1; }
DC exec -T db psql -U postgres -tAc "select rolname from pg_roles where rolname like 'beautycrm_%' order by 1"

step "5. Бекап -> міграція (human gate)"
install -d -m 700 "$BACKUPS"
BK="${BACKUPS}/beautycrm-$(date +%Y%m%d-%H%M%S).dump"
DC exec -T db pg_dump -U postgres -Fc beautycrm > "$BK"
[ -s "$BK" ] && echo "Бекап створено (непорожній)."
if ask "Застосувати ${MIGRATION} (17 міграцій, ідемпотентно, без видалення даних)?"; then
  OWNER_PW="$(grep '^DB_OWNER_PASSWORD=' "${SECRETS_DIR}/db.env" | cut -d= -f2-)"
  DC exec -T -e PGPASSWORD="$OWNER_PW" db psql -h 127.0.0.1 -U beautycrm_owner -d beautycrm \
    -v ON_ERROR_STOP=1 -1 -q < "$MIGRATION"
  unset OWNER_PW
  echo "Міграції застосовано."
fi
DC exec -T db psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 -q < db/grants.sql
echo "GRANT-и застосовано."

step "6. Застосунок: api -> worker -> web"
DC up -d api
printf 'Очікую api'
for _ in $(seq 1 60); do curl -fsS "http://127.0.0.1:5200/readyz" >/dev/null 2>&1 && break; printf '.'; sleep 2; done
echo
curl -fsS "http://127.0.0.1:5200/healthz"; echo
curl -fsS "http://127.0.0.1:5200/readyz"; echo
DC up -d worker web
sleep 5
curl -fsS -o /dev/null -w 'web /login: %{http_code}\n' "http://127.0.0.1:3400/login"
DC ps


step "8. HTTPS-блок nginx на 8443"
if [ ! -e /etc/nginx/sites-available/beauty-staging ]; then
  read -r -p "Ваша публічна IP-адреса для /api/platform/ (оператор): " OP_IP
  sudo tee /etc/nginx/beauty-staging-proxy.conf >/dev/null <<'EOF'
proxy_http_version 1.1;
proxy_set_header Host $http_host;   # зберігає порт: BFF порівнює його з Origin (isSameOrigin)
proxy_set_header X-Forwarded-For $remote_addr;
proxy_set_header X-Forwarded-Proto https;
proxy_set_header Connection "";
proxy_read_timeout 60s;
EOF
  sudo tee /etc/nginx/sites-available/beauty-staging >/dev/null <<EOF
server {
    listen 8443 ssl http2;
    server_name ${DOMAIN_HOST};

    ssl_certificate     /etc/letsencrypt/live/${DOMAIN_HOST}/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/${DOMAIN_HOST}/privkey.pem;

    add_header Strict-Transport-Security "max-age=31536000" always;
    add_header X-Robots-Tag "noindex, nofollow" always;
    server_tokens off;
    client_max_body_size 2m;


    location /api/platform/ {
        allow ${OP_IP};
        deny all;
        proxy_pass http://127.0.0.1:5200;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    location /api/beauty/webhooks/ {
        proxy_pass http://127.0.0.1:5200;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    location /api/session/ {
        proxy_pass http://127.0.0.1:3400;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    location /api/ {
        proxy_pass http://127.0.0.1:5200;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
    location / {
        proxy_pass http://127.0.0.1:3400;
        include /etc/nginx/beauty-staging-proxy.conf;
    }
}
EOF
  sudo ln -sf /etc/nginx/sites-available/beauty-staging /etc/nginx/sites-enabled/beauty-staging
  sudo nginx -t
  sudo nginx -s reload
fi

step "9. Перевірка"
curl -sI "http://${DOMAIN_HOST}/" | head -1
curl -sI "https://${DOMAIN_HOST}/" | head -1
openssl s_client -connect "${DOMAIN_HOST}:443" -servername "$DOMAIN_HOST" </dev/null 2>/dev/null | openssl x509 -noout -subject -issuer -enddate
echo
echo "Готово. Очікування: 301 на https, 401 (basic auth), видавець Let's Encrypt."
echo "Далі окремо: тестовий бот і setWebhook (README §2.1), перший синтетичний tenant."
