# Beauty CRM — B2B SaaS for Beauty & Wellness Businesses

A comprehensive cloud-based Customer Relationship Management platform for salons, barbershops, cosmologists, and beauty networks. Manage appointments, clients, staff, analytics, and multi-channel communication.

**Status:** MVP (Wave A), development ongoing

---

## Overview

### What is Beauty CRM?

Beauty CRM is a B2B SaaS platform that enables beauty & wellness businesses to:

- **Book & Manage Appointments** — Online booking with calendar, real-time availability, instant confirmations
- **Client Relationships** — 360° client profiles, history, notes, visit tracking
- **Service Catalog** — Services, prices (network-wide & per-location), promotions with real-time preview
- **Multi-Channel Communication** — Reach clients via Telegram, Instagram, WhatsApp, SMS; receive & respond in-app
- **Analytics & Reporting** — Revenue, appointment trends, promotion ROI, per-location/specialist insights
- **AI Assistant** — Claude-powered chatbot for client queries, appointment suggestions, automated campaigns
- **Payments & Refunds** — Card payments with configurable cancellation & refund policies
- **Team Management** — Invite specialists, control access by role (owner/admin/specialist)

**Key Differentiators:**
- Privacy-first: Row-Level Security (RLS) at database level
- Multi-tenant: Single deployment serves unlimited businesses
- Extensible: Modular architecture for easy feature additions
- Developer-friendly: REST API + SDK, well-documented

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| **Frontend** | Next.js 14+ (App Router) + React 18 + TypeScript + Tailwind + shadcn/ui |
| **Backend** | .NET 8 (ASP.NET Core) + EF Core 8 + C# 12 |
| **Database** | PostgreSQL 16+ with Row-Level Security (RLS) |
| **Cache & Jobs** | Redis + BullMQ (Node.js) |
| **AI** | Anthropic Claude (Messages API) |
| **Auth** | JWT (HS256) + Bcrypt password hashing |
| **Deployment** | Docker (containerized) |
| **Hosting** | Vercel (frontend) / ECS, K8s (backend) / Managed PostgreSQL (AWS RDS, Neon) |

---

## Quick Start

### Prerequisites

- .NET 8 SDK
- Node.js 20+
- PostgreSQL 16+
- Redis
- Docker (recommended)

### Setup (5 minutes with Docker)

```bash
# 1. Clone & navigate
git clone <repo>
cd BeautyCRMSystem

# 2. Set up environment
cp .env.example .env
# Edit .env with your secrets (JWT key, encryption key, API keys)

# 3. Start Docker containers (PostgreSQL + Redis)
docker compose up -d

# 4. Apply migrations
cd backend
dotnet ef database update --project BeautyCrm.Infrastructure --startup-project BeautyCrm.Api

# 5. Start services (3 terminals)
# Terminal 1: Backend
cd backend/BeautyCrm.Api && dotnet watch run

# Terminal 2: Worker
cd worker && npm install && npm run dev

# Terminal 3: Frontend
cd frontend && npm install && npm run dev

# 6. Create first tenant via API
curl -X POST http://localhost:5000/api/platform/tenants \
  -H "X-Platform-Key: <your-platform-key>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "My Salon",
    "slug": "my-salon",
    "modules": ["beauty_booking", "beauty_catalog", "beauty_clients", "beauty_ai"],
    "owner": {"email": "owner@salon.com", "fullName": "Jane Owner", "password": "SecurePass123!@"}
  }'

# 7. Open browser
# http://localhost:3000 (frontend)
# https://localhost:5001/swagger (API documentation)
```

**See [runbook.md](./.claude/docs/runbook.md) for detailed setup & troubleshooting.**

---

## Architecture

### Modular Monolith

Single deployment with feature zones:

