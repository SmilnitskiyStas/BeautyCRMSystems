#!/usr/bin/env bash
# Створює env-файли staging із шаблонів і генерує секрети НА СЕРВЕРІ (README §1 п.6 і §2).
# Значення секретів не друкуються і нікуди не передаються; файли mode 600 у каталозі mode 700.
# Запуск (людина, на сервері): cd deploy/staging && ./bootstrap-env.sh https://beauty.agrusystems.pp.ua
# Скрипт відмовляється перезаписувати наявні файли: повторний запуск нічого не псує.
set -euo pipefail
umask 077

ORIGIN="${1:?Вкажіть public origin, напр. https://beauty.agrusystems.pp.ua}"
case "$ORIGIN" in https://*[!/]) ;; *) echo "Origin має бути https://host без слеша в кінці" >&2; exit 1;; esac

HERE="$(cd "$(dirname "$0")" && pwd)"
AI_KID="k1"   # id майстер-ключа ШІ = Ai__Secrets__ActiveKeyId у шаблоні api.env
S="${SECRETS_DIR:-/opt/beauty_staging/secrets}"
GATEWAY="${BEAUTY_GATEWAY:-172.29.200.1}"

for f in db api worker migrator; do
  [ ! -e "$S/$f.env" ] || { echo "Вже існує $S/$f.env: нічого не змінюю" >&2; exit 1; }
done
[ ! -e "$HERE/.env.compose" ] || { echo "Вже існує .env.compose: нічого не змінюю" >&2; exit 1; }

install -d -m 700 "$S"

hex()  { openssl rand -hex "$1"; }
b64()  { openssl rand -base64 "$1" | tr -d '\n'; }

PG_PW="$(hex 24)"; OWNER_PW="$(hex 24)"; APP_PW="$(hex 24)"; WORKER_PW="$(hex 24)"
JWT="$(b64 48)"; PLATFORM="$(b64 36)"; ENC="$(b64 32)"; AIKEY="$(b64 32)"

# Підстановка через awk (значення не потрапляють в аргументи процесу і не інтерпретуються як regexp).
# Змінні передаються через середовище, а не через -v.
subst() { # subst <template> <out> KEY=ENVVAR ...
  local tpl="$1" out="$2"; shift 2
  local map=""; for kv in "$@"; do map+="${kv}"$'\n'; done
  MAP="$map" awk '
    BEGIN { n = split(ENVIRON["MAP"], lines, "\n"); for (i = 1; i <= n; i++) if (lines[i] != "") { split(lines[i], p, "="); key[p[1]] = p[2] } }
    {
      if (match($0, /^[A-Za-z_][A-Za-z0-9_]*=/)) {
        k = substr($0, 1, RLENGTH - 1)
        if (k in key) { sub(/REPLACE_ME/, ENVIRON[key[k]]) }
      }
      print
    }' "$tpl" > "$out"
  chmod 600 "$out"
}

export PG_PW OWNER_PW APP_PW WORKER_PW JWT PLATFORM ENC AIKEY

subst "$HERE/.env.db.example" "$S/db.env" \
  POSTGRES_PASSWORD=PG_PW DB_OWNER_PASSWORD=OWNER_PW DB_APP_PASSWORD=APP_PW DB_WORKER_PASSWORD=WORKER_PW

subst "$HERE/.env.api.example" "$S/api.env" \
  ConnectionStrings__Default=APP_PW Auth__JwtSigningKey=JWT Auth__PlatformKey=PLATFORM \
  Channels__EncryptionKey=ENC "Ai__Secrets__Keys__${AI_KID}=AIKEY"

subst "$HERE/.env.worker.example" "$S/worker.env" \
  WORKER_DATABASE_URL=WORKER_PW Channels__EncryptionKey=ENC

subst "$HERE/.env.migrator.example" "$S/migrator.env" \
  ConnectionStrings__Migrator=OWNER_PW

# Нессекретні значення: origin і шлюз
sed -i "s#^Beauty__AllowedOrigins=.*#Beauty__AllowedOrigins=${ORIGIN}#; s#^Beauty__TrustedProxies=.*#Beauty__TrustedProxies=${GATEWAY}#" "$S/api.env"

cp "$HERE/.env.compose.example" "$HERE/.env.compose"
chmod 600 "$HERE/.env.compose"

unset PG_PW OWNER_PW APP_PW WORKER_PW JWT PLATFORM ENC AIKEY

echo "Створено: $S/{db,api,worker,migrator}.env та $HERE/.env.compose (mode 600). Значення не виводяться."
echo "Лишилось вручну: API_IMAGE/WORKER_IMAGE/WEB_IMAGE у .env.compose (digest-и), перевірка портів і підмережі (README §1)."
echo "Збережіть зашифровану копію api.env (майстер-ключ ШІ) окремо від дампів БД."
grep -c REPLACE_ME "$S"/*.env | sed 's/^/залишки REPLACE_ME (має бути 0): /'
