# Beauty CRM — Local Development Setup & Runbook

**Target:** Full stack local development (backend, worker, frontend) with PostgreSQL, Redis, and hot-reload.

**Prerequisites:**
- .NET 8 SDK
- Node.js 20+ with npm/pnpm
- PostgreSQL 16+ (or Docker)
- Redis (or Docker)
- Docker (optional but recommended for DB & Redis)

---

## 1. Quick Start with Docker

### 1.1 Start PostgreSQL & Redis in Docker

```bash
docker run -d \
  --name beautycrm-postgres \
  -e POSTGRES_PASSWORD=changeme \
  -e POSTGRES_DB=beautycrm \
  -v beautycrm-data:/var/lib/postgresql/data \
  -p 5432:5432 \
  postgres:16-alpine

docker run -d \
  --name beautycrm-redis \
  -p 6379:6379 \
  redis:7-alpine
```

Or use Docker Compose (`docker-compose.yml` if exists in repo).

### 1.2 Environment Setup

Copy and update `.env` from `.env.example`:

```bash
cp .env.example .env
```

**Edit `.env`:**

```
# Database
ConnectionStrings__Default=Host=localhost;Database=beautycrm;Username=postgres;Password=changeme

# Redis
REDIS_URL=redis://localhost:6379

# API Keys (get from providers or leave blank for development)
ANTHROPIC_API_KEY=sk-ant-...
TELEGRAM_BOT_TOKEN=123:ABC...
TELEGRAM_WEBHOOK_SECRET=secret-key
INSTAGRAM_APP_SECRET=...
INSTAGRAM_VERIFY_TOKEN=...

# Channels encryption key (generate: openssl rand -base64 32)
Channels__EncryptionKey=<base64-32-bytes>

# Development: allow X-Tenant-Id header (production: false)
Beauty__AllowTenantHeader=true

# JWT signing key (generate: openssl rand -base64 48)
Auth__JwtSigningKey=<base64-48-bytes>

# Platform operator key (generate: openssl rand -base64 36)
Auth__PlatformKey=<base64-36-bytes>

# Mock channels for development
Channels__UseMocks=true
```

**Generate secure keys:**

```bash
# On macOS/Linux/WSL:
openssl rand -base64 32   # Channels__EncryptionKey
openssl rand -base64 48   # Auth__JwtSigningKey
openssl rand -base64 36   # Auth__PlatformKey

# On Windows PowerShell:
[Convert]::ToBase64String([byte[]] @(1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

---

## 2. Database Setup

### 2.1 Run Migrations

```bash
cd backend

# Install EF tools globally (if not already)
dotnet tool install --global dotnet-ef

# Apply all migrations
dotnet ef database update \
  --project BeautyCrm.Infrastructure \
  --startup-project BeautyCrm.Api
```

This will:
1. Create all `beauty_*` and auth tables.
2. Set up RLS policies.
3. Create indexes and triggers.

### 2.2 Verify Database

```bash
# Connect to PostgreSQL
psql -U postgres -h localhost -d beautycrm

# Check tables
\dt beauty_*
\dt public.tenants public.users

# Check RLS
SELECT schemaname, tablename, policyname FROM pg_policies WHERE tablename LIKE 'beauty_%';
```

---

## 3. Create First Tenant (Platform Operator)

Use the platform API to create a tenant + owner user.

### 3.1 Generate Platform Key

Ensure `Auth__PlatformKey` is set in `.env` (non-empty, >=32 chars).

### 3.2 Start Backend (temporary, for API call)

```bash
cd backend/BeautyCrm.Api

# Or directly:
dotnet run
# Backend starts at https://localhost:5001
```

### 3.3 Create Tenant via API

Use curl or Postman to POST `/api/platform/tenants`:

```bash
curl -X POST https://localhost:5001/api/platform/tenants \
  -H "Content-Type: application/json" \
  -H "X-Platform-Key: <Auth__PlatformKey-value>" \
  -d '{
    "name": "Acme Salon",
    "slug": "acme-salon",
    "modules": ["beauty_booking", "beauty_catalog", "beauty_clients", "beauty_channels", "beauty_analytics", "beauty_ai"],
    "owner": {
      "email": "owner@acme.com",
      "fullName": "Jane Owner",
      "password": "SecurePass123!@"
    }
  }' \
  --insecure

# Response:
# {
#   "tenantId": "550e8400-e29b-41d4-a716-446655440000",
#   "name": "Acme Salon",
#   "slug": "acme-salon",
#   "modules": [...],
#   "ownerUserId": "..."
# }
```

Save the `tenantId` and owner email/password for later login.

### 3.4 Create Locations, Specialists, Services

After login, use the admin dashboard (or API directly) to populate:

```bash
# Example: Create location via API
curl -X POST https://localhost:5001/api/beauty/locations \
  -H "Authorization: Bearer <access-token>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Kyiv Main",
    "timezone": "Europe/Kyiv",
    "address": "123 Main St",
    "phone": "+380501234567"
  }'
