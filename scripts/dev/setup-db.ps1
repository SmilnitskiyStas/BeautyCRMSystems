# Одноразове (ідемпотентне) налаштування: .env.dev.local, PostgreSQL 16 у Docker, ролі, міграції, GRANT.
. (Join-Path $PSScriptRoot '_common.ps1')

# 1. Секрети (лише відсутні ключі; значення не друкуються)
$kid = 'k1'   # id майстер-ключа ШІ; ім'я змінної складається нижче, значення генерується під час запуску
Add-DevEnvKeys @{
    DB_ADMIN_PASSWORD        = (New-RandomHex 24)
    DB_OWNER_PASSWORD        = (New-RandomHex 24)
    DB_APP_PASSWORD          = (New-RandomHex 24)
    DB_WORKER_PASSWORD       = (New-RandomHex 24)
    Auth__JwtSigningKey      = (New-RandomB64 48)
    Auth__PlatformKey        = (New-RandomB64 36)
    Channels__EncryptionKey  = (New-RandomB64 32)
    Ai__Secrets__ActiveKeyId = $kid
    ("Ai__Secrets__Keys__" + $kid) = (New-RandomB64 32)
    DEV_OWNER_EMAIL         = 'owner@beautylab-dev.test'
    DEV_OWNER_PASSWORD       = ('Bc-' + (New-RandomHex 12))
}
$e = Read-DevEnv
Write-Host 'Секрети: .env.dev.local готовий (значення не показуються).'

# 2. Контейнер
docker version --format '{{.Server.Version}}' *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker недоступний.' }
$exists = docker ps -a --filter "name=^/$($script:Container)$" --format '{{.Names}}'
if (-not $exists) {
    docker run -d --name $script:Container -e "POSTGRES_PASSWORD=$($e['DB_ADMIN_PASSWORD'])" -p 127.0.0.1:55432:5432 postgres:16-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker run не вдався (порт 55432 зайнятий?).' }
} else {
    $running = docker ps --filter "name=^/$($script:Container)$" --format '{{.Names}}'
    if (-not $running) { docker start $script:Container | Out-Null }
}
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    docker exec $script:Container pg_isready -U postgres -h 127.0.0.1 *> $null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $ready) { throw 'PostgreSQL не відповів за 60 с.' }
Start-Sleep -Seconds 3  # init-фаза образу перезапускає сервер один раз

# 3. Ролі й БД (ідемпотентно; паролі - hex, безпечні в SQL)
$own = $e['DB_OWNER_PASSWORD']; $app = $e['DB_APP_PASSWORD']; $wrk = $e['DB_WORKER_PASSWORD']
$sql = @"
DO `$`$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='beautycrm_owner') THEN CREATE ROLE beautycrm_owner LOGIN PASSWORD '$own'; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='beautycrm_app') THEN CREATE ROLE beautycrm_app LOGIN PASSWORD '$app' NOSUPERUSER NOBYPASSRLS; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='beautycrm_worker') THEN CREATE ROLE beautycrm_worker LOGIN PASSWORD '$wrk' NOSUPERUSER NOBYPASSRLS; END IF;
END `$`$;
ALTER ROLE beautycrm_owner PASSWORD '$own';
ALTER ROLE beautycrm_app PASSWORD '$app' NOSUPERUSER NOBYPASSRLS;
ALTER ROLE beautycrm_worker PASSWORD '$wrk' NOSUPERUSER NOBYPASSRLS;
"@
$sql | docker exec -i $script:Container psql -U postgres -v ON_ERROR_STOP=1 -q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Створення ролей не вдалось.' }
$db = docker exec $script:Container psql -U postgres -tAc "SELECT 1 FROM pg_database WHERE datname='beautycrm'"
if ($db -ne '1') { docker exec $script:Container psql -U postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE beautycrm OWNER beautycrm_owner" | Out-Null }
docker exec $script:Container psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 -q -c "GRANT CONNECT ON DATABASE beautycrm TO beautycrm_app, beautycrm_worker" | Out-Null

# 4. Міграції під owner
$env:ConnectionStrings__Migrator = "Host=localhost;Port=55432;Database=beautycrm;Username=beautycrm_owner;Password=$own"
Push-Location (Join-Path $script:Root 'backend')
try {
    dotnet ef database update --project BeautyCrm.Infrastructure --startup-project BeautyCrm.Infrastructure
    if ($LASTEXITCODE -ne 0) { throw 'dotnet ef database update не вдався.' }
} finally { Pop-Location; Remove-Item Env:\ConnectionStrings__Migrator -ErrorAction SilentlyContinue }

# 5. GRANT для app; журнал налаштувань ШІ append-only
$grants = @"
GRANT USAGE ON SCHEMA public TO beautycrm_app;
GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO beautycrm_app;
GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_app;
REVOKE UPDATE, DELETE ON beauty_ai_settings_audit FROM beautycrm_app;

-- Роль воркера: мінімальні права (runbook §6/§11; контракти §22.3, §22.5, §23)
GRANT USAGE ON SCHEMA public TO beautycrm_worker;
GRANT SELECT ON beauty_clients, beauty_client_contact_preferences, beauty_conversations, beauty_channels,
  beauty_appointments, beauty_locations, beauty_services, beauty_reminder_settings TO beautycrm_worker;
GRANT SELECT, UPDATE ON beauty_reminders TO beautycrm_worker;
GRANT SELECT, INSERT, UPDATE ON beauty_messages TO beautycrm_worker;
GRANT SELECT, INSERT ON beauty_call_tasks, beauty_chat_keyboards TO beautycrm_worker;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO beautycrm_worker;
"@
$grants | docker exec -i $script:Container psql -U postgres -d beautycrm -v ON_ERROR_STOP=1 -q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'GRANT не вдався.' }
Write-Host 'Готово: БД beautycrm, міграції застосовано, GRANT (app і worker) виконано.'
