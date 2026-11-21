using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// Дерево категорій товарів (ADR-025, контракт §32.27). Усе під RLS tenant. Зміни дерева - у транзакції під advisory-блокуванням tenant.
public sealed partial class EfBeautyStore
{
    private const string CategoryNameIndex = "ux_beauty_product_categories_name";
    private const string PathSeparator = " › ";

    private static bool IsCategoryFk(Exception? ex) =>
        ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } pg }
        && pg.ConstraintName?.Contains("categor", StringComparison.Ordinal) == true;

    /// <summary>Повні шляхи всіх категорій ("Волосся › Фарби"), у т.ч. архівних; обчислюється на читанні (дерево до 500 вузлів).</summary>
    private async Task<Dictionary<Guid, string>> CategoryPathsAsync(CancellationToken ct)
    {
        var rows = await db.ProductCategories.AsNoTracking().Select(c => new { c.Id, c.ParentId, c.Name }).ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);
        var paths = new Dictionary<Guid, string>(rows.Count);
        foreach (var r in rows)
        {
            var names = new List<string>(3);
            var cur = r;
            for (var guard = 0; guard < 8; guard++)
            {
                names.Add(cur.Name);
                if (cur.ParentId is not { } pid || !byId.TryGetValue(pid, out var parent)) break;
                cur = parent;
            }
            names.Reverse();
            paths[r.Id] = string.Join(PathSeparator, names);
        }
        return paths;
    }

    public async Task<IReadOnlyList<CategoryDto>> ListCategoryNodesAsync(CancellationToken ct)
    {
        var rows = await db.ProductCategories.AsNoTracking().ToListAsync(ct);
        var products = await db.Products.AsNoTracking().Where(p => p.CategoryId != null).GroupBy(p => p.CategoryId!.Value)
            .Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var children = rows.Where(r => r.ParentId != null).GroupBy(r => r.ParentId!.Value).ToDictionary(g => g.Key, g => g.Count());
        return [.. rows.Select(r => new CategoryDto(r.Id, r.ParentId, r.Name, r.SortOrder, r.IsArchived, products.GetValueOrDefault(r.Id), children.GetValueOrDefault(r.Id)))];
    }

    public async Task<CategoryRow?> GetCategoryRowAsync(Guid id, CancellationToken ct) =>
        await db.ProductCategories.AsNoTracking().Where(c => c.Id == id)
            .Select(c => new CategoryRow(c.Id, c.ParentId, c.Name, c.SortOrder, c.IsArchived)).FirstOrDefaultAsync(ct);

    private async Task<CategoryDto?> CategoryDtoAsync(Guid id, CancellationToken ct)
    {
        var c = await db.ProductCategories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        return new CategoryDto(c.Id, c.ParentId, c.Name, c.SortOrder, c.IsArchived,
            await db.Products.AsNoTracking().CountAsync(p => p.CategoryId == id, ct),
            await db.ProductCategories.AsNoTracking().CountAsync(x => x.ParentId == id, ct));
    }

    public async Task<CategoryWriteResult> WriteCategoryAsync(Func<IReadOnlyList<CategoryRow>, CategoryPlan> decide, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // одна зміна дерева tenant за раз: глибина, цикл і ліміт перевіряються проти актуального знімка
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('product-categories:' || coalesce(current_setting('app.tenant_id', true), ''), 0))", ct);
        var rows = await db.ProductCategories.AsNoTracking().Select(c => new CategoryRow(c.Id, c.ParentId, c.Name, c.SortOrder, c.IsArchived)).ToListAsync(ct);
        Guid id;
        switch (decide(rows))
        {
            case CategoryReject r:
                await tx.RollbackAsync(ct);
                return new(CategoryWriteOutcome.Rejected, Error: r.Error);
            case CategoryInsert i:
                var created = new ProductCategory { Id = Guid.NewGuid(), ParentId = i.ParentId, Name = i.Name, SortOrder = i.SortOrder, CreatedAt = now, UpdatedAt = now };
                db.ProductCategories.Add(created);
                id = created.Id;
                break;
            case CategoryUpdate u:
                var e = await db.ProductCategories.FirstOrDefaultAsync(x => x.Id == u.Id, ct);
                if (e is null) { await tx.RollbackAsync(ct); return new(CategoryWriteOutcome.NotFound); }
                e.ParentId = u.ParentId; e.Name = u.Name; e.SortOrder = u.SortOrder; e.IsArchived = u.IsArchived;
                id = u.Id;
                break;
            default:
                throw new InvalidOperationException("Unknown category plan.");
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUnique(ex, CategoryNameIndex)) { db.ChangeTracker.Clear(); return new(CategoryWriteOutcome.NameTaken); }
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return new(CategoryWriteOutcome.Ok, await CategoryDtoAsync(id, ct));
    }

    public async Task<CategoryWriteOutcome> DeleteCategoryAsync(Guid id, CancellationToken ct)
    {
        if (!await db.ProductCategories.AnyAsync(c => c.Id == id, ct)) return CategoryWriteOutcome.NotFound;
        // Перевірка + RESTRICT-FK як страховка від гонки (товар/підкатегорія додані між перевіркою й видаленням)
        if (await db.ProductCategories.AnyAsync(c => c.ParentId == id, ct) || await db.Products.AnyAsync(p => p.CategoryId == id, ct)) return CategoryWriteOutcome.NotEmpty;
        try { return await db.ProductCategories.Where(c => c.Id == id).ExecuteDeleteAsync(ct) > 0 ? CategoryWriteOutcome.Ok : CategoryWriteOutcome.NotFound; }
        catch (DbUpdateException ex) when (IsCategoryFk(ex)) { return CategoryWriteOutcome.NotEmpty; }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return CategoryWriteOutcome.NotEmpty; }
    }
}