```

See `api.md` for full endpoint reference.

---

## 4. Backend Setup

### 4.1 Install Dependencies & Build

```bash
cd backend
dotnet restore
dotnet build
```

### 4.2 Run Tests

```bash
# All tests (skipped if Docker not running unless BEAUTY_TEST_PG_ADMIN set)
dotnet test

# With PostgreSQL (if running)
export BEAUTY_TEST_PG_ADMIN="Host=localhost;Username=postgres;Password=changeme"
dotnet test --logger "console;verbosity=detailed"
```

### 4.3 Run Backend Server

```bash
cd backend/BeautyCrm.Api
dotnet run

# Or with watch mode (auto-reload on file changes)
dotnet watch run
```

**Output:**
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://localhost:5001
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
```

**Swagger UI:** Open https://localhost:5001/swagger in browser.

---

## 5. Worker Setup

### 5.1 Install Dependencies

```bash
cd worker
npm install
# or: pnpm install
```

### 5.2 Build & Run

```bash
# Development with watch
npm run dev

# Or production build
npm run build
npm start
```

**Output:**
```
[12:34:56] Worker ready, listening on port 3001
[12:34:56] Connected to Redis: redis://localhost:6379
[12:34:56] Registered jobs: beauty-reminder, beauty-outbox, beauty-campaign, beauty-winback, beauty-review
```

The worker processes BullMQ jobs for:
- Reminders (send SMS/notifications 1h or 2h before appointment)
- Outbox (send queued messages, retries ×3)
- Campaigns (run AI-driven promotions)
- Winback (win-back campaigns for inactive clients)
- Review requests (post-appointment follow-up)

---

## 6. Frontend Setup

### 6.1 Install Dependencies

```bash
cd frontend
npm install
# or: pnpm install
```

### 6.2 Environment Variables

Create `.env.local`:

```
NEXT_PUBLIC_API_URL=http://localhost:5000
NEXT_PUBLIC_TENANT=acme-salon
# (other frontend env vars as needed)
```

### 6.3 Run Development Server

```bash
npm run dev

# Output:
# ▲ Next.js 14.0.0
# - Local:        http://localhost:3000
# - Environments: .env.local
# 
# ✓ Ready in 2.5s
```

**Access:** http://localhost:3000

**Dashboard:** http://localhost:3000/beauty (admin)
**Booking:** http://localhost:3000/book (public)

---

## 7. Typical Development Workflow

### 7.1 Terminal Setup (3–4 terminals)

**Terminal 1: Backend**
```bash
cd backend/BeautyCrm.Api
dotnet watch run
```

**Terminal 2: Worker**
```bash
cd worker
npm run dev
```

**Terminal 3: Frontend**
```bash
cd frontend
npm run dev
```

**Terminal 4: (Optional) Logs**
```bash
# Docker logs
docker logs -f beautycrm-postgres
docker logs -f beautycrm-redis
```

### 7.2 Login

1. Open http://localhost:3000
2. Go to login (or follow redirect)
3. Enter:
   - Tenant: `acme-salon`
   - Email: `owner@acme.com`
   - Password: (from tenant creation)

### 7.3 Make Changes

- Edit backend code → auto-reload at http://localhost:5000
- Edit frontend code → live reload at http://localhost:3000
- Edit worker jobs → restart worker manually or set up hot-reload

### 7.4 Run Tests Frequently

```bash
# Backend tests (fast, no DB)
cd backend && dotnet test

# Frontend tests (if set up)
cd frontend && npm test

# E2E tests (if set up)
cd frontend && npm run test:e2e
```

---

## 8. Troubleshooting

### Backend won't start: "invalid JWT signing key"

**Solution:** Ensure `Auth__JwtSigningKey` is set in `.env` and non-empty.

```bash
# Generate if missing
openssl rand -base64 48
# Copy to .env: Auth__JwtSigningKey=<output>
```

### Migrations fail: "extension btree_gist does not exist"

**Solution:** Create the extension manually:

```bash
psql -U postgres -h localhost -d beautycrm -c "CREATE EXTENSION IF NOT EXISTS btree_gist;"

# Or inside Docker:
docker exec beautycrm-postgres psql -U postgres -d beautycrm -c "CREATE EXTENSION IF NOT EXISTS btree_gist;"
```

### "RLS policy" errors when querying beauty_* tables

**Solution:** Ensure `TenantContext.SetTenant(tenantId)` is called before accessing the database.

**Backend:**
- Middleware `TenantMiddleware` does this automatically (see `Program.cs`).
- If custom scope needed, call manually in Application service:
  ```csharp
  var connection = context.Database.GetDbConnection();
  await connection.ExecuteAsync("SELECT set_config('app.tenant_id', @tid::text, true)",
      new { tid = tenantId.ToString() });
  ```

### Webhook POST fails with "401 signature_invalid"

**Solution:** Ensure channel secrets are configured:
- Telegram: `TELEGRAM_WEBHOOK_SECRET` in `.env`
- Instagram: `INSTAGRAM_APP_SECRET` in `.env`
- Or enable mocks: `Channels__UseMocks=true`

### Worker jobs not processing

**Solution:** Check Redis is running and `REDIS_URL` is correct:

