using Microsoft.Extensions.DependencyInjection.Extensions;
using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.PublicBooking;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features;
using BeautyCrm.Infrastructure.AI.Beauty;using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Auth;
using BeautyCrm.Infrastructure.Integrations.Channels;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Application має бути зареєстровано разом з Data/Channels/Ai: вони реалізують порти одне одного.
builder.Services.AddControllers().ConfigureApiBehaviorOptions(o =>
{
    // Помилки валідації на межі API -> 422 (контракт beauty-contracts.md §3).
    o.InvalidModelStateResponseFactory = ctx => new UnprocessableEntityObjectResult(new ApiError(
        "validation_failed",
        string.Join("; ", ctx.ModelState.Where(e => e.Value?.Errors.Count > 0)
            .Select(e => $"{e.Key}: {e.Value!.Errors[0].ErrorMessage}"))));
});
// Mock-адаптери каналів — лише Development (аудит H2): за замовчуванням false, у решті середовищ true = помилка старту.
var useMocks = builder.Configuration.GetValue("Channels:UseMocks", false);
if (useMocks && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Channels:UseMocks=true is allowed only in the Development environment.");

// Mock-провайдер ШІ (TASK-715, ADR-010 §4): так само лише Development.
if (builder.Configuration.GetValue("Ai:UseMockProvider", false) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Ai:UseMockProvider=true is allowed only in the Development environment.");

// Асистент у чаті клієнта (TASK-733, M3): поза Development вмикається лише після явного підтвердження юридичного gate
// (§21.11 п. 3: тексти розмов ідуть провайдеру ШІ). Без ChatAssistant:LegalGateApproved=true старт падає.
if (builder.Configuration.GetValue("ChatAssistant:Enabled", false) && !builder.Environment.IsDevelopment()
    && !builder.Configuration.GetValue("ChatAssistant:LegalGateApproved", false))
    throw new InvalidOperationException(
        "ChatAssistant:Enabled=true outside Development requires ChatAssistant:LegalGateApproved=true (legal gate, contract section 21.11).");

// Розсилки (TASK-779, §26.2, ADR-015 gate §26.16): поза Development Marketing:Enabled=true лише з Marketing:LegalGateApproved=true.
if (builder.Configuration.GetValue("Marketing:Enabled", false) && !builder.Environment.IsDevelopment()
    && !builder.Configuration.GetValue("Marketing:LegalGateApproved", false))
    throw new InvalidOperationException(
        "Marketing:Enabled=true outside Development requires Marketing:LegalGateApproved=true (legal gate, contract section 26.16).");

builder.Services
    .AddBeautyData()
    .AddBeautyChannels(useMocks)
    .AddBeautyAi()
    .AddBeautyAiSecretsStartupCheck() // keyring ключів ШІ (Ai:Secrets:*): валідація лише логується, старт не ламає
    .AddBeautyApplication()
    .AddBeautyAuthData()
    .AddBeautyAuthApi(builder.Configuration) // JWT, ролі, rate limit; без Auth:JwtSigningKey додаток не стартує
    .AddBeautyAiApi(builder.Configuration) // налаштування ШІ (TASK-716): ліміт ключів, мапінг винятків ШІ, swagger-фільтр секрету
    .AddBeautyPublicBookingApi(builder.Configuration) // публічний запис /api/public/{slug} (TASK-688)
    .AddBeautyAnalyticsApi(builder.Configuration); // ліміт деталей аналітики на користувача (TASK-764, §24.2)
// Купони (TASK-778, §26.2): ліміти спроб і строк утримання пропозиції з конфігу (Coupons:*); значення обмежуються в CouponOptions
builder.Services.Replace(ServiceDescriptor.Singleton(new BeautyCrm.Application.Features.BeautyCoupons.CouponOptions
{
    ChatInvalidPerConversationPerHour = builder.Configuration.GetValue("Coupons:ChatInvalidPerConversationPerHour", 5),
    PublicCheckPerIpPer10Min = builder.Configuration.GetValue("Coupons:PublicCheckPerIpPer10Min", 10),
    StaffCheckPerUserPerMinute = builder.Configuration.GetValue("Coupons:StaffCheckPerUserPerMinute", 30),
    ConversationHoldHours = builder.Configuration.GetValue("Coupons:ConversationHoldHours", 72),
    ChatBareLookupPerConversationPerHour = builder.Configuration.GetValue("Coupons:ChatBareLookupPerConversationPerHour", 20),
    ChatBareLookupPerTenantPerHour = builder.Configuration.GetValue("Coupons:ChatBareLookupPerTenantPerHour", 200),
    WritePerUserPerMinute = builder.Configuration.GetValue("Coupons:WritePerUserPerMinute", 20),
    BlockedCodes = new HashSet<string>(BeautyCrm.Application.Features.BeautyCoupons.CouponOptions.DefaultBlockedCodes
        .Concat((builder.Configuration["Coupons:BlockedCodes"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToUpperInvariant())), StringComparer.Ordinal),
}));
builder.Services.AddScoped<ITenantModuleProvider, DbTenantModuleProvider>(); // tenants.modules
builder.Services.AddBeautyDbRoleGuard(); // старт падає, якщо runtime-роль БД superuser/BYPASSRLS (H1)
builder.Services.AddBeautyCors(builder.Configuration); // Beauty:AllowedOrigins (адмінка), без credentials

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
    });
    o.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Reference = new Microsoft.OpenApi.Models.OpenApiReference
            {
                Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer",
            },
        }] = [],
    });
});

var app = builder.Build();

// CLI-режим: `ai-secrets reencrypt ...` (ротація keyring, ADR-010 §2.5). Веб-сервер не стартує.
if (args.Length > 0 && args[0] == AiSecretsCli.Command)
{
    Environment.ExitCode = await AiSecretsCli.RunAsync(app.Services, args[1..], Console.Out);
    return;
}

app.UseBeautyForwardedHeaders(); // лише довірені проксі (Beauty:TrustedProxies); першим, щоб IP/схема були справжніми далі
app.UseBeautySecurityHeaders(app.Environment);
app.UseBeautyAiNoStore(); // Cache-Control: no-store на /ai/settings*, /ai/models, /ai/usage
app.UseBeautyAnalyticsNoStore(); // Cache-Control: no-store на /analytics/* (TASK-764, §24)

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseBeautyCors(); // до автентифікації: preflight (OPTIONS) не несе токена
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>(); // після автентифікації (claim tenant_id), до авторизації й контролерів
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
