# Спільні функції для scripts/dev (PowerShell 5.1+). Секрети не друкуються.
$ErrorActionPreference = 'Stop'
$script:Root      = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:EnvFile   = Join-Path $script:Root '.env.dev.local'
$script:StateDir  = Join-Path $PSScriptRoot '.state'
$script:StateFile = Join-Path $script:StateDir 'dev-state.json'
$script:PidFile   = Join-Path $script:StateDir 'api.pid'
$script:LogFile   = Join-Path $script:StateDir 'api.log'
$script:ErrFile   = Join-Path $script:StateDir 'api.err.log'
$script:ApiUrl    = 'http://localhost:5000'
$script:Container = 'beautycrm-dev'
if (-not (Test-Path $script:StateDir)) { New-Item -ItemType Directory -Path $script:StateDir | Out-Null }

# Читає .env.dev.local (KEY=VALUE) у хеш-таблицю.
function Read-DevEnv {
    $h = @{}
    if (Test-Path $script:EnvFile) {
        foreach ($line in [IO.File]::ReadAllLines($script:EnvFile, [Text.Encoding]::UTF8)) {
            if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
            $i = $line.IndexOf('=')
            $h[$line.Substring(0, $i).Trim()] = $line.Substring($i + 1)
        }
    }
    return $h
}

# Дописує відсутні ключі у .env.dev.local (наявні не змінює).
function Add-DevEnvKeys([hashtable]$Keys) {
    $cur = Read-DevEnv
    $add = @()
    foreach ($k in $Keys.Keys) { if (-not $cur.ContainsKey($k)) { $add += "$k=$($Keys[$k])" } }
    if ($add.Count -gt 0) {
        $prefix = ''
        if ((Test-Path $script:EnvFile) -and -not ([IO.File]::ReadAllText($script:EnvFile).EndsWith("`n"))) { $prefix = "`n" }
        [IO.File]::AppendAllText($script:EnvFile, $prefix + (($add -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
    }
}

function New-RandomB64([int]$Bytes) {
    $b = New-Object byte[] $Bytes
    $r = [Security.Cryptography.RandomNumberGenerator]::Create(); $r.GetBytes($b); $r.Dispose()
    return [Convert]::ToBase64String($b)
}
function New-RandomHex([int]$Bytes) {
    $b = New-Object byte[] $Bytes
    $r = [Security.Cryptography.RandomNumberGenerator]::Create(); $r.GetBytes($b); $r.Dispose()
    return (($b | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Get-DevState {
    if (Test-Path $script:StateFile) { return (Get-Content $script:StateFile -Raw -Encoding UTF8 | ConvertFrom-Json) }
    return $null
}
function Save-DevState($obj) {
    [IO.File]::WriteAllText($script:StateFile, ($obj | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
}

# HTTP-виклик: тіло йде UTF-8 байтами. Повертає @{Status; Json; Raw}; не кидає виняток на не-2xx.
function Invoke-Api([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null, [hashtable]$Headers = @{}) {
    $req = [Net.HttpWebRequest]::Create($script:ApiUrl + $Path)
    $req.Method = $Method
    $req.Timeout = 60000
    $req.Accept = 'application/json'
    foreach ($k in $Headers.Keys) { $req.Headers[$k] = $Headers[$k] }
    if ($Token) { $req.Headers['Authorization'] = "Bearer $Token" }
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 10 -Compress }
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $req.ContentType = 'application/json; charset=utf-8'
        $req.ContentLength = $bytes.Length
        $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
    }
    try { $resp = $req.GetResponse() } catch [Net.WebException] {
        if ($null -eq $_.Exception.Response) { return @{ Status = 0; Json = $null; Raw = $_.Exception.Message } }
        $resp = $_.Exception.Response
    }
    $sr = New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
    $raw = $sr.ReadToEnd(); $sr.Close()
    $status = [int]$resp.StatusCode; $resp.Close()
    $j = $null
    if ($raw) { try { $j = $raw | ConvertFrom-Json } catch { } }
    return @{ Status = $status; Json = $j; Raw = $raw }
}

# Вхід власника tenant; повертає access-токен (не друкується).
function Get-OwnerToken {
    $e = Read-DevEnv; $st = Get-DevState
    $r = Invoke-Api POST '/api/auth/login' @{ tenant = $st.tenantSlug; email = $e['DEV_OWNER_EMAIL']; password = $e['DEV_OWNER_PASSWORD'] }
    if ($r.Status -ne 200) { throw "Логін owner не вдався: HTTP $($r.Status). API запущено і seed.ps1 виконано?" }
    return $r.Json.accessToken
}

function Test-ApiUp {
    $r = Invoke-Api GET '/swagger/v1/swagger.json'
    return ($r.Status -eq 200)
}

# Запит секрету без ехо; plain-рядок живе лише в пам'яті процесу.
function Read-SecretPlain([string]$Prompt) {
    $ss = Read-Host -AsSecureString $Prompt
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}
