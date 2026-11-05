# Створює НОВИЙ салон (tenant) через POST /api/platform/tenants. Пароль не друкується і не зберігається.
# Автотест: змінні середовища BEAUTY_NEW_OWNER_EMAIL / BEAUTY_NEW_OWNER_PASSWORD (тоді Read-Host не викликається).
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Slug,
    [switch]$WithDemoData
)
. (Join-Path $PSScriptRoot '_common.ps1')

if ($Slug -notmatch '^[a-z0-9-]{3,40}$') { throw 'Slug: лише малі латинські літери, цифри й дефіс, довжина 3-40.' }
if ([string]::IsNullOrWhiteSpace($Name)) { throw 'Назва салону не може бути порожньою.' }
$e = Read-DevEnv
if (-not $e.ContainsKey('Auth__PlatformKey')) { throw 'Немає .env.dev.local: спершу запустіть setup-db.ps1.' }
if (-not (Test-ApiUp)) { throw 'API не запущено: виконайте scripts\dev\start-api.ps1.' }

$email = $env:BEAUTY_NEW_OWNER_EMAIL
$pwd = $env:BEAUTY_NEW_OWNER_PASSWORD
$fullName = 'Власник'
if (-not $email -or -not $pwd) {
    $email = (Read-Host 'Пошта власника').Trim()
    $fullName = (Read-Host 'Повне імʼя власника').Trim()
    $p1 = Read-SecretPlain 'Пароль (мін. 12 символів)'
    $p2 = Read-SecretPlain 'Повторіть пароль'
    if ($p1 -ne $p2) { $p1 = $null; $p2 = $null; throw 'Паролі не збігаються.' }
    $pwd = $p1; $p1 = $null; $p2 = $null
}
if ($email -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { $pwd = $null; throw 'Некоректна пошта.' }
if ([string]::IsNullOrWhiteSpace($fullName)) { $fullName = 'Власник' }
if ($pwd.Length -lt 12) { $pwd = $null; throw 'Пароль закороткий: потрібно щонайменше 12 символів.' }

$body = @{ name = $Name; slug = $Slug
    modules = @('beauty_booking', 'beauty_catalog', 'beauty_clients', 'beauty_analytics', 'beauty_channels', 'beauty_ai')
    owner = @{ email = $email; fullName = $fullName; password = $pwd } }
$r = Invoke-Api POST '/api/platform/tenants' $body $null @{ 'X-Platform-Key' = $e['Auth__PlatformKey'] }
$body = $null

switch ($r.Status) {
    201 { }
    409 { $pwd = $null; throw "Код бізнесу '$Slug' або така пошта вже зайняті. Оберіть інший slug/пошту." }
    422 { $pwd = $null; throw 'API відхилило дані (422): перевірте назву, slug, пошту й пароль (мін. 12 символів).' }
    401 { $pwd = $null; throw 'Невірний або відсутній Auth__PlatformKey (401/403): перегляньте .env.dev.local і перезапустіть API.' }
    403 { $pwd = $null; throw 'Невірний або відсутній Auth__PlatformKey (401/403): перегляньте .env.dev.local і перезапустіть API.' }
    default { $pwd = $null; throw "Не вдалося створити салон: HTTP $($r.Status)." }
}
Write-Host "Салон створено."

if ($WithDemoData) {
    function Need($x, [int[]]$ok, [string]$what) { if ($ok -notcontains $x.Status) { throw "${what}: HTTP $($x.Status)" } }
    $l = Invoke-Api POST '/api/auth/login' @{ tenant = $Slug; email = $email; password = $pwd }
    $pwd = $null
    Need $l 200 'логін власника'
    $tok = $l.Json.accessToken
    $days = 'mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'
    $locHours = @{}; $spHours = @{}
    foreach ($d in $days) { $locHours[$d] = @(@{ from = '09:00'; to = '20:00' }); $spHours[$d] = @(@{ from = '09:00'; to = '18:00' }) }

    $r = Invoke-Api POST '/api/beauty/locations' @{ name = $Name; address = 'вул. Хрещатик, 1, Київ'; timezone = 'Europe/Kyiv' } $tok
    Need $r 200, 201 'заклад'; $loc = $r.Json
    $r = Invoke-Api PUT "/api/beauty/locations/$($loc.id)/working-hours" @{ workingHours = $locHours; confirm = $true } $tok; Need $r 200 'години закладу'
    $r = Invoke-Api POST '/api/beauty/services' @{ name = 'Стрижка'; description = 'Жіноча/чоловіча стрижка'; category = 'Волосся'; durationMinutes = 60; isActive = $true } $tok
    Need $r 200, 201 'послуга'; $svc = $r.Json
    $r = Invoke-Api PUT "/api/beauty/services/$($svc.id)/prices" @{ price = 500 } $tok; Need $r 200, 204 'ціна'
    $r = Invoke-Api POST '/api/beauty/specialists' @{ name = 'Майстер Анна'; position = 'Стиліст'; locationIds = @($loc.id); serviceIds = @($svc.id); workingHours = $spHours } $tok
    Need $r 200, 201 'майстер'; $sp = $r.Json
    $r = Invoke-Api PUT "/api/beauty/specialists/$($sp.id)/services" @{ serviceIds = @($svc.id) } $tok; Need $r 200, 204 'послуги майстра'
    $r = Invoke-Api PUT "/api/beauty/specialists/$($sp.id)/schedule" @{ locationId = $loc.id; workingHours = $spHours } $tok; Need $r 200, 204 'графік майстра'
    Write-Host 'Демо-дані: заклад 09:00-20:00, послуга «Стрижка» 60 хв/500, майстер на 7 днів.'
}
$pwd = $null

Write-Host ''
Write-Host "Адреса входу : http://localhost:3100/login"
Write-Host "Код бізнесу  : $Slug"
Write-Host "Пошта        : $email"
Write-Host 'Пароль: той, що ви ввели (не показується).'
