# ADR-001: Modular Monolith Architecture & Technology Stack

**Date:** 2026-10-07

**Status:** Accepted (TASK-674…678)

**Context:**

Beauty CRM is a B2B SaaS platform for beauty & wellness businesses (salons, barbershops, cosmologists, networks). Initial scope includes:
- Multi-tenant management (one deployment per operator, each tenant isolated at DB level)
- CRM for clients & specialists
- Online booking with calendar & availability
- Multi-channel communication (Telegram, Instagram, etc.)
- AI-assisted conversation & campaign management
- Payment processing & analytics

Requirements from `BEAUTY_CRM_PROMPT.md`:
- Rapid initial deployment (Wave A: MVP with modular extension)
- Scalability within a single deployment or horizontal via multiple instances
- Type safety & maintainability (C#/.NET, TypeScript/React)
- Privacy-first (RLS at database level, no application-side leaks)

---

## Decision

We adopt a **modular monolith** architecture deployed as:

1. **Backend:** .NET 8 (ASP.NET Core) with EF Core + PostgreSQL
2. **Frontend:** Next.js 14+ (App Router) + React + TypeScript
3. **Worker:** Node.js + TypeScript + BullMQ for async jobs
4. **Database:** PostgreSQL 16+ with RLS (Row-Level Security)
5. **Cache/Queue:** Redis (BullMQ for job queues)
6. **Hosting:** Container-based (Docker), deployable to Vercel (frontend), ECS/Kubernetes (backend), or self-hosted

---

## Rationale

### Why Modular Monolith?

**Monolith:**
- Single deployment = simpler operational complexity (no service-to-service auth, fewer secrets, single database source of truth)
- Multi-tenancy naturally built at DB level (RLS)
- Easier to implement cross-feature transactions (booking + payment + reminder)
- Lower initial infrastructure cost

**Modular:**
- Feature zones (`BeautyCrm.Application.Features.*`) allow independent testing & replacement
- Ports/adapters pattern (`Infrastructure/`) isolates 3rd-party integrations
- Can split to microservices later (per tenant, per region) without API redesign

### Why .NET 8 Backend?

- Strong typing & LINQ for complex queries (e.g., appointment overlaps, timezone math)
- EF Core with PostgreSQL RLS integration (set session variables before query)
- Rich async/await ecosystem & middleware pipeline (perfect for multi-tenant routing)
- Azure/AWS tooling mature; C# ecosystem large

### Why Next.js Frontend?

- Full-stack TypeScript (frontend + backend services)
- App Router (file-based routing) with server/client boundaries clear
- Built-in optimizations (image, fonts, code-splitting)
- Vercel hosting (first-class support) or any Docker container
- Tailwind + shadcn/ui for rapid UI development

### Why Node.js Worker?

- Lightweight, fast startup (ideal for containerized jobs)
- BullMQ (Redis-backed) for reliable job queues
- TypeScript for type safety in async/event-driven code
- Easy to integrate with .NET backend via shared database & Redis

### Why PostgreSQL + RLS?

- **Row-Level Security (FORCE mode):** Tenant isolation enforced at DB level, not application code
  - Prevents accidental data leaks (e.g., query bug that forgets `WHERE tenant_id = ?`)
  - Superuser cannot bypass → `beautycrm_app` role cannot use `SELECT ... FROM beauty_appointments WITHOUT RLS`
- **Rich types & constraints:** JSONB (working hours), EXCLUDE (appointment overlaps), triggers (end_at calculation)
- **Exclusion constraint:** Prevents two overlapping appointments for same specialist → database-level race condition safety
- **Temporal types:** TIMESTAMPTZ for UTC storage, easy timezone conversion in application

---

## Consequences

### Positive

1. **Single source of truth:** One database = one "latest" state; easier debugging
2. **Tenant isolation at DB level:** No accidental leaks; audit trail built-in
3. **Transactional consistency:** Booking + payment + reminder in one transaction
4. **Rapid iteration:** Feature zones can be developed & tested independently
5. **Cost-effective scale:** Single deployment handles many tenants up to ~1M appointments/month per database instance
6. **Type safety across stack:** TypeScript frontend, C# backend, TypeScript worker

### Negative

1. **Monolith couples features:** Strongly related features (booking + cancellation) are tightly bound
   - **Mitigation:** Ports/adapters & clear dependency graph; can split later
2. **Database becomes bottleneck at scale:** Single PostgreSQL instance max ~500 concurrent connections
   - **Mitigation:** Read replicas for analytics; connection pooling (PgBouncer); sharding by tenant_id if needed
3. **RLS overhead:** Every query goes through policy check
   - **Mitigation:** Indexes on (tenant_id, other_columns); RLS is minor overhead (<5%) vs. application-level filtering
4. **Blob storage:** appointments, messages, media stored in PostgreSQL (no S3 yet)
   - **Mitigation:** Add S3/Cloudinary layer later without API changes

---

## Implementation Status

- [x] Backend: ASP.NET Core project structure (Domain, Application, Infrastructure, Api)
- [x] Database: PostgreSQL schema with RLS (TASK-674)
- [x] API: Initial endpoints (TASK-675)
- [x] Channels: Integration layer for Telegram/Instagram (TASK-676)
- [x] AI: Claude integration for assistant (TASK-677)
- [x] Worker: BullMQ jobs (TASK-678)
- [ ] Frontend: Dashboard & booking UI (TASK-689, TASK-690)

---

## Trade-offs vs. Alternatives

### Alternative 1: Microservices from day 1

**Pros:** Independent scaling, team autonomy, technology diversity
**Cons:** Operational complexity (service discovery, distributed tracing, saga/event-sourcing), higher latency, harder to test end-to-end

**Decision:** Monolith wins for MVP; can extract to services later

### Alternative 2: Separate auth service (OAuth/OIDC)

**Pros:** Multi-app SSO, delegated auth
**Cons:** Extra infrastructure, JWT refresh token complexity

**Decision:** Built-in JWT auth (simpler for single product); OAuth can be added if needed

### Alternative 3: EventSourcing for bookings

**Pros:** Audit trail, event replay, temporal queries
**Cons:** Complexity (aggregate roots, event store), harder to debug

**Decision:** Soft deletes + audit logs in tables; events only for webhook delivery (outbox pattern)

---

## References

- `.claude/docs/domain-model.md` — Entity definitions
- `.claude/docs/api.md` — API specification
- `.claude/docs/database.md` — Schema & RLS
- `backend/BeautyCrm.Infrastructure/Data/BeautyDbContext.cs` — EF model
- `backend/BeautyCrm.Api/Program.cs` — DI setup
