using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using BeautyCrm.Application.Features.BeautyProductImages;
using BeautyCrm.Infrastructure.Media;
using SkiaSharp;

namespace BeautyCrm.Tests.Beauty;

/// <summary>Фікстури зображень для тестів фото товарів (генеруються в пам'яті SkiaSharp, без мережі й файлів).</summary>
internal static class Images
{
    private static SKBitmap Canvas(int w, int h, SKColor fill)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bmp.Erase(fill);
        var diag = new SKColor(0, 0, 0, fill.Alpha);
        for (var i = 0; i < Math.Min(w, h); i++) bmp.SetPixel(i, i, diag); // діагональ: щоб поворот був помітний
        return bmp;
    }

    private static byte[] Encode(SKBitmap bmp, SKEncodedImageFormat format, int quality = 90)
    {
        using (bmp)
        using (var data = bmp.Encode(format, quality)) return data.ToArray();
    }

    /// <summary>JPEG; orientation - EXIF Orientation; withMetadata додає EXIF Make, XMP і ICC-профіль (мають зникнути).</summary>
    public static byte[] Jpeg(int w, int h, ushort? orientation = null, bool withMetadata = false)
    {
        var jpeg = Encode(Canvas(w, h, new SKColor(200, 80, 40, 255)), SKEncodedImageFormat.Jpeg);
        if (orientation is null && !withMetadata) return jpeg;
        var segments = new List<byte[]> { App1Exif(orientation, withMetadata ? "SECRETMAKE" : null) };
        if (withMetadata)
        {
            segments.Add(Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8.ToArray(), .. "<x:xmpmeta>SECRETXMP</x:xmpmeta>"u8.ToArray()]));
            segments.Add(Segment(0xE2, [.. "ICC_PROFILE\0"u8.ToArray(), 1, 1, .. "SECRETICC"u8.ToArray(), .. new byte[128]]));
        }
        // сегменти APPn - одразу після SOI
        return [jpeg[0], jpeg[1], .. segments.SelectMany(x => x), .. jpeg[2..]];
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var len = payload.Length + 2;
        return [0xFF, marker, (byte)(len >> 8), (byte)len, .. payload];
    }

    /// <summary>APP1 Exif (TIFF big-endian, IFD0: Orientation і, за потреби, Make).</summary>
    private static byte[] App1Exif(ushort? orientation, string? make)
    {
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>();
        if (make is not null) entries.Add((0x010F, 2, (uint)make.Length + 1, [.. Encoding.ASCII.GetBytes(make), 0]));
        if (orientation is { } o) entries.Add((0x0112, 3, 1, [(byte)(o >> 8), (byte)o, 0, 0]));
        var tiff = new List<byte> { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8 };
        var ifdSize = 2 + entries.Count * 12 + 4;
        var extra = new List<byte>();
        void U16(List<byte> l, int v) { l.Add((byte)(v >> 8)); l.Add((byte)v); }
        void U32(List<byte> l, uint v) { l.Add((byte)(v >> 24)); l.Add((byte)(v >> 16)); l.Add((byte)(v >> 8)); l.Add((byte)v); }
        U16(tiff, entries.Count);
        foreach (var (tag, type, count, value) in entries)
        {
            U16(tiff, tag); U16(tiff, type); U32(tiff, count);
            if (value.Length <= 4) tiff.AddRange(value.Concat(new byte[4 - value.Length]));
            else { U32(tiff, (uint)(8 + ifdSize + extra.Count)); extra.AddRange(value); }
        }
        U32(tiff, 0);
        tiff.AddRange(extra);
        return Segment(0xE1, [.. "Exif\0\0"u8.ToArray(), .. tiff]);
    }

    public static byte[] Png(int w, int h, byte alpha = 255) => Encode(Canvas(w, h, new SKColor(10, 200, 10, alpha)), SKEncodedImageFormat.Png);

    public static byte[] Webp(int w, int h, SKColor fill) => Encode(Canvas(w, h, fill), SKEncodedImageFormat.Webp, 80);

    /// <summary>Анімований WebP з двох кадрів (VP8X + ANIM + ANMF), зібраний з двох статичних lossy WebP.</summary>
    public static byte[] AnimatedWebp(int w, int h)
    {
        static byte[] Vp8Chunk(byte[] webp)
        {
            for (var i = 12; i + 8 <= webp.Length;)
            {
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(i + 4));
                var total = 8 + size + (size & 1);
                if (webp.AsSpan(i, 4).SequenceEqual("VP8 "u8)) return webp[i..(i + total)];
                i += total;
            }
            throw new InvalidOperationException("no VP8 chunk");
        }
        static byte[] U24(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16)];
        static byte[] Chunk(string fourcc, byte[] data)
        {
            var b = new byte[8 + data.Length + (data.Length & 1)];
            Encoding.ASCII.GetBytes(fourcc).CopyTo(b, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)data.Length);
            data.CopyTo(b, 8);
            return b;
        }
        byte[] Frame(SKColor c) => Chunk("ANMF", [.. U24(0), .. U24(0), .. U24(w - 1), .. U24(h - 1), .. U24(100), 0, .. Vp8Chunk(Webp(w, h, c))]);

        byte[] body = [.. "WEBP"u8.ToArray(),
            .. Chunk("VP8X", [0x02, 0, 0, 0, .. U24(w - 1), .. U24(h - 1)]),
            .. Chunk("ANIM", [0, 0, 0, 0, 0, 0]),
            .. Frame(new SKColor(255, 0, 0)), .. Frame(new SKColor(0, 0, 255))];
        var riff = new byte[8 + body.Length];
        "RIFF"u8.CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)body.Length);
        body.CopyTo(riff, 8);
        return riff;
    }

    public static int FrameCount(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        return Math.Max(1, codec.FrameCount);
    }

    public static byte[] Gif() => [.. "GIF89a"u8.ToArray(), .. new byte[64]];
    public static byte[] Svg() => "<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"><rect width=\"300\" height=\"300\"/></svg>"u8.ToArray();
    public static byte[] Heic() => [0, 0, 0, 0x18, .. "ftypheic"u8.ToArray(), .. new byte[64]];

    /// <summary>Валідна сигнатура JPEG, далі сміття: Identify/декодування не проходять.</summary>
    public static byte[] Corrupt() => [0xFF, 0xD8, 0xFF, 0xE0, .. Enumerable.Range(0, 400).Select(i => (byte)(i * 7 + 3))];

    /// <summary>Тіло заданого розміру з сигнатурою JPEG (для перевірки ліміту розміру).</summary>
    public static byte[] Noise(int bytes)
    {
        var b = new byte[bytes];
        new Random(1).NextBytes(b);
        b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF;
        return b;
    }

    /// <summary>PNG із заголовком 30000x30000 (900 Мп) без даних зображення: перевірка розмірів має спрацювати до декодування.</summary>
    public static byte[] Bomb()
    {
        var ihdr = new byte[13];
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(30000)).CopyTo(ihdr, 0);
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(30000)).CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 2;
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(MemoryStream ms, string type, byte[] data)
    {
        void Be(uint v) => ms.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        Be((uint)data.Length);
        var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        ms.Write(td);
        Be(Crc32(td));
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}

