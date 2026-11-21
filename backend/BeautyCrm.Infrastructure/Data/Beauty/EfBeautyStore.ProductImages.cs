using BeautyCrm.Application.Features.BeautyProductImages;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// IProductImageStore (ADR-027, §33.22.7). Усе під RLS tenant. Єдине блокування операцій з фото - рядок товару FOR NO KEY UPDATE
// (серіалізує upload/reorder/delete одного товару; не конфліктує з FK-перевірками FOR KEY SHARE складу й замовлень).
public sealed partial class EfBeautyStore : IProductImageStore
{
    private const int MaxSortOrder = 99;

    private static ProductImageRow ToRow(ProductImage i) => new(i.Id, i.StorageKey, i.SizeBytes, i.Width, i.Height, i.SortOrder, i.CreatedAt);

    private string? FirstThumb(IReadOnlyDictionary<Guid, List<ProductImageRow>> images, Guid productId) =>
        images.TryGetValue(productId, out var l) && l.Count > 0 ? media!.Url(l[0].StorageKey, MediaOptions.ThumbSize) : null;

    /// <summary>Фото за товарами (порядок sort_order, created_at, id); ids = null - усі товари tenant. Функцію вимкнено - порожньо без запиту.</summary>
    private async Task<Dictionary<Guid, List<ProductImageRow>>> ImagesByProductAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        if (media is not { Enabled: true }) return [];
        var q = db.ProductImages.AsNoTracking().AsQueryable();
        if (ids is not null)
        {
            var list = ids.ToArray();
            q = q.Where(i => list.Contains(i.ProductId));
        }
        var rows = await q.OrderBy(i => i.ProductId).ThenBy(i => i.SortOrder).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(i => new { i.ProductId, Row = new ProductImageRow(i.Id, i.StorageKey, i.SizeBytes, i.Width, i.Height, i.SortOrder, i.CreatedAt) }).ToListAsync(ct);
        return rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.Select(r => r.Row).ToList());
    }

    public Task<bool> ProductExistsAsync(Guid productId, CancellationToken ct) => db.Products.AsNoTracking().AnyAsync(p => p.Id == productId, ct);

    public async Task<(int Product, int Tenant)> CountsAsync(Guid productId, CancellationToken ct) =>
        (await db.ProductImages.AsNoTracking().CountAsync(i => i.ProductId == productId, ct), await db.ProductImages.AsNoTracking().CountAsync(ct));

    private async Task<bool> LockProductForImagesAsync(Guid productId, CancellationToken ct) =>
        (await db.Database.SqlQuery<Guid>($"""SELECT id AS "Value" FROM beauty_products WHERE id = {productId} FOR NO KEY UPDATE""").ToListAsync(ct)).Count == 1;

    public async Task<ImageWriteResult> AddAsync(Guid productId, NewProductImage image, int maxPerProduct, int maxPerTenant, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await LockProductForImagesAsync(productId, ct)) return new(ImageWriteStatus.ProductNotFound);
        var rows = await db.ProductImages.AsNoTracking().Where(i => i.ProductId == productId).OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(i => i.Id).ToListAsync(ct);
        if (rows.Count >= maxPerProduct) return new(ImageWriteStatus.TooManyImages);
        if (await db.ProductImages.AsNoTracking().CountAsync(ct) >= maxPerTenant) return new(ImageWriteStatus.QuotaExceeded);

        var max = rows.Count == 0 ? -1 : await db.ProductImages.AsNoTracking().Where(i => i.ProductId == productId).MaxAsync(i => (int)i.SortOrder, ct);
        if (max + 1 > MaxSortOrder)
        {
            await ApplyOrderAsync(productId, rows, ct); // пропуски від видалень вичерпали 0..99: ущільнення
            max = rows.Count - 1;
        }
        var entity = new ProductImage
        {
            Id = Guid.NewGuid(), ProductId = productId, StorageKey = image.StorageKey, Mime = "image/webp", SizeBytes = image.SizeBytes,
            Width = (short)image.Width, Height = (short)image.Height, SortOrder = (short)(max + 1), CreatedByUserId = image.UserId, CreatedAt = image.Now,
        };
        db.ProductImages.Add(entity);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(ImageWriteStatus.Ok, ToRow(entity));
    }

    public async Task<ImageWriteResult> DeleteAsync(Guid productId, Guid imageId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await LockProductForImagesAsync(productId, ct)) return new(ImageWriteStatus.ProductNotFound);
        var img = await db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId && i.ProductId == productId, ct);
        if (img is null) return new(ImageWriteStatus.ImageNotFound);
        db.ProductImages.Remove(img);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(ImageWriteStatus.Ok, Key: img.StorageKey);
    }

    public async Task<ImageWriteResult> ReorderAsync(Guid productId, IReadOnlyList<Guid> imageIds, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await LockProductForImagesAsync(productId, ct)) return new(ImageWriteStatus.ProductNotFound);
        var current = await db.ProductImages.AsNoTracking().Where(i => i.ProductId == productId).Select(i => i.Id).ToListAsync(ct);
        if (imageIds.Count != current.Count || imageIds.Distinct().Count() != imageIds.Count || !current.ToHashSet().SetEquals(imageIds))
            return new(ImageWriteStatus.InvalidOrder);
        await ApplyOrderAsync(productId, imageIds, ct);
        var rows = await db.ProductImages.AsNoTracking().Where(i => i.ProductId == productId).OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return new(ImageWriteStatus.Ok, Rows: [.. rows.Select(ToRow)]);
    }

    /// <summary>sort_order = індекс у списку одним UPDATE (deferrable UNIQUE перевіряється при commit). Лише id товару: чужі id не потрапляють.</summary>
    private async Task ApplyOrderAsync(Guid productId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var arr = ids.ToArray();
        var ord = Enumerable.Range(0, arr.Length).Select(i => (short)i).ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE beauty_product_images i SET sort_order = v.ord FROM unnest({arr}, {ord}) AS v(id, ord) WHERE i.id = v.id AND i.product_id = {productId}", ct);
    }
}
