# Запуск API у фоні (http://localhost:5000). Зупиняє лише процес, запущений цим скриптом (за PID).
param([int]$WaitSeconds = 180)
. (Join-Path $PSScriptRoot '_common.ps1')

# 1. Зупинка попереднього екземпляра за збереженим PID (дерево процесів, лише наш PID)
if (Test-Path $script:PidFile) {
    $old = [int](Get-Content $script:PidFile -Raw).Trim()
    if (Get-Process -Id $old -ErrorAction SilentlyContinue) {
        Write-Host "Зупиняю попередній API (PID $old)..."
        # taskkill може повідомити про вже завершений дочірній процес: це не помилка (у PS 5.1 stderr стає винятком)
        try { & taskkill /PID $old /T /F *> $null } catch { }
        Stop-Process -Id $old -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    Remove-Item $script:PidFile -Force
}

# 2. Середовище
$e = Read-DevEnv
if (-not $e.ContainsKey('DB_APP_PASSWORD')) { throw 'Немає .env.dev.local: спершу запустіть setup-db.ps1.' }
foreach ($k in $e.Keys) {
    if ($k -like 'DB_*' -or $k -like 'DEV_*') { continue }   # паролі БД/owner не потрібні процесу API напряму
    Set-Item -Path "Env:\$k" -Value $e[$k]
}
$env:ConnectionStrings__Default = "Host=localhost;Port=55432;Database=beautycrm;Username=beautycrm_app;Password=$($e['DB_APP_PASSWORD'])"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = $script:ApiUrl
${env:Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command} = 'Warning'   # менше SQL-шуму в логу
$env:Channels__UseMocks = 'false'
$env:Beauty__AllowedOrigins = 'http://localhost:3100'
$env:ChatAssistant__Enabled = 'true'
$env:ChatButtons__Enabled = 'true'
$env:ChatPhone__Enabled = 'true'   # dev: збір телефону для дзвінка (юридичний gate відкритий, поза dev вимкнено)
$env:Marketing__Enabled = 'true'   # dev: маркетинг (згода, розсилки, купони); AdminGrant лишається вимкненим; юридичний gate відкритий, поза dev не вмикати
$env:Ai__RateLimit__PerTenantPerMinute = '120'   # dev: щоб діалог із кількома кроками tools не впирався у хвилинний ліміт tenant
$env:Channels__Telegram__Polling__Enabled = 'true'
$st = Get-DevState
if ($st -and $st.channelId) { $env:Channels__Telegram__Polling__ChannelIds__0 = $st.channelId }
else { Write-Host 'УВАГА: канал ще не створено (seed.ps1), polling не матиме каналів.' }

# 3. Запуск
# Порт має бути вільний: інакше «готовність» підтвердив би старий процес, а нова збірка не стартувала б.
$busy = Get-NetTCPConnection -LocalPort ([uri]$script:ApiUrl).Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($busy) {
    Write-Host "Порт $(([uri]$script:ApiUrl).Port) зайнятий процесом PID $($busy.OwningProcess) (старий API?)."
    Write-Host "Зупиніть його (у PowerShell: taskkill /PID $($busy.OwningProcess) /T /F) і запустіть скрипт знову."
    exit 1
}
Remove-Item $script:LogFile, $script:ErrFile -Force -ErrorAction SilentlyContinue
$proj = Join-Path $script:Root 'backend\BeautyCrm.Api'
$p = Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', $proj, '--no-launch-profile') `
    -WorkingDirectory $script:Root -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $script:LogFile -RedirectStandardError $script:ErrFile
Set-Content -Path $script:PidFile -Value $p.Id
Write-Host "API запускається, PID $($p.Id), лог: scripts\dev\.state\api.log"

# 4. Очікування готовності
$ok = $false
for ($i = 0; $i -lt $WaitSeconds; $i += 2) {
    if ($p.HasExited) { break }
    if (Test-ApiUp) { $ok = $true; break }
    Start-Sleep -Seconds 2
}
if ($ok) { Write-Host "API готовий: $($script:ApiUrl)/swagger" }
else {
    Write-Host 'API не стартував. Кінець логів:'
    foreach ($f in @($script:ErrFile, $script:LogFile)) { if (Test-Path $f) { Get-Content $f -Tail 15 } }
    exit 1
}