/// <summary>ADR-027 §2.5, §33.22.9 п.2: конвеєр обробки зображень (unit, без БД і мережі).</summary>
public sealed class ProductImageProcessorTests
{
    private static SkiaSharpProcessor Processor(int maxPixels = 25_000_000) => new(new MediaOptions { MaxPixels = maxPixels });

    private static async Task<ProcessedImage> Ok(byte[] bytes)
    {
        var r = await Processor().ProcessAsync(new MemoryStream(bytes), default);
        Assert.True(r.IsOk, r.Error?.Code);
        return r.Value!;
    }

    private static async Task<BeautyCrm.Application.Features.BeautyCommon.Error> Rejected(byte[] bytes)
    {
        var r = await Processor().ProcessAsync(new MemoryStream(bytes), default);
        Assert.False(r.IsOk);
        return r.Error!;
    }

    [Fact]
    public async Task Exif_orientation_is_applied_to_pixels_and_all_metadata_is_stripped()
    {
        var src = Images.Jpeg(300, 200, orientation: 6, withMetadata: true);
        foreach (var marker in new[] { "SECRETMAKE", "SECRETXMP", "SECRETICC" }) Assert.Contains(marker, Encoding.Latin1.GetString(src)); // фікстура справді несе метадані
        var p = await Ok(src);
        Assert.Equal((200, 300), (p.Width, p.Height)); // 300x200 з орієнтацією 6 -> 200x300
        foreach (var v in p.Variants)
        {
            using var codec = SKCodec.Create(new SKMemoryStream(v.Bytes));
            Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
            Assert.Equal(SKEncodedOrigin.TopLeft, codec.EncodedOrigin);
            var text = Encoding.Latin1.GetString(v.Bytes);
            foreach (var marker in new[] { "SECRETMAKE", "SECRETXMP", "SECRETICC", "EXIF", "XMP ", "ICCP", "VP8X" })
                Assert.DoesNotContain(marker, text);
        }
        // орієнтація 6 (поворот 90° за годинниковою): діагональ з (0,0) переходить у верхній правий кут
        using var full = SKBitmap.Decode(p.Variants[0].Bytes);
        Assert.Equal((200, 300), (full.Width, full.Height));
        Assert.True(full.GetPixel(full.Width - 2, 1).Red < 120, "dark diagonal expected top-right after rotation");
        Assert.True(full.GetPixel(1, 1).Red > 150, "background expected top-left after rotation");
    }

