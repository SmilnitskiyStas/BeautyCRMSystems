#!/usr/bin/env bash
# Підключає Telegram-вебхук на staging: UI зберігає лише токен (webhookSecret=null), тож бекенд відхиляє вебхуки.
# Скрипт: 1) входить власником, 2) задає каналу Telegram секрет вебхука (PUT /api/beauty/channels/{id}, токен лишається),
# 3) викликає Telegram setWebhook із тим самим секретом, 4) показує стан getWebhookInfo (без токена).
# Запуск людиною на сервері: cd /opt/beauty_staging/deploy && ./telegram-webhook.sh
# Токен бота, пароль власника й згенерований секрет не друкуються і не потрапляють в аргументи процесів.
set -euo pipefail
umask 077

API="http://127.0.0.1:${BEAUTY_API_PORT:-5200}"
PUBLIC="${PUBLIC_ORIGIN:-https://beauty.agrusystems.pp.ua}"
command -v python3 >/dev/null || { echo "Потрібен python3"; exit 1; }

read -r -p "Код бізнесу (slug) [beauty-staging]: " T_SLUG; T_SLUG="${T_SLUG:-beauty-staging}"
read -r -p "Пошта власника: " T_EMAIL
read -rs -p "Пароль власника: " T_PW; echo
read -rs -p "Токен Telegram-бота (з BotFather): " TG_TOKEN; echo

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
export T_SLUG T_EMAIL T_PW TMP

# 1. Вхід
python3 -c 'import json,os;e=os.environ;print(json.dumps({"tenant":e["T_SLUG"],"email":e["T_EMAIL"],"password":e["T_PW"]}))' > "$TMP/login.json"
unset T_PW
CODE="$(curl -sS -o "$TMP/login.out" -w '%{http_code}' -X POST "$API/api/auth/login" -H 'Content-Type: application/json' --data-binary @"$TMP/login.json")"
rm -f "$TMP/login.json"
[ "$CODE" = 200 ] || { echo "Вхід не вдався (HTTP $CODE)"; exit 1; }
ACCESS="$(python3 -c "import json;print(json.load(open('$TMP/login.out'))['accessToken'])")"
rm -f "$TMP/login.out"
AUTH() { printf 'header = "Authorization: Bearer %s"\n' "$ACCESS"; }

# 2. Знайти канал Telegram
curl -sS -o "$TMP/channels.json" -K <(AUTH) "$API/api/beauty/channels"
CHANNEL_ID="$(python3 - <<'PY'
import json, os
rows = json.load(open(os.environ["TMP"] + "/channels.json"))
r = next((x for x in rows if x.get("type") == "telegram"), None)
print(r["id"] if r else "")
PY
)"
[ -n "$CHANNEL_ID" ] || { echo "Канал Telegram не знайдено: спершу натисніть «Підключити Telegram» у застосунку."; exit 1; }

# 3. Секрет вебхука -> канал (токен null = залишити наявний)
TG_SECRET="$(openssl rand -hex 32)"
export TG_SECRET CHANNEL_ID
python3 - <<'PY' > "$TMP/put.json"
import json, os
rows = json.load(open(os.environ["TMP"] + "/channels.json"))
r = next(x for x in rows if x.get("type") == "telegram")
print(json.dumps({
    "type": "telegram", "name": r.get("name"), "locationId": r.get("locationId"),
    "isActive": True, "token": None, "webhookSecret": os.environ["TG_SECRET"],
    "settingsJson": r.get("settingsJson"),
}))
PY
CODE="$(curl -sS -o /dev/null -w '%{http_code}' -X PUT "$API/api/beauty/channels/${CHANNEL_ID}" -K <(AUTH) -H 'Content-Type: application/json' --data-binary @"$TMP/put.json")"
rm -f "$TMP/put.json" "$TMP/channels.json"
case "$CODE" in 200|201|204) echo "Секрет вебхука збережено в каналі (HTTP $CODE).";; *) echo "Не вдалося зберегти секрет (HTTP $CODE)"; exit 1;; esac
unset ACCESS

# 4. setWebhook у Telegram (токен і секрет через stdin curl-конфіга)
URL="${PUBLIC}/api/beauty/webhooks/telegram/${CHANNEL_ID}"
RES="$(curl -sS -K - <<EOF
url = "https://api.telegram.org/bot${TG_TOKEN}/setWebhook"
data-urlencode = "url=${URL}"
data-urlencode = "secret_token=${TG_SECRET}"
data-urlencode = "allowed_updates=[\"message\",\"callback_query\"]"
EOF
)"
echo "setWebhook: $(python3 -c 'import json,sys;d=json.loads(sys.argv[1]);print(d.get("ok"),d.get("description",""))' "$RES")"
unset TG_SECRET

# 5. Стан вебхука (без токена у виводі)
INFO="$(curl -sS -K - <<EOF
url = "https://api.telegram.org/bot${TG_TOKEN}/getWebhookInfo"
EOF
)"
python3 -c 'import json,sys;r=json.loads(sys.argv[1]).get("result",{});print({k:r.get(k) for k in ("url","pending_update_count","last_error_date","last_error_message")})' "$INFO"
unset TG_TOKEN
echo "Тепер напишіть боту повідомлення і перевірте Inbox."
