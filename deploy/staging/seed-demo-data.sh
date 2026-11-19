#!/usr/bin/env bash
# Синтетичні демо-дані для staging через API (відтворює scripts/dev/create-salon.ps1 -WithDemoData):
# заклад 09:00-20:00 (Europe/Kyiv), послуга «Стрижка» 60 хв / 500 грн, майстер на 7 днів 09:00-18:00.
# Причина: в UI немає створення послуг і майстрів (є лише заклади), тому для перевірки запису (/book) дані створюються так.
# Запуск людиною на сервері: cd /opt/beauty_staging/deploy && ./seed-demo-data.sh
# Пароль власника вводиться приховано і не потрапляє в аргументи процесів. Лише синтетичні дані. Ідемпотентність: якщо
# у tenant уже є заклад/послуга/майстер, відповідний крок пропускається.
set -euo pipefail
umask 077

API="http://127.0.0.1:${BEAUTY_API_PORT:-5200}"
command -v python3 >/dev/null || { echo "Потрібен python3"; exit 1; }

read -r -p "Код бізнесу (slug) [beauty-staging]: " T_SLUG; T_SLUG="${T_SLUG:-beauty-staging}"
read -r -p "Пошта власника: " T_EMAIL
read -rs -p "Пароль власника: " T_PW; echo

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
export T_SLUG T_EMAIL T_PW TMP

python3 -c 'import json,os;e=os.environ;print(json.dumps({"tenant":e["T_SLUG"],"email":e["T_EMAIL"],"password":e["T_PW"]}))' > "$TMP/login.json"
unset T_PW
CODE="$(curl -sS -o "$TMP/login.out" -w '%{http_code}' -X POST "$API/api/auth/login" -H 'Content-Type: application/json' --data-binary @"$TMP/login.json")"
rm -f "$TMP/login.json"
[ "$CODE" = 200 ] || { echo "Вхід не вдався (HTTP $CODE)"; exit 1; }
ACCESS="$(python3 -c "import json;print(json.load(open('$TMP/login.out'))['accessToken'])")"
rm -f "$TMP/login.out"

# api METHOD PATH [JSON]: відповідь у $TMP/out.json, код у $CODE
api() {
  local m="$1" p="$2" body="${3:-}"
  if [ -n "$body" ]; then
    CODE="$(curl -sS -o "$TMP/out.json" -w '%{http_code}' -X "$m" "$API$p" -K <(printf 'header = "Authorization: Bearer %s"\n' "$ACCESS") \
      -H 'Content-Type: application/json' --data-binary "$body")"
  else
    CODE="$(curl -sS -o "$TMP/out.json" -w '%{http_code}' -X "$m" "$API$p" -K <(printf 'header = "Authorization: Bearer %s"\n' "$ACCESS"))"
  fi
}
need() { case " $2 " in *" $CODE "*) ;; *) echo "$1: HTTP $CODE"; exit 1;; esac; }
jget() { python3 -c "import json,sys;d=json.load(open('$TMP/out.json'));$1"; }
count() { python3 -c "import json;d=json.load(open('$TMP/out.json'));print(len(d if isinstance(d,list) else d.get('items',[])))"; }
first_id() { python3 -c "import json;d=json.load(open('$TMP/out.json'));d=d if isinstance(d,list) else d.get('items',[]);print(d[0]['id'] if d else '')"; }

HOURS_LOC="$(python3 -c 'import json;print(json.dumps({d:[{"from":"09:00","to":"20:00"}] for d in ["mon","tue","wed","thu","fri","sat","sun"]}))')"
HOURS_SP="$(python3 -c 'import json;print(json.dumps({d:[{"from":"09:00","to":"18:00"}] for d in ["mon","tue","wed","thu","fri","sat","sun"]}))')"

# Заклад
api GET /api/beauty/locations; need "список закладів" "200"
LOC="$(first_id)"
if [ -z "$LOC" ]; then
  api POST /api/beauty/locations '{"name":"Beauty Staging","address":"вул. Хрещатик, 1, Київ","timezone":"Europe/Kyiv"}'; need "заклад" "200 201"
  LOC="$(jget "print(d['id'])")"
  api PUT "/api/beauty/locations/$LOC/working-hours" "{\"workingHours\":$HOURS_LOC,\"confirm\":true}"; need "години закладу" "200"
  echo "Заклад створено."
else echo "Заклад уже є, пропускаю."; fi

# Послуга
api GET /api/beauty/services; need "список послуг" "200"
SVC="$(first_id)"
if [ -z "$SVC" ]; then
  api POST /api/beauty/services '{"name":"Стрижка","description":"Жіноча/чоловіча стрижка","category":"Волосся","durationMinutes":60,"isActive":true}'; need "послуга" "200 201"
  SVC="$(jget "print(d['id'])")"
  api PUT "/api/beauty/services/$SVC/prices" '{"price":500}'; need "ціна" "200 204"
  echo "Послугу «Стрижка» створено."
else echo "Послуга вже є, пропускаю."; fi

# Майстер
api GET /api/beauty/specialists; need "список майстрів" "200"
SP="$(first_id)"
if [ -z "$SP" ]; then
  api POST /api/beauty/specialists "{\"name\":\"Майстер Анна\",\"position\":\"Стиліст\",\"locationIds\":[\"$LOC\"],\"serviceIds\":[\"$SVC\"],\"workingHours\":$HOURS_SP}"; need "майстер" "200 201"
  SP="$(jget "print(d['id'])")"
  api PUT "/api/beauty/specialists/$SP/services" "{\"serviceIds\":[\"$SVC\"]}"; need "послуги майстра" "200 204"
  api PUT "/api/beauty/specialists/$SP/schedule" "{\"locationId\":\"$LOC\",\"workingHours\":$HOURS_SP}"; need "графік майстра" "200 204"
  echo "Майстра створено."
else echo "Майстер уже є, пропускаю."; fi

echo "Готово: заклад, послуга й майстер є. Оновіть сторінки застосунку."
