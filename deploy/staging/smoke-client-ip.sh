#!/usr/bin/env bash
# Smoke ADR-024 F-1 (§31.9 п.5, §31.15 п.19): справжній IP клієнта за ланцюгом nginx -> web (BFF) -> api.
# Запускає ЛЮДИНА на сервері staging ПІСЛЯ застосування змін F-1. Лише синтетичні дані; секрети не друкуються.
# Перевіряє: (а) два різні клієнтські IP мають окремі кошики ліміту auth; (б) підроблений XFF від клієнта не міняє ключ.
# Ліміт auth = 10 запитів / 60 с на IP. Скрипт шле неіснуючий логін, відповіді 401/423 = дозволено, 429 = ліміт.
# Використання: BEAUTY_WEB_PORT=3400 ./smoke-client-ip.sh   (напряму на loopback web, nginx імітується заголовком X-Forwarded-For)
# Увага: прямий loopback-запит САМ підставляє XFF, як це робить nginx; тому для кроку (б) окремо див. README §6.6 (через nginx).
set -euo pipefail
WEB="http://127.0.0.1:${BEAUTY_WEB_PORT:?set BEAUTY_WEB_PORT}"
PATH_LOGIN="/api/session/login"
ORIGIN="${SMOKE_ORIGIN:?set SMOKE_ORIGIN=https://beauty.<DOMAIN>}"
HOSTH="${ORIGIN#https://}"
A="203.0.113.11"; B="203.0.113.22"   # TEST-NET-3, не реальні адреси

hit() { # $1 = XFF
  curl -s -o /dev/null -w '%{http_code}' -X POST "$WEB$PATH_LOGIN" \
    -H "Origin: $ORIGIN" -H "Host: $HOSTH" -H "X-Forwarded-Host: $HOSTH" -H "Content-Type: application/json" \
    -H "X-Forwarded-For: $1" \
    --data '{"email":"smoke-f1-'"$RANDOM"'@example.invalid","password":"x"}'
}

echo "== (а) 11 запитів з IP A ($A): очікуємо 429 на 11-му"
last=""; for i in $(seq 1 11); do last=$(hit "$A"); done
echo "A #11 -> $last"; [ "$last" = "429" ] || { echo "FAIL: IP A не отримав 429"; exit 1; }

echo "== (а) перший запит з IP B ($B) одразу після цього: очікуємо НЕ 429"
b=$(hit "$B"); echo "B #1 -> $b"
[ "$b" != "429" ] || { echo "FAIL: B заблоковано разом з A (спільний кошик, F-1 не виправлено)"; exit 1; }

echo "== (б) B з підробленим ланцюгом '$B, $A' не має потрапити в кошик A і не обходить власний"
# Якщо ланцюг розбирається справа наліво до недовіреного адреса, ключ = останній недовірений ($A), тобто кошик A (вже 429).
# Підробка не повинна давати НОВИЙ кошик: очікуємо 429, а не 401.
s=$(hit "$B, $A"); echo "spoof chain -> $s"
[ "$s" = "429" ] || echo "WARN: ланцюг '$B, $A' не мапиться на A; зафіксуйте поведінку в task log TASK-871 (див. README §6.6)"
echo "== Далі: ліміт скидається за 60 с; перевірка IP у логах API: README §6.6 п.3"
echo "OK (а). Результат (б) і п.3 README §6.6 внесіть у .claude/logs/tasks/TASK-871.md"