```
Beauty CRM Deployment
├── Frontend (Next.js, Vercel)
│   ├── Admin Dashboard: /beauty/...
│   └── Public Booking: /book/...
├── Backend (ASP.NET Core, ECS/K8s)
│   ├── API Controllers: /api/beauty, /api/auth, /api/platform
│   ├── Application Features:
│   │   ├── BeautyBooking: Appointments, slots, cancellations
│   │   ├── BeautyCatalog: Services, prices, promotions
│   │   ├── BeautyClients: Profiles, history, notes
│   │   ├── BeautyChannels: Telegram, Instagram, webhooks
│   │   ├── BeautyAnalytics: Revenue, metrics, per-location reports
│   │   └── BeautyAI: Claude assistant, tool execution, action journal
│   └── Infrastructure:
│       ├── Data: PostgreSQL ORM (EF Core), RLS policies
│       ├── Integrations: Channel adapters (Telegram, Instagram, mocks)
│       └── AI: Anthropic client, prompt templates, tool definitions
└── Worker (Node.js, BullMQ)
    ├── beauty-reminder: Send appointment reminders (1h, 2h before)
    ├── beauty-outbox: Deliver queued messages (retries ×3)
    ├── beauty-campaign: Run AI-driven promotions
    ├── beauty-winback: Win-back campaigns for inactive clients
    └── beauty-review: Post-appointment follow-up requests
```

**Design principles:**
- **RLS at DB level:** `app.tenant_id` session variable filters all queries
- **Ports & adapters:** Infrastructure implements Application interfaces (DI)
- **Transactional outbox:** Booking + message saved atomically; worker processes asynchronously
- **No self-signup:** Operator creates tenants; owner invites users

---

## Core Concepts

### Multi-Tenancy

- **Single deployment, unlimited tenants:** Each tenant (salon) isolated via PostgreSQL RLS
- **Tenant creation:** Platform operator (SaaS admin) creates via `/api/platform/tenants` (X-Platform-Key)
- **Data isolation:** RLS policy ensures `beautycrm_app` role cannot read/write across tenants

### Authentication & Authorization

- **No self-registration:** Closed system; only invited users can join
- **Invitations:** Owner/admin sends one-time token; user accepts with password
- **Roles:** owner (full access), admin (manage specialists/catalog), specialist (view own appointments)
- **JWT:** Access token (15 min) + refresh token (14 days); rate-limited login (5 attempts → 15 min lockout)

### Appointments & Bookings

