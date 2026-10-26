# Запуск воркера нагадувань у фоні (роль БД beautycrm_worker). Зупиняє лише власний попередній процес (за PID).
param([int]$WaitSeconds = 40)
. (Join-Path $PSScriptRoot '_common.ps1')
$WorkerPidFile = Join-Path $script:StateDir 'worker.pid'
$WorkerLog     = Join-Path $script:StateDir 'worker.log'
$WorkerErr     = Join-Path $script:StateDir 'worker.err.log'

# 1. Зупинка попереднього воркера за збереженим PID (дерево процесів, лише наш PID)
if (Test-Path $WorkerPidFile) {
    $old = [int](Get-Content $WorkerPidFile -Raw).Trim()
    if (Get-Process -Id $old -ErrorAction SilentlyContinue) {
        Write-Host "Зупиняю попередній воркер (PID $old)..."
        try { & taskkill /PID $old /T /F *> $null } catch { }
        Stop-Process -Id $old -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    Remove-Item $WorkerPidFile -Force
}

# 2. Середовище
$e = Read-DevEnv
if (-not $e.ContainsKey('DB_WORKER_PASSWORD')) { throw 'Немає DB_WORKER_PASSWORD у .env.dev.local: спершу запустіть setup-db.ps1.' }
$st = Get-DevState
$slug = if ($st -and $st.tenantSlug) { $st.tenantSlug } else { 'beauty-lab-dev' }
$tid = (docker exec $script:Container psql -U postgres -d beautycrm -tAc "SELECT id FROM tenants WHERE slug='$slug'" | Out-String).Trim()
if ($tid -notmatch '^[0-9a-fA-F-]{36}$') { throw "Tenant '$slug' не знайдено в БД: виконайте seed.ps1." }

$workerDir = Join-Path $script:Root 'worker'
if (-not (Test-Path (Join-Path $workerDir 'node_modules'))) { throw 'worker/node_modules відсутні: виконайте npm.cmd install у worker/.' }

$names = 'WORKER_DATABASE_URL', 'WORKER_TENANT_IDS', 'Channels__EncryptionKey', 'Channels__UseMocks',
    'WORKER_REMINDERS_ENABLED', 'WORKER_REMINDER_BUTTONS_ENABLED'
$env:WORKER_DATABASE_URL = "postgres://beautycrm_worker:$($e['DB_WORKER_PASSWORD'])@localhost:55432/beautycrm"
$env:WORKER_TENANT_IDS = $tid
$env:Channels__EncryptionKey = $e['Channels__EncryptionKey']
$env:Channels__UseMocks = 'false'
$env:WORKER_REMINDERS_ENABLED = 'true'
$env:WORKER_REMINDER_BUTTONS_ENABLED = 'true'   # потребує ChatButtons__Enabled=true і ChatAssistant__Enabled=true в API (start-api.ps1)

# 3. Запуск (npm.cmd: npm.ps1 блокується політикою виконання)
Remove-Item $WorkerLog, $WorkerErr -Force -ErrorAction SilentlyContinue
try {
    $p = Start-Process -FilePath 'npm.cmd' -ArgumentList @('start') -WorkingDirectory $workerDir -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $WorkerLog -RedirectStandardError $WorkerErr
} finally {
    foreach ($n in $names) { Remove-Item "Env:\$n" -ErrorAction SilentlyContinue }   # не лишаємо значення в поточній сесії
}
Set-Content -Path $WorkerPidFile -Value $p.Id
Write-Host "Воркер запускається, PID $($p.Id), лог: scripts\dev\.state\worker.log"

# 4. Перевірка, що не впав одразу (права ролі, RLS-guard)
$ok = $false
for ($i = 0; $i -lt $WaitSeconds; $i += 2) {
    Start-Sleep -Seconds 2
    if ($p.HasExited) { break }
    if ((Test-Path $WorkerLog) -and (Select-String -Path $WorkerLog -Pattern 'beauty\.reminders|poll' -Quiet)) { $ok = $true; break }
}
if (-not $ok -and -not $p.HasExited) { $ok = $true }   # живий і без помилок: перший опит ще не відбувся
if ($ok) { Write-Host 'Воркер працює (опит нагадувань щохвилини).' }
else {
    Write-Host 'Воркер завершився. Кінець логів:'
    foreach ($f in @($WorkerErr, $WorkerLog)) { if (Test-Path $f) { Get-Content $f -Tail 15 } }
    exit 1
}
