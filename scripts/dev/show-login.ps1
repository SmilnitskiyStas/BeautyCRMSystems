# Показує в консолі дані входу в dev-салон (seed.ps1). Нічого не пише у файли/логи.
. (Join-Path $PSScriptRoot '_common.ps1')
$e = Read-DevEnv
$st = Get-DevState
if (-not $st -or -not $e.ContainsKey('DEV_OWNER_EMAIL')) { throw 'Немає стану/.env.dev.local: спершу запустіть setup-db.ps1 і seed.ps1.' }

Write-Host ''
Write-Host 'ПОПЕРЕДЖЕННЯ: Не вставляйте це в чат і не робіть скриншотів з паролем.' -ForegroundColor Yellow
Write-Host ''
Write-Host "Адреса входу : http://localhost:3100/login"
Write-Host "Код бізнесу  : $($st.tenantSlug)"
Write-Host "Пошта        : $($e['DEV_OWNER_EMAIL'])"
Write-Host "Пароль       : $($e['DEV_OWNER_PASSWORD'])"
Write-Host ''