```bash
# Test Redis connection
redis-cli -u redis://localhost:6379 PING
# Expected: PONG

# Check if queues have jobs
redis-cli -u redis://localhost:6379 LLEN bull:beauty-reminder:waiting
```

### Frontend can't reach backend API

**Solution:** Check `NEXT_PUBLIC_API_URL` in `.env.local`:

```
# If backend runs on 5001 (HTTPS):
NEXT_PUBLIC_API_URL=https://localhost:5001

# If backend runs on 5000 (HTTP):
NEXT_PUBLIC_API_URL=http://localhost:5000
```

Restart frontend dev server after changing.

---

## 9. Database Backup & Restore

### Backup

```bash
pg_dump -U postgres -h localhost beautycrm --blobs > beautycrm_backup.sql
```

### Restore

```bash
psql -U postgres -h localhost beautycrm < beautycrm_backup.sql
```

---

## 10. Clean Slate (Reset Database)

**Warning:** This deletes all data.

```bash
# Drop database
psql -U postgres -h localhost -c "DROP DATABASE beautycrm;"

# Recreate and re-migrate
psql -U postgres -h localhost -c "CREATE DATABASE beautycrm;"

cd backend
dotnet ef database update --project BeautyCrm.Infrastructure --startup-project BeautyCrm.Api

# Re-create tenant (see section 3.3)
```

---

## 11. Environment Variables Reference

See `.env.example` and the sections above. Key variables:

| Variable | Default | Purpose |
|---|---|---|
| `ConnectionStrings__Default` | — | PostgreSQL connection string |
| `REDIS_URL` | — | Redis connection URL |
| `ANTHROPIC_API_KEY` | — | Claude API for AI assistant |
| `TELEGRAM_BOT_TOKEN` | — | Telegram bot token (webhook) |
| `TELEGRAM_WEBHOOK_SECRET` | — | Signature verification for Telegram |
| `INSTAGRAM_APP_SECRET` | — | Instagram app secret (webhook) |
| `INSTAGRAM_VERIFY_TOKEN` | — | Verification token for Instagram |
| `Channels__EncryptionKey` | — | AES-256-GCM key for storing credentials (base64, 32 bytes) |
| `Channels__UseMocks` | true | Enable mock adapters for testing channels without real credentials |
| `Beauty__AllowTenantHeader` | false | Allow X-Tenant-Id header (dev only); disabled in production |
| `Auth__JwtSigningKey` | — | HS256 signing key (base64, ≥32 bytes) |
| `Auth__PlatformKey` | — | Platform operator API key (≥32 chars) |
| `Auth__Issuer` | beautycrm | JWT issuer claim |
| `Auth__Audience` | beautycrm-api | JWT audience claim |
| `Auth__MaxFailedAttempts` | 5 | Login lockout threshold |
| `Auth__LockoutMinutes` | 15 | Lockout duration |
| `Auth__AccessTokenMinutes` | 15 | JWT expiration |
| `Auth__RefreshTokenDays` | 14 | Refresh token lifetime |
| `Auth__InviteDays` | 7 | Invitation token validity |
| `Auth__MinPasswordLength` | 12 | Password validation |
| `Auth__BcryptWorkFactor` | 12 | Bcrypt hashing rounds |

---

## 12. Next Steps

1. **Create test data:** Use admin dashboard or API calls to populate locations, specialists, services, prices.
2. **Configure channels:** Set up Telegram/Instagram credentials (or use mocks).
3. **Test bookings:** Create an appointment via `/api/beauty/appointments` and verify slot allocation, payment, and reminders.
4. **Run worker jobs:** Verify reminders, outbox, and campaigns process correctly.
5. **Build UI flows:** Implement admin dashboard (TASK-689) and booking form (TASK-690).

---

## 13. CI/CD (GitHub Actions)

See `.github/workflows/` (if set up). Typically:
- Run tests on PR
- Build & push Docker image on merge to main
- Deploy to staging/production (see Deployment section in docs)

---

## 14. Production Deployment

See `deployment.md` (if exists) or ADRs for infrastructure decisions:
- Hosting: Vercel (frontend) / Docker (backend) / Managed PostgreSQL (AWS RDS, Neon, Supabase)
- CI/CD: GitHub Actions → ECR/Vercel
- Secrets: AWS Secrets Manager, Vercel Environment Variables
- Monitoring: CloudWatch, Sentry, LogRocket

---

## Quick Commands Reference

```bash
# Database
dotnet ef database update --project BeautyCrm.Infrastructure --startup-project BeautyCrm.Api
dotnet ef database update --project BeautyCrm.Infrastructure --startup-project BeautyCrm.Api -- --Environment Development

# Tests
dotnet test --filter "Category=Unit"
dotnet test --filter "Category=Integration" -- --BEAUTY_TEST_PG_ADMIN="..."

# Docker
docker compose up -d  # if docker-compose.yml exists
docker logs -f <container>
docker exec -it <container> psql -U postgres -d beautycrm

# Frontend
npm run lint
npm run typecheck
npm run build

# Worker
npm run dev
npm run build
npm start
```
