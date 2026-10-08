using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.PublicBooking;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.Data;
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

builder.Services
    .AddBeautyData()
    .AddBeautyChannels(useMocks)
    .AddBeautyAi()
    .AddBeautyApplication()
    .AddBeautyAuthData()
    .AddBeautyAuthApi(builder.Configuration) // JWT, ролі, rate limit; без Auth:JwtSigningKey додаток не стартує
    .AddBeautyPublicBookingApi(builder.Configuration); // публічний запис /api/public/{slug} (TASK-688)
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

app.UseBeautyForwardedHeaders(); // лише довірені проксі (Beauty:TrustedProxies); першим, щоб IP/схема були справжніми далі
app.UseBeautySecurityHeaders(app.Environment);

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
