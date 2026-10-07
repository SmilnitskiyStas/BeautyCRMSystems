# ADR-006: Authentication Without Self-Registration

**Date:** 2026-10-07

**Status:** Accepted (TASK-684)

**Context:**

Beauty CRM is a B2B SaaS for salon management. Unlike consumer apps, we need controlled onboarding:
- **No public sign-up** (prevents spam, credential stuffing, competitor accounts)
- **Tenant creation** only by platform operator (SaaS admin)
- **User invitations** by tenant owner/admin (onboarding flow)

**Flows:**
1. **Operator creates tenant + owner** (POST /api/platform/tenants with X-Platform-Key)
2. **Owner invites specialists** (POST /api/invites with one-time token)
3. **User accepts invite** (POST /api/invites/accept with token + password)

---

## Decision

**Closed-system authentication:**
1. **No self-registration endpoint** — no POST /auth/register
2. **Invitation-based onboarding** — owner/admin sends invite link to new users
3. **One-time tokens** — invites expire after 7 days, valid only once
4. **JWT for sessions** — standard HS256 access/refresh token pair
5. **Role-based access** — owner, admin, specialist with hierarchical permissions

---

## Architecture

### Tables

#### tenants (auth schema)
```sql
id              UUID PRIMARY KEY
name            VARCHAR NOT NULL
slug            VARCHAR NOT NULL UNIQUE (DNS-safe: acme-salon)
modules         TEXT[] (array of enabled features)
status          VARCHAR (active | suspended)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ
```

#### users (auth schema)
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL FK tenants
email           VARCHAR NOT NULL
full_name       VARCHAR NOT NULL
role            VARCHAR NOT NULL (owner | admin | specialist)
specialist_id   UUID (nullable, FK beauty_specialists)
is_active       BOOLEAN DEFAULT true
password_hash   VARCHAR NOT NULL (bcrypt)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ

UNIQUE (tenant_id, email)
RLS tenant_isolation
```

#### invites (auth schema)
```sql
id              UUID PRIMARY KEY
tenant_id       UUID NOT NULL FK tenants
email           VARCHAR NOT NULL
role            VARCHAR NOT NULL (admin | specialist)
specialist_id   UUID (nullable, required for specialist)
token_hash      VARCHAR NOT NULL (SHA-256)
is_used         BOOLEAN DEFAULT false
expires_at      TIMESTAMPTZ
created_at      TIMESTAMPTZ

UNIQUE (tenant_id, email, is_used = false)
RLS tenant_isolation
```

#### refresh_tokens (auth schema)
```sql
id              UUID PRIMARY KEY
user_id         UUID NOT NULL FK users
token_hash      VARCHAR NOT NULL (SHA-256)
issued_at       TIMESTAMPTZ NOT NULL
expires_at      TIMESTAMPTZ NOT NULL
revoked_at      TIMESTAMPTZ (nullable, invalidated on logout/reuse)

