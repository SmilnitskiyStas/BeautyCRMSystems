using System.Net;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Auth;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// §31.15 п. 24 (F-6): <c>users.sessions_valid_after</c> читається тим самим запитом, що й решта доступу, тож кількість SQL-команд
/// <c>AccessContextMiddleware</c> на запит не зросла відносно ADR-023 (рядок користувача з правами + модулі tenant = 2 команди).
/// Лічильник - EF-інтерсептор у DI (IInterceptor), прив'язаний до виклику <see cref="IAccessStore.LoadAsync"/> через AsyncLocal,
/// тож команди самого контролера не враховуються.
/// </summary>
public sealed class AccessContextQueryCountTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    /// <summary>Команди, виконані під час одного <see cref="IAccessStore.LoadAsync"/> (ADR-023: користувач+права, потім модулі tenant).</summary>
    private const int ExpectedCommandsPerRequest = 2;

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private sealed class Probe
    {
        public readonly List<int> PerRequest = [];
    }

    private static readonly AsyncLocal<int[]?> Current = new();

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private static void Count()
        {
            if (Current.Value is { } box) Interlocked.Increment(ref box[0]);
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
        {
            Count();
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }

        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            Count();
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    private sealed class CountingAccessStore(IAccessStore inner, Probe probe) : IAccessStore
    {
        public async Task<AccessSnapshot?> LoadAsync(Guid userId, CancellationToken ct)
        {
            var box = new int[1];
            Current.Value = box;
            try
            {
                return await inner.LoadAsync(userId, ct);
            }
            finally
            {
                Current.Value = null;
                lock (probe.PerRequest) probe.PerRequest.Add(box[0]);
            }
        }
    }

    private HttpClient CountingClient(Probe probe)
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            // EF8 не підхоплює IInterceptor з DI: перереєструємо контекст з тими ж опціями + лічильник команд
            s.RemoveAll<BeautyDbContext>();
            s.RemoveAll<DbContextOptions<BeautyDbContext>>();
            s.RemoveAll<DbContextOptions>();
            var counter = new CommandCounter();
            s.AddDbContext<BeautyDbContext>((sp, o) => o.UseBeautyNpgsql(fx.Db.AppConnectionString)
                .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>(), counter));
            s.RemoveAll<IAccessStore>();
            s.AddScoped<IAccessStore>(sp => new CountingAccessStore(ActivatorUtilities.CreateInstance<EfAccessStore>(sp), probe));
        }));
        _disposables.Add(f);
        var c = f.CreateClient();
        _disposables.Add(c);
        return c;
    }

    [SkippableFact]
    public async Task AccessContextMiddleware_runs_the_same_number_of_sql_commands_with_and_without_sessions_valid_after()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var t = await fx.CreateTenantAsync();
        var probe = new Probe();
        var c = CountingClient(probe);
        var session = await fx.LoginAsync(t.Slug, t.OwnerEmail);

        // sessions_valid_after IS NULL (як раніше)
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Get, "/api/auth/me", bearer: session.AccessToken))).StatusCode);
        int withNull;
        lock (probe.PerRequest) withNull = probe.PerRequest.Last();

        // sessions_valid_after встановлено в минулому (токен виданий пізніше -> проходить)
        await using (var conn = new NpgsqlConnection(fx.Db.AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE users SET sessions_valid_after = now() - interval '1 hour' WHERE tenant_id = @t", conn);
            cmd.Parameters.AddWithValue("t", t.TenantId);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Get, "/api/auth/me", bearer: session.AccessToken))).StatusCode);
        int withValue;
        lock (probe.PerRequest) withValue = probe.PerRequest.Last();

        // токен, виданий до позначки: 401 session_revoked теж без додаткового запиту
        await using (var conn = new NpgsqlConnection(fx.Db.AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE users SET sessions_valid_after = now() + interval '1 hour' WHERE tenant_id = @t", conn);
            cmd.Parameters.AddWithValue("t", t.TenantId);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(HttpMethod.Get, "/api/auth/me", bearer: session.AccessToken))).StatusCode);
        int revoked;
        lock (probe.PerRequest) revoked = probe.PerRequest.Last();

        Assert.Equal(ExpectedCommandsPerRequest, withNull);
        Assert.Equal(withNull, withValue);
        Assert.Equal(withNull, revoked);
    }
}
