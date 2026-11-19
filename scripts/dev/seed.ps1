# Ідемпотентний seed через HTTP API: tenant, owner, заклад, послуга, майстер, канал Telegram (плейсхолдер-токен).
. (Join-Path $PSScriptRoot '_common.ps1')
$e = Read-DevEnv
if (-not $e.ContainsKey('Auth__PlatformKey')) { throw 'Немає .env.dev.local: спершу запустіть setup-db.ps1.' }
$slug = 'beauty-lab-dev'
$locName = 'Beauty Lab Dev'
$svcName = 'Стрижка'
$spName  = 'Майстер Анна'

# GUID каналу створюється один раз і зберігається в gitignored стані
$st = Get-DevState
if (-not $st) { $st = [pscustomobject]@{ tenantSlug = $slug; channelId = ([guid]::NewGuid().ToString()) } }
if (-not $st.channelId) { $st | Add-Member -NotePropertyName channelId -NotePropertyValue ([guid]::NewGuid().ToString()) -Force }
$st.tenantSlug = $slug
Save-DevState $st

if (-not (Test-ApiUp)) { Write-Host 'API не запущено, стартую...'; & (Join-Path $PSScriptRoot 'start-api.ps1'); if ($LASTEXITCODE -ne 0) { throw 'API не стартував.' } }

function Step([string]$m) { Write-Host "  - $m" }
function Need($r, [int[]]$ok, [string]$what) {
    if ($ok -notcontains $r.Status) { throw "${what}: HTTP $($r.Status) $($r.Raw)" }
}

# 1. Tenant + owner (повторний запуск дає 409 - це нормально)
$tenant = @{ name = 'Beauty Lab Dev'; slug = $slug
    modules = @('beauty_booking', 'beauty_catalog', 'beauty_clients', 'beauty_analytics', 'beauty_channels', 'beauty_ai')
    owner = @{ email = $e['DEV_OWNER_EMAIL']; fullName = 'Owner Dev'; password = $e['DEV_OWNER_PASSWORD'] } }
$r = Invoke-Api POST '/api/platform/tenants' $tenant $null @{ 'X-Platform-Key' = $e['Auth__PlatformKey'] }
if ($r.Status -eq 201) { Step 'tenant створено' } elseif ($r.Status -eq 409) { Step 'tenant вже існує' } else { throw "tenant: HTTP $($r.Status) $($r.Raw)" }
$tok = Get-OwnerToken
Step 'логін owner OK'

# 2. Заклад + години Пн-Нд 09:00-20:00
$days = 'mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'
$locHours = @{}; $spHours = @{}
foreach ($d in $days) {
    $locHours[$d] = @(@{ from = '09:00'; to = '20:00' })
    $spHours[$d]  = @(@{ from = '09:00'; to = '18:00' })
}
$r = Invoke-Api GET '/api/beauty/locations?includeInactive=true' $null $tok; Need $r 200 'locations'
$loc = @($r.Json) | Where-Object { $_.name -eq $locName } | Select-Object -First 1
if (-not $loc) {
    $r = Invoke-Api POST '/api/beauty/locations' @{ name = $locName; address = 'вул. Хрещатик, 1, Київ'; timezone = 'Europe/Kyiv' } $tok
    Need $r 200, 201 'create location'; $loc = $r.Json; Step 'заклад створено'
} else { Step 'заклад вже існує' }
$r = Invoke-Api PUT "/api/beauty/locations/$($loc.id)/working-hours" @{ workingHours = $locHours; confirm = $true } $tok; Need $r 200 'location hours'
Step 'години закладу Пн-Нд 09:00-20:00'

# 3. Послуга «Стрижка» 60 хв, ціна 500
$r = Invoke-Api GET '/api/beauty/services' $null $tok; Need $r 200 'services'
$svc = @($r.Json) | Where-Object { $_.name -eq $svcName } | Select-Object -First 1
if (-not $svc) {
    $r = Invoke-Api POST '/api/beauty/services' @{ name = $svcName; description = 'Жіноча/чоловіча стрижка'; category = 'Волосся'; durationMinutes = 60; isActive = $true } $tok
    Need $r 200, 201 'create service'; $svc = $r.Json; Step 'послугу створено'
} else { Step 'послуга вже існує' }
$r = Invoke-Api PUT "/api/beauty/services/$($svc.id)/prices" @{ price = 500 } $tok; Need $r 200, 204 'service price'
Step 'мережева ціна 500'

# 4. Майстер: графік 7 днів 09:00-18:00 у закладі, послуга призначена
$r = Invoke-Api GET '/api/beauty/specialists' $null $tok; Need $r 200 'specialists'
$sp = @($r.Json) | Where-Object { $_.name -eq $spName } | Select-Object -First 1
if (-not $sp) {
    $r = Invoke-Api POST '/api/beauty/specialists' @{ name = $spName; position = 'Стиліст'; locationIds = @($loc.id); serviceIds = @($svc.id); workingHours = $spHours } $tok
    Need $r 200, 201 'create specialist'; $sp = $r.Json; Step 'майстра створено'
} else { Step 'майстер вже існує' }
$r = Invoke-Api PUT "/api/beauty/specialists/$($sp.id)/services" @{ serviceIds = @($svc.id) } $tok; Need $r 200, 204 'specialist services'
$r = Invoke-Api PUT "/api/beauty/specialists/$($sp.id)/schedule" @{ locationId = $loc.id; workingHours = $spHours } $tok; Need $r 200, 204 'specialist schedule'
Step 'графік майстра 09:00-18:00, послуга призначена'

# 5. Канал Telegram. Токен-плейсхолдер лише при створенні; наявний токен не перезаписується (token=null).
$cid = $st.channelId
$r = Invoke-Api GET '/api/beauty/channels' $null $tok; Need $r 200 'channels'
$ch = @($r.Json) | Where-Object { $_.id -eq $cid } | Select-Object -First 1
$created = $false
$body = @{ type = 'telegram'; name = 'Telegram dev'; locationId = $loc.id; isActive = $true; settingsJson = '{"assistant":{"enabled":true,"buttons":true}}' }
if (-not $ch) { $body.token = '000000:PLACEHOLDER_replace_via_set-telegram-token'; $created = $true }
$r = Invoke-Api PUT "/api/beauty/channels/$cid" $body $tok; Need $r 200, 201 'channel'
Step $(if ($created) { 'канал створено (плейсхолдер-токен)' } else { 'канал оновлено (токен не змінено)' })

# 6. Налаштування нагадувань tenant: дзвінки увімкнено (решта полів зберігається; PUT - повна заміна, лише owner)
$r = Invoke-Api GET '/api/beauty/settings/reminders' $null $tok; Need $r 200 'reminder settings'
$rs = $r.Json
$qs = if ($rs.quietHoursStart) { $rs.quietHoursStart } else { '22:00' }
$qe = if ($rs.quietHoursEnd) { $rs.quietHoursEnd } else { '08:00' }
$tg = if ($null -ne $rs.telegramEnabled) { [bool]$rs.telegramEnabled } else { $true }
$r = Invoke-Api PUT '/api/beauty/settings/reminders' @{ telegramEnabled = $tg; callEnabled = $true; quietHoursStart = $qs; quietHoursEnd = $qe } $tok
Need $r 200 'reminder settings PUT'
Step 'нагадування: Telegram + дзвінки увімкнено'

Write-Host 'Seed завершено.'
if ($created) {
    Write-Host 'Канал створено: перезапускаю API, щоб polling підхопив канал...'
    & (Join-Path $PSScriptRoot 'start-api.ps1')
}