RLS (via user_id FK)
```

---

## API Flows

### 1. Operator Creates Tenant (Platform API)

**Endpoint:** `POST /api/platform/tenants`

**Auth:** `X-Platform-Key` header (API key, ≥32 chars from .env)

**Request:**
```json
{
  "name": "Acme Salon Network",
  "slug": "acme-salon",
  "modules": ["beauty_booking", "beauty_catalog", "beauty_clients", "beauty_ai"],
  "owner": {
    "email": "owner@acme.com",
    "fullName": "Jane Owner",
    "password": "SecurePass123!@"
  }
}
```

**Response:** `201`
```json
{
  "tenantId": "550e8400-e29b-41d4-a716-446655440000",
  "name": "Acme Salon Network",
  "slug": "acme-salon",
  "modules": ["beauty_booking", ...],
  "ownerUserId": "uuid"
}
```

**Implementation:**
```csharp
public class PlatformTenantService
{
    public async Task<Result<TenantCreatedDto>> CreateTenantAsync(
        CreateTenantRequest req, CancellationToken ct)
    {
        // 1. Validate slug (DNS-safe, unique)
        if (!IsValidSlug(req.Slug))
            return Error.Validation("invalid_slug", "Slug must be lowercase alphanumeric + hyphens.");
        
        var existing = await db.Tenants.FirstOrDefaultAsync(
            t => t.Slug == req.Slug, ct);
        if (existing is not null)
            return Error.Conflict("tenant_slug_taken", "Slug already taken.");
        
        // 2. Validate password
        if (req.Owner.Password.Length < 12)
            return Error.Validation("weak_password", "Password must be ≥12 characters.");
        
        // 3. Create tenant + owner in transaction
        using var txn = await db.BeginTransactionAsync(ct);
        
        var tenant = new Tenant
        {
            Name = req.Name,
            Slug = req.Slug,
            Modules = req.Modules?.ToArray() ?? ["beauty_booking", "beauty_catalog"],
            Status = "active"
        };
        db.Tenants.Add(tenant);
        
        var owner = new User
        {
            TenantId = tenant.Id,
            Email = req.Owner.Email,
            FullName = req.Owner.FullName,
            Role = "owner",
            PasswordHash = _hasher.Hash(req.Owner.Password),
            IsActive = true
        };
        db.Users.Add(owner);
        
        await db.SaveChangesAsync(ct);
        await txn.CommitAsync(ct);
        
        return Result.Ok(new TenantCreatedDto(tenant.Id, tenant.Name, tenant.Slug, tenant.Modules, owner.Id));
    }
}
```

### 2. Owner Invites User

**Endpoint:** `POST /api/invites`

**Auth:** JWT bearer (owner or admin)

**Request:**
```json
{
  "email": "specialist@acme.com",
  "role": "specialist",
  "specialistId": "uuid-of-specialist"
}
```

**Response:** `201`
```json
{
  "invite": {
    "id": "uuid",
    "email": "specialist@acme.com",
    "role": "specialist",
    "createdAt": "2026-10-07T12:00:00Z"
  },
  "token": "one-time-token-show-to-user"
}
```

**Token generation:**
```csharp
public class InviteService
{
    public async Task<Result<InviteCreatedDto>> CreateInviteAsync(
        Actor actor, CreateInviteRequest req, CancellationToken ct)
    {
        // 1. Permission check: owner can invite anyone; admin can invite specialist only
        if (actor.Role == "admin" && req.Role != "specialist")
            return Error.Forbidden("forbidden_role", "Admins can only invite specialists.");
        
        // 2. Validate
        if (req.Role == "specialist" && req.SpecialistId is null)
            return Error.Validation("specialist_id_required", "specialistId required for specialist role.");
        
        // 3. Check email not already in tenant
        var existing = await db.Users.FirstOrDefaultAsync(
            u => u.TenantId == actor.TenantId && u.Email == req.Email, ct);
        if (existing is not null)
            return Error.Conflict("user_exists", "User already in tenant.");
        
        // 4. Generate token (random) & store hash
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        
        var invite = new Invite
        {
            TenantId = actor.TenantId,
            Email = req.Email,
            Role = req.Role,
            SpecialistId = req.SpecialistId,
            TokenHash = Convert.ToBase64String(tokenHash),
            IsUsed = false,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };
        
        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);
        