    [Theory]
    [InlineData(1, 300, 200)] [InlineData(3, 300, 200)] [InlineData(6, 200, 300)] [InlineData(8, 200, 300)]
    [InlineData(2, 300, 200)] [InlineData(4, 300, 200)] [InlineData(5, 200, 300)] [InlineData(7, 200, 300)]
    public async Task Every_exif_orientation_gives_expected_dimensions(ushort orientation, int w, int h)
    {
        var p = await Ok(Images.Jpeg(300, 200, orientation: orientation));
        Assert.Equal((w, h), (p.Width, p.Height));
    }

    [Fact]
    public async Task Variants_are_1600_and_400_by_the_longer_side_and_small_images_are_not_upscaled()
    {
        var big = await Ok(Images.Jpeg(3000, 2000));
        Assert.Equal([(1600, 1600, 1067), (400, 400, 267)], big.Variants.Select(v => (v.Size, v.Width, v.Height)).ToArray());
        Assert.Equal((1600, 1067), (big.Width, big.Height));
        var tall = await Ok(Images.Png(500, 2500));
        Assert.Equal([(1600, 320, 1600), (400, 80, 400)], tall.Variants.Select(v => (v.Size, v.Width, v.Height)).ToArray());
        var small = await Ok(Images.Jpeg(300, 250));
        Assert.All(small.Variants, v => Assert.Equal((300, 250), (v.Width, v.Height)));
        Assert.Equal(big.Variants.Sum(v => v.Bytes.Length), big.TotalBytes);
    }

    [Fact]
    public async Task Png_alpha_is_preserved_in_webp()
    {
        var p = await Ok(Images.Png(300, 300, alpha: 0));
        using var img = SKBitmap.Decode(p.Variants[0].Bytes);
        Assert.True(img.GetPixel(100, 150).Alpha < 8);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(p.Variants[0].Bytes, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(p.Variants[0].Bytes, 8, 4));
    }

    [Fact]
    public async Task Animated_webp_keeps_only_the_first_frame()
    {
        var src = Images.AnimatedWebp(300, 300);
        Assert.Equal(2, Images.FrameCount(src));
        var p = await Ok(src);
        Assert.All(p.Variants, v => Assert.Equal(1, Images.FrameCount(v.Bytes)));
        using var first = SKBitmap.Decode(p.Variants[0].Bytes);
        Assert.True(first.GetPixel(150, 20).Red > 150 && first.GetPixel(150, 20).Blue < 100, "first (red) frame expected");
    }

    [Fact]
    public async Task Unsupported_types_are_415_regardless_of_name()
    {
        foreach (var bytes in new[] { Images.Gif(), Images.Svg(), Images.Heic(), "plain text pretending to be a.jpg"u8.ToArray(), Array.Empty<byte>(), new byte[] { 0xFF, 0xD8 } })
        {
            var e = await Rejected(bytes);
            Assert.Equal((BeautyCrm.Application.Features.BeautyCommon.ErrorKind.UnsupportedMediaType, "unsupported_image_type"), (e.Kind, e.Code));
        }
    }

    [Fact]
    public async Task Out_of_range_dimensions_are_422_and_the_bomb_header_is_rejected_before_decoding()
    {
        foreach (var bytes in new[] { Images.Png(1, 10_000), Images.Png(199, 199), Images.Jpeg(199, 400), Images.Png(10_001, 300) })
            Assert.Equal("image_dimensions_invalid", (await Rejected(bytes)).Code);

        var sw = Stopwatch.StartNew();
        var e = await Rejected(Images.Bomb());
        Assert.Equal("image_dimensions_invalid", e.Code);
        Assert.True(sw.ElapsedMilliseconds < 2000, "bomb must be rejected by Identify, not decoded"); // час, не GC: unmanaged-пам'ять невидима для GC
        // ліміт мегапікселів з конфігурації
        var tooMany = await Processor(maxPixels: 1_000_000).ProcessAsync(new MemoryStream(Images.Jpeg(1200, 1000)), default);
        Assert.Equal("image_dimensions_invalid", tooMany.Error?.Code);
    }

    [Fact]
    public async Task Processing_timeout_gives_422_decode_failed_not_a_hang()
    {
        using var p = new SkiaSharpProcessor(new MediaOptions()) { ProcessingTimeout = TimeSpan.Zero };
        var r = await p.ProcessAsync(new MemoryStream(Images.Jpeg(800, 800)), default);
        Assert.Equal("image_decode_failed", r.Error?.Code);
    }

    [Fact]
    public async Task Slot_of_timed_out_work_is_released_so_the_next_upload_is_not_503()
    {
        using var p = new SkiaSharpProcessor(new MediaOptions()) { ProcessingTimeout = TimeSpan.Zero };
        for (var i = 0; i < 3; i++)
            Assert.Equal("image_decode_failed", (await p.ProcessAsync(new MemoryStream(Images.Jpeg(800, 800)), default)).Error?.Code);
    }

