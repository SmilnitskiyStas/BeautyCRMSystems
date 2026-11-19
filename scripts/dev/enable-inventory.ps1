# Вмикає модуль beauty_inventory для dev-салону (лише оператор платформи). Секрети не друкуються.
param([string]$Slug = "beauty-lab-dev")
. (Join-Path $PSScriptRoot "_common.ps1")
$e = Read-DevEnv
if (-not $e.ContainsKey("Auth__PlatformKey")) { throw "Немає .env.dev.local: спершу запустіть setup-db.ps1." }
$id = (docker exec $script:Container psql -U postgres -d beautycrm -Atc "select id from tenants where slug='$Slug'").Trim()
if (-not $id) { throw "Салон '$Slug' не знайдено." }
$mods = (docker exec $script:Container psql -U postgres -d beautycrm -Atc "select array_to_string(modules, ',') from tenants where id='$id'").Trim().Split(",") | Where-Object { $_ }
if ($mods -contains "beauty_inventory") { Write-Host "Модуль beauty_inventory уже увімкнено для $Slug."; return }
$new = @($mods) + "beauty_inventory"
$r = Invoke-Api PATCH "/api/platform/tenants/$id" @{ modules = $new } $null @{ "X-Platform-Key" = $e["Auth__PlatformKey"] }
Write-Host "Модуль beauty_inventory увімкнено для $Slug (всього модулів: $($new.Count))."
