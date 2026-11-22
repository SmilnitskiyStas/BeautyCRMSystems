using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyProductImages;
using SkiaSharp;

namespace BeautyCrm.Infrastructure.Media;

/// <summary>
/// ADR-027 §2.5 / §12.4 (SkiaSharp, MIT; резервний вибір замість ImageSharp, за портом <see cref="IImageProcessor"/>):
/// тип за сигнатурою + формат кодека лише JPEG/PNG/WebP, розміри з заголовка до декодування, один (перший) кадр,
/// EXIF-орієнтація в пікселі, вихід без метаданих (EXIF/XMP/ICC), варіанти 400/1600 без збільшення, WebP lossy q80.
/// Паралельність - семафор (очікування &gt; WaitTimeout = 503 media_busy). Нативна обробка не переривається посередині,
/// тому слот семафора звільняється лише коли робота справді завершилась (навіть після таймауту відповіді).
/// </summary>
public sealed class SkiaSharpProcessor : IImageProcessor, IDisposable
{
    private const int WebpQuality = 80;

    private readonly MediaOptions _options;
    private readonly SemaphoreSlim _gate;

    /// <summary>Очікування черги обробки (ADR-027: 10 с).</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Ліміт часу декодування + кодування одного зображення (security-рев'ю m1).</summary>
    public TimeSpan ProcessingTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Тестовий хук: викликається у фоновій роботі перед декодуванням (імітація довгої нативної обробки).</summary>
    internal Action<CancellationToken>? BeforeDecode { get; init; }

