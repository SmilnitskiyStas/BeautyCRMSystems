using System.Diagnostics;
using System.Text;
using BeautyCrm.Application.Features.BeautyProductImages;
using BeautyCrm.Infrastructure.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace BeautyCrm.Tests.Beauty;

/// <summary>Фікстури зображень для тестів фото товарів (генеруються в пам'яті, без мережі й файлів).</summary>
internal static class Images
{
    private static byte[] Save(Image img, Action<Image, MemoryStream> save)
    {
        using (img)
        {
            var ms = new MemoryStream();
            save(img, ms);
            return ms.ToArray();
        }
    }

    private static Image<Rgba32> Canvas(int w, int h, Rgba32 fill)
    {
        var img = new Image<Rgba32>(w, h, fill);
        for (var i = 0; i < Math.Min(w, h); i++) img[i, i] = new Rgba32(0, 0, 0, fill.A); // діагональ: щоб поворот був помітний
        return img;
    }

    /// <summary>JPEG; orientation - EXIF Orientation; withMetadata додає Make і GPS-мітку (мають зникнути).</summary>
    public static byte[] Jpeg(int w, int h, ushort? orientation = null, bool withMetadata = false) =>
        Save(Canvas(w, h, new Rgba32(200, 80, 40, 255)), (img, ms) =>
        {
            if (orientation is not null || withMetadata)
            {
                var exif = new ExifProfile();
                if (orientation is { } o) exif.SetValue(ExifTag.Orientation, o);
                if (withMetadata) { exif.SetValue(ExifTag.Make, "SECRETMAKE"); exif.SetValue(ExifTag.GPSLatitudeRef, "N"); }
                img.Metadata.ExifProfile = exif;
            }
            img.SaveAsJpeg(ms);
        });

    public static byte[] Png(int w, int h, byte alpha = 255) => Save(Canvas(w, h, new Rgba32(10, 200, 10, alpha)), (img, ms) => img.SaveAsPng(ms));

    public static byte[] AnimatedWebp(int w, int h)
    {
        using var img = Canvas(w, h, new Rgba32(255, 0, 0, 255));
        using var second = Canvas(w, h, new Rgba32(0, 0, 255, 255));
        img.Frames.AddFrame(second.Frames.RootFrame);
        img.Frames[0].Metadata.GetWebpMetadata().FrameDelay = 100;
        img.Frames[1].Metadata.GetWebpMetadata().FrameDelay = 100;
        var ms = new MemoryStream();
        img.SaveAsWebp(ms, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy });
        return ms.ToArray();
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
    private static ImageSharpProcessor Processor(int maxPixels = 25_000_000) => new(new MediaOptions { MaxPixels = maxPixels });

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
        var p = await Ok(Images.Jpeg(300, 200, orientation: 6, withMetadata: true));
        Assert.Equal((200, 300), (p.Width, p.Height)); // 300x200 з орієнтацією 6 -> 200x300
        foreach (var v in p.Variants)
        {
            using var img = Image.Load(v.Bytes);
            Assert.Equal((200, 300), (img.Width, img.Height));
            Assert.Null(img.Metadata.ExifProfile);
            Assert.Null(img.Metadata.IccProfile);
            Assert.DoesNotContain("SECRETMAKE", Encoding.Latin1.GetString(v.Bytes));
            Assert.DoesNotContain("EXIF", Encoding.Latin1.GetString(v.Bytes));
            Assert.DoesNotContain("XMP", Encoding.Latin1.GetString(v.Bytes));
        }
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
        using var img = Image.Load<Rgba32>(p.Variants[0].Bytes);
        Assert.True(img[100, 150].A < 8);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(p.Variants[0].Bytes, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(p.Variants[0].Bytes, 8, 4));
    }

    [Fact]
    public async Task Animated_webp_keeps_only_the_first_frame()
    {
        var src = Images.AnimatedWebp(300, 300);
        Assert.Equal(2, Image.Load(src).Frames.Count);
        var p = await Ok(src);
        Assert.All(p.Variants, v => Assert.Equal(1, Image.Load(v.Bytes).Frames.Count));
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
        var before = GC.GetTotalAllocatedBytes(true);
        var e = await Rejected(Images.Bomb());
        Assert.Equal("image_dimensions_invalid", e.Code);
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 50_000_000, "bomb must not be decoded into pixels");
        // ліміт мегапікселів з конфігурації
        var tooMany = await Processor(maxPixels: 1_000_000).ProcessAsync(new MemoryStream(Images.Jpeg(1200, 1000)), default);
        Assert.Equal("image_dimensions_invalid", tooMany.Error?.Code);
    }

    [Fact]
    public async Task Corrupt_image_is_422_decode_failed()
    {
        Assert.Equal("image_decode_failed", (await Rejected(Images.Corrupt())).Code);
        var png = Images.Png(800, 800);
        Assert.Equal("image_decode_failed", (await Rejected(png[..(png.Length * 2 / 3)])).Code); // обірваний PNG
    }
}
