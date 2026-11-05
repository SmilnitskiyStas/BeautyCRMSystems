# Зберігає ключ Anthropic (власний) і налаштовує ШІ: own-ключ, моделі, режим confirm, обов'язкові ліміти.
# Ключ не друкується й не логується.
. (Join-Path $PSScriptRoot '_common.ps1')
if (-not (Test-ApiUp)) { throw 'API не запущено (start-api.ps1).' }
$tok = Get-OwnerToken

$key = Read-SecretPlain 'Ключ Anthropic (ввід приховано)'
if ([string]::IsNullOrWhiteSpace($key)) { throw 'Порожній ключ.' }

# 1. Ключ із перевіркою (відхилений не зберігається)
$r = Invoke-Api PUT '/api/beauty/ai/settings/keys/anthropic' @{ apiKey = $key; forRole = 'chat'; verify = $true; activate = $true } $tok
$key = $null
if ($r.Status -ne 200) {
    $code = if ($r.Json -and $r.Json.code) { $r.Json.code } else { '' }
    throw "Збереження ключа: HTTP $($r.Status) $code"
}
Write-Host "Ключ збережено (last4: $($r.Json.last4))."

# 2. Налаштування: own, моделі, confirm, ліміти (обов'язкові для own-ключа)
$s = @{
    chat = @{ provider = 'anthropic'; model = 'claude-haiku-5-5' }
    analytics = @{ provider = 'anthropic'; model = 'claude-sonnet-5-5' }
    mode = 'confirm'
    promptAddendum = $null
    limits = @{ monthlyTokenLimit = 200000; monthlyRequestLimit = 300; maxOutputTokensPerRequest = 1024 }
}
$r = Invoke-Api PUT '/api/beauty/ai/settings' $s $tok
if ($r.Status -ne 200) { throw "PUT /ai/settings: HTTP $($r.Status) $($r.Raw)" }
$r = Invoke-Api PUT '/api/beauty/ai/settings/key-source' @{ keySource = 'own' } $tok
if ($r.Status -ne 200) { throw "key-source: HTTP $($r.Status) $($r.Raw)" }

$r = Invoke-Api GET '/api/beauty/ai/settings' $null $tok
if ($r.Status -eq 200) {
    Write-Host "keySource=$($r.Json.keySource) mode=$($r.Json.mode) status.chat=$($r.Json.status.chat.state) $($r.Json.status.chat.reason)"
}