        return Result.Ok(new InviteCreatedDto(
            new InviteDto(invite.Id, invite.Email, invite.Role, invite.CreatedAt),
            token // Show only once to caller; they forward to invitee out-of-band
        ));
    }
}
```

### 3. User Accepts Invite

**Endpoint:** `POST /api/auth/invites/accept`

**Auth:** None (public, rate-limited)

**Request:**
```json
{
  "token": "one-time-token-from-invite",
  "fullName": "John Specialist",
  "password": "SecurePass123!@"
}
```

**Response:** `201`
```json
{
  "id": "uuid",
  "email": "specialist@acme.com",
  "fullName": "John Specialist",
  "role": "specialist",
  "specialistId": "uuid",
  "isActive": true
}
```

**Implementation:**
```csharp
public class AuthService
{
    public async Task<Result<UserDto>> AcceptInviteAsync(
        AcceptInviteRequest req, CancellationToken ct)
    {
        // 1. Find invite by token hash
        var tokenHash = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(req.Token)));
        
        var invite = await db.Invites.FirstOrDefaultAsync(
            i => i.TokenHash == tokenHash && !i.IsUsed && i.ExpiresAt > DateTimeOffset.UtcNow, ct);
        
        if (invite is null)
            return Error.NotFound("invite_invalid", "Token invalid or expired.");
        
        // 2. Validate password
        if (req.Password.Length < 12)
            return Error.Validation("weak_password", "Password must be ≥12 characters.");
        
        // 3. Check email not taken (race condition: use UNIQUE constraint)
        var user = new User
        {
            TenantId = invite.TenantId,
            Email = invite.Email,
            FullName = req.FullName,
            Role = invite.Role,
            SpecialistId = invite.SpecialistId,
            PasswordHash = _hasher.Hash(req.Password),
            IsActive = true
        };
        
        using var txn = await db.BeginTransactionAsync(ct);
        
        try
        {
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            
            // 4. Mark invite as used
            invite.IsUsed = true;
            await db.SaveChangesAsync(ct);
            
            await txn.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("unique") == true)
        {
            return Error.Conflict("user_exists", "Email already registered.");
        }
        
        return Result.Ok(new UserDto(user));
    }
}
```

### 4. Login

**Endpoint:** `POST /api/auth/login`

**Auth:** None (public, rate-limited)

**Request:**
```json
{
  "tenant": "acme-salon",
  "email": "owner@acme.com",
  "password": "SecurePass123!@"
}
```

**Response:** `200`
```json
{
  "accessToken": "eyJ...",
  "expiresInSeconds": 900,
  "refreshToken": "opaque...",
  "user": { "id", "email", "fullName", "role", "specialistId" }
}
```

**Lockout:** 5 failed attempts → 423 for 15 minutes.

```csharp
public class AuthService
{
    public async Task<Result<TokenResponse>> LoginAsync(
        LoginRequest req, CancellationToken ct)
    {
        // 1. Resolve tenant
        var tenant = await db.Tenants.FirstOrDefaultAsync(
            t => t.Slug == req.Tenant, ct);
        if (tenant is null || tenant.Status != "active")
            return Error.NotFound("tenant_not_found", "Tenant not found or suspended.");
        
        // 2. Resolve user
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.TenantId == tenant.Id && u.Email == req.Email, ct);
        if (user is null || !user.IsActive)
            return Error.Unauthorized("invalid_credentials", "Invalid email or password.");
        
        // 3. Check lockout
        if (user.FailedLoginAttempts >= 5)
        {
            if (DateTimeOffset.UtcNow < user.LockedOutUntil)
                return Error.Locked("account_locked", $"Account locked for {(user.LockedOutUntil - DateTimeOffset.UtcNow).TotalMinutes:F0} minutes.");
            
            // Unlock
            user.FailedLoginAttempts = 0;
        }
        