    public SkiaSharpProcessor(MediaOptions options)
    {
        _options = options;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentProcessing));
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

    private static bool IsAllowedFormat(SKEncodedImageFormat f) =>
        f is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp;

    /// <summary>Максимум сканів (SOS) у JPEG: звичайний progressive має ~10; тисячі сканів - CPU-DoS нативного декодера (security M1).</summary>
    internal const int MaxJpegScans = 100;

    /// <summary>Керований прохід маркерами JPEG без декодування: кількість SOS (зупиняється, щойно перевищено limit).</summary>
    internal static int CountJpegScans(ReadOnlySpan<byte> b, int limit)
    {
        var scans = 0;
        var i = 2; // після SOI
        while (i + 1 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; } // entropy-дані скану
            var m = b[i + 1];
            if (m == 0xFF) { i++; continue; } // fill-байти
            if (m == 0x00 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; } // stuffing, TEM, RSTn
            if (m == 0xD9) break; // EOI
            if (i + 3 >= b.Length) break;
            if (m == 0xDA && ++scans > limit) return scans;
            i += 2 + ((b[i + 2] << 8) | b[i + 3]);
        }
        return scans;
    }

    public async Task<Result<ProcessedImage>> ProcessAsync(Stream input, CancellationToken ct)
    {
        var head = new byte[12];
        input.Position = 0;
        var n = await input.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        if (!HasSupportedSignature(head.AsSpan(0, n))) return Unsupported;

        // Одна нативна копія байтів (SKData) на обидва кодеки: потік закриває викликач, а нативна робота може пережити таймаут відповіді.
        SKData data;
        if (input is MemoryStream ms && ms.TryGetBuffer(out var seg))
            data = SKData.CreateCopy(seg.AsSpan());
        else
        {
            input.Position = 0;
            using var copy = new MemoryStream();
            await input.CopyToAsync(copy, ct);
            data = SKData.CreateCopy(copy.GetBuffer().AsSpan(0, (int)copy.Length));
        }

        var handedOff = false;
        try
        {
            if (data.AsSpan()[0] == 0xFF && CountJpegScans(data.AsSpan(), MaxJpegScans) > MaxJpegScans) return DecodeFailed;

            // Identify: SKCodec читає лише заголовок, пікселі не декодуються (захист від декомпресійної бомби).
            SKImageInfo info;
            try
            {
                using var codec = SKCodec.Create(data);
                if (codec is null) return DecodeFailed;
                if (!IsAllowedFormat(codec.EncodedFormat)) return Unsupported;
                info = codec.Info;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return DecodeFailed; }

            if (info.Width < MediaOptions.MinSide || info.Height < MediaOptions.MinSide || info.Width > MediaOptions.MaxSide || info.Height > MediaOptions.MaxSide
                || (long)info.Width * info.Height > _options.MaxPixels)
                return Error.Validation("image_dimensions_invalid", "Image dimensions are out of the allowed range.") with
                {
                    Extras = new Dictionary<string, object?> { ["minSide"] = MediaOptions.MinSide, ["maxSide"] = MediaOptions.MaxSide, ["maxPixels"] = _options.MaxPixels },
                };

            if (!await _gate.WaitAsync(WaitTimeout, ct)) return Error.Unavailable("media_busy", "Image processing is busy. Try again shortly.");

            Task<ProcessedImage?> work;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var token = cts.Token; // токен лишається валідним і після Dispose CTS (IsCancellationRequested)
                var owned = data;
                var hook = BeforeDecode;
                work = Task.Run(() => { using (owned) { hook?.Invoke(token); return Process(owned, token); } }, CancellationToken.None);
                handedOff = true;
            }
            catch
            {
                _gate.Release();
                throw;
            }
            // слот звільняється, коли нативна робота справді закінчилась, а не коли віддано відповідь
            _ = work.ContinueWith(t =>
            {
                _ = t.Exception;
                try { _gate.Release(); } catch (ObjectDisposedException) { } // зупинка хоста під час роботи
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            try
            {
                var result = await work.WaitAsync(ProcessingTimeout, ct);
                return result is null ? DecodeFailed : result;
            }
            catch (TimeoutException) { cts.Cancel(); return DecodeFailed; } // таймаут обробки; скасування спрацює між кроками
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return DecodeFailed; }
            catch (OperationCanceledException) { cts.Cancel(); throw; }
            catch (Exception) { return DecodeFailed; }
        }
        finally
        {
            if (!handedOff) data.Dispose();
        }
    }

    /// <summary>null = не вдалося декодувати. Скасування перевіряється між кроками (нативні виклики не перериваються).</summary>
    private static ProcessedImage? Process(SKData data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var source = Decode(data, out var origin); // кодек і буфери декодера звільнено до ресайзу (security M2)
        if (source is null) return null;
        using (source)
        {
            ct.ThrowIfCancellationRequested();
            var swap = SwapsAxes(origin);
            int ow = swap ? source.Height : source.Width, oh = swap ? source.Width : source.Height; // розміри після орієнтації

            var full = Variant(source, origin, ow, oh, MediaOptions.FullSize, ct);
            ct.ThrowIfCancellationRequested();
            var thumb = Variant(source, origin, ow, oh, MediaOptions.ThumbSize, ct);
            var variants = new List<ImageVariantData> { full, thumb };
            return new ProcessedImage(variants, full.Width, full.Height);
        }
    }

    private static SKBitmap? Decode(SKData data, out SKEncodedOrigin origin)
    {
        origin = SKEncodedOrigin.TopLeft;
        using var codec = SKCodec.Create(data);
        if (codec is null || !IsAllowedFormat(codec.EncodedFormat)) return null;
        origin = codec.EncodedOrigin;

        // фіксовано RGBA 8 біт (4 Б/піксель; 16-бітні PNG не розгортаються), колір приводиться до sRGB
        var decodeInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var source = new SKBitmap();
        // кадр 0 (анімовані WebP/APNG); IncompleteInput (обірваний файл) = збій, а не частково сіре зображення
        if (source.TryAllocPixels(decodeInfo) && codec.GetPixels(decodeInfo, source.GetPixels(), new SKCodecOptions(0)) == SKCodecResult.Success)
            return source;
        source.Dispose();
        return null;
    }

    private static ImageVariantData Variant(SKBitmap source, SKEncodedOrigin origin, int ow, int oh, int size, CancellationToken ct)
    {
        // без збільшення; вписування в квадрат size за довшою стороною
        var (tw, th) = Fit(ow, oh, size);
        var swap = SwapsAxes(origin);
        int sw = swap ? th : tw, sh = swap ? tw : th; // ціль у неповернутих осях

        using var scaled = sw == source.Width && sh == source.Height
            ? null
            : source.Resize(new SKImageInfo(sw, sh, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (scaled is null && (sw != source.Width || sh != source.Height)) throw new InvalidOperationException("resize failed");
        ct.ThrowIfCancellationRequested();

        using var oriented = Orient(scaled ?? source, origin, tw, th);
        // піксельний буфер без колірного простору: енкодер не вбудовує ICC (значення вже в sRGB), EXIF/XMP у SKBitmap відсутні
        using var pixmap = oriented.PeekPixels();
        using var bare = pixmap.WithColorSpace(null);
        using var data = bare.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, WebpQuality))
            ?? throw new InvalidOperationException("webp encode failed");
        return new ImageVariantData(size, tw, th, data.ToArray());
    }

    internal static (int W, int H) Fit(int w, int h, int size)
    {
        var longer = Math.Max(w, h);
        if (longer <= size) return (w, h);
        var scale = (double)size / longer;
        return w >= h
            ? (size, Math.Max(1, (int)Math.Round(h * scale, MidpointRounding.AwayFromZero)))
            : (Math.Max(1, (int)Math.Round(w * scale, MidpointRounding.AwayFromZero)), size);
    }

    private static bool SwapsAxes(SKEncodedOrigin o) =>
        o is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>Копія з застосованою EXIF-орієнтацією (tw x th - розміри результату). Для TopLeft - проста копія.</summary>
    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin, int tw, int th)
    {
        var dst = new SKBitmap(new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        int w = src.Width, h = src.Height;
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // 2: дзеркало по горизонталі
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // 3: 180°
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // 4: дзеркало по вертикалі
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // 5: транспонування
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // 6: 90° за годинниковою
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // 7: трансверсія
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // 8: 90° проти годинникової
            _ => SKMatrix.Identity,
        };
        canvas.SetMatrix(m);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(src, 0, 0, paint);
        canvas.Flush();
        return dst;
    }
}
