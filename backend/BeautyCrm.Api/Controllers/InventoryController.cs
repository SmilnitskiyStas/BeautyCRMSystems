using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Application.Features.BeautyProductImages;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Err = BeautyCrm.Application.Features.BeautyCommon.Error;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeautyCrm.Api.Controllers;

/// <summary>
/// Склад (ADR-025, контракт §32.5): модуль beauty_inventory, права inventory.* (§32.2). Порядок фільтрів: tenant_suspended ->
/// module_disabled -> forbidden_role -> 422 прив'язки -> specialist_inactive. Scope own (заклади профілю) застосовує Application;
/// чужий заклад/партія/операція -> 404 без розкриття. Усі відповіді Cache-Control: no-store. Ціни, контакти й кількості не логуються.
/// Контролер лише маршрутизує: уся логіка в <see cref="InventoryService"/>.
/// </summary>
[ApiController]
[Route("api/beauty/inventory")]
[RequireModule(BeautyModules.Inventory)]
[Authorize(Policy = AuthPolicies.Staff)]
[RequirePermission(PermissionKeys.InventoryView, PermissionLevels.Own)]
[InboxNoStore]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class InventoryController(InventoryService inventory, InventoryThrottle throttle, ProductImageService images, MediaOptions media, UploadThrottle uploads) : ControllerBase
{
    private const int SmallBody = 8 * 1024, BigBody = 64 * 1024;
    public const string IdempotencyHeader = "Idempotency-Key";

    private IActionResult RateLimited()
    {
        Response.Headers.RetryAfter = "60";
        return StatusCode(StatusCodes.Status429TooManyRequests, new ApiError("rate_limited", "Too many requests. Try again later."));
    }

    private async Task<IActionResult> Run<T>(Func<Actor, Task<Result<T>>> call, Func<T, IActionResult>? ok = null, bool write = false)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (write && !throttle.TryAcquire(actor.TenantId, actor.UserId)) return RateLimited();
        return this.ToResult(await call(actor), ok);
    }

    private IActionResult Created<T>(T v) => StatusCode(StatusCodes.Status201Created, v);

    private IActionResult OperationResponse(OperationResult r)
    {
        if (!r.Replayed) return Created(r.Operation);
        Response.Headers["Idempotent-Replayed"] = "true";
        return Ok(r.Operation);
    }

    private string? Key => Request.Headers[IdempotencyHeader].FirstOrDefault();

    // ---------- довідники ----------

    [HttpGet("scope")]
    [ProducesResponseType<InventoryScopeDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Scope(CancellationToken ct) => Run(a => inventory.GetScopeAsync(a, ct));

    // ---------- товари ----------

    [HttpGet("products")]
    [ProducesResponseType<Page<ProductDto>>(StatusCodes.Status200OK)]
    public Task<IActionResult> Products([FromQuery] string? search, [FromQuery] string? category, [FromQuery] string? purpose, [FromQuery] Guid? supplierId,
        [FromQuery] string? active, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        Run(a => inventory.ListProductsAsync(a, new ProductListQuery(search, category, purpose, supplierId, active, cursor, take), ct));

    [HttpGet("products/by-barcode")]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ByBarcode([FromQuery] string? code, CancellationToken ct) => Run(a => inventory.GetProductByBarcodeAsync(a, code, ct));

    [HttpGet("products/{id:guid}")]
    [ProducesResponseType<ProductDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Product(Guid id, CancellationToken ct) => Run(a => inventory.GetProductAsync(a, id, ct));

    /// <summary>Застарілий плоский список текстових категорій товарів (було GET /categories; дерево категорій - §32.27).</summary>
    [HttpGet("products/categories")]
    public Task<IActionResult> LegacyCategories(CancellationToken ct) =>
        Run(a => inventory.ListCategoriesAsync(a, ct), items => Ok(new { items }));

    // ---------- дерево категорій (§32.27) ----------

    [HttpGet("categories")]
    [ProducesResponseType<IReadOnlyList<CategoryDto>>(StatusCodes.Status200OK)]
    public Task<IActionResult> CategoryNodes([FromQuery] bool includeArchived = false, CancellationToken ct = default) =>
        Run(a => inventory.ListCategoryNodesAsync(a, includeArchived, ct), items => Ok(new { items }));

    [HttpPost("categories")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> CreateCategory([FromBody] CategoryRequest request, CancellationToken ct) =>
        Run(a => inventory.CreateCategoryAsync(a, request, ct), Created, write: true);

    [HttpPut("categories/{id:guid}")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> UpdateCategory(Guid id, [FromBody] CategoryRequest request, CancellationToken ct) =>
        Run(a => inventory.UpdateCategoryAsync(a, id, request, ct), write: true);

    [HttpPut("categories/{id:guid}/move")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> MoveCategory(Guid id, [FromBody] CategoryMoveRequest request, CancellationToken ct) =>
        Run(a => inventory.MoveCategoryAsync(a, id, request, ct), write: true);

    [HttpPost("categories/{id:guid}/archive")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> ArchiveCategory(Guid id, CancellationToken ct) =>
        Run(a => inventory.SetCategoryArchivedAsync(a, id, true, ct), write: true);

    [HttpPost("categories/{id:guid}/unarchive")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> UnarchiveCategory(Guid id, CancellationToken ct) =>
        Run(a => inventory.SetCategoryArchivedAsync(a, id, false, ct), write: true);

    [HttpDelete("categories/{id:guid}")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct) =>
        Run(a => inventory.DeleteCategoryAsync(a, id, ct), _ => NoContent(), write: true);

    [HttpPost("products")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<ProductDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> CreateProduct([FromBody] ProductRequest request, CancellationToken ct) =>
        Run(a => inventory.CreateProductAsync(a, request, ct), Created, write: true);

    [HttpPut("products/{id:guid}")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> UpdateProduct(Guid id, [FromBody] ProductRequest request, CancellationToken ct) =>
        Run(a => inventory.UpdateProductAsync(a, id, request, ct), write: true);

    [HttpPut("products/{id:guid}/locations/{locationId:guid}")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<LocationSettingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> PutSetting(Guid id, Guid locationId, [FromBody] LocationSettingRequest request, CancellationToken ct) =>
        Run(a => inventory.PutLocationSettingAsync(a, id, locationId, request, ct), write: true);

    [HttpDelete("products/{id:guid}/locations/{locationId:guid}")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> DeleteSetting(Guid id, Guid locationId, CancellationToken ct) =>
        Run(a => inventory.DeleteLocationSettingAsync(a, id, locationId, ct), _ => NoContent(), write: true);

    // ---------- фото товарів (ADR-027, §33.22.5): мережевий каталог -> inventory.manage = all ----------

    private const int MultipartOverhead = 64 * 1024;

    /// <summary>201 ProductImageDto. Порядок перевірок §33.22.5: права -> функція ввімкнена -> товар -> файл (422/413) -> ліміти -> формат -> розміри -> обробка -> запис.</summary>
    [HttpPost("products/{id:guid}/images")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [DisableFormValueModelBinding]
    [ProducesResponseType<ProductImageDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UploadImage(Guid id, CancellationToken ct)
    {
        if (User.ToActor() is not { } actor) return Unauthorized();
        if (!throttle.TryAcquire(actor.TenantId, actor.UserId) || !uploads.TryAcquire(actor.TenantId, actor.UserId)) return RateLimited();
        // ліміт тіла для цього маршруту: файл + службові частини (глобальні ліміти форм не застосовуються: тіло читається потоково нижче)
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = media.MaxUploadBytes + MultipartOverhead;
        // глобальний ліміт одночасних upload на процес - ДО читання тіла в пам'ять (security M1)
        if (!uploads.TryEnter())
        {
            Response.Headers.RetryAfter = "5";
            Result<ProductImageDto> busy = Err.Unavailable("media_busy", "Image processing is busy. Try again shortly.");
            return this.ToResult(busy, Created);
        }
        Result<ProductImageDto> result;
        try { result = await images.UploadAsync(actor, id, ReadFilePartAsync, ct); }
        finally { uploads.Exit(); }
        if (!result.IsOk && result.Error!.Code == "media_busy") Response.Headers.RetryAfter = "5";
        return this.ToResult(result, Created);
    }

    /// <summary>Перша частина multipart з іменем <c>file</c> (решта ігнорується) у пам'ять з лімітом <c>MaxUploadBytes</c>.</summary>
    private async Task<Result<Stream>> ReadFilePartAsync(CancellationToken ct)
    {
        var tooLarge = Err.TooLarge("file_too_large", "The file is too large.");
        var required = Err.Validation("file_required", "A multipart part named 'file' is required.");
        if (Request.ContentLength > media.MaxUploadBytes + MultipartOverhead) return tooLarge;
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var mt) || !mt.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mt.Boundary).Length is 0 or > 200)
            return required;
        try
        {
            var reader = new MultipartReader(HeaderUtilities.RemoveQuotes(mt.Boundary).Value!, Request.Body) { HeadersLengthLimit = 16 * 1024 };
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd) || !cd.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
                    || HeaderUtilities.RemoveQuotes(cd.Name).Value != "file")
                    continue;
                var ms = new MemoryStream();
                var buffer = new byte[81920];
                int n;
                while ((n = await section.Body.ReadAsync(buffer, ct)) > 0)
                {
                    if (ms.Length + n > media.MaxUploadBytes) { await ms.DisposeAsync(); return tooLarge; }
                    ms.Write(buffer, 0, n);
                }
                if (ms.Length == 0) return required;
                return ms;
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { return tooLarge; }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return required; }
        return required;
    }

    [HttpDelete("products/{id:guid}/images/{imageId:guid}")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> DeleteImage(Guid id, Guid imageId, CancellationToken ct) =>
        Run(a => images.DeleteAsync(a, id, imageId, ct), _ => NoContent(), write: true);

    [HttpPatch("products/{id:guid}/images/order")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [RequestSizeLimit(4 * 1024)]
    [ProducesResponseType<ProductImageListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> ReorderImages(Guid id, [FromBody] ProductImageOrderRequest? request, CancellationToken ct) =>
        Run(a => images.ReorderAsync(a, id, request, ct), write: true);

    // ---------- постачальники ----------

    [HttpGet("suppliers")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [ProducesResponseType<Page<SupplierDto>>(StatusCodes.Status200OK)]
    public Task<IActionResult> Suppliers([FromQuery] string? search, [FromQuery] string? active, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        Run(a => inventory.ListSuppliersAsync(a, new SupplierListQuery(search, active, cursor, take), ct));

    [HttpPost("suppliers")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<SupplierDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> CreateSupplier([FromBody] SupplierRequest request, CancellationToken ct) =>
        Run(a => inventory.CreateSupplierAsync(a, request, ct), Created, write: true);

    [HttpPut("suppliers/{id:guid}")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.All)]
    [RequestSizeLimit(SmallBody)]
    [ProducesResponseType<SupplierDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> UpdateSupplier(Guid id, [FromBody] SupplierRequest request, CancellationToken ct) =>
        Run(a => inventory.UpdateSupplierAsync(a, id, request, ct), write: true);

    // ---------- залишки, партії, журнал ----------

    [HttpGet("stock")]
    [ProducesResponseType<StockPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Stock([FromQuery] Guid? locationId, [FromQuery] string? search, [FromQuery] string? category, [FromQuery] string? purpose,
        [FromQuery] string? state, [FromQuery] int? expiringWithinDays, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        Run(a => inventory.GetStockAsync(a, new StockListQuery(locationId, search, category, purpose, state, expiringWithinDays, cursor, take), ct));

    [HttpGet("stock/summary")]
    [ProducesResponseType<StockSummaryPageDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Summary([FromQuery] string? search, [FromQuery] string? category, [FromQuery] string? purpose, [FromQuery] bool? lowOnly,
        [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        Run(a => inventory.GetSummaryAsync(a, new SummaryListQuery(search, category, purpose, lowOnly, cursor, take), ct));

    [HttpGet("batches")]
    [ProducesResponseType<Page<BatchDto>>(StatusCodes.Status200OK)]
    public Task<IActionResult> Batches([FromQuery] Guid? locationId, [FromQuery] Guid? productId, [FromQuery] string? status, [FromQuery] int? expiringWithinDays,
        [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        Run(a => inventory.ListBatchesAsync(a, new BatchListQuery(locationId, productId, status, expiringWithinDays, cursor, take), ct));

    [HttpGet("movements")]
    [ProducesResponseType<Page<MovementDto>>(StatusCodes.Status200OK)]
    public Task<IActionResult> Movements([FromQuery] Guid? locationId, [FromQuery] Guid? productId, [FromQuery] string? type, [FromQuery] Guid? operationId,
        [FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        Run(a => inventory.ListMovementsAsync(a, new MovementListQuery(locationId, productId, type, operationId, from, to, cursor, take), ct));

    [HttpGet("operations")]
    [ProducesResponseType<Page<OperationSummaryDto>>(StatusCodes.Status200OK)]
    public Task<IActionResult> Operations([FromQuery] string? kind, [FromQuery] Guid? locationId, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        Run(a => inventory.ListOperationsAsync(a, new OperationListQuery(kind, locationId, from, to, cursor, take), ct));

    [HttpGet("operations/{id:guid}")]
    [ProducesResponseType<OperationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Operation(Guid id, CancellationToken ct) => Run(a => inventory.GetOperationAsync(a, id, ct));

    // ---------- операції (Idempotency-Key обов'язковий) ----------

    /// <summary>201 або 200 + Idempotent-Replayed. 404 location_not_found | product_not_found | supplier_not_found; 422 валідація, insufficient_stock.</summary>
    [HttpPost("receipts")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(BigBody)]
    [ProducesResponseType<OperationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<OperationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> Receipt([FromBody] ReceiptRequest request, CancellationToken ct) =>
        Run(a => inventory.ReceiveAsync(a, Key, request, ct), OperationResponse, write: true);

    [HttpPost("write-offs")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(BigBody)]
    [ProducesResponseType<OperationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<OperationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> WriteOff([FromBody] WriteOffRequest request, CancellationToken ct) =>
        Run(a => inventory.WriteOffAsync(a, Key, request, ct), OperationResponse, write: true);

    [HttpPost("transfers")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(BigBody)]
    [ProducesResponseType<OperationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<OperationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> Transfer([FromBody] TransferRequest request, CancellationToken ct) =>
        Run(a => inventory.TransferAsync(a, Key, request, ct), OperationResponse, write: true);

    // ---------- інвентаризації ----------

    [HttpGet("stocktakes")]
    [ProducesResponseType<Page<StocktakeSummaryDto>>(StatusCodes.Status200OK)]
    public Task<IActionResult> Stocktakes([FromQuery] Guid? locationId, [FromQuery] string? status, [FromQuery] string? cursor, [FromQuery] int? take, CancellationToken ct) =>
        Run(a => inventory.ListStocktakesAsync(a, new StocktakeListQuery(locationId, status, cursor, take), ct));

    [HttpPost("stocktakes")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(BigBody)]
    [ProducesResponseType<StocktakeDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> CreateStocktake([FromBody] StocktakeCreateRequest request, CancellationToken ct) =>
        Run(a => inventory.CreateStocktakeAsync(a, request, ct), Created, write: true);

    [HttpGet("stocktakes/{id:guid}")]
    [ProducesResponseType<StocktakeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Stocktake(Guid id, CancellationToken ct) => Run(a => inventory.GetStocktakeAsync(a, id, ct));

    [HttpPut("stocktakes/{id:guid}/lines")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(BigBody)]
    [ProducesResponseType<StocktakeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> PutLines(Guid id, [FromBody] StocktakeLinesRequest request, CancellationToken ct) =>
        Run(a => inventory.PutStocktakeLinesAsync(a, id, request, ct), write: true);

    [HttpPost("stocktakes/{id:guid}/post")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(BigBody)]
    [ProducesResponseType<StocktakePostDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> PostStocktake(Guid id, [FromBody] StocktakeVersionRequest request, CancellationToken ct) =>
        Run(a => inventory.PostStocktakeAsync(a, id, request, ct), write: true);

    [HttpPost("stocktakes/{id:guid}/cancel")]
    [RequirePermission(PermissionKeys.InventoryManage, PermissionLevels.Own)]
    [RequestSizeLimit(BigBody)]
    [ProducesResponseType<StocktakeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public Task<IActionResult> CancelStocktake(Guid id, [FromBody] StocktakeVersionRequest request, CancellationToken ct) =>
        Run(a => inventory.CancelStocktakeAsync(a, id, request, ct), write: true);
}
