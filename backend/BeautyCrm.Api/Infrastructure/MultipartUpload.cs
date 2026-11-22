using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyProductImages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Err = BeautyCrm.Application.Features.BeautyCommon.Error;

namespace BeautyCrm.Api.Infrastructure;

/// <summary>Спільне читання multipart-частини <c>file</c> для upload зображень (фото товарів, логотип закладу): потоково, у пам'ять, з лімітом <c>MaxUploadBytes</c>.</summary>
public static class MultipartUpload
{
    /// <summary>Службові частини multipart понад розмір файлу (ліміт тіла запиту = MaxUploadBytes + Overhead).</summary>
    public const int Overhead = 64 * 1024;

    /// <summary>Перша частина multipart з іменем <c>file</c> (решта ігнорується) у пам'ять з лімітом <c>MaxUploadBytes</c>.</summary>
    public static async Task<Result<Stream>> ReadFilePartAsync(HttpRequest request, MediaOptions media, CancellationToken ct)
    {
        var tooLarge = Err.TooLarge("file_too_large", "The file is too large.");
        var required = Err.Validation("file_required", "A multipart part named 'file' is required.");
        if (request.ContentLength > media.MaxUploadBytes + Overhead) return tooLarge;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mt) || !mt.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mt.Boundary).Length is 0 or > 200)
            return required;
        try
        {
            var reader = new MultipartReader(HeaderUtilities.RemoveQuotes(mt.Boundary).Value!, request.Body) { HeadersLengthLimit = 16 * 1024 };
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
}
