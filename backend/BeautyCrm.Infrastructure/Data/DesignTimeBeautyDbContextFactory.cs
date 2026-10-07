using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BeautyCrm.Infrastructure.Data;

/// <summary>
/// Used by `dotnet ef` (startup project = BeautyCrm.Infrastructure), so migrations don't depend
/// on Program.cs. `migrations add` does not connect; `database update` uses env
/// ConnectionStrings__Default — run it as the schema owner, not the runtime app role.
/// </summary>
public sealed class DesignTimeBeautyDbContextFactory : IDesignTimeDbContextFactory<BeautyDbContext>
{
    public BeautyDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Database=beautycrm;Username=postgres";

        var options = new DbContextOptionsBuilder<BeautyDbContext>();
        options.UseBeautyNpgsql(connectionString);
        return new BeautyDbContext(options.Options, new TenantContext());
    }
}
