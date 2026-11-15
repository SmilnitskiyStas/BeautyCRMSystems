using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// Модель EF збігається зі знімком міграцій (без БД): нова зміна моделі без міграції (або міграція, що перейменовує
/// наявні ключі/обмеження через порядок конфігурації) помітна одразу.
/// </summary>
public class ModelSnapshotTests
{
    [Fact]
    public void model_has_no_pending_changes_against_the_migrations_snapshot()
    {
        var options = new DbContextOptionsBuilder<BeautyDbContext>();
        options.UseBeautyNpgsql("Host=localhost;Database=unused;Username=unused");
        using var ctx = new BeautyDbContext(options.Options, new TenantContext());

        var snapshot = ctx.GetService<IMigrationsAssembly>().ModelSnapshot?.Model
                       ?? throw new InvalidOperationException("model snapshot not found");
        var snapshotModel = ctx.GetService<IModelRuntimeInitializer>().Initialize(snapshot);
        var diffs = ctx.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshotModel.GetRelationalModel(), ctx.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.True(diffs.Count == 0, "Pending model changes:\n" + string.Join("\n", diffs.Select(Describe)));
    }

    private static string Describe(MigrationOperation op) => op switch
    {
        AddUniqueConstraintOperation u => $"AddUnique {u.Table}.{u.Name}",
        DropUniqueConstraintOperation u => $"DropUnique {u.Table}.{u.Name}",
        AddForeignKeyOperation f => $"AddFk {f.Table}.{f.Name}",
        DropForeignKeyOperation f => $"DropFk {f.Table}.{f.Name}",
        CreateIndexOperation i => $"CreateIndex {i.Table}.{i.Name}",
        DropIndexOperation i => $"DropIndex {i.Table}.{i.Name}",
        RenameIndexOperation i => $"RenameIndex {i.Table}.{i.Name} -> {i.NewName}",
        AddCheckConstraintOperation c => $"AddCheck {c.Table}.{c.Name}",
        DropCheckConstraintOperation c => $"DropCheck {c.Table}.{c.Name}",
        AddColumnOperation c => $"AddColumn {c.Table}.{c.Name}",
        AlterColumnOperation c => $"AlterColumn {c.Table}.{c.Name}",
        _ => op.GetType().Name,
    };
}
