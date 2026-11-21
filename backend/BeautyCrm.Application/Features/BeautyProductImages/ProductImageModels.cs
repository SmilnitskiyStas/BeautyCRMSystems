using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyProductImages;

public enum MediaSweepMode { Off, DryRun, Delete }

/// <summary>
/// Зображення товарів (ADR-027, §33.22). Без <see cref="RootPath"/> функцію вимкнено: CRM-маршрути 404 product_images_disabled,
/// DTO віддають null/[]. Значення валідує Api (<c>MediaSetup</c>), тут лише дані й побудова URL.
/// </summary>
public sealed class MediaOptions
{
    public const int ThumbSize = 400, FullSize = 1600, MinSide = 200, MaxSide = 10_000, PublicPermitPerMinute = 600;

    public string? RootPath { get; set; }
    public string? PublicBaseUrl { get; set; }
    public int MaxUploadBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxImagesPerProduct { get; set; } = 8;
    public int MaxImagesPerTenant { get; set; } = 5000;
    public int MaxPixels { get; set; } = 25_000_000;
    public int MaxConcurrentProcessing { get; set; } = 1;
    public int UploadsPerUserPerMinute { get; set; } = 20;
    public MediaSweepMode SweepMode { get; set; } = MediaSweepMode.Off;

    public bool Enabled => !string.IsNullOrEmpty(RootPath);

    /// <summary>Абсолютний URL варіанта: {PublicBaseUrl}/api/public/media/p/{key}-{size}.webp.</summary>
    public string Url(string key, int size) => $"{PublicBaseUrl}/api/public/media/p/{key}-{size}.webp";
}

// ---------- DTO ----------

public sealed record ProductImageDto(Guid Id, string Url, string ThumbUrl, int Width, int Height, int SizeBytes, int SortOrder, DateTimeOffset CreatedAt);

public sealed record ProductImageOrderRequest(IReadOnlyList<Guid>? ImageIds);

public sealed record ProductImageListDto(IReadOnlyList<ProductImageDto> Images);

public sealed record PublicProductImageDto(string Url, string ThumbUrl, int Width, int Height);

// ---------- порти ----------

/// <summary>Рядок фото (без URL: URL будує <see cref="ProductImageMapping"/>).</summary>
public sealed record ProductImageRow(Guid Id, string StorageKey, int SizeBytes, int Width, int Height, int SortOrder, DateTimeOffset CreatedAt);

public static class ProductImageMapping
{
    public static ProductImageDto ToDto(this ProductImageRow r, MediaOptions o) =>
        new(r.Id, o.Url(r.StorageKey, MediaOptions.FullSize), o.Url(r.StorageKey, MediaOptions.ThumbSize), r.Width, r.Height, r.SizeBytes, r.SortOrder, r.CreatedAt);

    public static PublicProductImageDto ToPublic(this ProductImageRow r, MediaOptions o) =>
        new(o.Url(r.StorageKey, MediaOptions.FullSize), o.Url(r.StorageKey, MediaOptions.ThumbSize), r.Width, r.Height);
}

public sealed record ImageVariantData(int Size, int Width, int Height, byte[] Bytes);

/// <param name="Width">Розміри варіанта 1600 (найбільшого).</param>
public sealed record ProcessedImage(IReadOnlyList<ImageVariantData> Variants, int Width, int Height)
{
    public int TotalBytes => Variants.Sum(v => v.Bytes.Length);
}

/// <summary>Обробка зображення (ADR-027 §2.5): сигнатура, розміри до декодування, орієнтація, зняття метаданих, варіанти WebP.</summary>
public interface IImageProcessor
{
    /// <summary>415 unsupported_image_type, 422 image_dimensions_invalid | image_decode_failed, 503 media_busy.</summary>
    Task<Result<ProcessedImage>> ProcessAsync(Stream input, CancellationToken ct);
}

/// <summary>Файлове сховище (ADR-027 §2.2). Імена файлів збираються лише з валідного ключа; ім'я від клієнта в шлях не потрапляє.</summary>
public interface IProductImageStorage
{
    /// <summary>
    /// Маркер -> *.tmp -> rename. false = ключ уже зайнятий (нічого не змінено). Збій: те, що створено цим викликом, видаляється, виняток піднімається.
    /// </summary>
    Task<bool> TryWriteAsync(Guid tenantId, string key, IReadOnlyList<ImageVariantData> variants, CancellationToken ct);

    /// <summary>Варіанти, потім маркер (best effort). false = хоч щось не вдалося видалити.</summary>
    bool Delete(Guid tenantId, string key);
}

public enum ImageWriteStatus { Ok, ProductNotFound, ImageNotFound, TooManyImages, QuotaExceeded, InvalidOrder }

public sealed record ImageWriteResult(ImageWriteStatus Status, ProductImageRow? Row = null, IReadOnlyList<ProductImageRow>? Rows = null, string? Key = null);

public sealed record NewProductImage(string StorageKey, int SizeBytes, int Width, int Height, Guid UserId, DateTimeOffset Now);

/// <summary>Порт даних фото (все під RLS tenant). Записи — під <c>FOR NO KEY UPDATE</c> рядка товару.</summary>
public interface IProductImageStore
{
    Task<bool> ProductExistsAsync(Guid productId, CancellationToken ct);
    /// <summary>Швидка перевірка (оптимізація до обробки): (кількість у товарі, кількість у tenant).</summary>
    Task<(int Product, int Tenant)> CountsAsync(Guid productId, CancellationToken ct);
    /// <summary>Остаточна перевірка лімітів і вставка під блокуванням товару.</summary>
    Task<ImageWriteResult> AddAsync(Guid productId, NewProductImage image, int maxPerProduct, int maxPerTenant, CancellationToken ct);
    /// <summary>Ok + ключ видаленого рядка; ProductNotFound / ImageNotFound.</summary>
    Task<ImageWriteResult> DeleteAsync(Guid productId, Guid imageId, CancellationToken ct);
    Task<ImageWriteResult> ReorderAsync(Guid productId, IReadOnlyList<Guid> imageIds, CancellationToken ct);
}
