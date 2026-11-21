using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyProductImages;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BeautyCrm.Infrastructure.Media;

/// <summary>
/// ADR-027 §2.5 (SixLabors.ImageSharp 3.1.x, за портом <see cref="IImageProcessor"/>): конфігурація лише з JPEG/PNG/WebP, тип за сигнатурою,
/// Identify до декодування (розміри/мегапікселі), один кадр, ліміт алокатора 256 МБ, AutoOrient + зняття всіх метаданих,
/// варіанти 400/1600 без збільшення, WebP lossy q80. Паралельність - семафор (очікування &gt; WaitTimeout = 503 media_busy).
/// </summary>
public sealed class ImageSharpProcessor : IImageProcessor, IDisposable
{
    private static readonly WebpEncoder Encoder = new() { FileFormat = WebpFileFormatType.Lossy, Quality = 80, SkipMetadata = true };

    private readonly MediaOptions _options;
    private readonly Configuration _config;
    private readonly SemaphoreSlim _gate;

    /// <summary>Очікування черги обробки (ADR-027: 10 с).</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Ліміт часу декодування + кодування одного зображення (security-рев'ю m1).</summary>
    public TimeSpan ProcessingTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public ImageSharpProcessor(MediaOptions options)
    {
        _options = options;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentProcessing));
        _config = new Configuration(new JpegConfigurationModule(), new PngConfigurationModule(), new WebpConfigurationModule())
        {
            MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions { AllocationLimitMegabytes = 256, MaximumPoolSizeMegabytes = 64 }),
        };
    }

    public void Dispose() => _gate.Dispose();

    private static readonly Error Unsupported = Error.UnsupportedMedia("unsupported_image_type", "Only JPEG, PNG and WebP images are accepted.");
    private static readonly Error DecodeFailed = Error.Validation("image_decode_failed", "The image could not be decoded.");

    /// <summary>JPEG: FF D8 FF; PNG: 89 'PNG' 0D 0A 1A 0A; WebP: 'RIFF' .... 'WEBP'.</summary>
    internal static bool HasSupportedSignature(ReadOnlySpan<byte> h) =>
        h.Length >= 12 && (
            h[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF })
            || h[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
            || (h[..4].SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("WEBP"u8)));

    public async Task<Result<ProcessedImage>> ProcessAsync(Stream input, CancellationToken ct)
    {
        var head = new byte[12];
        input.Position = 0;
        var n = await input.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        if (!HasSupportedSignature(head.AsSpan(0, n))) return Unsupported;

        ImageInfo info;
        try
        {
            input.Position = 0;
            info = await Image.IdentifyAsync(new DecoderOptions { Configuration = _config }, input, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return DecodeFailed; }

        if (info.Width < MediaOptions.MinSide || info.Height < MediaOptions.MinSide || info.Width > MediaOptions.MaxSide || info.Height > MediaOptions.MaxSide
            || (long)info.Width * info.Height > _options.MaxPixels)
            return Error.Validation("image_dimensions_invalid", "Image dimensions are out of the allowed range.") with
            {
                Extras = new Dictionary<string, object?> { ["minSide"] = MediaOptions.MinSide, ["maxSide"] = MediaOptions.MaxSide, ["maxPixels"] = _options.MaxPixels },
            };

        if (!await _gate.WaitAsync(WaitTimeout, ct)) return Error.Unavailable("media_busy", "Image processing is busy. Try again shortly.");
        try
        {
            input.Position = 0;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProcessingTimeout);
            var t = cts.Token;
            // фіксований Rgba32 (4 Б/піксель; 16-бітні PNG не розгортаються в Rgba64); JPEG TargetSize не застосовано: декодер масштабує до цільового розміру (в т.ч. збільшує)
            using var image = await Image.LoadAsync<Rgba32>(new DecoderOptions { Configuration = _config, MaxFrames = 1 }, input, t);
            image.Mutate(x => x.AutoOrient()); // EXIF-орієнтація - у пікселі, далі метадані знімаються повністю
            var meta = image.Metadata;
            meta.ExifProfile = null; meta.XmpProfile = null; meta.IptcProfile = null; meta.IccProfile = null; meta.CicpProfile = null;

            var variants = new List<ImageVariantData> { await Variant(image, MediaOptions.FullSize, t), await Variant(image, MediaOptions.ThumbSize, t) };
            return new ProcessedImage(variants, variants[0].Width, variants[0].Height);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return DecodeFailed; } // таймаут обробки
        catch (Exception ex) when (ex is not OperationCanceledException) { return DecodeFailed; }
        finally { _gate.Release(); }
    }

    private static async Task<ImageVariantData> Variant(Image image, int size, CancellationToken ct)
    {
        // без збільшення: ResizeMode.Max збільшив би менше зображення
        using var v = Math.Max(image.Width, image.Height) > size
            ? image.Clone(c => c.Resize(new ResizeOptions { Size = new Size(size, size), Mode = ResizeMode.Max, Sampler = KnownResamplers.Lanczos3 }))
            : image.Clone(_ => { });
        using var ms = new MemoryStream();
        await v.SaveAsWebpAsync(ms, Encoder, ct);
        return new ImageVariantData(size, v.Width, v.Height, ms.ToArray());
    }
}