    [Fact]
    public async Task Slot_stays_held_after_timeout_until_native_work_really_ends()
    {
        using var release = new ManualResetEventSlim();
        using var p = new SkiaSharpProcessor(new MediaOptions())
        {
            ProcessingTimeout = TimeSpan.FromMilliseconds(100), WaitTimeout = TimeSpan.FromMilliseconds(300),
            BeforeDecode = _ => release.Wait(TimeSpan.FromSeconds(30)), // "нативна" робота, яку не можна перервати
        };
        Assert.Equal("image_decode_failed", (await p.ProcessAsync(new MemoryStream(Images.Jpeg(400, 400)), default)).Error?.Code);
        // перша робота ще йде: слот зайнятий, друга обробка не стартує
        Assert.Equal("media_busy", (await p.ProcessAsync(new MemoryStream(Images.Jpeg(400, 400)), default)).Error?.Code);
        release.Set();
        var r = await p.ProcessAsync(new MemoryStream(Images.Jpeg(400, 400)), default);
        Assert.True(r.IsOk, r.Error?.Code);
    }

    [Fact]
    public async Task Client_cancel_during_processing_throws_and_frees_the_slot_after_work_ends()
    {
        using var started = new ManualResetEventSlim();
        using var p = new SkiaSharpProcessor(new MediaOptions())
        {
            WaitTimeout = TimeSpan.FromSeconds(5),
            BeforeDecode = t => { if (started.IsSet) return; started.Set(); while (!t.IsCancellationRequested) Thread.Sleep(5); },
        };
        using var cts = new CancellationTokenSource();
        var first = p.ProcessAsync(new MemoryStream(Images.Jpeg(400, 400)), cts.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var r = await p.ProcessAsync(new MemoryStream(Images.Jpeg(400, 400)), default);
        Assert.True(r.IsOk, r.Error?.Code);
    }

    [Fact]
    public async Task Jpeg_with_too_many_scans_is_rejected_before_decoding()
    {
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        for (var i = 0; i < SkiaSharpProcessor.MaxJpegScans + 1; i++) jpeg.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 63, 0, 0x12, 0x34 });
        jpeg.AddRange(new byte[] { 0xFF, 0xD9 });
        Assert.Equal(SkiaSharpProcessor.MaxJpegScans + 1, SkiaSharpProcessor.CountJpegScans(jpeg.ToArray(), SkiaSharpProcessor.MaxJpegScans));
        Assert.Equal("image_decode_failed", (await Rejected(jpeg.ToArray())).Code);
        Assert.Equal(1, SkiaSharpProcessor.CountJpegScans(Images.Jpeg(300, 300), SkiaSharpProcessor.MaxJpegScans)); // baseline JPEG
    }

    [Fact]
    public async Task Jpeg_header_bomb_is_rejected_by_dimensions()
    {
        var jpeg = Images.Jpeg(300, 300);
        var sof = Enumerable.Range(2, jpeg.Length - 10).First(i => jpeg[i] == 0xFF && jpeg[i + 1] is 0xC0 or 0xC2);
        jpeg[sof + 5] = 30000 >> 8; jpeg[sof + 6] = 30000 & 0xFF; jpeg[sof + 7] = 30000 >> 8; jpeg[sof + 8] = 30000 & 0xFF;
        Assert.Equal("image_dimensions_invalid", (await Rejected(jpeg)).Code);
    }

    [Fact]
    public async Task Truncated_jpeg_and_webp_are_422_decode_failed()
    {
        var jpeg = Images.Jpeg(800, 800);
        Assert.Equal("image_decode_failed", (await Rejected(jpeg[..(jpeg.Length / 2)])).Code);
        var webp = Images.Webp(800, 800, new SkiaSharp.SKColor(1, 2, 3));
        Assert.Equal("image_decode_failed", (await Rejected(webp[..(webp.Length / 2)])).Code);
    }

    [Fact]
    public async Task Concurrent_uploads_are_processed_one_at_a_time_without_errors()
    {
        using var p = new SkiaSharpProcessor(new MediaOptions());
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => p.ProcessAsync(new MemoryStream(Images.Png(1200, 900)), default)));
        Assert.All(results, r => Assert.True(r.IsOk, r.Error?.Code));
    }

    [Fact]
    public async Task Corrupt_image_is_422_decode_failed()
    {
        Assert.Equal("image_decode_failed", (await Rejected(Images.Corrupt())).Code);
        var png = Images.Png(800, 800);
        Assert.Equal("image_decode_failed", (await Rejected(png[..(png.Length * 2 / 3)])).Code); // обірваний PNG
    }
}
