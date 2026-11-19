# Стан локального dev-середовища (без секретів).
. (Join-Path $PSScriptRoot '_common.ps1')
$st = Get-DevState

Write-Host '== Контейнер =='
docker ps -a --filter "name=^/$($script:Container)$" --format '{{.Names}}: {{.Status}} {{.Ports}}'

Write-Host '== API =='
$pidTxt = if (Test-Path $script:PidFile) { (Get-Content $script:PidFile -Raw).Trim() } else { $null }
$alive = $pidTxt -and (Get-Process -Id ([int]$pidTxt) -ErrorAction SilentlyContinue)
Write-Host "PID: $pidTxt (процес живий: $([bool]$alive))"
$up = Test-ApiUp
Write-Host "swagger/health: $(if ($up) { 'OK (200)' } else { 'недоступний' })"

Write-Host '== Канал =='
Write-Host "tenant: $(if ($st) { $st.tenantSlug } else { '-' }); GUID каналу: $(if ($st -and $st.channelId) { 'є (' + $st.channelId + ')' } else { 'немає' })"

Write-Host '== Polling (за логом) =='
if (Test-Path $script:LogFile) {
    $m = Select-String -Path $script:LogFile -Pattern 'telegram_poll|telegram_polling' | Select-Object -Last 5
    if ($m) { $m | ForEach-Object { $_.Line.Trim() } } else { Write-Host 'записів telegram_poll* немає' }
    $stopped = Select-String -Path $script:LogFile -Pattern 'telegram_polling_stopped' | Select-Object -Last 1
    Write-Host "polling зупинено: $(if ($stopped) { $stopped.Line.Trim() } else { 'ні' })"
} else { Write-Host 'лог відсутній' }

Write-Host '== ШІ =='
if ($up -and $st) {
    try {
        $tok = Get-OwnerToken
        $r = Invoke-Api GET '/api/beauty/ai/settings' $null $tok
        if ($r.Status -eq 200) {
            $j = $r.Json
            $own = $j.keys | Where-Object { $_.provider -eq 'anthropic' } | Select-Object -First 1
            Write-Host "keySource=$($j.keySource) mode=$($j.mode) ключ anthropic: $(if ($own -and $own.hasKey) { 'збережено, last4 ' + $own.last4 } else { 'немає' })"
            Write-Host "status.chat: state=$($j.status.chat.state) reason=$($j.status.chat.reason)"
        } else { Write-Host "GET /ai/settings: HTTP $($r.Status)" }
    } catch { Write-Host "Не вдалося прочитати налаштування ШІ: $($_.Exception.Message)" }
}

Write-Host '== Воркер =='
$WorkerPidFile = Join-Path $script:StateDir 'worker.pid'
$WorkerLog = Join-Path $script:StateDir 'worker.log'
$WorkerErr = Join-Path $script:StateDir 'worker.err.log'
$wpid = if (Test-Path $WorkerPidFile) { (Get-Content $WorkerPidFile -Raw).Trim() } else { $null }
$walive = $wpid -and (Get-Process -Id ([int]$wpid) -ErrorAction SilentlyContinue)
Write-Host "PID: $wpid (процес живий: $([bool]$walive))"
foreach ($f in @($WorkerErr, $WorkerLog)) {
    if ((Test-Path $f) -and (Get-Item $f).Length -gt 0) {
        Write-Host "останні рядки $(Split-Path $f -Leaf):"
        Get-Content $f -Tail 5 | ForEach-Object { '  ' + $_ }
    }
}
$cnt = { param($sql) (docker exec $script:Container psql -U postgres -d beautycrm -tAc $sql | Out-String).Trim() }
Write-Host "нагадувань scheduled: $(& $cnt "SELECT count(*) FROM beauty_reminders WHERE status='scheduled'")"
Write-Host "завдань дзвінків open: $(& $cnt "SELECT count(*) FROM beauty_call_tasks WHERE status='open'")"

Write-Host '== Кнопки =='
Write-Host 'API: ChatButtons__Enabled=true, ChatAssistant__Enabled=true (start-api.ps1); воркер: WORKER_REMINDERS_ENABLED=true, WORKER_REMINDER_BUTTONS_ENABLED=true (start-worker.ps1)'
if ($up -and $st) {
    try {
        $tok2 = Get-OwnerToken
        $r = Invoke-Api GET '/api/beauty/channels' $null $tok2
        $c = @($r.Json) | Where-Object { $_.id -eq $st.channelId } | Select-Object -First 1
        if ($c) { Write-Host "канал: isActive=$($c.isActive) settings=$($c.settingsJson)" } else { Write-Host 'канал не знайдено' }
    } catch { Write-Host "Не вдалося прочитати канал: $($_.Exception.Message)" }
}
