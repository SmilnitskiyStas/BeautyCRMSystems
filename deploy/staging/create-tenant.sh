#!/usr/bin/env bash
# Створює перший (синтетичний) tenant на staging через POST /api/platform/tenants (runbook §5).
# Запуск людиною на сервері: cd /opt/beauty_staging/deploy && ./create-tenant.sh
# Ключ платформи береться з api.env і не друкується; пароль власника вводиться приховано і не потрапляє в аргументи процесів.
# Лише синтетичні дані: вигадана назва, службова пошта, окремий пароль (не з інших сервісів).
set -euo pipefail
umask 077

SECRETS_DIR="${SECRETS_DIR:-/opt/beauty_staging/secrets}"
API_PORT="${BEAUTY_API_PORT:-5200}"
command -v python3 >/dev/null || { echo "Потрібен python3 (для безпечного формування JSON)"; exit 1; }

KEY="$(grep '^Auth__PlatformKey=' "${SECRETS_DIR}/api.env" | cut -d= -f2-)"
[ -n "$KEY" ] || { echo "Auth__PlatformKey не знайдено в ${SECRETS_DIR}/api.env"; exit 1; }

read -r -p "Назва закладу (синтетична) [Beauty Staging]: " T_NAME; T_NAME="${T_NAME:-Beauty Staging}"
read -r -p "Код бізнесу (slug, a-z 0-9 -, 3-40) [beauty-staging]: " T_SLUG; T_SLUG="${T_SLUG:-beauty-staging}"
read -r -p "Пошта власника (службова, не особиста): " T_EMAIL
read -r -p "Ім'я власника [Власник]: " T_OWNER; T_OWNER="${T_OWNER:-Власник}"
read -rs -p "Пароль власника (мін. 12 символів, унікальний): " T_PW; echo
read -rs -p "Повторіть пароль: " T_PW2; echo
[ "$T_PW" = "$T_PW2" ] || { echo "Паролі не збігаються"; exit 1; }
[ "${#T_PW}" -ge 12 ] || { echo "Пароль закороткий (мін. 12 символів)"; exit 1; }

TMP="$(mktemp)"; RESP="$(mktemp)"; trap 'rm -f "$TMP" "$RESP"' EXIT
export T_NAME T_SLUG T_EMAIL T_OWNER T_PW
python3 - > "$TMP" <<'PY'
import json, os
e = os.environ
print(json.dumps({
    "name": e["T_NAME"], "slug": e["T_SLUG"],
    "modules": ["beauty_booking", "beauty_catalog", "beauty_clients", "beauty_analytics", "beauty_channels", "beauty_ai"],
    "owner": {"email": e["T_EMAIL"], "fullName": e["T_OWNER"], "password": e["T_PW"]},
}))
PY
unset T_PW T_PW2

CODE="$(curl -sS -o "$RESP" -w '%{http_code}' -X POST "http://127.0.0.1:${API_PORT}/api/platform/tenants" \
  -K <(printf 'header = "X-Platform-Key: %s"\n' "$KEY") \
  -H 'Content-Type: application/json' --data-binary @"$TMP")"
rm -f "$TMP"
echo "HTTP ${CODE}"
case "$CODE" in
  20*) ;;
  409) echo "Код бізнесу або пошта вже зайняті."; exit 1;;
  422) echo "API відхилило дані: перевірте назву, slug, пошту й пароль."; exit 1;;
  401|403) echo "Ключ платформи не прийнято."; exit 1;;
  *) echo "Неочікувана відповідь (тіло не показую: може містити чутливе)."; exit 1;;
esac

# Показуємо лише ідентифікатор і slug; решту відповіді не друкуємо.
TENANT_ID="$(python3 -c "
import json,sys
d=json.load(open('$RESP'))
t=d.get('tenant',d)
print(t.get('id') or t.get('tenantId') or d.get('tenantId') or d.get('id') or '')
")"
echo "Tenant створено. slug=${T_SLUG} id=${TENANT_ID}"

if [ -n "$TENANT_ID" ] && read -r -p "Додати tenant у worker.env (WORKER_TENANT_IDS) і перезапустити worker? [y/N] " A && [[ "$A" == y || "$A" == Y ]]; then
  CUR="$(grep '^WORKER_TENANT_IDS=' "${SECRETS_DIR}/worker.env" | cut -d= -f2-)"
  NEW="${CUR:+${CUR},}${TENANT_ID}"
  sed -i "s#^WORKER_TENANT_IDS=.*#WORKER_TENANT_IDS=${NEW}#" "${SECRETS_DIR}/worker.env"
  docker compose --env-file .env.compose -f docker-compose.staging.yml up -d worker
fi
echo "Вхід: https://beauty.agrusystems.pp.ua:10054/login, код бізнесу ${T_SLUG}, пошта власника та ваш пароль."