        // 4. Verify password
        if (!_hasher.Verify(user.PasswordHash, req.Password))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= 5)
                user.LockedOutUntil = DateTimeOffset.UtcNow.AddMinutes(15);
            
            await db.SaveChangesAsync(ct);
            return Error.Unauthorized("invalid_credentials", "Invalid email or password.");
        }
        
        // 5. Reset attempts, issue tokens
        user.FailedLoginAttempts = 0;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        
        var accessToken = _tokenCodec.IssueAccessToken(tenant.Id, user);
        var refreshTokenValue = GenerateRefreshToken();
        var refreshTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(refreshTokenValue));
        
        var refreshToken = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Convert.ToBase64String(refreshTokenHash),
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(14)
        };
        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync(ct);
        
        return Result.Ok(new TokenResponse(
            accessToken,
            expiresInSeconds: 15 * 60,
            refreshTokenValue,
            new UserDto(user)
        ));
    }
}
```

### 5. Refresh Token

**Endpoint:** `POST /api/auth/refresh`

**Auth:** None (public, rate-limited)

**Request:**
```json
{ "refreshToken": "opaque..." }
```

**Response:** `200` (new token pair)

**Rotation:** Old token becomes invalid; reuse closes all sessions (security).

```csharp
public async Task<Result<TokenResponse>> RefreshAsync(string tokenValue, CancellationToken ct)
{
    // 1. Hash & look up
    var tokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(tokenValue)));
    var refreshToken = await db.RefreshTokens.FirstOrDefaultAsync(
        rt => rt.TokenHash == tokenHash && rt.ExpiresAt > DateTimeOffset.UtcNow
            && rt.RevokedAt == null, ct);
    
    if (refreshToken is null)
        return Error.Unauthorized("invalid_token", "Token invalid or expired.");
    
    // 2. Revoke old token
    refreshToken.RevokedAt = DateTimeOffset.UtcNow;
    
    // 3. Issue new pair
    var user = await db.Users.FirstAsync(u => u.Id == refreshToken.UserId, ct);
    var tenant = await db.Tenants.FirstAsync(t => t.Id == user.TenantId, ct);
    
    var newAccessToken = _tokenCodec.IssueAccessToken(tenant.Id, user);
    var newRefreshValue = GenerateRefreshToken();
    var newRefreshTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(newRefreshValue));
    
    var newRefreshToken = new RefreshToken
    {
        UserId = user.Id,
        TokenHash = Convert.ToBase64String(newRefreshTokenHash),
        IssuedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(14)
    };
    db.RefreshTokens.Add(newRefreshToken);
    
    await db.SaveChangesAsync(ct);
    
    return Result.Ok(new TokenResponse(
        newAccessToken,
        expiresInSeconds: 15 * 60,
        newRefreshValue,
        new UserDto(user)
    ));
}
```

### 6. Logout

**Endpoint:** `POST /api/auth/logout`

**Auth:** None (public)

**Request:**
```json
{ "refreshToken": "opaque..." }
```

**Response:** `204`

Revokes the refresh token.

---

## JWT Tokens

### Access Token (HS256, 15 min expiry)

```csharp
public class TokenCodec(IConfiguration config)
{
    public string IssueAccessToken(Guid tenantId, User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            config["Auth:JwtSigningKey"] ?? throw new InvalidOperationException("JwtSigningKey not configured")));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        
        var claims = new List<Claim>
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("email", user.Email),
            new Claim("role", user.Role),
        };
        
        if (user.SpecialistId.HasValue)
            claims.Add(new Claim("specialist_id", user.SpecialistId.ToString()));
        
        var token = new JwtSecurityToken(
            issuer: "beautycrm",
            audience: "beautycrm-api",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: creds);
        
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
```

### Refresh Token (opaque, 14 days)

Stored as SHA-256 hash in database; value shown only once at issuance.

---

## Consequences

### Positive

1. **No spam sign-ups** (only invited users)
2. **Audit trail** (operator creates tenant, admin invites users)
3. **Secure onboarding** (one-time tokens, expiry, rate limit)
4. **Role hierarchy** (owner > admin > specialist)

### Negative

1. **Operational overhead** (SaaS operator must create tenants)
   - **Mitigation:** Automate via dashboard or API calls
2. **Invite management** (track invites, resend if expired)
   - **Mitigation:** Dashboard shows pending invites; resend link option

---

## Testing

- Unit: Login logic, password hashing, lockout
- Integration: Create tenant → Invite → Accept → Login → Refresh
- Security: Test lockout, rate limit, token expiry

---

## References

- [JWT.io](https://jwt.io)
- `backend/BeautyCrm.Application/Features/BeautyAuth/` — Implementation
- `backend/BeautyCrm.Infrastructure/Data/Auth/` — Storage
- `.claude/docs/api.md` — `/auth/*` endpoints