- **Status:** pending → confirmed → completed (or cancelled / no_show)
- **Slots:** 15-minute increments, respecting specialist availability & working hours per location
- **Overlap prevention:** EXCLUDE constraint at DB level (specialist can't have overlapping active appointments)
- **Cancellation policy:** Owner configures window (e.g., ≤12h → 50% refund, >12h → 100%), with optional fees
- **Payment:** Card or cash; card payments charged at booking, soft fails mark appointment cancelled

### Multi-Channel Communication

- **Channels:** Telegram, Instagram (Meta), mock adapters for testing (Viber, WhatsApp, Widget)
- **Inbound:** Webhooks receive customer messages → stored in `beauty_messages` → AI processes (if enabled)
- **Outbound:** Transactional outbox pattern; queued messages delivered via worker, retried ×3 with backoff
- **Secrets:** Channel tokens/webhooks encrypted (AES-256-GCM) in database

### AI Assistant

- **Modes:** `suggest` (all require approval), `confirm` (auto-execute low-risk), `auto` (execute all)
- **Tools:** 8 available (find_slots, get_prices, create_appointment, suggest_promotion, build_audience, etc.)
- **Safety:** Prompt injection prevention (XML tags for client input), tool access control per scope
- **Approval:** High-risk actions require manager sign-off; audit trail in `beauty_ai_actions`

---

## API

**Base URL:** `https://api.beautycrm.com/api` (or `http://localhost:5000/api` locally)

**Authentication:** JWT bearer token in `Authorization: Bearer <token>` header

### Core Endpoints

- **Auth:** `POST /auth/login`, `POST /auth/refresh`, `POST /auth/logout`, `POST /auth/invites/accept`, `GET /auth/me`
- **Booking:** `GET /beauty/slots`, `GET /beauty/appointments`, `POST /beauty/appointments`, `PATCH /beauty/appointments/{id}`, `POST /beauty/appointments/{id}/cancel`
- **Catalog:** `GET /beauty/services`, `GET /beauty/promotions`, `GET /beauty/promotions/preview`
- **Clients:** `GET /beauty/clients`, `GET /beauty/clients/{id}`, `POST /beauty/clients/{id}/notes`
- **Analytics:** `GET /beauty/analytics/network`, `GET /beauty/analytics/locations`, `GET /beauty/analytics/promotions`
- **Channels:** `GET /beauty/channels`, `PUT /beauty/channels/{id}`, `POST /beauty/webhooks/{channel}/{channelId}` (public)
- **AI:** `GET /beauty/ai/actions`, `POST /beauty/ai/actions/{id}/approve`, `POST /beauty/ai/actions/{id}/reject`, `POST /beauty/ai/actions/{id}/revert`
- **Settings:** `GET /beauty/settings/cancellation`, `PUT /beauty/settings/cancellation`

**Full reference:** [.claude/docs/api.md](./.claude/docs/api.md)

---

## Project Structure

```
BeautyCRMSystem/
├── backend/
│   ├── BeautyCrm.Domain/              # Domain entities (mostly in Infrastructure for now)
│   ├── BeautyCrm.Application/         # Business logic, feature services, ports
│   │   └── Features/
│   │       ├── BeautyBooking/
│   │       ├── BeautyCatalog/
│   │       ├── BeautyClients/
│   │       ├── BeautyChannels/
│   │       ├── BeautyAnalytics/
│   │       └── BeautyAI/
│   ├── BeautyCrm.Infrastructure/      # EF Core, RLS, adapters, third-party integration
│   │   ├── Data/                      # Entities, migrations, RLS policies, TenantContext
│   │   └── Integrations/
│   │       ├── Channels/              # Channel adapters (Telegram, Instagram, mocks)
│   │       └── AI/                    # Claude client, tools, journal
│   ├── BeautyCrm.Api/                 # ASP.NET Core controllers, middleware, auth
│   │   ├── Controllers/               # BookingController, ChannelsController, etc.
│   │   ├── Middleware/                # TenantMiddleware, error handling
│   │   └── Program.cs                 # DI, middleware pipeline
│   └── BeautyCrm.Tests/               # Unit & integration tests
├── frontend/
│   ├── app/
│   │   ├── (dashboard)/               # Admin dashboard
│   │   ├── (public)/                  # Public booking page
│   │   └── page.tsx
│   ├── features/
│   │   ├── beauty-admin/              # Admin UI components
│   │   └── beauty-booking/            # Booking form components
│   └── lib/
│       └── api.ts                     # API client
├── worker/
│   ├── src/
│   │   ├── jobs/                      # BullMQ job definitions
│   │   │   ├── beauty-reminder.ts
│   │   │   ├── beauty-outbox.ts
│   │   │   ├── beauty-campaign.ts
│   │   │   └── ...
│   │   ├── ports.ts                   # Interfaces for data/channels
│   │   └── testing/fakes.ts           # Mock implementations
│   └── package.json
├── .claude/
│   ├── docs/
│   │   ├── domain-model.md            # Entities, statuses, roles
│   │   ├── api.md                     # Full API reference
│   │   ├── database.md                # Schema, RLS, migrations, GRANT
│   │   ├── runbook.md                 # Local setup & troubleshooting
│   │   ├── beauty-contracts.md        # System contracts (signed by all agents)
│   │   ├── adr/
│   │   │   ├── ADR-001-modular-monolith-and-stack.md
│   │   │   ├── ADR-002-row-level-security-and-tenant-context.md
│   │   │   ├── ADR-003-transactional-outbox-and-worker.md
│   │   │   ├── ADR-004-channels-and-mock-adapters.md
│   │   │   ├── ADR-005-ai-modes-and-prompt-injection.md
│   │   │   ├── ADR-006-authentication-without-self-registration.md
│   │   │   └── ADR-007-configurable-cancellation-and-refund-policy.md
│   │   └── known-issues.md
│   ├── agents/                        # Multi-agent workflow roles
│   └── logs/tasks/                    # Task logs (TASK-674, TASK-675, etc.)
├── .env.example                       # Environment variables template
├── docker-compose.yml                 # Local dev: PostgreSQL + Redis
└── README.md                          # This file
```

---

## Development

### Build & Test

```bash
# Backend
cd backend
dotnet build
dotnet test

# Frontend
cd frontend
npm run build
npm run lint
npm run typecheck

# Worker
cd worker
npm run build
npm test
```

### Local Development (3 terminals)

```bash
# Terminal 1: Backend (auto-reload)
cd backend/BeautyCrm.Api
dotnet watch run
# http://localhost:5000 (HTTP, for mobile), https://localhost:5001 (HTTPS, Swagger)

# Terminal 2: Worker
cd worker
npm run dev

# Terminal 3: Frontend (auto-reload)
cd frontend
npm run dev
# http://localhost:3000
```

### Environment Variables

See `.env.example` and [runbook.md](./.claude/docs/runbook.md) for configuration.

Key secrets:
- `ConnectionStrings__Default` — PostgreSQL connection
- `REDIS_URL` — Redis URL
- `Auth__JwtSigningKey` — JWT signing key (base64, ≥32 bytes)
- `Auth__PlatformKey` — Platform operator API key (≥32 chars)
- `Channels__EncryptionKey` — Channel credentials encryption (base64, 32 bytes)
- `ANTHROPIC_API_KEY` — Claude API key
- `TELEGRAM_BOT_TOKEN`, `INSTAGRAM_APP_SECRET` — Channel credentials

---

## Documentation

- **[Domain Model](./.claude/docs/domain-model.md)** — Entities, statuses, roles, AI modes
- **[API Reference](./.claude/docs/api.md)** — All endpoints, error codes, examples
- **[Database Schema](./.claude/docs/database.md)** — Tables, RLS policies, migrations, grants
- **[Setup & Runbook](./.claude/docs/runbook.md)** — Local development, troubleshooting, commands
- **[Contracts](./.claude/docs/beauty-contracts.md)** — System requirements (v0.4, TASK-674–685)
- **ADRs** (Architectural Decision Records):
  - [ADR-001: Modular Monolith & Stack](./.claude/docs/adr/ADR-001-modular-monolith-and-stack.md)
  - [ADR-002: RLS & Tenant Context](./.claude/docs/adr/ADR-002-row-level-security-and-tenant-context.md)
  - [ADR-003: Transactional Outbox & Worker](./.claude/docs/adr/ADR-003-transactional-outbox-and-worker.md)
  - [ADR-004: Channels & Mock Adapters](./.claude/docs/adr/ADR-004-channels-and-mock-adapters.md)
  - [ADR-005: AI Modes & Prompt Injection](./.claude/docs/adr/ADR-005-ai-modes-and-prompt-injection.md)
  - [ADR-006: Authentication Without Self-Registration](./.claude/docs/adr/ADR-006-authentication-without-self-registration.md)
  - [ADR-007: Configurable Cancellation & Refund Policy](./.claude/docs/adr/ADR-007-configurable-cancellation-and-refund-policy.md)

---

## Wave A (MVP, 2026-10-07)

Completed:
- [x] Database schema with RLS (TASK-674)
- [x] Backend API (TASK-675)
- [x] Channel integrations (TASK-676)
- [x] AI assistant (TASK-677)
- [x] Worker jobs (TASK-678)
- [x] Documentation (TASK-683)

In progress:
- [ ] Admin dashboard (TASK-689)
- [ ] Public booking page (TASK-690)
- [ ] Public appointment API (TASK-688)
- [ ] Mobile app (Wave B)

---

## Contributing

**Multi-agent development workflow:**
1. Main CLI session routes tasks to specialized agents (backend-developer, frontend-developer, database-engineer, etc.)
2. Each agent has clear responsibilities (see `.claude/agents/` & workflow docs)
3. Task logs record decisions & changes (`.claude/logs/tasks/`)
4. Documentation stays in sync with code (this file, `.claude/docs/*`, ADRs)

---

## Support & Licensing

For issues, feature requests, or questions:
- Check [runbook.md](./.claude/docs/runbook.md) for troubleshooting
- Review [API docs](./.claude/docs/api.md) for endpoint details
- See ADRs for architectural context

(Add license, contact, SLA info as needed.)

---

## Next Steps

1. **Deploy:** Configure Docker, cloud database (AWS RDS/Neon), Redis (ElastiCache/Upstash)
2. **Frontend:** Build admin dashboard & public booking UI (TASK-689/690)
3. **Testing:** E2E tests, load testing, security audit
4. **Go-live:** Beta with 3–5 salon customers, gather feedback
5. **Scale:** Multi-region deployment, analytics improvements, mobile app

---

**Last updated:** 2026-10-07
**Status:** Development (Wave A)
**Owner:** Beauty CRM Team
