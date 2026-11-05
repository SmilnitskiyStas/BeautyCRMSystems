# Встановлює справжній токен Telegram-бота в канал і перезапускає API. Токен не друкується й не логується.
. (Join-Path $PSScriptRoot '_common.ps1')
$st = Get-DevState
if (-not $st -or -not $st.channelId) { throw 'Спершу виконайте seed.ps1.' }
if (-not (Test-ApiUp)) { throw 'API не запущено (start-api.ps1).' }
$tok = Get-OwnerToken

$token = Read-SecretPlain 'Токен Telegram-бота (від @BotFather, ввід приховано)'
if ([string]::IsNullOrWhiteSpace($token)) { throw 'Порожній токен.' }

# Беремо поточні поля каналу, щоб PUT (повна заміна) нічого не скинув
$r = Invoke-Api GET '/api/beauty/channels' $null $tok
$ch = @($r.Json) | Where-Object { $_.id -eq $st.channelId } | Select-Object -First 1
if (-not $ch) { throw 'Канал не знайдено; виконайте seed.ps1.' }
$body = @{ type = 'telegram'; name = $ch.name; locationId = $ch.locationId; isActive = $true
    settingsJson = '{"assistant":{"enabled":true,"buttons":true}}'; token = $token }
$r = Invoke-Api PUT "/api/beauty/channels/$($st.channelId)" $body $tok
$token = $null; $body = $null
if ($r.Status -ne 200 -and $r.Status -ne 201) { throw "PUT каналу: HTTP $($r.Status)" }
Write-Host 'Токен збережено (зашифровано в БД). Перезапускаю API...'
& (Join-Path $PSScriptRoot 'start-api.ps1')
Write-Host 'Перед тестом: у getWebhookInfo бота url має бути порожнім (інакше polling на паузі 60 с, див. runbook).'
